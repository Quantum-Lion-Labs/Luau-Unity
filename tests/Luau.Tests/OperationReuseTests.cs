using System.Diagnostics;

namespace Luau.Tests;

/// <summary>
/// A root reuses one synchronous operation instance for consecutive
/// operations, creates the callback-visible cancellation token only when a
/// callback asks for it, and backs up the native wall-clock check with one
/// shared watchdog timer. These tests pin the steady-state cost of a hook call
/// and each hazard that reuse and laziness introduce.
/// </summary>
public sealed class OperationReuseTests
{
    // Generous: the checks themselves take microseconds, but CI runners can
    // stall a thread for a long time.
    static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    // A safety net, not a budget under test.
    static readonly LuauExecutionOptions SafetyNet = LuauExecutionOptions.Unbounded with
    {
        WallClockLimit = TimeSpan.FromSeconds(20),
    };

    const int WarmupCalls = 2_000;
    const int MeasuredCalls = 10_000;

    // Budget shapes for the allocation tests. Each uses the same machinery as
    // the shipped profiles; the wall-clock limits are long so a stalled runner
    // cannot end a measured call with a budget exception.
    public static TheoryData<string> BudgetShapes => new()
    {
        "default-shape",
        "wall-clock",
        "interrupt-count",
        "unbounded",
    };

    static LuauExecutionOptions BudgetShape(string name) => name switch
    {
        "default-shape" => LuauExecutionOptions.Default with { WallClockLimit = TimeSpan.FromSeconds(10) },
        "wall-clock" => LuauExecutionOptions.Unbounded with { WallClockLimit = TimeSpan.FromSeconds(10) },
        "interrupt-count" => LuauExecutionOptions.Unbounded with { InterruptCountLimit = 1_000_000 },
        _ => LuauExecutionOptions.Unbounded,
    };

    static LuauState CreateRoot(LuauExecutionOptions options)
    {
        var root = LuauState.Create(LuauStateOptions.Default with { DefaultExecutionOptions = options });
        root.OpenBaseLibrary();
        root.OpenCoroutineLibrary();
        return root;
    }

    static LuauFunction LoadFunction(LuauState root, string source)
    {
        using var owner = root.DoString(source);
        return owner[0].Read<LuauFunction>().Retain();
    }

    static long AllocatedBytesPerWindow(Action call)
    {
        for (var i = 0; i < WarmupCalls; i++)
        {
            call();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < MeasuredCalls; i++)
        {
            call();
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    // Steady-state allocation. GC.GetAllocatedBytesForCurrentThread counts
    // only this thread, so parallel tests cannot disturb it.

    [Theory]
    [MemberData(nameof(BudgetShapes))]
    public void HookInvocationAllocatesNothingOnceWarm(string shape)
    {
        using var root = CreateRoot(BudgetShape(shape));
        using var hook = LoadFunction(root, "return function(dt) end");
        var arguments = new[] { LuauValue.FromNumber(1d / 72) };
        var argument = arguments[0];

        Assert.Equal(0, AllocatedBytesPerWindow(() => hook.InvokeVoid(arguments)));
        Assert.Equal(0, AllocatedBytesPerWindow(() => hook.InvokeVoid(argument)));
        Assert.Equal(0, AllocatedBytesPerWindow(() => hook.InvokeVoid()));
    }

    [Fact]
    public void HookInvocationWithPerCallOptionsAllocatesNothingOnceWarm()
    {
        // The Unity scheduler passes one options instance per phase on every
        // call; the merged options are reused rather than rebuilt.
        using var root = CreateRoot(BudgetShape("default-shape"));
        using var hook = LoadFunction(root, "return function(dt) end");
        var arguments = new[] { LuauValue.FromNumber(1d / 72) };
        var phaseOptions = LuauExecutionOptions.Default with { WallClockLimit = TimeSpan.FromSeconds(5) };
        var otherPhaseOptions = phaseOptions with { InterruptCountLimit = 50_000 };

        Assert.Equal(0, AllocatedBytesPerWindow(() =>
        {
            hook.InvokeVoid(arguments, phaseOptions);
            hook.InvokeVoid(arguments, otherPhaseOptions);
        }));
    }

    [Fact]
    public void InvocationIntoCallerStorageAllocatesNothingOnceWarm()
    {
        using var root = CreateRoot(BudgetShape("default-shape"));
        using var hook = LoadFunction(root, "return function(dt) return dt * 2, true, nil end");
        var arguments = new[] { LuauValue.FromNumber(0.5) };
        var destination = new LuauValue[3];

        // Checks inside the measured window must not allocate; xUnit's
        // Assert.Equal does.
        Assert.Equal(0, AllocatedBytesPerWindow(() =>
        {
            if (hook.InvokeInto(arguments, destination) != 3)
            {
                throw new InvalidOperationException("Unexpected result count.");
            }
        }));
        Assert.Equal(1d, destination[0].Read<double>());
        Assert.True(destination[1].Read<bool>());
        Assert.Equal(LuauType.Nil, destination[2].Type);
    }

    [Fact]
    public void CoroutineResumeIntoCallerStorageAllocatesNothingOnceWarm()
    {
        using var root = CreateRoot(BudgetShape("default-shape"));
        using var thread = root.CreateThread();
        thread.DoString("local dt = coroutine.yield() while true do dt = coroutine.yield(dt * 2) end").Dispose();
        var arguments = new[] { LuauValue.FromNumber(0.25) };
        var destination = new LuauValue[1];

        Assert.Equal(0, AllocatedBytesPerWindow(() => thread.ResumeInto(arguments, destination)));
        Assert.Equal(0.5, destination[0].Read<double>());
    }

    [Fact]
    public async Task ScriptEntrypointAllocatesNothingOnceWarm()
    {
        using var root = CreateRoot(BudgetShape("default-shape"));
        root.SandboxRoot();
        using var instance = await LuauScriptInstance.CreateAsync(
            root,
            "hooks",
            static (thread, _) => ValueTask.FromResult(
                thread.DoString("return { update = function(dt) end }")));
        var update = instance.GetRequiredEntrypoint("update");
        var argument = LuauValue.FromNumber(1d / 72);
        var phaseOptions = LuauExecutionOptions.Default with { WallClockLimit = TimeSpan.FromSeconds(5) };

        Assert.Equal(0, AllocatedBytesPerWindow(() => update.InvokeVoid(argument, phaseOptions)));
    }

    [Fact]
    public void DirectTableAccessOutsideAnOperationAllocatesNothingOnceWarm()
    {
        using var root = CreateRoot(BudgetShape("default-shape"));
        using var table = root.CreateTable();
        var key = LuauValue.FromNumber(1);
        var value = LuauValue.FromNumber(42);

        Assert.Equal(0, AllocatedBytesPerWindow(() =>
        {
            table[key] = value;
            if (table[key].Type != LuauType.Number)
            {
                throw new InvalidOperationException("Unexpected table value.");
            }
        }));
    }

    // Loading a chunk encodes its name into two buffers rented from the
    // shared ArrayPool. The pool drops its cached buffers when a gen-2 GC
    // trims it, so a window can include a refill (about 112 bytes) when a
    // parallel test triggers one; per call this is zero.
    const long PooledBufferRefillAllowance = 512;

    [Fact]
    public void ExecutingCachedCompilerOutputAllocatesNothingPerCall()
    {
        using var root = CreateRoot(BudgetShape("default-shape"));
        var output = LuauCompiler.Compile("return 7"u8);
        var destination = new LuauValue[1];

        var allocated = AllocatedBytesPerWindow(() =>
            root.ExecuteCompilerOutputInto(output, destination, "@hooks/cached.luau"));

        Assert.InRange(allocated, 0, PooledBufferRefillAllowance);
        Assert.Equal(7, destination[0].Read<int>());
    }

    // Requirement 6: an instance is reused only after its life ended, and
    // never while anything can still name it.

    [Fact]
    public void ConsecutiveSynchronousOperationsReuseOneInstanceWithANewGeneration()
    {
        using var root = CreateRoot(SafetyNet);
        var observed = new List<(ScriptOperation Operation, long Generation)>();
        using var probe = root.CreateFunction("probe", context =>
        {
            var active = context.State.Context.GetActiveOperation()!;
            observed.Add((active, active.Generation));
        });
        root["probe"] = probe;
        using var hook = LoadFunction(root, "return function() probe() end");

        hook.InvokeVoid();
        hook.InvokeVoid();

        Assert.Equal(2, observed.Count);
        Assert.Same(observed[0].Operation, observed[1].Operation);
        Assert.True(observed[1].Generation > observed[0].Generation);
        Assert.Null(root.Context.GetActiveOperation());
        Assert.Same(observed[1].Operation, root.Context.PooledOperation);
    }

    [Fact]
    public void NestedHostWorkBorrowsTheActiveOperationInsteadOfTakingAnother()
    {
        using var root = CreateRoot(SafetyNet);
        using var table = root.CreateTable();
        ScriptOperation? outer = null;
        ScriptOperation? duringTableRead = null;
        using var probe = root.CreateFunction("probe", context =>
        {
            outer = context.State.Context.GetActiveOperation();
            table[LuauValue.FromNumber(1)] = LuauValue.FromNumber(2);
            duringTableRead = context.State.Context.GetActiveOperation();
            Assert.Null(context.State.Context.PooledOperation);
        });
        root["probe"] = probe;

        root.DoString("probe()").Dispose();

        Assert.NotNull(outer);
        Assert.Same(outer, duringTableRead);
        Assert.Same(outer, root.Context.PooledOperation);
    }

    [Fact]
    public async Task AsynchronousOperationsAreNeverReused()
    {
        using var root = CreateRoot(SafetyNet);
        var observed = new List<ScriptOperation>();
        using var probe = root.CreateAsyncFunction("probe", async context =>
        {
            await Task.Yield();
            observed.Add(context.State.Context.GetActiveOperation()!);
        });
        root["probe"] = probe;
        using var hook = LoadFunction(root, "return function() probe() end");

        await hook.InvokeVoidAsync().AsTask().WaitAsync(TestTimeout);
        await hook.InvokeVoidAsync().AsTask().WaitAsync(TestTimeout);

        Assert.Equal(2, observed.Count);
        Assert.NotSame(observed[0], observed[1]);
        Assert.NotSame(observed[0], root.Context.PooledOperation);
        Assert.NotSame(observed[1], root.Context.PooledOperation);
    }

    [Fact]
    public async Task SynchronousOperationInsideAnAsynchronousFlowIsNotReused()
    {
        // A synchronous operation that starts inside an asynchronous
        // operation's flow publishes an ambient marker, which execution
        // contexts captured during it can keep; the instance is dropped.
        using var outerRoot = CreateRoot(SafetyNet);
        using var innerRoot = CreateRoot(SafetyNet);
        ScriptOperation? inner = null;
        using var innerProbe = innerRoot.CreateFunction("probe", context =>
            inner = context.State.Context.GetActiveOperation());
        innerRoot["probe"] = innerProbe;
        using var innerHook = LoadFunction(innerRoot, "return function() probe() end");
        using var callInner = outerRoot.CreateAsyncFunction("callInner", async _ =>
        {
            await Task.Yield();
            innerHook.InvokeVoid();
        });
        outerRoot["callInner"] = callInner;

        await outerRoot.DoStringAsync("callInner()").AsTask().WaitAsync(TestTimeout);

        Assert.NotNull(inner);
        Assert.NotSame(inner, innerRoot.Context.PooledOperation);

        // From a plain synchronous flow the root reuses its instance again.
        innerHook.InvokeVoid();
        var plain = inner;
        innerHook.InvokeVoid();
        Assert.Same(plain, inner);
    }

    [Fact]
    public void FailedArgumentPreparationLeavesTheRootReusable()
    {
        using var root = CreateRoot(SafetyNet);
        using var otherRoot = CreateRoot(SafetyNet);
        using var foreignTable = otherRoot.CreateTable();
        using var hook = LoadFunction(root, "return function(...) return select('#', ...) end");
        hook.Invoke([LuauValue.FromNumber(1)]).Dispose();

        var pooled = root.Context.PooledOperation;
        Assert.NotNull(pooled);
        Assert.Throws<InvalidOperationException>(() => hook.Invoke([LuauValue.FromNumber(1), LuauValue.FromTable(foreignTable)]));

        Assert.Null(root.Context.GetActiveOperation());
        Assert.Same(pooled, root.Context.PooledOperation);
        using var results = hook.Invoke([LuauValue.FromNumber(1), LuauValue.FromNumber(2)]);
        Assert.Equal(2, Assert.Single(results).Read<int>());
    }

    [Fact]
    public void CallbackThatDisposesItsRootEndsTheOperationWithoutKeepingIt()
    {
        var root = CreateRoot(SafetyNet);
        using var dispose = root.CreateFunction("disposeRoot", _ => root.Dispose());
        root["disposeRoot"] = dispose;
        using var hook = LoadFunction(root, "return function() disposeRoot() while true do end end");
        var context = root.Context;

        Assert.Throws<ObjectDisposedException>(() => hook.InvokeVoid());

        Assert.True(context.IsClosed);
        Assert.Null(context.GetActiveOperation());
        Assert.Null(context.PooledOperation);
    }

    // Requirement 5: a late signal from an ended life never reaches the next.

    [Fact]
    public void LateSignalsForAnEndedLifeDoNotTouchTheNextLife()
    {
        using var root = CreateRoot(LuauExecutionOptions.Unbounded);
        using var brief = LoadFunction(root, "return function() end");
        // The watchdog is armed for this 1 ms deadline, which passes while the
        // next operation runs on the same instance.
        brief.InvokeVoid(LuauExecutionOptions.Unbounded with { WallClockLimit = TimeSpan.FromMilliseconds(1) });
        var instance = root.Context.PooledOperation!;
        var endedLife = instance.Generation;

        Exception? probeFailure = null;
        CancellationToken nextLifeToken = default;
        using var probe = root.CreateFunction("probe", context =>
        {
            try
            {
                var active = context.State.Context.GetActiveOperation()!;
                Assert.Same(instance, active);
                Assert.NotEqual(endedLife, active.Generation);
                Thread.Sleep(5);

                // What a late timer, registration, or captured callback token
                // from the ended life could still do:
                context.State.Context.RequestInterruptAttention(instance, endedLife);
                Assert.Null(context.State.Context.SignalWatchdogDeadline());
                Assert.Throws<InvalidOperationException>(() => instance.GetCancellationToken(endedLife));

                nextLifeToken = context.CancellationToken;
                Assert.False(nextLifeToken.IsCancellationRequested);
                Assert.Null(active.GetHardStopException());
            }
            catch (Exception exception)
            {
                probeFailure = exception;
            }
        });
        root["probe"] = probe;

        using var result = root.DoString(
            "probe() local s = 0 for i = 1, 100000 do s += i end return s",
            executionOptions: SafetyNet);

        Assert.Null(probeFailure);
        Assert.Equal(5000050000L, Assert.Single(result).Read<long>());
        Assert.False(nextLifeToken.IsCancellationRequested);
    }

    [Fact]
    public void TokenCapturedInAnEndedLifeIsNeverCanceledByLaterLives()
    {
        var root = CreateRoot(LuauExecutionOptions.Unbounded);
        CancellationToken captured = default;
        CancellationToken later = default;
        using var capture = root.CreateFunction("capture", context => captured = context.CancellationToken);
        using var expire = root.CreateFunction("expire", context =>
        {
            Thread.Sleep(60);
            later = context.CancellationToken;
        });
        root["capture"] = capture;
        root["expire"] = expire;

        root.DoString("capture()").Dispose();
        var firstInstance = root.Context.PooledOperation;
        var budget = Assert.Throws<LuauExecutionBudgetException>(() => root.DoString(
            "expire() while true do end",
            executionOptions: LuauExecutionOptions.Unbounded with { WallClockLimit = TimeSpan.FromMilliseconds(30) }));
        Assert.Same(firstInstance, root.Context.PooledOperation);
        root.Dispose();

        Assert.Equal(LuauExecutionBudgetKind.WallClock, budget.BudgetKind);
        Assert.True(later.IsCancellationRequested);
        Assert.False(captured.IsCancellationRequested);
        // As before, a token outliving its operation stays usable.
        captured.Register(static () => { }).Dispose();
        Assert.False(captured.IsCancellationRequested);
    }

    // Requirement 4: the lazily created token reflects stops that already
    // happened, and later ones still cancel it.

    [Fact]
    public void TokenRequestedAfterTheDeadlinePassedIsAlreadyCanceled()
    {
        using var root = CreateRoot(LuauExecutionOptions.Unbounded);
        bool? canceledWhenRequested = null;
        using var late = root.CreateFunction("late", context =>
        {
            Thread.Sleep(60);
            canceledWhenRequested = context.CancellationToken.IsCancellationRequested;
        });
        root["late"] = late;

        var exception = Assert.Throws<LuauExecutionBudgetException>(() => root.DoString(
            "late() while true do end",
            executionOptions: LuauExecutionOptions.Unbounded with { WallClockLimit = TimeSpan.FromMilliseconds(30) }));

        Assert.Equal(LuauExecutionBudgetKind.WallClock, exception.BudgetKind);
        Assert.True(canceledWhenRequested);
    }

    [Fact]
    public async Task TokenRequestedAfterCallerCancellationIsAlreadyCanceled()
    {
        using var root = CreateRoot(SafetyNet);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool? canceledWhenRequested = null;
        using var wait = root.CreateAsyncFunction("wait", async context =>
        {
            entered.TrySetResult();
            await resume.Task.ConfigureAwait(false);
            canceledWhenRequested = context.CancellationToken.IsCancellationRequested;
        });
        root["wait"] = wait;

        var execution = root.DoStringAsync("wait()", cancellationToken: cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TestTimeout);
        cancellation.Cancel();
        resume.TrySetResult();

        await Assert.ThrowsAsync<LuauExecutionCanceledException>(() => execution.WaitAsync(TestTimeout));
        Assert.True(canceledWhenRequested);
    }

    [Fact]
    public void TokenRequestedAfterItsRootWasDisposedIsAlreadyCanceled()
    {
        var root = CreateRoot(SafetyNet);
        bool? canceledWhenRequested = null;
        using var disposeThenAsk = root.CreateFunction("disposeThenAsk", context =>
        {
            root.Dispose();
            canceledWhenRequested = context.CancellationToken.IsCancellationRequested;
        });
        root["disposeThenAsk"] = disposeThenAsk;

        Assert.Throws<ObjectDisposedException>(() => root.DoString("disposeThenAsk() while true do end"));

        Assert.True(canceledWhenRequested);
    }

    [Fact]
    public void TokenRequestedAfterItsCoroutineWasDisposedIsAlreadyCanceled()
    {
        using var root = CreateRoot(SafetyNet);
        using var thread = root.CreateThread();
        bool? canceledWhenRequested = null;
        using var disposeThenAsk = root.CreateFunction("disposeThenAsk", context =>
        {
            thread.Dispose();
            canceledWhenRequested = context.CancellationToken.IsCancellationRequested;
        });
        root["disposeThenAsk"] = disposeThenAsk;

        Assert.Throws<ObjectDisposedException>(() => thread.DoString("disposeThenAsk() while true do end"));

        Assert.True(canceledWhenRequested);
        using var result = root.DoString("return 1");
        Assert.Equal(1, Assert.Single(result).Read<int>());
    }

    [Fact]
    public void WatchdogCancelsATokenRequestedBeforeTheDeadline()
    {
        // The callback blocks inside the host, so no safepoint runs: only the
        // shared watchdog can cancel the token at the deadline.
        using var root = CreateRoot(LuauExecutionOptions.Unbounded);
        var canceled = false;
        using var block = root.CreateFunction("block", context =>
        {
            using var signal = new ManualResetEventSlim();
            using var registration = context.CancellationToken.Register(signal.Set);
            canceled = signal.Wait(TestTimeout);
        });
        root["block"] = block;

        var exception = Assert.Throws<LuauExecutionBudgetException>(() => root.DoString(
            "block() while true do end",
            executionOptions: LuauExecutionOptions.Unbounded with { WallClockLimit = TimeSpan.FromMilliseconds(30) }));

        Assert.Equal(LuauExecutionBudgetKind.WallClock, exception.BudgetKind);
        Assert.True(canceled, "The watchdog did not cancel the token at the deadline.");
    }

    [Fact]
    public async Task DeadlineCancellationHandlerThatNeedsTheVmDoesNotStallTheStop()
    {
        // The handler runs on the watchdog's thread and waits for the VM, which
        // the executing thread holds until the script stops. The watchdog asks
        // for the poll before running it, as disposal does.
        var root = CreateRoot(LuauExecutionOptions.Unbounded);
        using var handlerEntered = new ManualResetEventSlim();
        CancellationTokenRegistration registration = default;
        using var hook = root.CreateFunction("hook", context =>
        {
            registration = context.CancellationToken.Register(() =>
            {
                handlerEntered.Set();
                _ = root.MemoryUsage;
            });
        });
        root["hook"] = hook;
        try
        {
            var execution = Task.Run(() => root.DoString(
                "hook() while true do end",
                executionOptions: LuauExecutionOptions.Unbounded with { WallClockLimit = TimeSpan.FromMilliseconds(30) }));

            var exception = await Assert.ThrowsAsync<LuauExecutionBudgetException>(() => execution.WaitAsync(TestTimeout));
            Assert.Equal(LuauExecutionBudgetKind.WallClock, exception.BudgetKind);
        }
        finally
        {
            registration.Dispose();
            root.Dispose();
        }
    }

    // Requirement 2: the watchdog bounds detection after an abrupt slowdown.

    [Fact]
    public void WatchdogStopsAScriptThatAbruptlySlowsDown()
    {
        // The fast loop drives the native clock stride to its maximum of 128
        // safepoints. Each later iteration spends 100 ms in a host call, so
        // the native clock alone would next be read about 128 safepoints
        // (over four seconds) after the slowdown. The watchdog requests a poll
        // at the deadline, so the script stops right after the host call in
        // progress returns.
        var limit = TimeSpan.FromMilliseconds(50);
        using var root = CreateRoot(LuauExecutionOptions.Unbounded with { WallClockLimit = limit });
        using var slow = root.CreateFunction("slow", _ => Thread.Sleep(100));
        root["slow"] = slow;
        var stopwatch = Stopwatch.StartNew();

        var exception = Assert.Throws<LuauExecutionBudgetException>(() => root.DoString(
            "for i = 1, 200000 do end while true do slow() end"));

        stopwatch.Stop();
        Assert.Equal(LuauExecutionBudgetKind.WallClock, exception.BudgetKind);
        Assert.True(exception.Elapsed >= limit);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2.5),
            $"Stopping took {stopwatch.Elapsed}; the watchdog did not request a poll.");
    }

    // Requirement 7: the ambient-operation check rejects what it rejected
    // before, without an execution-context write per synchronous operation.

    [Fact]
    public async Task AnotherThreadIsRejectedDuringASynchronousOperation()
    {
        using var root = CreateRoot(SafetyNet);
        Exception? unrelatedThread = null;
        Exception? forkedFlow = null;
        using var probe = root.CreateFunction("probe", _ =>
        {
            // A thread with no relation to the operation.
            Task unrelated;
            using (ExecutionContext.SuppressFlow())
            {
                unrelated = Task.Run(() => unrelatedThread = Record(() => root.CreateTable().Dispose()));
            }

            unrelated.Wait(TestTimeout);

            // A flow forked from inside the callback. It used to pass this
            // check and then wait on the VM, which the callback holds.
            Task.Run(() => forkedFlow = Record(() => root.CreateTable().Dispose())).Wait(TestTimeout);
        });
        root["probe"] = probe;

        await Task.Run(() => root.DoString("probe()").Dispose()).WaitAsync(TestTimeout);

        AssertRejectedAsAnotherOperation(unrelatedThread);
        AssertRejectedAsAnotherOperation(forkedFlow);
    }

    [Fact]
    public void NestedOperationOnAnotherRootCannotTouchTheOuterRoot()
    {
        using var outer = CreateRoot(SafetyNet);
        using var inner = CreateRoot(SafetyNet);
        Exception? fromInnerCallback = null;
        Exception? afterInner = null;
        using var touchOuter = inner.CreateFunction("touchOuter", _ =>
            fromInnerCallback = Record(() => outer.CreateTable().Dispose()));
        inner["touchOuter"] = touchOuter;
        using var callInner = outer.CreateFunction("callInner", _ =>
        {
            inner.DoString("touchOuter()").Dispose();
            afterInner = Record(() => outer.CreateTable().Dispose());
        });
        outer["callInner"] = callInner;

        outer.DoString("callInner()").Dispose();

        AssertRejectedAsAnotherOperation(fromInnerCallback);
        Assert.Null(afterInner);
    }

    [Fact]
    public async Task AsynchronousOperationStartedFromASynchronousCallbackOwnsItsFlow()
    {
        using var outer = CreateRoot(SafetyNet);
        using var inner = CreateRoot(SafetyNet);
        Exception? fromInnerCallback = null;
        Exception? afterInnerStarted = null;
        Task? innerExecution = null;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var touchOuter = inner.CreateFunction("touchOuter", _ =>
            fromInnerCallback = Record(() => outer.CreateTable().Dispose()));
        using var park = inner.CreateAsyncFunction("park", async _ => await release.Task.ConfigureAwait(false));
        inner["touchOuter"] = touchOuter;
        inner["park"] = park;
        using var startInner = outer.CreateFunction("startInner", _ =>
        {
            innerExecution = inner.DoStringAsync("touchOuter() park()").AsTask();
            afterInnerStarted = Record(() => outer.CreateTable().Dispose());
        });
        outer["startInner"] = startInner;

        outer.DoString("startInner()").Dispose();
        release.TrySetResult();
        await innerExecution!.WaitAsync(TestTimeout);

        AssertRejectedAsAnotherOperation(fromInnerCallback);
        Assert.Null(afterInnerStarted);
    }

    [Fact]
    public async Task AsyncCallbackContinuationKeepsItsAccessAndOtherThreadsStayRejected()
    {
        using var root = CreateRoot(SafetyNet);
        using var table = root.CreateTable();
        var suspended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? continuation = null;
        using var wait = root.CreateAsyncFunction("wait", async _ =>
        {
            suspended.TrySetResult();
            await resume.Task.ConfigureAwait(false);
            continuation = Record(() => table[LuauValue.FromNumber(1)] = LuauValue.FromNumber(1));
        });
        root["wait"] = wait;

        var execution = root.DoStringAsync("wait()").AsTask();
        await suspended.Task.WaitAsync(TestTimeout);
        Task<Exception?> unrelatedAccess;
        using (ExecutionContext.SuppressFlow())
        {
            unrelatedAccess = Task.Run(() => Record(() => _ = table[LuauValue.FromNumber(1)]));
        }

        var unrelated = await unrelatedAccess.WaitAsync(TestTimeout);
        resume.TrySetResult();
        await execution.WaitAsync(TestTimeout);

        AssertRejectedAsAnotherOperation(unrelated);
        Assert.Null(continuation);
    }

    [Fact]
    public async Task ExecutionContextCapturedInAnEndedOperationCannotAccessALaterOne()
    {
        using var root = CreateRoot(SafetyNet);
        ExecutionContext? captured = null;
        using var capture = root.CreateAsyncFunction("capture", async _ =>
        {
            await Task.Yield();
            captured = ExecutionContext.Capture();
        });
        root["capture"] = capture;
        await root.DoStringAsync("capture()").AsTask().WaitAsync(TestTimeout);

        Exception? replayed = null;
        using var replay = root.CreateFunction("replay", _ =>
            ExecutionContext.Run(
                captured!,
                _ => replayed = Record(() => root.CreateTable().Dispose()),
                null));
        root["replay"] = replay;

        root.DoString("replay()").Dispose();

        AssertRejectedAsAnotherOperation(replayed);
    }

    [Fact]
    public async Task ThreadThatRanAnEarlierLifeIsRejectedDuringTheNextLife()
    {
        using var root = CreateRoot(SafetyNet);
        using var hook = LoadFunction(root, "return function() end");
        using var requests = new BlockingCollectionLite();
        var worker = new Thread(() =>
        {
            hook.InvokeVoid();
            requests.Serve();
        })
        {
            IsBackground = true,
        };
        worker.Start();
        await requests.Ready.WaitAsync(TestTimeout);
        var firstInstance = root.Context.PooledOperation;

        Exception? fromWorker = null;
        using var probe = root.CreateFunction("probe", _ =>
        {
            Assert.Same(firstInstance, root.Context.GetActiveOperation());
            fromWorker = requests.Run(() => root.CreateTable().Dispose(), TestTimeout);
        });
        root["probe"] = probe;

        root.DoString("probe()").Dispose();
        requests.Stop();
        Assert.True(worker.Join(TestTimeout));

        AssertRejectedAsAnotherOperation(fromWorker);
    }

    // Merged options and decoded chunk names are cached; neither may leak
    // between operations that differ.

    [Fact]
    public void ZeroResultMergeDoesNotLeakIntoOrdinaryInvocations()
    {
        using var root = CreateRoot(SafetyNet);
        using var pair = LoadFunction(root, "return function() return 1, 2 end");
        var options = SafetyNet with { MaxResultCount = 5 };
        var tighter = SafetyNet with { MaxResultCount = 1 };

        for (var round = 0; round < 3; round++)
        {
            Assert.Equal(0, Assert.Throws<LuauResultLimitException>(() => pair.InvokeVoid(options)).Limit);
            using (var results = pair.Invoke(executionOptions: options))
            {
                Assert.Equal(2, results.Count);
            }

            Assert.Equal(0, Assert.Throws<LuauResultLimitException>(() => pair.InvokeVoid()).Limit);
            using (var results = pair.Invoke())
            {
                Assert.Equal(2, results.Count);
            }

            Assert.Equal(1, Assert.Throws<LuauResultLimitException>(() => pair.Invoke(executionOptions: tighter)).Limit);
        }
    }

    [Fact]
    public void ReusedChunkNamesStillNameTheirOwnChunk()
    {
        using var root = CreateRoot(SafetyNet);
        var output = LuauCompiler.Compile("error('boom')"u8);
        var destination = new LuauValue[1];

        var first = Assert.Throws<LuauException>(() => root.ExecuteCompilerOutputInto(output, destination, "@hooks/a.luau"));
        var second = Assert.Throws<LuauException>(() => root.ExecuteCompilerOutputInto(output, destination, "@hooks/b.luau"));
        var third = Assert.Throws<LuauException>(() => root.DoString("error('boom')"u8, "@hooks/c.luau"u8));
        var fourth = Assert.Throws<LuauException>(() => root.DoString("error('boom')"u8, "@hooks/d.luau"u8));

        Assert.Equal("@hooks/a.luau", first.ChunkName);
        Assert.Equal("@hooks/b.luau", second.ChunkName);
        Assert.Equal("@hooks/c.luau", third.ChunkName);
        Assert.Equal("@hooks/d.luau", fourth.ChunkName);
    }

    static Exception? Record(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    static void AssertRejectedAsAnotherOperation(Exception? exception)
    {
        var rejected = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("executing on another operation", rejected.Message, StringComparison.Ordinal);
    }

    /// <summary>Runs actions on one dedicated thread on request.</summary>
    sealed class BlockingCollectionLite : IDisposable
    {
        readonly object gate = new();
        readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Action? pending;
        Exception? result;
        bool completed;
        bool stopped;

        internal Task Ready => ready.Task;

        internal void Serve()
        {
            ready.TrySetResult();
            lock (gate)
            {
                while (!stopped)
                {
                    if (pending is { } action)
                    {
                        pending = null;
                        result = Record(action);
                        completed = true;
                        Monitor.PulseAll(gate);
                    }
                    else
                    {
                        Monitor.Wait(gate);
                    }
                }
            }
        }

        internal Exception? Run(Action action, TimeSpan timeout)
        {
            lock (gate)
            {
                completed = false;
                pending = action;
                Monitor.PulseAll(gate);
                var deadline = Stopwatch.StartNew();
                while (!completed)
                {
                    var remaining = timeout - deadline.Elapsed;
                    if (remaining <= TimeSpan.Zero || !Monitor.Wait(gate, remaining))
                    {
                        throw new TimeoutException("The worker thread did not run the request.");
                    }
                }

                return result;
            }
        }

        internal void Stop()
        {
            lock (gate)
            {
                stopped = true;
                Monitor.PulseAll(gate);
            }
        }

        public void Dispose() => Stop();
    }
}
