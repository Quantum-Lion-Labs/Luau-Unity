using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Luau.Unity.Editor;
using NUnit.Framework;
using UnityEngine;

namespace Luau.Unity.Tests
{
    public sealed class LuauCompilationServiceTests
    {
        [TestCase(RuntimePlatform.WindowsEditor, 32, 8L * 1024 * 1024)]
        [TestCase(RuntimePlatform.WindowsPlayer, 32, 8L * 1024 * 1024)]
        [TestCase(RuntimePlatform.LinuxEditor, 32, 8L * 1024 * 1024)]
        [TestCase(RuntimePlatform.LinuxPlayer, 32, 8L * 1024 * 1024)]
        [TestCase(RuntimePlatform.Android, 16, 4L * 1024 * 1024)]
        public void RecommendedOptionsMatchMaintainedPlatformPolicy(
            RuntimePlatform platform,
            int queuedRequests,
            long queuedSourceBytes)
        {
            var options = LuauUnity.GetRecommendedCompilationOptions(platform);

            Assert.That(options.WorkerCount, Is.EqualTo(1));
            Assert.That(options.MaxQueuedRequestCount, Is.EqualTo(queuedRequests));
            Assert.That(options.MaxQueuedSourceBytes, Is.EqualTo(queuedSourceBytes));
            Assert.That(options.MaxSourceBytesPerRequest, Is.EqualTo(1024 * 1024));
            Assert.That(options.MaxBytecodeBytesPerResult, Is.EqualTo(4 * 1024 * 1024));
            Assert.That(options.ShutdownTimeout, Is.EqualTo(TimeSpan.FromSeconds(5)));
        }

        [Test]
        public void RecommendedOptionsRejectUnsupportedUnityPlatforms()
        {
            Assert.Throws<PlatformNotSupportedException>(() =>
                LuauUnity.GetRecommendedCompilationOptions(RuntimePlatform.OSXEditor));
        }

        [Test]
        public void RecommendedOptionsCanBeReadOnABackgroundThread()
        {
            var options = Task.Run(LuauUnity.GetRecommendedCompilationOptions)
                .GetAwaiter()
                .GetResult();

            Assert.That(options.WorkerCount, Is.EqualTo(1));
        }

        [Test]
        public void SharedFacadeCompilesOnThePackageOwnedLane()
        {
            var result = LuauUnity
                .CompileAsync(Encoding.UTF8.GetBytes("return 42"))
                .AsTask()
                .GetAwaiter()
                .GetResult();

            Assert.That(result.Kind, Is.EqualTo(LuauCompileResultKind.Success));
            Assert.That(result.Output, Is.Not.Null);
        }

        [Test]
        public void SharedFacadeCompilesModuleBundleThroughThePackageOwnedLane()
        {
            var providerCalls = 0;
            using var providerOverride = LuauUnity.OverrideAssetCompilationProviderForTests(
                (source, options, cancellationToken) =>
                {
                    Interlocked.Increment(ref providerCalls);
                    return new ValueTask<LuauCompileResult>(
                        LuauCompileResult.Success(LuauCompiler.Compile(source.Span, options)));
                });
            var map = new LuauModuleMap(new Dictionary<string, byte[]>
            {
                ["shared/answer"] = Encoding.UTF8.GetBytes("return 42"),
                ["shared/name"] = Encoding.UTF8.GetBytes("return 'Luau'"),
            });

            var bundle = LuauUnity.CompileModuleBundleAsync(map)
                .AsTask()
                .GetAwaiter()
                .GetResult();

            Assert.That(bundle.Count, Is.EqualTo(2));
            Assert.That(bundle.TotalBytecodeBytes, Is.GreaterThan(0));
            Assert.That(providerCalls, Is.EqualTo(2));
            Assert.Throws<ArgumentNullException>(() =>
                LuauUnity.CompileModuleBundleAsync(null));
        }

        [Test]
        public void EditorReloadHookDrainsSharedLaneAndRejectsAdmissionUntilReset()
        {
            var source = Encoding.UTF8.GetBytes("return 42");
            var asset = CreateSourceAsset(
                "@unity/package-shutdown-race.luau",
                "return 42");
            using var state = LuauState.Create();

            try
            {
                var initial = LuauUnity.CompileAsync(source)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
                Assert.That(initial.Kind, Is.EqualTo(LuauCompileResultKind.Success));

                LuauCompilationServiceEditorLifetime.DrainForAssemblyReload();

                var admissionException = Assert.Throws<ObjectDisposedException>(() =>
                    LuauUnity.CompileAsync(source));
                Assert.That(admissionException.Message, Does.Contain("shutting down"));

                var execution = state.ExecuteAsync(asset).AsTask();
                var executionException = Assert.Throws<ObjectDisposedException>(
                    () => execution.GetAwaiter().GetResult());
                Assert.That(executionException.Message, Does.Contain("shutting down"));

                LuauUnity.ResetCompilationServiceAfterDrainForTests();

                var restarted = LuauUnity.CompileAsync(source)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
                Assert.That(restarted.Kind, Is.EqualTo(LuauCompileResultKind.Success));
            }
            finally
            {
                // A real lifecycle drain intentionally leaves the static gate
                // stopped until domain reload. Restore that domain-reset state
                // explicitly so this fixture cannot poison later EditMode tests.
                LuauUnity.DrainCompilationServiceAsync()
                    .GetAwaiter()
                    .GetResult();
                LuauUnity.ResetCompilationServiceAfterDrainForTests();
                LuauUnity.ResetTrackedRootAdmissionAfterDrainForTests();
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void DefaultAssetExecutionCompilesOffOwnerAndStartsVmOnOwner()
        {
            var ownerThreadId = Environment.CurrentManagedThreadId;
            var scheduler = new QueuedOwnerScheduler();
            using var state = LuauState.Create(new LuauStateOptions
            {
                DefaultExecutionOptions = new LuauExecutionOptions
                {
                    ContinuationScheduler = scheduler,
                },
            });
            var asset = CreateSourceAsset(
                "@unity/default-background-lane.luau",
                "return 42");
            using var providerEntered = new ManualResetEventSlim();
            using var releaseProvider = new ManualResetEventSlim();
            var providerCalls = 0;
            var compilerThreadId = 0;
            string sourceSnapshot = null;

            using var providerOverride = LuauUnity.OverrideAssetCompilationProviderForTests(
                (source, options, cancellationToken) =>
                    new ValueTask<LuauCompileResult>(Task.Run(() =>
                    {
                        Interlocked.Increment(ref providerCalls);
                        compilerThreadId = Environment.CurrentManagedThreadId;
                        sourceSnapshot = Encoding.UTF8.GetString(source.Span);
                        providerEntered.Set();
                        releaseProvider.Wait();
                        return LuauCompileResult.Success(
                            LuauCompiler.Compile(source.Span, options));
                    })));

            try
            {
                var execution = state.ExecuteAsync(asset).AsTask();

                Assert.That(providerEntered.Wait(TimeSpan.FromSeconds(2)), Is.True);
                Assert.That(providerCalls, Is.EqualTo(1));
                Assert.That(compilerThreadId, Is.Not.EqualTo(ownerThreadId));
                Assert.That(sourceSnapshot, Is.EqualTo("return 42"));
                Assert.That(execution.IsCompleted, Is.False);

                releaseProvider.Set();
                Assert.That(
                    SpinWait.SpinUntil(() => scheduler.PendingCount == 1, TimeSpan.FromSeconds(2)),
                    Is.True,
                    "Compilation completed without posting VM execution to the state owner.");
                Assert.That(execution.IsCompleted, Is.False);

                scheduler.RunNext();
                using var results = execution.GetAwaiter().GetResult();

                Assert.That(results, Has.Length.EqualTo(1));
                Assert.That(results[0].Read<int>(), Is.EqualTo(42));
                Assert.That(scheduler.PostCount, Is.EqualTo(1));
            }
            finally
            {
                releaseProvider.Set();
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void DefaultAssetExecutionRejectsStateSourceLimitBeforeAdmission()
        {
            var source = Encoding.UTF8.GetBytes("return 42");
            var asset = CreateSourceAsset(
                "@unity/default-source-limit.luau",
                "return 42");
            using var state = LuauState.Create(new LuauStateOptions
            {
                MaxSourceBytes = source.Length - 1,
            });
            var providerCalls = 0;
            using var providerOverride = LuauUnity.OverrideAssetCompilationProviderForTests(
                (utf8Source, options, cancellationToken) =>
                {
                    Interlocked.Increment(ref providerCalls);
                    return new ValueTask<LuauCompileResult>(LuauCompileResult.InfrastructureFailure(
                        new InvalidOperationException("The provider should not be called.")));
                });

            try
            {
                var exception = Assert.ThrowsAsync<LuauLoadLimitException>(async () =>
                    await state.ExecuteAsync(asset));

                Assert.That(exception.ChunkName, Is.EqualTo(asset.name));
                Assert.That(exception.InputKind, Is.EqualTo(LuauLoadInputKind.Source));
                Assert.That(exception.ActualBytes, Is.EqualTo(source.Length));
                Assert.That(exception.LimitBytes, Is.EqualTo(source.Length - 1));
                Assert.That(providerCalls, Is.Zero);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void DefaultAssetExecutionTranslatesDiagnosticWithAssetContext()
        {
            var asset = CreateSourceAsset(
                "@unity/diagnostic-context.luau",
                "this is not valid Luau");
            using var state = LuauState.Create();
            var diagnostic = new LuauCompilationException("expected an expression");
            using var providerOverride = LuauUnity.OverrideAssetCompilationProviderForTests(
                (source, options, cancellationToken) =>
                    new ValueTask<LuauCompileResult>(LuauCompileResult.Diagnostic(diagnostic)));

            try
            {
                var exception = Assert.ThrowsAsync<LuauCompilationException>(async () =>
                    await state.ExecuteAsync(asset));

                Assert.That(exception.ChunkName, Is.EqualTo(asset.name));
                Assert.That(exception.Message, Does.Contain(asset.name));
                Assert.That(exception.Message, Does.Contain("expected an expression"));
                Assert.That(exception.InnerException, Is.SameAs(diagnostic));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void DefaultAssetExecutionRethrowsInfrastructureFailure()
        {
            var asset = CreateSourceAsset(
                "@unity/infrastructure-failure.luau",
                "return 42");
            using var state = LuauState.Create();
            var failure = new InvalidOperationException("Compiler worker failed.");
            using var providerOverride = LuauUnity.OverrideAssetCompilationProviderForTests(
                (source, options, cancellationToken) =>
                    new ValueTask<LuauCompileResult>(
                        LuauCompileResult.InfrastructureFailure(failure)));

            try
            {
                var exception = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await state.ExecuteAsync(asset));

                Assert.That(exception, Is.SameAs(failure));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void VerifiedAssetBypassesCompilationProvider()
        {
            var output = LuauCompiler.Compile(Encoding.UTF8.GetBytes("return 42"));
            var asset = CreateVerifiedAsset(
                "@unity/verified-bypass.luau",
                "return 42",
                LuauBytecodeArtifact.Create(
                    output,
                    "unity-tests/verified-provider-bypass",
                    "tests:first-party"));
            using var state = LuauState.Create(new LuauStateOptions
            {
                BytecodePolicy = LuauBytecodePolicy.RequireValidator,
                BytecodeValidator = AcceptArtifactValidator.Instance,
            });
            var providerCalls = 0;
            using var providerOverride = LuauUnity.OverrideAssetCompilationProviderForTests(
                (source, options, cancellationToken) =>
                {
                    Interlocked.Increment(ref providerCalls);
                    throw new InvalidOperationException("Verified assets must not compile.");
                });

            try
            {
                using var results = state.ExecuteAsync(asset).AsTask().GetAwaiter().GetResult();

                Assert.That(results, Has.Length.EqualTo(1));
                Assert.That(results[0].Read<int>(), Is.EqualTo(42));
                Assert.That(providerCalls, Is.Zero);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void RejectedVerifiedAssetFailsBeforeCompilationProvider()
        {
            var output = LuauCompiler.Compile(Encoding.UTF8.GetBytes("return 42"));
            var asset = CreateVerifiedAsset(
                "@unity/rejected-verified.luau",
                "return 42",
                LuauBytecodeArtifact.Create(
                    output,
                    "unity-tests/rejected-verified-provider",
                    "tests:untrusted"));
            using var state = LuauState.Create();
            var providerCalls = 0;
            using var providerOverride = LuauUnity.OverrideAssetCompilationProviderForTests(
                (source, options, cancellationToken) =>
                {
                    Interlocked.Increment(ref providerCalls);
                    throw new InvalidOperationException("Verified assets must not compile.");
                });

            try
            {
                var exception = Assert.ThrowsAsync<LuauException>(async () =>
                    await state.ExecuteAsync(asset));

                Assert.That(exception.ChunkName, Is.EqualTo(asset.name));
                Assert.That(exception.Message, Does.Contain("disabled").IgnoreCase);
                Assert.That(providerCalls, Is.Zero);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void CompilerOutputExecutionPostsOperationStartToStateOwner()
        {
            var scheduler = new QueuedOwnerScheduler();
            using var state = LuauState.Create(new LuauStateOptions
            {
                BytecodePolicy = LuauBytecodePolicy.Reject,
                DefaultExecutionOptions = new LuauExecutionOptions
                {
                    ContinuationScheduler = scheduler,
                },
            });
            var output = LuauCompiler.Compile(Encoding.UTF8.GetBytes("return 42"));
            Task<LuauResultScope> execution = null;

            Task.Run(() =>
            {
                execution = state.ExecuteCompilerOutputOnOwnerAsync(
                        output,
                        "@unity/owner-thread-handoff.luau".AsMemory())
                    .AsTask();
            }).GetAwaiter().GetResult();

            Assert.That(
                SpinWait.SpinUntil(() => scheduler.PendingCount == 1, TimeSpan.FromSeconds(1)),
                Is.True,
                "The background caller did not post the VM operation to its owner.");
            Assert.That(execution, Is.Not.Null);
            Assert.That(execution.IsCompleted, Is.False);

            scheduler.RunNext();
            using var results = execution.GetAwaiter().GetResult();

            Assert.That(results, Has.Length.EqualTo(1));
            Assert.That(results[0].Read<int>(), Is.EqualTo(42));
            Assert.That(scheduler.PostCount, Is.EqualTo(1));
        }

        [Test]
        public void BackgroundAssetExecutionPreservesTheStateSourceLimit()
        {
            var source = Encoding.UTF8.GetBytes("return 42");
            var asset = ScriptableObject.CreateInstance<LuauAsset>();
            asset.name = "@unity/source-limit.luau";
            asset.SetSource("return 42", source);
            using var state = LuauState.Create(new LuauStateOptions
            {
                MaxSourceBytes = source.Length - 1,
                MaxBytecodeBytes = 1024 * 1024,
            });
            var service = new LuauThreadedCompilationService(
                new LuauThreadedCompilationOptions
                {
                    MaxSourceBytesPerRequest = source.Length,
                });

            try
            {
                var exception = Assert.ThrowsAsync<LuauLoadLimitException>(async () =>
                    await state.ExecuteWithCompilationServiceAsync(asset, service));

                Assert.That(exception.InputKind, Is.EqualTo(LuauLoadInputKind.Source));
                Assert.That(exception.ActualBytes, Is.EqualTo(source.Length));
                Assert.That(exception.LimitBytes, Is.EqualTo(source.Length - 1));
            }
            finally
            {
                service.DisposeAsync().AsTask().GetAwaiter().GetResult();
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void AdvancedAssetExecutionPreservesServiceSourceLimit()
        {
            var source = Encoding.UTF8.GetBytes("return 42");
            var asset = CreateSourceAsset(
                "@unity/service-source-limit.luau",
                "return 42");
            using var state = LuauState.Create(new LuauStateOptions
            {
                MaxSourceBytes = source.Length,
            });
            var service = new LuauThreadedCompilationService(
                new LuauThreadedCompilationOptions
                {
                    MaxSourceBytesPerRequest = source.Length - 1,
                });

            try
            {
                var exception = Assert.ThrowsAsync<LuauCompilationLimitException>(async () =>
                    await state.ExecuteWithCompilationServiceAsync(asset, service));

                Assert.That(
                    exception.LimitKind,
                    Is.EqualTo(LuauCompilationLimitKind.SourceBytesPerRequest));
                Assert.That(exception.Actual, Is.EqualTo(source.Length));
                Assert.That(exception.Limit, Is.EqualTo(source.Length - 1));
            }
            finally
            {
                service.DisposeAsync().AsTask().GetAwaiter().GetResult();
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void CancellationBeforeAdmissionSkipsCompilationProvider()
        {
            var asset = CreateSourceAsset(
                "@unity/cancel-before-admission.luau",
                "return 42");
            using var state = LuauState.Create();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var providerCalls = 0;
            using var providerOverride = LuauUnity.OverrideAssetCompilationProviderForTests(
                (source, options, cancellationToken) =>
                {
                    Interlocked.Increment(ref providerCalls);
                    return new ValueTask<LuauCompileResult>(LuauCompileResult.Canceled());
                });

            try
            {
                var execution = state.ExecuteAsync(asset, cancellation.Token).AsTask();
                var exception = Assert.Throws<LuauExecutionCanceledException>(
                    () => execution.GetAwaiter().GetResult());

                Assert.That(exception.ChunkName, Is.EqualTo(asset.name));
                Assert.That(exception.CancellationToken, Is.EqualTo(cancellation.Token));
                Assert.That(providerCalls, Is.Zero);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void CancellationWhileCompilationIsPendingRetainsTypedContext()
        {
            var asset = CreateSourceAsset(
                "@unity/cancel-pending-compilation.luau",
                "return 42");
            using var state = LuauState.Create();
            using var cancellation = new CancellationTokenSource();
            var providerCompletion = new TaskCompletionSource<LuauCompileResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var providerCalls = 0;
            var observedToken = default(CancellationToken);
            using var providerOverride = LuauUnity.OverrideAssetCompilationProviderForTests(
                (source, options, cancellationToken) =>
                {
                    Interlocked.Increment(ref providerCalls);
                    observedToken = cancellationToken;
                    return new ValueTask<LuauCompileResult>(providerCompletion.Task);
                });

            try
            {
                var execution = state.ExecuteAsync(asset, cancellation.Token).AsTask();
                Assert.That(providerCalls, Is.EqualTo(1));
                Assert.That(observedToken, Is.EqualTo(cancellation.Token));

                cancellation.Cancel();
                providerCompletion.TrySetResult(LuauCompileResult.Canceled());
                var exception = Assert.Throws<LuauExecutionCanceledException>(
                    () => execution.GetAwaiter().GetResult());

                Assert.That(exception.ChunkName, Is.EqualTo(asset.name));
                Assert.That(exception.CancellationToken, Is.EqualTo(cancellation.Token));
            }
            finally
            {
                providerCompletion.TrySetResult(LuauCompileResult.Canceled());
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void CancellationBeforeOwnerDispatchStopsVmInstallation()
        {
            var scheduler = new QueuedOwnerScheduler();
            using var state = LuauState.Create(new LuauStateOptions
            {
                DefaultExecutionOptions = new LuauExecutionOptions
                {
                    ContinuationScheduler = scheduler,
                },
            });
            var asset = CreateSourceAsset(
                "@unity/cancel-before-owner-dispatch.luau",
                "return 42");
            using var cancellation = new CancellationTokenSource();
            var output = LuauCompiler.Compile(Encoding.UTF8.GetBytes("return 42"));
            var providerCompletion = new TaskCompletionSource<LuauCompileResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var providerOverride = LuauUnity.OverrideAssetCompilationProviderForTests(
                (source, options, cancellationToken) =>
                    new ValueTask<LuauCompileResult>(providerCompletion.Task));

            try
            {
                var execution = state.ExecuteAsync(asset, cancellation.Token).AsTask();
                Task.Run(() => providerCompletion.TrySetResult(LuauCompileResult.Success(output)))
                    .GetAwaiter()
                    .GetResult();

                Assert.That(
                    SpinWait.SpinUntil(() => scheduler.PendingCount == 1, TimeSpan.FromSeconds(2)),
                    Is.True,
                    "Successful compilation did not queue owner-thread execution.");
                cancellation.Cancel();
                scheduler.RunNext();

                var exception = Assert.Throws<LuauExecutionCanceledException>(
                    () => execution.GetAwaiter().GetResult());
                Assert.That(exception.ChunkName, Is.EqualTo(asset.name));
                Assert.That(exception.CancellationToken, Is.EqualTo(cancellation.Token));
            }
            finally
            {
                providerCompletion.TrySetResult(LuauCompileResult.Canceled());
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void CancellationDuringVmExecutionRetainsAssetContext()
        {
            var asset = CreateSourceAsset(
                "@unity/cancel-during-vm.luau",
                "pending(); return 42");
            using var state = LuauState.Create();
            using var cancellation = new CancellationTokenSource();
            using var callbackEntered = new ManualResetEventSlim();
            var releaseCallback = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var output = LuauCompiler.Compile(asset.AsSpan());
            state["pending"] = state.CreateAsyncFunction(
                "pending",
                async context =>
                {
                    callbackEntered.Set();
                    await releaseCallback.Task.ConfigureAwait(false);
                });
            using var providerOverride = LuauUnity.OverrideAssetCompilationProviderForTests(
                (source, options, cancellationToken) =>
                    new ValueTask<LuauCompileResult>(LuauCompileResult.Success(output)));

            try
            {
                var execution = state.ExecuteAsync(asset, cancellation.Token).AsTask();
                Assert.That(callbackEntered.Wait(TimeSpan.FromSeconds(2)), Is.True);

                cancellation.Cancel();
                Assert.That(execution.IsCompleted, Is.False);
                releaseCallback.TrySetResult(true);

                var exception = Assert.Throws<LuauExecutionCanceledException>(
                    () => execution.GetAwaiter().GetResult());
                Assert.That(exception.ChunkName, Is.EqualTo(asset.name));
                Assert.That(exception.CancellationToken, Is.EqualTo(cancellation.Token));
            }
            finally
            {
                releaseCallback.TrySetResult(true);
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void DestinationAndAllocatingPathsShareCompilationSemantics()
        {
            const string sourceText = "return 19, 23";
            var asset = CreateSourceAsset(
                "@unity/destination-parity.luau",
                sourceText);
            using var state = LuauState.Create();
            var output = LuauCompiler.Compile(Encoding.UTF8.GetBytes(sourceText));
            var providerCalls = 0;
            using var providerOverride = LuauUnity.OverrideAssetCompilationProviderForTests(
                (source, options, cancellationToken) =>
                {
                    Interlocked.Increment(ref providerCalls);
                    return new ValueTask<LuauCompileResult>(LuauCompileResult.Success(output));
                });

            try
            {
                using var allocating = state.ExecuteAsync(asset).AsTask().GetAwaiter().GetResult();
                var destination = new LuauValue[2];
                var count = state.ExecuteIntoAsync(asset, destination)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();

                Assert.That(providerCalls, Is.EqualTo(2));
                Assert.That(allocating, Has.Length.EqualTo(2));
                Assert.That(count, Is.EqualTo(2));
                Assert.That(allocating[0].Read<int>(), Is.EqualTo(destination[0].Read<int>()));
                Assert.That(allocating[1].Read<int>(), Is.EqualTo(destination[1].Read<int>()));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void UnknownAssetContentFailsClosedBeforeCompilationProvider()
        {
            var asset = CreateSourceAsset(
                "@unity/unknown-content.luau",
                "return 42");
            asset.contentKind = (LuauAssetContentKind)12345;
            using var state = LuauState.Create();
            var providerCalls = 0;
            using var providerOverride = LuauUnity.OverrideAssetCompilationProviderForTests(
                (source, options, cancellationToken) =>
                {
                    Interlocked.Increment(ref providerCalls);
                    throw new InvalidOperationException("Unknown assets must not compile.");
                });

            try
            {
                var exception = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await state.ExecuteAsync(asset));

                Assert.That(exception.Message, Does.Contain("unknown serialized content kind"));
                Assert.That(providerCalls, Is.Zero);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        static LuauAsset CreateSourceAsset(string name, string sourceText)
        {
            var asset = ScriptableObject.CreateInstance<LuauAsset>();
            asset.name = name;
            asset.SetSource(sourceText, Encoding.UTF8.GetBytes(sourceText));
            return asset;
        }

        static LuauAsset CreateVerifiedAsset(
            string name,
            string sourceText,
            LuauBytecodeArtifact artifact)
        {
            var asset = ScriptableObject.CreateInstance<LuauAsset>();
            asset.name = name;
            asset.SetVerifiedBytecode(sourceText, artifact);
            return asset;
        }

        sealed class AcceptArtifactValidator : ILuauBytecodeValidator
        {
            public static AcceptArtifactValidator Instance { get; } = new AcceptArtifactValidator();

            public bool IsValid(
                LuauBytecodeArtifact artifact,
                ReadOnlySpan<byte> bytecode)
            {
                return true;
            }
        }

        sealed class QueuedOwnerScheduler : ILuauContinuationScheduler
        {
            readonly object gate = new object();
            readonly Queue<Action> pending = new Queue<Action>();
            readonly int ownerThreadId = Environment.CurrentManagedThreadId;

            public int PostCount { get; private set; }

            public int PendingCount
            {
                get
                {
                    lock (gate)
                    {
                        return pending.Count;
                    }
                }
            }

            public bool CheckAccess()
            {
                return Environment.CurrentManagedThreadId == ownerThreadId;
            }

            public void Post(Action continuation)
            {
                lock (gate)
                {
                    pending.Enqueue(continuation);
                    PostCount++;
                }
            }

            public void RunNext()
            {
                Action continuation;
                lock (gate)
                {
                    continuation = pending.Dequeue();
                }

                continuation();
            }
        }

    }
}
