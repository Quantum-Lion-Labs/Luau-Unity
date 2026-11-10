using System.Diagnostics;
using System.Text;

namespace Luau.Tests;

/// <summary>
/// The native host throttles interrupt polls: it counts safepoints, reads the
/// clock, and calls managed code only when a budget event or a request is
/// due. These tests pin the behavior the throttle must not change. Most use
/// unbounded or very large budgets, so that only an explicit request can stop
/// the script: a missing request then shows up as a script that keeps running.
/// </summary>
public sealed class InterruptThrottleTests
{
    // Generous: the detections themselves take microseconds, but CI runners
    // can stall a thread for a long time.
    static readonly TimeSpan TestTimeout = TestBudgets.WaitTimeout;

    // A safety net, not a budget under test: a stalled test fails with a
    // wall-clock budget exception, or on StopBound, instead of hanging.
    static readonly LuauExecutionOptions SafetyNet = LuauExecutionOptions.Unbounded with
    {
        WallClockLimit = TestBudgets.WallClockSafetyNet,
    };

    // An interrupt-count budget with no wall-clock limit. A bare
    // `new LuauExecutionOptions { InterruptCountLimit = n }` also carries the
    // default 250 ms wall-clock limit, which a busy runner can exceed first.
    static LuauExecutionOptions CountOnly(long limit) =>
        LuauExecutionOptions.Unbounded with { InterruptCountLimit = limit };

    // A signal must stop the script well before the safety net would.
    static readonly TimeSpan StopBound = TimeSpan.FromSeconds(10);

    static LuauState CreateRoot(LuauExecutionOptions? options = null)
    {
        var root = LuauState.Create(LuauStateOptions.UnboundedResources with
        {
            DefaultExecutionOptions = options ?? LuauExecutionOptions.Unbounded,
        });
        root.OpenBaseLibrary();
        root.OpenCoroutineLibrary();
        root.OpenStringLibrary();
        root.OpenTableLibrary();
        return root;
    }

    // Requirement 1: pending work reaches Luau at the very next safepoint.

    [Theory]
    [InlineData("metamethod", false)]
    [InlineData("metamethod", true)]
    [InlineData("sort", false)]
    [InlineData("sort", true)]
    [InlineData("gsub", false)]
    [InlineData("gsub", true)]
    public async Task PendingCallbackWorkIsDeliveredBeforeAnyFurtherCall(string boundary, bool asyncCallback)
    {
        // A non-yieldable parent rejects the callback's suspension, the script
        // catches the boundary error, and the failure stays pending. Its
        // delivery must come at the next safepoint: before marker() runs and
        // before any other coroutine is created or resumed.
        using var root = CreateRoot(SafetyNet);
        var callbackInvoked = false;
        var markers = 0;
        using var host = asyncCallback
            ? root.CreateAsyncFunction("host", _ =>
            {
                callbackInvoked = true;
                return default;
            })
            : root.CreateFunction("host", _ => throw new InvalidOperationException("managed failure"));
        using var marker = root.CreateFunction("marker", _ => markers++);
        root["host"] = host;
        root["marker"] = marker;
        var body = boundary switch
        {
            "metamethod" => "local proxy = setmetatable({}, {__index = function() return coroutine.resume(co) end}); return proxy.value",
            "sort" => "table.sort({2, 1}, function(a, b) coroutine.resume(co); return a < b end)",
            "gsub" => "return string.gsub('x', '.', function() return coroutine.resume(co) end)",
            _ => throw new ArgumentOutOfRangeException(nameof(boundary)),
        };
        // While an async callback is pending, calling any other managed
        // callback fails on its own, which would deliver the failure by
        // another route; that variant observes the delay as a loop that runs
        // until the safety net instead.
        var after = asyncCallback
            ? "while true do end"
            : "marker() coroutine.resume(coroutine.create(function() marker() end)) while true do end";
        var source =
            "local co = coroutine.create(function() return host(42) end) " +
            "local ok, err = pcall(function() " + body + " end) " +
            after;
        var stopwatch = Stopwatch.StartNew();

        var failure = asyncCallback
            ? await Assert.ThrowsAsync<LuauManagedCallbackException>(async () =>
            {
                using var rejected = await root.DoStringAsync(source).AsTask().WaitAsync(TestTimeout);
            })
            : Assert.Throws<LuauManagedCallbackException>(() =>
            {
                using var rejected = root.DoString(source);
            });

        Assert.Equal("host", failure.CallbackName);
        Assert.IsType<InvalidOperationException>(failure.InnerException);
        Assert.False(callbackInvoked);
        Assert.Equal(0, markers);
        Assert.True(stopwatch.Elapsed < StopBound, $"Delivery took {stopwatch.Elapsed}.");
        using var recovery = root.DoString("return 5");
        Assert.Equal(5, Assert.Single(recovery).Read<int>());
    }

    [Fact]
    public void ImmediatelyRaisedCallbackFailureLeavesNothingPendingForLaterSafepoints()
    {
        // The failing callback runs where it cannot suspend, so its failure is
        // raised as a Luau error immediately; nothing stays pending and the
        // script keeps running normally.
        using var root = CreateRoot(SafetyNet);
        using var fail = root.CreateFunction("fail", _ => throw new InvalidOperationException("managed failure"));
        root["fail"] = fail;

        using var result = root.DoString(
            "local proxy = setmetatable({}, {__index = function() return pcall(fail) end}) " +
            "local ok = proxy.value " +
            "local s = 0 for i = 1, 1000 do s += i end " +
            "return ok, s");

        Assert.False(result[0].Read<bool>());
        Assert.Equal(500500, result[1].Read<int>());
    }

    // Requirement 2: interrupt-count limits stay exact.

    [Theory]
    [InlineData("for i = 1, 100 do end", 101)]
    [InlineData("local i = 0 while i < 100 do i += 1 end", 101)]
    [InlineData("local function f(x) return x end for i = 1, 50 do f(i) end", 151)]
    [InlineData("for i = 1, 50 do cb() end", 101)]
    [InlineData("local t = {} for i = 1, 3000 do t[i] = {i} end", 3001)]
    [InlineData("for i = 1, 20 do pcall(error, 'x') end", 41)]
    [InlineData("for i = 1, 10 do pcall(fail) end", 21)]
    [InlineData("local co = coroutine.wrap(function() for i = 1, 10 do coroutine.yield(i) end end) for i = 1, 10 do co() end", 41)]
    public void InterruptCountLimitStopsAtExactlyTheSafepointAfterTheLimit(string source, long safepoints)
    {
        // These totals match the per-safepoint managed poll this replaces.
        foreach (var limit in new[] { 1L, safepoints / 2, safepoints - 1 })
        {
            using var root = CreateRoot();
            using var cb = root.CreateFunction("cb", _ => { });
            using var fail = root.CreateFunction("fail", _ => throw new InvalidOperationException("x"));
            root["cb"] = cb;
            root["fail"] = fail;

            var exception = Assert.Throws<LuauExecutionBudgetException>(() => root.DoString(
                source,
                "@throttle/count.luau",
                executionOptions: CountOnly(limit)));

            Assert.Equal(LuauExecutionBudgetKind.InterruptCount, exception.BudgetKind);
            Assert.Equal(limit, exception.InterruptCountLimit);
            Assert.Equal(limit + 1, exception.ObservedInterruptCount);
            Assert.Contains($"exceeded at count {limit + 1}.", exception.Message);
        }

        using var exact = CreateRoot();
        using var exactCb = exact.CreateFunction("cb", _ => { });
        using var exactFail = exact.CreateFunction("fail", _ => throw new InvalidOperationException("x"));
        exact["cb"] = exactCb;
        exact["fail"] = exactFail;
        exact.DoString(
            source,
            executionOptions: CountOnly(safepoints)).Dispose();
    }

    [Fact]
    public void DefaultCountLimitAllowsExactlyTenMillionSafepoints()
    {
        // The default count limit with the wall clock as a safety net only:
        // under the default 250 ms, a stalled runner stops the loop by time
        // before it reaches the count.
        using var root = TestBudgets.CreateRoot();

        // 9,999,999 back-edges plus the final return.
        root.DoString("for i = 1, 9999999 do end").Dispose();
        var exception = Assert.Throws<LuauExecutionBudgetException>(
            () => root.DoString("for i = 1, 10000000 do end"));
        Assert.Equal(LuauExecutionBudgetKind.InterruptCount, exception.BudgetKind);
        Assert.Equal(10_000_000, exception.InterruptCountLimit);
        Assert.Equal(10_000_001, exception.ObservedInterruptCount);
    }

    [Theory]
    [InlineData("while true do end")]
    [InlineData("local function f() end while true do f() end")]
    public void DefaultCountLimitStopsARunawayScript(string source)
    {
        // With the wall clock only as a safety net, the count is what stops a
        // script that never ends, as it does for a host that sets no wall
        // clock.
        using var root = TestBudgets.CreateRoot();

        var exception = Assert.Throws<LuauExecutionBudgetException>(
            () => root.DoString(source, "@throttle/runaway.luau"));

        Assert.Equal(LuauExecutionBudgetKind.InterruptCount, exception.BudgetKind);
        Assert.Equal("@throttle/runaway.luau", exception.ChunkName);
        Assert.Equal(10_000_000, exception.InterruptCountLimit);
        Assert.Equal(10_000_001, exception.ObservedInterruptCount);
        using var recovered = root.DoString("return 1");
        Assert.Equal(1, Assert.Single(recovered).Read<int>());
    }

    [Fact]
    public async Task InterruptCountContinuesExactlyAcrossAsyncSuspensions()
    {
        // 10 calls and 10 back-edges plus the return; each await suspends
        // and resumes the same operation.
        foreach (var (limit, fails) in new[] { (20L, true), (21L, false) })
        {
            using var root = CreateRoot();
            using var wait = root.CreateAsyncFunction("wait", async _ => await Task.Yield());
            root["wait"] = wait;
            var options = CountOnly(limit);
            if (fails)
            {
                var exception = await Assert.ThrowsAsync<LuauExecutionBudgetException>(async () =>
                {
                    using var _ = await root.DoStringAsync("for i = 1, 10 do wait() end", executionOptions: options);
                });
                Assert.Equal(21, exception.ObservedInterruptCount);
            }
            else
            {
                using var _ = await root.DoStringAsync("for i = 1, 10 do wait() end", executionOptions: options);
            }
        }
    }

    // Requirement 3: wall-clock limits stay accurate.

    [Theory]
    [InlineData("while true do end")]
    [InlineData("local function f() end while true do f() f() f() f() end")]
    [InlineData("local t = {} local i = 0 while true do i += 1 t[i % 64] = {i} end")]
    public void ShortWallClockLimitStopsTightLoopsAndCallOnlyLoops(string source)
    {
        var limit = TimeSpan.FromMilliseconds(2);
        using var root = CreateRoot(LuauExecutionOptions.Unbounded with { WallClockLimit = limit });
        var stopwatch = Stopwatch.StartNew();

        var exception = Assert.Throws<LuauExecutionBudgetException>(() => root.DoString(source));

        Assert.Equal(LuauExecutionBudgetKind.WallClock, exception.BudgetKind);
        Assert.True(exception.Elapsed >= limit);
        Assert.True(stopwatch.Elapsed < TestTimeout, $"Stopping took {stopwatch.Elapsed}.");
    }

    [Fact]
    public void WallClockLimitStopsAScriptWhoseSafepointsAreSlowHostCalls()
    {
        // Every call takes a millisecond, so the host reads its clock at every
        // safepoint instead of every few dozen.
        var limit = TimeSpan.FromMilliseconds(20);
        using var root = CreateRoot(LuauExecutionOptions.Unbounded with { WallClockLimit = limit });
        using var slow = root.CreateFunction("slow", _ => Thread.Sleep(1));
        root["slow"] = slow;
        var stopwatch = Stopwatch.StartNew();

        var exception = Assert.Throws<LuauExecutionBudgetException>(() => root.DoString("while true do slow() end"));

        Assert.Equal(LuauExecutionBudgetKind.WallClock, exception.BudgetKind);
        Assert.True(exception.Elapsed >= limit);
        Assert.True(stopwatch.Elapsed < TestTimeout, $"Stopping took {stopwatch.Elapsed}.");
    }

    [Fact]
    public void ExpiredDeadlineIsObservedBeforeTheFirstLoopCompletes()
    {
        // A limit shorter than the operation setup is already due when the
        // host configures it; the first clock read must stop the loop.
        using var root = CreateRoot(LuauExecutionOptions.Unbounded with { WallClockLimit = TimeSpan.FromTicks(1) });

        var exception = Assert.Throws<LuauExecutionBudgetException>(() => root.DoString("while true do end"));

        Assert.Equal(LuauExecutionBudgetKind.WallClock, exception.BudgetKind);
    }

    // Requirement 4: cancellation and disposal from another thread.

    [Fact]
    public async Task CancellationFromAnotherThreadStopsAnUnboundedTightLoop()
    {
        using var root = CreateRoot(SafetyNet);
        using var started = new ManualResetEventSlim();
        using var signal = root.CreateFunction("started", _ => started.Set());
        root["started"] = signal;
        using var cancellation = new CancellationTokenSource();

        var execution = Task.Run(async () =>
        {
            using var _ = await root.DoStringAsync(
                "started() while true do end",
                cancellationToken: cancellation.Token);
        });
        Assert.True(started.Wait(TestTimeout));
        var stopwatch = Stopwatch.StartNew();
        cancellation.Cancel();

        await Assert.ThrowsAsync<LuauExecutionCanceledException>(() => execution.WaitAsync(TestTimeout));
        Assert.True(stopwatch.Elapsed < StopBound, $"Stopping took {stopwatch.Elapsed}.");
        using var recovery = root.DoString("return 1");
        Assert.Equal(1, Assert.Single(recovery).Read<int>());
    }

    [Fact]
    public async Task AlreadyCanceledTokenStopsAnUnboundedLoopAtItsFirstSafepoint()
    {
        using var root = CreateRoot(SafetyNet);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<LuauExecutionCanceledException>(async () =>
        {
            using var _ = await root
                .DoStringAsync("while true do end", cancellationToken: cancellation.Token)
                .AsTask()
                .WaitAsync(TestTimeout);
        });
        Assert.True(stopwatch.Elapsed < StopBound, $"Stopping took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task CancellationStopsNonYieldablePatternMatching()
    {
        // string.find polls from inside the matcher, where Luau cannot
        // suspend; the hard stop unwinds it instead.
        using var root = CreateRoot(SafetyNet);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var signal = root.CreateFunction("started", _ => started.TrySetResult());
        root["started"] = signal;
        var execution = Task.Run(async () =>
        {
            using var _ = await root.DoStringAsync(
                "local h = string.rep('x', 40) local p = string.rep('x?', 40) .. string.rep('x', 40) " +
                "started() return string.find(h, p)",
                cancellationToken: cancellation.Token);
        });

        // Cancel only once the script is running, so the stop cannot be the
        // already-canceled admission path. The match starts right after the
        // signal and would run for far longer than this test.
        await started.Task.WaitAsync(TestTimeout);
        await Task.Delay(20);
        var stopwatch = Stopwatch.StartNew();
        cancellation.Cancel();

        await Assert.ThrowsAsync<LuauExecutionCanceledException>(() => execution.WaitAsync(TestTimeout));
        Assert.True(stopwatch.Elapsed < StopBound, $"Stopping took {stopwatch.Elapsed}.");
        using var recovery = root.DoString("return 2");
        Assert.Equal(2, Assert.Single(recovery).Read<int>());
    }

    [Fact]
    public void RootDisposalFromAnotherThreadStopsAnUnboundedTightLoop()
    {
        var root = CreateRoot(SafetyNet);
        using var started = new ManualResetEventSlim();
        root["started"] = root.CreateFunction("started", _ => started.Set());
        Exception? observed = null;
        var thread = new Thread(() =>
        {
            try
            {
                root.DoString("started() while true do end").Dispose();
            }
            catch (Exception exception)
            {
                observed = exception;
            }
        });
        thread.Start();
        Assert.True(started.Wait(TestTimeout));
        var stopwatch = Stopwatch.StartNew();

        // Dispose waits for the running operation to stop.
        root.Dispose();

        Assert.True(thread.Join(TestTimeout));
        Assert.True(stopwatch.Elapsed < StopBound, $"Stopping took {stopwatch.Elapsed}.");
        Assert.IsType<ObjectDisposedException>(observed);
        Assert.True(root.IsDisposed);
    }

    [Fact]
    public void ChildDisposalFromAnotherThreadStopsItsUnboundedTightLoop()
    {
        using var root = CreateRoot(SafetyNet);
        using var started = new ManualResetEventSlim();
        using var signal = root.CreateFunction("started", _ => started.Set());
        root["started"] = signal;
        var child = root.CreateThread();
        Exception? observed = null;
        var thread = new Thread(() =>
        {
            try
            {
                child.DoString("started() while true do end").Dispose();
            }
            catch (Exception exception)
            {
                observed = exception;
            }
        });
        thread.Start();
        Assert.True(started.Wait(TestTimeout));
        var stopwatch = Stopwatch.StartNew();

        child.Dispose();

        Assert.True(thread.Join(TestTimeout));
        Assert.True(stopwatch.Elapsed < StopBound, $"Stopping took {stopwatch.Elapsed}.");
        Assert.IsType<ObjectDisposedException>(observed);
        using var recovery = root.DoString("return 3");
        Assert.Equal(3, Assert.Single(recovery).Read<int>());
    }

    // Requirement 5: non-yieldable contexts and GC notifications.

    [Fact]
    public void BudgetStopsATightLoopInsideAMetamethod()
    {
        // The 5 ms limit applies to the operation under test only: as the
        // root's default it would also bound the recovery operation, which a
        // stalled runner can take longer than 5 ms to compile.
        using var root = CreateRoot(SafetyNet);

        var exception = Assert.Throws<LuauExecutionBudgetException>(() => root.DoString(
            "local proxy = setmetatable({}, {__index = function() while true do end end}) " +
            "local ok = pcall(function() return proxy.value end) " +
            "while true do end",
            executionOptions: LuauExecutionOptions.Unbounded with { WallClockLimit = TimeSpan.FromMilliseconds(5) }));

        // The script's pcall caught the first stop inside the metamethod; the
        // hard stop is raised again at the next safepoint.
        Assert.Equal(LuauExecutionBudgetKind.WallClock, exception.BudgetKind);
        using var recovery = root.DoString("return 4");
        Assert.Equal(4, Assert.Single(recovery).Read<int>());
    }

    [Fact]
    public void CollectionNeitherCountsNorStopsExecution()
    {
        using var root = CreateRoot();

        // Allocation-heavy code still completes without any budget.
        using (var result = root.DoString("local t = {} for i = 1, 200000 do t[i] = {i} end return #t"))
        {
            Assert.Equal(200000, Assert.Single(result).Read<int>());
        }

        // A full collection between operations does not disturb the next count.
        root.CollectGarbage();
        var exception = Assert.Throws<LuauExecutionBudgetException>(() => root.DoString(
            "local t = {} while true do t[#t % 1024 + 1] = {} end",
            executionOptions: CountOnly(50_000)));
        Assert.Equal(50_001, exception.ObservedInterruptCount);
    }

    // Requirement 6: concurrent roots and racing signals.

    [Fact]
    public async Task ConcurrentRootsKeepIndependentExactCountBudgets()
    {
        var limits = Enumerable.Range(1, 8).Select(index => index * 25_000L).ToArray();
        var observed = await Task.WhenAll(limits.Select(limit => Task.Run(() =>
        {
            using var root = CreateRoot();
            var exception = Assert.Throws<LuauExecutionBudgetException>(() => root.DoString(
                limit % 50_000 == 0 ? "local function f() end while true do f() end" : "while true do end",
                executionOptions: CountOnly(limit)));
            return exception.ObservedInterruptCount!.Value;
        }))).WaitAsync(TestTimeout);

        Assert.Equal(limits.Select(limit => limit + 1), observed);
    }

    [Fact]
    public async Task CancellationRacingOperationCompletionLeavesRootsUsable()
    {
        using var root = CreateRoot();
        var random = new Random(1234);
        for (var round = 0; round < 200; round++)
        {
            using var cancellation = new CancellationTokenSource();
            var execution = Task.Run(async () =>
            {
                using var _ = await root.DoStringAsync(
                    "local s = 0 for i = 1, 20000 do s += i end return s",
                    cancellationToken: cancellation.Token);
            });
            if (random.Next(2) == 0)
            {
                Thread.SpinWait(random.Next(20_000));
            }
            cancellation.Cancel();
            try
            {
                await execution.WaitAsync(TestTimeout);
            }
            catch (LuauExecutionCanceledException)
            {
            }
        }

        using var recovery = root.DoString("return 6");
        Assert.Equal(6, Assert.Single(recovery).Read<int>());
    }

    [Fact]
    public void DeadlineTimersOutlivingTheirOperationsDoNotStopLaterOperations()
    {
        // A 1 ms deadline arms a timer that can fire just as its operation
        // ends. The next operation on the same root has only the safety net,
        // so nothing but a late signal from the earlier one could stop it.
        using var root = CreateRoot();
        var brief = LuauExecutionOptions.Unbounded with { WallClockLimit = TimeSpan.FromMilliseconds(1) };
        for (var round = 0; round < 500; round++)
        {
            try
            {
                root.DoString("return 1", executionOptions: brief).Dispose();
            }
            catch (LuauExecutionBudgetException)
            {
                // A stalled runner can legitimately exhaust a 1 ms budget;
                // that is not what this test checks.
            }

            using var result = root.DoString(
                "local s = 0 for i = 1, 10000 do s += i end return s",
                executionOptions: SafetyNet);
            Assert.Equal(50005000, Assert.Single(result).Read<int>());
        }
    }

    [Fact]
    public async Task DisposalStopsALoopEvenWhenACancellationHandlerNeedsTheVm()
    {
        // Disposal cancels the callback-visible token, and its handlers are
        // host code. This one needs the VM, which the executing thread holds
        // until the script stops. Within StopBound only the disposal request
        // can stop the script, and it must not wait for the handler. The
        // safety net ends the loop later if that ever regresses, so a failing
        // run does not leave a spinning VM and a blocked disposal behind.
        var root = CreateRoot(SafetyNet);
        using var entered = new ManualResetEventSlim();
        CancellationTokenRegistration registration = default;
        using var hook = root.CreateFunction("hook", context =>
        {
            registration = context.CancellationToken.Register(() => _ = root.MemoryUsage);
            entered.Set();
        });
        root["hook"] = hook;

        var execution = Task.Run(() => root.DoString("hook() while true do end").Dispose());
        Task? disposal = null;
        try
        {
            Assert.True(entered.Wait(TestTimeout));
            disposal = Task.Run(root.Dispose);

            await Assert.ThrowsAsync<ObjectDisposedException>(() => execution.WaitAsync(StopBound));
            await disposal.WaitAsync(StopBound);
        }
        finally
        {
            // Let both tasks finish before the test returns, whatever happened.
            disposal ??= Task.Run(root.Dispose);
            await Task.WhenAny(Task.WhenAll(execution.ContinueWith(static _ => { }), disposal.ContinueWith(static _ => { })), Task.Delay(TestTimeout));
            registration.Dispose();
        }
    }

    [Fact]
    public async Task DisposalRacingCancellationNeverCrashes()
    {
        for (var round = 0; round < 50; round++)
        {
            var root = CreateRoot(SafetyNet);
            using var cancellation = new CancellationTokenSource();
            var execution = Task.Run(async () =>
            {
                using var _ = await root.DoStringAsync("while true do end", cancellationToken: cancellation.Token);
            });
            var canceler = Task.Run(() => cancellation.Cancel());
            root.Dispose();
            await canceler.WaitAsync(TestTimeout);
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => execution.WaitAsync(TestTimeout));
            Assert.True(
                failure is ObjectDisposedException or OperationCanceledException,
                $"Unexpected {failure.GetType().Name}: {failure.Message}");
        }
    }

    // Requirement 7: nested operations, re-entrancy, and suspension.

    [Fact]
    public void RequireInsideAnOperationSharesItsExactCount()
    {
        // The module's 26 safepoints count toward the requiring operation:
        // 1 call, the module, 10 back-edges, and the return make 38.
        foreach (var (limit, expected) in new[] { (37L, 38L), (20L, 21L) })
        {
            using var root = CreateRoot();
            root.OpenRequireLibrary(new LuauModuleMap(new Dictionary<string, byte[]>
            {
                ["mod"] = Encoding.UTF8.GetBytes("local s = 0 for i = 1, 25 do s += i end return s"),
            }));

            var exception = Assert.Throws<LuauExecutionBudgetException>(() => root.DoString(
                "local m = require('mod') for i = 1, 10 do end return m",
                executionOptions: CountOnly(limit)));
            Assert.Equal(expected, exception.ObservedInterruptCount);
        }

        using var exact = CreateRoot();
        exact.OpenRequireLibrary(new LuauModuleMap(new Dictionary<string, byte[]>
        {
            ["mod"] = Encoding.UTF8.GetBytes("local s = 0 for i = 1, 25 do s += i end return s"),
        }));
        using var result = exact.DoString(
            "local m = require('mod') for i = 1, 10 do end return m",
            executionOptions: CountOnly(38));
        Assert.Equal(325, Assert.Single(result).Read<int>());
    }

    [Fact]
    public void NestedOperationOnAnotherRootKeepsBothBudgetsExact()
    {
        using var outer = CreateRoot();
        using var inner = CreateRoot();
        var innerFailures = 0;
        using var runInner = outer.CreateFunction("runInner", _ =>
        {
            try
            {
                inner.DoString(
                    "for i = 1, 1000 do end",
                    executionOptions: CountOnly(500)).Dispose();
            }
            catch (LuauExecutionBudgetException exception) when (exception.ObservedInterruptCount == 501)
            {
                innerFailures++;
            }
        });
        outer["runInner"] = runInner;

        // 10 calls and 10 back-edges plus the return: the inner root's 501
        // safepoints per call never reach the outer count.
        var outerException = Assert.Throws<LuauExecutionBudgetException>(() => outer.DoString(
            "for i = 1, 10 do runInner() end",
            executionOptions: CountOnly(20)));
        Assert.Equal(21, outerException.ObservedInterruptCount);
        Assert.Equal(10, innerFailures);

        outer.DoString(
            "for i = 1, 10 do runInner() end",
            executionOptions: CountOnly(21)).Dispose();
        Assert.Equal(20, innerFailures);
    }

    [Fact]
    public async Task NestedOperationWaitsInsideAnAsyncCallbackWithoutDisturbingTheOuterDeadline()
    {
        using var outer = CreateRoot(LuauExecutionOptions.Unbounded with { WallClockLimit = TimeSpan.FromMilliseconds(200) });
        using var inner = CreateRoot();
        using var work = outer.CreateAsyncFunction("work", async _ =>
        {
            await Task.Yield();
            // A tight inner loop under its own deadline, on another root.
            var exception = Assert.Throws<LuauExecutionBudgetException>(() => inner.DoString(
                "while true do end",
                executionOptions: LuauExecutionOptions.Unbounded with { WallClockLimit = TimeSpan.FromMilliseconds(5) }));
            Assert.Equal(LuauExecutionBudgetKind.WallClock, exception.BudgetKind);
        });
        outer["work"] = work;

        var stopwatch = Stopwatch.StartNew();
        var outerException = await Assert.ThrowsAsync<LuauExecutionBudgetException>(async () =>
        {
            using var _ = await outer.DoStringAsync("work() while true do end").AsTask().WaitAsync(TestTimeout);
        });
        Assert.Equal(LuauExecutionBudgetKind.WallClock, outerException.BudgetKind);
        Assert.True(outerException.Elapsed >= TimeSpan.FromMilliseconds(200));
        Assert.True(stopwatch.Elapsed < TestTimeout);
    }

    [Fact]
    public void EachCoroutineResumeStartsAFreshCountBudget()
    {
        using var root = CreateRoot();
        using var owner = root.DoString(
            "return coroutine.create(function() while true do for i = 1, 50 do end coroutine.yield() end end)");
        using var coroutine = owner.Read<LuauState>(0);
        var options = CountOnly(60);

        // Each resume runs about 52 safepoints; five resumes would exceed a
        // shared budget of 60 four times over.
        for (var resume = 0; resume < 5; resume++)
        {
            coroutine.Resume(executionOptions: options).Dispose();
        }

        var exception = Assert.Throws<LuauExecutionBudgetException>(
            () => coroutine.Resume(executionOptions: CountOnly(10)));
        Assert.Equal(11, exception.ObservedInterruptCount);
    }
}
