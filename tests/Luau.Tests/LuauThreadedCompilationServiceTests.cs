using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Luau.Tests;

public sealed class LuauThreadedCompilationServiceTests
{
    static readonly TimeSpan TestTimeout = TestBudgets.WaitTimeout;

    // The shutdown timeout of a test that starts disposal while a request is
    // still blocked and releases it afterwards. Only a hung drain reaches it,
    // and it is shorter than TestTimeout, so that drain reports its shutdown
    // exception first.
    static readonly TimeSpan ShutdownSafetyNet = TimeSpan.FromSeconds(20);

    [Fact]
    public void OptionsRejectInvalidOrUnboundedValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LuauThreadedCompilationOptions { WorkerCount = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LuauThreadedCompilationOptions { WorkerCount = 3 });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LuauThreadedCompilationOptions { MaxQueuedRequestCount = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LuauThreadedCompilationOptions { MaxQueuedSourceBytes = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LuauThreadedCompilationOptions { MaxSourceBytesPerRequest = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LuauThreadedCompilationOptions { MaxBytecodeBytesPerResult = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LuauThreadedCompilationOptions { ShutdownTimeout = TimeSpan.Zero });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LuauThreadedCompilationOptions { ShutdownTimeout = TimeSpan.FromTicks(-1) });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LuauThreadedCompilationOptions { ShutdownTimeout = Timeout.InfiniteTimeSpan });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LuauThreadedCompilationOptions { ShutdownTimeout = TimeSpan.FromDays(30) });
    }

    [Fact]
    public async Task DefaultsAreFiniteAndServiceOwnsAnOptionsSnapshot()
    {
        var defaults = LuauThreadedCompilationOptions.Default;

        Assert.InRange(defaults.WorkerCount, 1, 2);
        Assert.True(defaults.MaxQueuedRequestCount > 0);
        Assert.True(defaults.MaxQueuedSourceBytes > 0);
        Assert.True(defaults.MaxSourceBytesPerRequest > 0);
        Assert.True(defaults.MaxBytecodeBytesPerResult > 0);
        Assert.True(defaults.ShutdownTimeout > TimeSpan.Zero);
        Assert.NotEqual(Timeout.InfiniteTimeSpan, defaults.ShutdownTimeout);

        var supplied = new LuauThreadedCompilationOptions
        {
            WorkerCount = 2,
            MaxQueuedRequestCount = 7,
            MaxQueuedSourceBytes = 101,
            MaxSourceBytesPerRequest = 31,
            MaxBytecodeBytesPerResult = 43,
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        };
        await using var service = CreateService(supplied);

        Assert.NotSame(supplied, service.Options);
        Assert.Equal(supplied.WorkerCount, service.Options.WorkerCount);
        Assert.Equal(supplied.MaxQueuedRequestCount, service.Options.MaxQueuedRequestCount);
        Assert.Equal(supplied.MaxQueuedSourceBytes, service.Options.MaxQueuedSourceBytes);
        Assert.Equal(supplied.MaxSourceBytesPerRequest, service.Options.MaxSourceBytesPerRequest);
        Assert.Equal(supplied.MaxBytecodeBytesPerResult, service.Options.MaxBytecodeBytesPerResult);
        Assert.Equal(supplied.ShutdownTimeout, service.Options.ShutdownTimeout);
    }

    [Fact]
    public async Task CompileSnapshotsCallerSourceAndCompileOptionsBeforeWorkerReadsThem()
    {
        using var release = new ManualResetEventSlim();
        var blockerEntered = NewSignal();
        byte[]? observedSource = null;
        LuauCompileOptions? observedOptions = null;
        var blockerSource = new byte[] { 0xff };
        var callerSource = new byte[] { 10, 20, 30 };
        var callerOptions = new LuauCompileOptions
        {
            OptimizationLevel = 2,
            DebugLevel = 0,
            TypeInfoLevel = 1,
            CoverageLevel = 2,
        };
        var service = CreateService(
            new LuauThreadedCompilationOptions
            {
                WorkerCount = 1,
                MaxQueuedRequestCount = 2,
                MaxQueuedSourceBytes = 32,
                MaxSourceBytesPerRequest = 16,
            },
            (source, options, _) =>
            {
                if (source.AsSpan().SequenceEqual(blockerSource))
                {
                    blockerEntered.TrySetResult();
                    WaitForRelease(release);
                }
                else
                {
                    observedSource = source;
                    observedOptions = options;
                }

                return CreateOutput(source, options);
            });

        try
        {
            var blocker = service.CompileAsync(blockerSource).AsTask();
            await blockerEntered.Task.WaitAsync(TestTimeout);

            var snapshotted = service.CompileAsync(callerSource, callerOptions).AsTask();
            callerSource.AsSpan().Fill(99);
            release.Set();

            Assert.Equal(LuauCompileResultKind.Success, (await blocker.WaitAsync(TestTimeout)).Kind);
            Assert.Equal(LuauCompileResultKind.Success, (await snapshotted.WaitAsync(TestTimeout)).Kind);
            Assert.Equal(new byte[] { 10, 20, 30 }, observedSource);
            Assert.NotSame(callerSource, observedSource);
            Assert.Equal(callerOptions, observedOptions);
            Assert.NotSame(callerOptions, observedOptions);
        }
        finally
        {
            release.Set();
            await service.DisposeAsync().AsTask().WaitAsync(TestTimeout);
        }
    }

    [Fact]
    public async Task DedicatedWorkerIsNotAThreadPoolThreadAndIsReused()
    {
        var threadIds = new ConcurrentBag<int>();
        var threadNames = new ConcurrentBag<string?>();
        var threadPoolFlags = new ConcurrentBag<bool>();
        await using var service = CreateService(
            new LuauThreadedCompilationOptions { WorkerCount = 1 },
            (source, options, _) =>
            {
                threadIds.Add(Environment.CurrentManagedThreadId);
                threadNames.Add(Thread.CurrentThread.Name);
                threadPoolFlags.Add(Thread.CurrentThread.IsThreadPoolThread);
                return CreateOutput(source, options);
            });

        for (var index = 0; index < 6; index++)
        {
            var result = await service.CompileAsync(new byte[] { checked((byte)(index + 1)) })
                .AsTask()
                .WaitAsync(TestTimeout);
            Assert.Equal(LuauCompileResultKind.Success, result.Kind);
        }

        Assert.Single(threadIds.Distinct());
        Assert.All(threadPoolFlags, Assert.False);
        Assert.All(threadNames, name => Assert.StartsWith("Luau compiler ", name));
    }

    [Fact]
    public async Task DedicatedWorkersDoNotInheritCallerExecutionContext()
    {
        var ambient = new AsyncLocal<string?> { Value = "caller-owned" };
        string? observed = "not-called";
        await using var service = CreateService(
            backend: (source, options, _) =>
            {
                observed = ambient.Value;
                return CreateOutput(source, options);
            });

        var result = await service.CompileAsync(new byte[] { 1 })
            .AsTask()
            .WaitAsync(TestTimeout);

        Assert.Equal(LuauCompileResultKind.Success, result.Kind);
        Assert.Null(observed);
        Assert.Equal("caller-owned", ambient.Value);
    }

    [Fact]
    public async Task WorkerCountCapsParallelismAndReusesOnlyConfiguredWorkers()
    {
        using var release = new ManualResetEventSlim();
        var bothWorkersEntered = NewSignal();
        var workerIds = new ConcurrentDictionary<int, byte>();
        var active = 0;
        var maximumActive = 0;
        var service = CreateService(
            new LuauThreadedCompilationOptions
            {
                WorkerCount = 2,
                MaxQueuedRequestCount = 8,
                MaxQueuedSourceBytes = 64,
            },
            (source, options, _) =>
            {
                workerIds.TryAdd(Environment.CurrentManagedThreadId, 0);
                var currentActive = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, currentActive);
                if (currentActive == 2)
                {
                    bothWorkersEntered.TrySetResult();
                }

                try
                {
                    WaitForRelease(release);
                    return CreateOutput(source, options);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            });

        try
        {
            var requests = Enumerable.Range(1, 8)
                .Select(value => service.CompileAsync(new byte[] { checked((byte)value) }).AsTask())
                .ToArray();

            await bothWorkersEntered.Task.WaitAsync(TestTimeout);
            Assert.Equal(2, service.ReservationSnapshot.ActiveRequests);
            Assert.Equal(2, Volatile.Read(ref maximumActive));
            Assert.Equal(2, workerIds.Count);

            release.Set();
            var results = await Task.WhenAll(requests).WaitAsync(TestTimeout);

            Assert.All(results, result => Assert.Equal(LuauCompileResultKind.Success, result.Kind));
            Assert.Equal(2, maximumActive);
            Assert.Equal(2, workerIds.Count);
        }
        finally
        {
            release.Set();
            await service.DisposeAsync().AsTask().WaitAsync(TestTimeout);
        }
    }

    [Fact]
    public async Task ResultsDistinguishSuccessDiagnosticCancellationAndInfrastructureFailure()
    {
        var backendCalls = 0;
        await using var service = CreateService(
            backend: (source, options, _) =>
            {
                Interlocked.Increment(ref backendCalls);
                return source[0] switch
                {
                    1 => CreateOutput(source, options),
                    2 => throw new LuauCompilationException("expected source diagnostic"),
                    _ => throw new InvalidOperationException("expected backend failure"),
                };
            });

        var success = await service.CompileAsync(new byte[] { 1 }).AsTask().WaitAsync(TestTimeout);
        var diagnostic = await service.CompileAsync(new byte[] { 2 }).AsTask().WaitAsync(TestTimeout);
        var infrastructure = await service.CompileAsync(new byte[] { 3 }).AsTask().WaitAsync(TestTimeout);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceled = await service.CompileAsync(new byte[] { 1 }, cancellationToken: cancellation.Token);

        AssertResultShape(success, LuauCompileResultKind.Success);
        AssertResultShape(diagnostic, LuauCompileResultKind.Diagnostic);
        Assert.Equal("expected source diagnostic", diagnostic.CompilationDiagnostic!.Message);
        AssertResultShape(infrastructure, LuauCompileResultKind.InfrastructureFailure);
        Assert.Equal("expected backend failure", infrastructure.InfrastructureException!.Message);
        AssertResultShape(canceled, LuauCompileResultKind.Canceled);
        Assert.Equal(3, backendCalls);
        Assert.Equal((0, 0L, 0), service.ReservationSnapshot);
    }

    [Fact]
    public async Task PublicServiceImplementerCanConstructEveryResultKind()
    {
        var output = LuauCompiler.Compile("return 42"u8);
        await using ILuauCompilationService service = new FactoryBackedCompilationService(output);

        var success = await service.CompileAsync(new byte[] { 0 });
        var diagnostic = await service.CompileAsync(new byte[] { 1 });
        var canceled = await service.CompileAsync(new byte[] { 2 });
        var infrastructure = await service.CompileAsync(new byte[] { 3 });

        AssertResultShape(success, LuauCompileResultKind.Success);
        Assert.Same(output, success.Output);
        AssertResultShape(diagnostic, LuauCompileResultKind.Diagnostic);
        Assert.Equal("service diagnostic", diagnostic.CompilationDiagnostic!.Message);
        AssertResultShape(canceled, LuauCompileResultKind.Canceled);
        AssertResultShape(infrastructure, LuauCompileResultKind.InfrastructureFailure);
        Assert.Equal("service infrastructure failure", infrastructure.InfrastructureException!.Message);

        Assert.Throws<ArgumentNullException>(() => LuauCompileResult.Success(null!));
        Assert.Throws<ArgumentNullException>(() => LuauCompileResult.Diagnostic(null!));
        Assert.Throws<ArgumentNullException>(() => LuauCompileResult.InfrastructureFailure(null!));
    }

    [Fact]
    public async Task OversizedCompilerOutputIsAnInfrastructureLimitFailure()
    {
        await using var exactService = CreateService(
            new LuauThreadedCompilationOptions { MaxBytecodeBytesPerResult = 3 },
            (source, options, _) => CreateOutput(source, options, bytecodeLength: 3));

        var exact = await exactService.CompileAsync(new byte[] { 1 }).AsTask().WaitAsync(TestTimeout);

        AssertResultShape(exact, LuauCompileResultKind.Success);
        Assert.Equal(3, exact.Output!.BytecodeLength);
        Assert.Equal((0, 0L, 0), exactService.ReservationSnapshot);

        await using var service = CreateService(
            new LuauThreadedCompilationOptions { MaxBytecodeBytesPerResult = 3 },
            (source, options, _) => CreateOutput(source, options, bytecodeLength: 4));

        var result = await service.CompileAsync(new byte[] { 1 }).AsTask().WaitAsync(TestTimeout);

        AssertResultShape(result, LuauCompileResultKind.InfrastructureFailure);
        var exception = Assert.IsType<LuauCompilationLimitException>(result.InfrastructureException);
        Assert.Equal(LuauCompilationLimitKind.BytecodeBytesPerResult, exception.LimitKind);
        Assert.Equal(4, exception.Actual);
        Assert.Equal(3, exception.Limit);
        Assert.Equal((0, 0L, 0), service.ReservationSnapshot);
    }

    [Fact]
    public async Task RealBackendEnforcesOutputLimitBeforePublishingCapability()
    {
        await using var service = new LuauThreadedCompilationService(
            new LuauThreadedCompilationOptions
            {
                MaxBytecodeBytesPerResult = 1,
            });

        var result = await service.CompileAsync("return 42"u8.ToArray())
            .AsTask()
            .WaitAsync(TestTimeout);

        AssertResultShape(result, LuauCompileResultKind.InfrastructureFailure);
        var exception = Assert.IsType<LuauCompilationLimitException>(
            result.InfrastructureException);
        Assert.Equal(LuauCompilationLimitKind.BytecodeBytesPerResult, exception.LimitKind);
        Assert.True(exception.Actual > exception.Limit);
        Assert.Equal(1, exception.Limit);
        Assert.Equal((0, 0L, 0), service.ReservationSnapshot);

        // The rejected native buffer was freed and did not poison later use of
        // the standalone compiler.
        Assert.True(LuauCompiler.Compile("return 7"u8).BytecodeLength > 1);
    }

    [Fact]
    public async Task BytecodeLimitDoesNotReclassifyCompilerDiagnostics()
    {
        await using var service = new LuauThreadedCompilationService(
            new LuauThreadedCompilationOptions
            {
                MaxBytecodeBytesPerResult = 1,
            });

        var result = await service.CompileAsync("local broken = )"u8.ToArray())
            .AsTask()
            .WaitAsync(TestTimeout);

        AssertResultShape(result, LuauCompileResultKind.Diagnostic);
        Assert.Contains("Expected", result.CompilationDiagnostic!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((0, 0L, 0), service.ReservationSnapshot);
    }

    [Fact]
    public async Task PerRequestSourceLimitAcceptsTheBoundaryAndRejectsTheNextByte()
    {
        var backendCalls = 0;
        await using var service = CreateService(
            new LuauThreadedCompilationOptions { MaxSourceBytesPerRequest = 3 },
            (source, options, _) =>
            {
                Interlocked.Increment(ref backendCalls);
                return CreateOutput(source, options);
            });

        var accepted = await service.CompileAsync(new byte[] { 1, 2, 3 })
            .AsTask()
            .WaitAsync(TestTimeout);
        var exception = Assert.Throws<LuauCompilationLimitException>(() =>
            service.CompileAsync(new byte[] { 1, 2, 3, 4 }));

        Assert.Equal(LuauCompileResultKind.Success, accepted.Kind);
        Assert.Equal(LuauCompilationLimitKind.SourceBytesPerRequest, exception.LimitKind);
        Assert.Equal(4, exception.Actual);
        Assert.Equal(3, exception.Limit);
        Assert.Equal(1, backendCalls);
    }

    [Fact]
    public async Task QueuedRequestCountAcceptsTheBoundaryAndRejectsTheNextRequest()
    {
        using var release = new ManualResetEventSlim();
        var activeEntered = NewSignal();
        var service = CreateBlockingService(
            release,
            activeEntered,
            new LuauThreadedCompilationOptions
            {
                WorkerCount = 1,
                MaxQueuedRequestCount = 2,
                MaxQueuedSourceBytes = 32,
            });

        try
        {
            var active = service.CompileAsync(new byte[] { 1 }).AsTask();
            await activeEntered.Task.WaitAsync(TestTimeout);
            var queuedAtBoundary = service.CompileAsync(new byte[] { 2 }).AsTask();

            var exception = Assert.Throws<LuauCompilationLimitException>(() =>
                service.CompileAsync(new byte[] { 3 }));

            Assert.Equal(LuauCompilationLimitKind.QueuedRequestCount, exception.LimitKind);
            Assert.Equal(3, exception.Actual);
            Assert.Equal(2, exception.Limit);
            Assert.Equal((2, 2L, 1), service.ReservationSnapshot);

            release.Set();
            var results = await Task.WhenAll(active, queuedAtBoundary).WaitAsync(TestTimeout);
            Assert.All(results, result => Assert.Equal(LuauCompileResultKind.Success, result.Kind));
        }
        finally
        {
            release.Set();
            await service.DisposeAsync().AsTask().WaitAsync(TestTimeout);
        }
    }

    [Fact]
    public async Task RejectedRequestIsNotSnapshottedOutsideTheAdmissionBound()
    {
        using var release = new ManualResetEventSlim();
        var activeEntered = NewSignal();
        var service = CreateBlockingService(
            release,
            activeEntered,
            new LuauThreadedCompilationOptions
            {
                WorkerCount = 1,
                MaxQueuedRequestCount = 1,
                MaxQueuedSourceBytes = 8,
                MaxSourceBytesPerRequest = 8,
            });
        using var source = new CountingMemoryManager(new byte[] { 2, 3, 4 });
        var memory = source.Memory;
        var readsBeforeAdmission = source.SpanReadCount;

        try
        {
            var active = service.CompileAsync(new byte[] { 1 }).AsTask();
            await activeEntered.Task.WaitAsync(TestTimeout);

            var exception = Assert.Throws<LuauCompilationLimitException>(() =>
                service.CompileAsync(memory));

            Assert.Equal(LuauCompilationLimitKind.QueuedRequestCount, exception.LimitKind);
            Assert.Equal(readsBeforeAdmission, source.SpanReadCount);

            release.Set();
            Assert.Equal(
                LuauCompileResultKind.Success,
                (await active.WaitAsync(TestTimeout)).Kind);
        }
        finally
        {
            release.Set();
            await service.DisposeAsync().AsTask().WaitAsync(TestTimeout);
        }
    }

    [Fact]
    public async Task QueuedSourceBytesAcceptTheBoundaryAndRejectTheNextByte()
    {
        using var release = new ManualResetEventSlim();
        var activeEntered = NewSignal();
        var service = CreateBlockingService(
            release,
            activeEntered,
            new LuauThreadedCompilationOptions
            {
                WorkerCount = 1,
                MaxQueuedRequestCount = 3,
                MaxQueuedSourceBytes = 5,
                MaxSourceBytesPerRequest = 4,
            });

        try
        {
            var active = service.CompileAsync(new byte[] { 1, 1 }).AsTask();
            await activeEntered.Task.WaitAsync(TestTimeout);
            var queuedAtBoundary = service.CompileAsync(new byte[] { 2, 2, 2 }).AsTask();

            var exception = Assert.Throws<LuauCompilationLimitException>(() =>
                service.CompileAsync(new byte[] { 3 }));

            Assert.Equal(LuauCompilationLimitKind.QueuedSourceBytes, exception.LimitKind);
            Assert.Equal(6, exception.Actual);
            Assert.Equal(5, exception.Limit);
            Assert.Equal((2, 5L, 1), service.ReservationSnapshot);

            release.Set();
            var results = await Task.WhenAll(active, queuedAtBoundary).WaitAsync(TestTimeout);
            Assert.All(results, result => Assert.Equal(LuauCompileResultKind.Success, result.Kind));
        }
        finally
        {
            release.Set();
            await service.DisposeAsync().AsTask().WaitAsync(TestTimeout);
        }
    }

    [Fact]
    public async Task CancelingQueuedRequestReleasesItsReservationForReplacementWork()
    {
        using var release = new ManualResetEventSlim();
        var activeEntered = NewSignal();
        using var queuedCancellation = new CancellationTokenSource();
        var service = CreateBlockingService(
            release,
            activeEntered,
            new LuauThreadedCompilationOptions
            {
                WorkerCount = 1,
                MaxQueuedRequestCount = 2,
                MaxQueuedSourceBytes = 8,
                MaxSourceBytesPerRequest = 4,
            });

        try
        {
            var active = service.CompileAsync(new byte[] { 1, 1 }).AsTask();
            await activeEntered.Task.WaitAsync(TestTimeout);
            var queued = service.CompileAsync(
                new byte[] { 2, 2 },
                cancellationToken: queuedCancellation.Token).AsTask();
            Assert.Equal((2, 4L, 1), service.ReservationSnapshot);

            queuedCancellation.Cancel();
            var canceled = await queued.WaitAsync(TestTimeout);

            AssertResultShape(canceled, LuauCompileResultKind.Canceled);
            Assert.Equal((1, 2L, 1), service.ReservationSnapshot);
            var replacement = service.CompileAsync(new byte[] { 3, 3 }).AsTask();
            Assert.Equal((2, 4L, 1), service.ReservationSnapshot);

            release.Set();
            Assert.Equal(LuauCompileResultKind.Success, (await active.WaitAsync(TestTimeout)).Kind);
            Assert.Equal(LuauCompileResultKind.Success, (await replacement.WaitAsync(TestTimeout)).Kind);
        }
        finally
        {
            release.Set();
            await service.DisposeAsync().AsTask().WaitAsync(TestTimeout);
        }
    }

    [Fact]
    public async Task CancellationRequestedBeforeDequeueSkipsTheBackendEvenWhenCallbackIsSuppressed()
    {
        using var release = new ManualResetEventSlim();
        var activeEntered = NewSignal();
        using var cancellation = new CancellationTokenSource();
        var secondBackendCalls = 0;
        var service = CreateService(
            new LuauThreadedCompilationOptions
            {
                WorkerCount = 1,
                MaxQueuedRequestCount = 2,
            },
            (source, options, _) =>
            {
                if (source[0] == 1)
                {
                    activeEntered.TrySetResult();
                    WaitForRelease(release);
                }
                else
                {
                    Interlocked.Increment(ref secondBackendCalls);
                }

                return CreateOutput(source, options);
            });

        try
        {
            var active = service.CompileAsync(new byte[] { 1 }).AsTask();
            await activeEntered.Task.WaitAsync(TestTimeout);
            var queued = service.CompileAsync(
                new byte[] { 2 },
                cancellationToken: cancellation.Token).AsTask();
            using var suppressingCallback = cancellation.Token.Register(
                static () => throw new InvalidOperationException(
                    "expected cancellation callback failure"));

            Assert.Throws<InvalidOperationException>(() =>
                cancellation.Cancel(throwOnFirstException: true));
            release.Set();

            Assert.Equal(
                LuauCompileResultKind.Success,
                (await active.WaitAsync(TestTimeout)).Kind);
            Assert.Equal(
                LuauCompileResultKind.Canceled,
                (await queued.WaitAsync(TestTimeout)).Kind);
            Assert.Equal(0, secondBackendCalls);
            Assert.Equal((0, 0L, 0), service.ReservationSnapshot);
        }
        finally
        {
            release.Set();
            await service.DisposeAsync().AsTask().WaitAsync(TestTimeout);
        }
    }

    [Fact]
    public async Task CancelingRunningRequestWaitsForBackendAndDiscardsItsOutput()
    {
        using var release = new ManualResetEventSlim();
        var activeEntered = NewSignal();
        using var cancellation = new CancellationTokenSource();
        var service = CreateBlockingService(release, activeEntered);

        try
        {
            var running = service.CompileAsync(
                new byte[] { 1 },
                cancellationToken: cancellation.Token).AsTask();
            await activeEntered.Task.WaitAsync(TestTimeout);

            cancellation.Cancel();

            Assert.False(running.IsCompleted);
            Assert.Equal((1, 1L, 1), service.ReservationSnapshot);
            release.Set();

            var result = await running.WaitAsync(TestTimeout);
            AssertResultShape(result, LuauCompileResultKind.Canceled);
            Assert.Equal((0, 0L, 0), service.ReservationSnapshot);
        }
        finally
        {
            release.Set();
            await service.DisposeAsync().AsTask().WaitAsync(TestTimeout);
        }
    }

    [Fact]
    public async Task CancellationAfterCompletionDoesNotRewritePublishedSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        await using var service = CreateService();

        var result = await service.CompileAsync(
                new byte[] { 1 },
                cancellationToken: cancellation.Token)
            .AsTask()
            .WaitAsync(TestTimeout);
        cancellation.Cancel();

        AssertResultShape(result, LuauCompileResultKind.Success);
        Assert.Equal((0, 0L, 0), service.ReservationSnapshot);
    }

    [Fact]
    public async Task ShutdownCancelsQueuedWorkRejectsAdmissionAndWaitsForActiveWork()
    {
        using var release = new ManualResetEventSlim();
        var activeEntered = NewSignal();
        var service = CreateBlockingService(
            release,
            activeEntered,
            new LuauThreadedCompilationOptions
            {
                WorkerCount = 1,
                MaxQueuedRequestCount = 2,
                ShutdownTimeout = ShutdownSafetyNet,
            });
        Task? disposal = null;

        try
        {
            var active = service.CompileAsync(new byte[] { 1 }).AsTask();
            await activeEntered.Task.WaitAsync(TestTimeout);
            var queued = service.CompileAsync(new byte[] { 2 }).AsTask();

            disposal = service.DisposeAsync().AsTask();
            var concurrentDisposal = service.DisposeAsync().AsTask();
            var queuedResult = await queued.WaitAsync(TestTimeout);

            Assert.Same(disposal, concurrentDisposal);
            AssertResultShape(queuedResult, LuauCompileResultKind.Canceled);
            Assert.False(disposal.IsCompleted);
            Assert.Equal((1, 1L, 1), service.ReservationSnapshot);
            Assert.Throws<ObjectDisposedException>(() => service.CompileAsync(new byte[] { 3 }));

            release.Set();
            Assert.Equal(LuauCompileResultKind.Success, (await active.WaitAsync(TestTimeout)).Kind);
            await Task.WhenAll(disposal, concurrentDisposal).WaitAsync(TestTimeout);
            Assert.Equal((0, 0L, 0), service.ReservationSnapshot);
        }
        finally
        {
            release.Set();
            if (disposal != null)
            {
                await disposal.WaitAsync(TestTimeout);
            }
            else
            {
                await service.DisposeAsync().AsTask().WaitAsync(TestTimeout);
            }
        }
    }

    [Fact]
    public async Task DisposalTimeoutReportsActiveRequestWithoutAbortingItsWorker()
    {
        using var release = new ManualResetEventSlim();
        var activeEntered = NewSignal();
        var configuredTimeout = TimeSpan.FromMilliseconds(150);
        var service = CreateBlockingService(
            release,
            activeEntered,
            new LuauThreadedCompilationOptions
            {
                WorkerCount = 1,
                ShutdownTimeout = configuredTimeout,
            });

        try
        {
            var active = service.CompileAsync(new byte[] { 1 }).AsTask();
            await activeEntered.Task.WaitAsync(TestTimeout);

            var disposal = service.DisposeAsync().AsTask();
            var exception = await Assert.ThrowsAsync<LuauCompilationShutdownException>(
                () => disposal.WaitAsync(TestTimeout));

            Assert.Equal(configuredTimeout, exception.Timeout);
            Assert.Equal(1, exception.ActiveRequestCount);
            Assert.Contains("were not aborted", exception.Message);
            Assert.False(active.IsCompleted);
            Assert.Equal((1, 1L, 1), service.ReservationSnapshot);

            release.Set();
            Assert.Equal(LuauCompileResultKind.Success, (await active.WaitAsync(TestTimeout)).Kind);
            Assert.Equal((0, 0L, 0), service.ReservationSnapshot);
            await service.DisposeAsync().AsTask().WaitAsync(TestTimeout);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task CompileAfterCompletedDisposalIsRejected()
    {
        var service = CreateService();

        await service.DisposeAsync().AsTask().WaitAsync(TestTimeout);

        Assert.Throws<ObjectDisposedException>(() => service.CompileAsync(new byte[] { 1 }));
        await service.DisposeAsync().AsTask().WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task CompletedDisposalHasJoinedTheDedicatedWorker()
    {
        Thread? workerThread = null;
        var service = CreateService(
            backend: (source, options, _) =>
            {
                workerThread = Thread.CurrentThread;
                return CreateOutput(source, options);
            });

        Assert.Equal(
            LuauCompileResultKind.Success,
            (await service.CompileAsync(new byte[] { 1 }).AsTask().WaitAsync(TestTimeout)).Kind);
        await service.DisposeAsync().AsTask().WaitAsync(TestTimeout);

        Assert.NotNull(workerThread);
        Assert.False(workerThread!.IsAlive);
    }

    [Fact]
    public async Task QueuedCancellationRacingDisposalAlwaysPublishesBeforeDrainCompletes()
    {
        for (var iteration = 0; iteration < 40; iteration++)
        {
            using var release = new ManualResetEventSlim();
            var activeEntered = NewSignal();
            using var cancellation = new CancellationTokenSource();
            var service = CreateBlockingService(
                release,
                activeEntered,
                new LuauThreadedCompilationOptions
                {
                    WorkerCount = 1,
                    MaxQueuedRequestCount = 2,
                    ShutdownTimeout = ShutdownSafetyNet,
                });

            var active = service.CompileAsync(new byte[] { 1 }).AsTask();
            await activeEntered.Task.WaitAsync(TestTimeout);
            var queued = service.CompileAsync(
                new byte[] { 2 },
                cancellationToken: cancellation.Token).AsTask();

            using var start = new ManualResetEventSlim();
            var disposalStarted = NewSignal();
            var cancelTask = Task.Run(() =>
            {
                start.Wait(TestTimeout);
                cancellation.Cancel();
            });
            var disposeTask = Task.Run(async () =>
            {
                start.Wait(TestTimeout);
                var disposal = service.DisposeAsync();
                disposalStarted.TrySetResult();
                await disposal;
            });
            start.Set();
            await Task.WhenAll(cancelTask, disposalStarted.Task).WaitAsync(TestTimeout);
            release.Set();

            await disposeTask.WaitAsync(TestTimeout);
            Assert.True(queued.IsCompleted);
            Assert.Equal(
                LuauCompileResultKind.Canceled,
                (await queued.WaitAsync(TestTimeout)).Kind);
            Assert.Equal(
                LuauCompileResultKind.Success,
                (await active.WaitAsync(TestTimeout)).Kind);
            Assert.Equal((0, 0L, 0), service.ReservationSnapshot);
        }
    }

    [Fact]
    public async Task AdmissionRacingDisposalHasOnlyLinearizedFiniteOutcomes()
    {
        for (var iteration = 0; iteration < 40; iteration++)
        {
            var service = CreateService();
            using var start = new ManualResetEventSlim();
            var admission = Task.Run(async () =>
            {
                start.Wait(TestTimeout);
                try
                {
                    return await service.CompileAsync(new byte[] { 1 });
                }
                catch (ObjectDisposedException)
                {
                    return null;
                }
            });
            var disposal = Task.Run(async () =>
            {
                start.Wait(TestTimeout);
                await service.DisposeAsync();
            });

            start.Set();
            await Task.WhenAll(admission, disposal).WaitAsync(TestTimeout);
            var admissionResult = await admission;

            if (admissionResult != null)
            {
                Assert.True(
                    admissionResult.Kind is LuauCompileResultKind.Success or
                        LuauCompileResultKind.Canceled);
            }
            Assert.Equal((0, 0L, 0), service.ReservationSnapshot);
        }
    }

    [Fact]
    public async Task RunningCancellationRacingCompletionHasOnlyLinearizedOutcomes()
    {
        for (var iteration = 0; iteration < 40; iteration++)
        {
            using var release = new ManualResetEventSlim();
            var activeEntered = NewSignal();
            using var cancellation = new CancellationTokenSource();
            var service = CreateBlockingService(release, activeEntered);
            var request = service.CompileAsync(
                new byte[] { 1 },
                cancellationToken: cancellation.Token).AsTask();
            await activeEntered.Task.WaitAsync(TestTimeout);

            using var start = new ManualResetEventSlim();
            var cancel = Task.Run(() =>
            {
                start.Wait(TestTimeout);
                cancellation.Cancel();
            });
            var complete = Task.Run(() =>
            {
                start.Wait(TestTimeout);
                release.Set();
            });
            start.Set();

            await Task.WhenAll(cancel, complete).WaitAsync(TestTimeout);
            var result = await request.WaitAsync(TestTimeout);
            Assert.True(
                result.Kind is LuauCompileResultKind.Success or LuauCompileResultKind.Canceled);
            await service.DisposeAsync().AsTask().WaitAsync(TestTimeout);
            Assert.Equal((0, 0L, 0), service.ReservationSnapshot);
        }
    }

    [Fact]
    public async Task ParallelCompilationProducesTheSameBytesAsSerialCompilation()
    {
        var source = "local total = 0; for index = 1, 100 do total += index end; return total"u8.ToArray();
        var compileOptions = new LuauCompileOptions
        {
            OptimizationLevel = 2,
            DebugLevel = 1,
            TypeInfoLevel = 1,
            CoverageLevel = 0,
        };
        var serial = LuauCompiler.Compile(source, compileOptions);
        var serialBytes = serial.ToBytecodeArray();
        await using var service = new LuauThreadedCompilationService(
            new LuauThreadedCompilationOptions
            {
                WorkerCount = 2,
                MaxQueuedRequestCount = 32,
                MaxQueuedSourceBytes = source.Length * 32L,
                MaxSourceBytesPerRequest = source.Length,
            });

        var requests = Enumerable.Range(0, 32)
            .Select(_ => service.CompileAsync(source, compileOptions).AsTask())
            .ToArray();
        var results = await Task.WhenAll(requests).WaitAsync(TestTimeout);

        Assert.All(results, result =>
        {
            AssertResultShape(result, LuauCompileResultKind.Success);
            Assert.Equal(serial.SourceSha256, result.Output!.SourceSha256);
            Assert.Equal(serial.BytecodeSha256, result.Output.BytecodeSha256);
            Assert.Equal(serial.CompileOptions, result.Output.CompileOptions);
            Assert.Equal(serial.UpstreamRevisionHash, result.Output.UpstreamRevisionHash);
            Assert.Equal(serial.HostBuildFingerprint, result.Output.HostBuildFingerprint);
            Assert.Equal(serialBytes, result.Output.ToBytecodeArray());
        });
    }

    static LuauThreadedCompilationService CreateService(
        LuauThreadedCompilationOptions? options = null,
        Func<byte[], LuauCompileOptions, int, LuauCompilerOutput>? backend = null)
    {
        return new LuauThreadedCompilationService(
            options,
            backend ?? (static (source, compileOptions, _) => CreateOutput(source, compileOptions)));
    }

    static LuauThreadedCompilationService CreateBlockingService(
        ManualResetEventSlim release,
        TaskCompletionSource activeEntered,
        LuauThreadedCompilationOptions? options = null)
    {
        return CreateService(
            options,
            (source, compileOptions, _) =>
            {
                activeEntered.TrySetResult();
                WaitForRelease(release);
                return CreateOutput(source, compileOptions);
            });
    }

    static LuauCompilerOutput CreateOutput(
        byte[] source,
        LuauCompileOptions options,
        int bytecodeLength = 4)
    {
        var bytecode = new byte[bytecodeLength];
        for (var index = 0; index < bytecode.Length; index++)
        {
            bytecode[index] = checked((byte)(index + 1));
        }

        return new LuauCompilerOutput(
            bytecode,
            options,
            Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant(),
            upstreamRevisionHash: 11,
            hostBuildFingerprint: 22);
    }

    static void AssertResultShape(LuauCompileResult result, LuauCompileResultKind expectedKind)
    {
        Assert.Equal(expectedKind, result.Kind);
        Assert.Equal(expectedKind == LuauCompileResultKind.Success, result.IsSuccess);
        Assert.Equal(expectedKind == LuauCompileResultKind.Success, result.Output != null);
        Assert.Equal(expectedKind == LuauCompileResultKind.Diagnostic, result.CompilationDiagnostic != null);
        Assert.Equal(
            expectedKind == LuauCompileResultKind.InfrastructureFailure,
            result.InfrastructureException != null);
    }

    static void WaitForRelease(ManualResetEventSlim release)
    {
        if (!release.Wait(TestTimeout))
        {
            throw new TimeoutException("The test did not release the compiler backend in time.");
        }
    }

    static void UpdateMaximum(ref int target, int candidate)
    {
        var observed = Volatile.Read(ref target);
        while (candidate > observed)
        {
            var prior = Interlocked.CompareExchange(ref target, candidate, observed);
            if (prior == observed)
            {
                return;
            }

            observed = prior;
        }
    }

    static TaskCompletionSource NewSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    sealed class CountingMemoryManager : MemoryManager<byte>
    {
        readonly byte[] bytes;

        public CountingMemoryManager(byte[] bytes)
        {
            this.bytes = bytes;
        }

        public int SpanReadCount { get; private set; }

        public override Span<byte> GetSpan()
        {
            SpanReadCount++;
            return bytes;
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            return default;
        }

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
        }
    }

    sealed class FactoryBackedCompilationService(LuauCompilerOutput output) : ILuauCompilationService
    {
        public ValueTask<LuauCompileResult> CompileAsync(
            ReadOnlyMemory<byte> utf8Source,
            LuauCompileOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var result = utf8Source.Span[0] switch
            {
                0 => LuauCompileResult.Success(output),
                1 => LuauCompileResult.Diagnostic(
                    new LuauCompilationException("service diagnostic", "@service/diagnostic.luau")),
                2 => LuauCompileResult.Canceled(),
                _ => LuauCompileResult.InfrastructureFailure(
                    new InvalidOperationException("service infrastructure failure")),
            };
            return ValueTask.FromResult(result);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
