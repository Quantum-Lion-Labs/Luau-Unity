using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace Luau.Unity.Tests
{
    /// <summary>
    /// Every asset helper forwards to a core entry point that refuses to start
    /// a new call on a suspended thread instead of resuming its continuation.
    /// </summary>
    public sealed class LuauSuspendedThreadTests
    {
        const string NewChunk = "mark() return 'B'";

        static readonly Dictionary<string, Func<LuauState, LuauAsset, LuauAsset, string>> Helpers =
            new Dictionary<string, Func<LuauState, LuauAsset, LuauAsset, string>>
            {
                ["Execute(source)"] = (thread, source, verified) => ReadScope(thread.Execute(source)),
                ["Execute(verified)"] = (thread, source, verified) => ReadScope(thread.Execute(verified)),
                ["ExecuteInto(source)"] = (thread, source, verified) => ReadInto(destination =>
                    thread.ExecuteInto(source, destination)),
                ["ExecuteInto(verified)"] = (thread, source, verified) => ReadInto(destination =>
                    thread.ExecuteInto(verified, destination)),
                ["ExecuteAsync(source)"] = (thread, source, verified) => ReadScope(Wait(
                    thread.ExecuteAsync(source))),
                ["ExecuteAsync(verified)"] = (thread, source, verified) => ReadScope(Wait(
                    thread.ExecuteAsync(verified))),
                ["ExecuteIntoAsync(source)"] = (thread, source, verified) => ReadIntoAsync(destination =>
                    thread.ExecuteIntoAsync(source, destination)),
                ["ExecuteWithCompilationServiceAsync"] = (thread, source, verified) => ReadScope(Wait(
                    thread.ExecuteWithCompilationServiceAsync(source, InlineCompilationService.Instance))),
                ["ExecuteIntoWithCompilationServiceAsync"] = (thread, source, verified) => ReadIntoAsync(destination =>
                    thread.ExecuteIntoWithCompilationServiceAsync(
                        source,
                        InlineCompilationService.Instance,
                        destination)),
            };

        [TestCase("Execute(source)")]
        [TestCase("Execute(verified)")]
        [TestCase("ExecuteInto(source)")]
        [TestCase("ExecuteInto(verified)")]
        [TestCase("ExecuteAsync(source)")]
        [TestCase("ExecuteAsync(verified)")]
        [TestCase("ExecuteIntoAsync(source)")]
        [TestCase("ExecuteWithCompilationServiceAsync")]
        [TestCase("ExecuteIntoWithCompilationServiceAsync")]
        public void AssetHelperRejectsASuspendedThreadAndItsContinuationStillResumes(string helper)
        {
            var source = CreateSourceAsset("@unity/suspended-source.luau", NewChunk);
            var verified = CreateVerifiedAsset("@unity/suspended-verified.luau", NewChunk);
            var marks = 0;
            using var providerOverride = LuauUnity.OverrideAssetCompilationProviderForTests(
                (utf8Source, options, cancellationToken) => new ValueTask<LuauCompileResult>(
                    LuauCompileResult.Success(LuauCompiler.Compile(utf8Source.Span, options))));
            using var root = LuauState.Create(new LuauStateOptions
            {
                DefaultExecutionOptions = LuauExecutionOptions.Unbounded,
                BytecodePolicy = LuauBytecodePolicy.RequireValidator,
                BytecodeValidator = new AcceptArtifactValidator(),
            });
            try
            {
                root.OpenBaseLibrary();
                root.OpenCoroutineLibrary();
                using var mark = root.CreateFunction(_ => marks++);
                root["mark"] = mark;
                using var thread = root.CreateThread();
                using (var first = thread.DoString("coroutine.yield(1) return 'A'"))
                {
                    Assert.That(first[0].Read<int>(), Is.EqualTo(1));
                }

                var exception = Assert.Throws<InvalidOperationException>(() =>
                    Helpers[helper](thread, source, verified));

                Assert.That(exception.Message, Does.Contain("suspended"));
                Assert.That(marks, Is.Zero);
                Assert.That(thread.GetStatus(), Is.EqualTo(LuauThreadStatus.Suspended));
                using (var resumed = thread.Resume())
                {
                    Assert.That(resumed[0].Read<string>(), Is.EqualTo("A"));
                }

                Assert.That(marks, Is.Zero);
                Assert.That(Helpers[helper](thread, source, verified), Is.EqualTo("B"));
                Assert.That(marks, Is.EqualTo(1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
                UnityEngine.Object.DestroyImmediate(verified);
            }
        }

        [Test]
        public void ScriptInstanceAssetDoesNotRunOnAThreadItsConfigurationSuspended()
        {
            var asset = CreateSourceAsset("@unity/suspended-instance.luau", "return {}");
            using var providerOverride = LuauUnity.OverrideAssetCompilationProviderForTests(
                (utf8Source, options, cancellationToken) => new ValueTask<LuauCompileResult>(
                    LuauCompileResult.Success(LuauCompiler.Compile(utf8Source.Span, options))));
            using var root = LuauState.Create(new LuauStateOptions
            {
                DefaultExecutionOptions = LuauExecutionOptions.Unbounded,
            });
            try
            {
                root.OpenBaseLibrary();
                root.OpenCoroutineLibrary();
                root.SandboxRoot();

                var exception = Assert.Throws<InvalidOperationException>(() => Wait(
                    root.CreateScriptInstanceAsync(
                        asset,
                        thread => thread.DoString("coroutine.yield({})").Dispose())).Dispose());

                Assert.That(exception.Message, Does.Contain("suspended"));
                using var rootResult = root.DoString("return 1");
                Assert.That(rootResult[0].Read<int>(), Is.EqualTo(1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        static string ReadScope(LuauResultScope results)
        {
            using (results)
            {
                Assert.That(results.Length, Is.EqualTo(1));
                return results[0].Read<string>();
            }
        }

        static string ReadInto(Func<LuauValue[], int> execute)
        {
            var destination = new LuauValue[2];
            Assert.That(execute(destination), Is.EqualTo(1));
            return destination[0].Read<string>();
        }

        static string ReadIntoAsync(Func<Memory<LuauValue>, ValueTask<int>> execute)
        {
            var destination = new LuauValue[2];
            Assert.That(Wait(execute(destination)), Is.EqualTo(1));
            return destination[0].Read<string>();
        }

        // Every compilation step above completes synchronously and the states
        // have no continuation scheduler, so the helpers never need the
        // Unity main thread to make progress.
        static T Wait<T>(ValueTask<T> task)
        {
            return task.AsTask().GetAwaiter().GetResult();
        }

        static LuauAsset CreateSourceAsset(string name, string sourceText)
        {
            var asset = ScriptableObject.CreateInstance<LuauAsset>();
            asset.name = name;
            asset.SetSource(sourceText, Encoding.UTF8.GetBytes(sourceText));
            return asset;
        }

        static LuauAsset CreateVerifiedAsset(string name, string sourceText)
        {
            var output = LuauCompiler.Compile(Encoding.UTF8.GetBytes(sourceText));
            var asset = ScriptableObject.CreateInstance<LuauAsset>();
            asset.name = name;
            asset.SetVerifiedBytecode(
                sourceText,
                LuauBytecodeArtifact.Create(output, name, "tests:suspended-thread"));
            return asset;
        }

        sealed class AcceptArtifactValidator : ILuauBytecodeValidator
        {
            public bool IsValid(LuauBytecodeArtifact artifact, ReadOnlySpan<byte> bytecode)
            {
                return true;
            }
        }

        sealed class InlineCompilationService : ILuauCompilationService
        {
            public static InlineCompilationService Instance { get; } = new InlineCompilationService();

            public ValueTask<LuauCompileResult> CompileAsync(
                ReadOnlyMemory<byte> utf8Source,
                LuauCompileOptions options = null,
                CancellationToken cancellationToken = default)
            {
                return new ValueTask<LuauCompileResult>(
                    LuauCompileResult.Success(LuauCompiler.Compile(utf8Source.Span, options)));
            }

            public ValueTask DisposeAsync()
            {
                return default;
            }
        }
    }
}
