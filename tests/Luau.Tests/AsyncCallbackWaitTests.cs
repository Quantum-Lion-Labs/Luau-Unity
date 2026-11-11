using static Luau.Tests.NonFlowingContinuationTests;

namespace Luau.Tests;

/// <summary>
/// While an asynchronous operation waits for its callback, a root with a
/// continuation scheduler accepts callers on the scheduler that carry no
/// marker (maintainer guide section 6.8). Such an access makes several
/// checked steps (push, call, read the result, restore the stack). The wait
/// must not end between them, however the callback's completion arrives, and
/// the operation must not resume, be validated, or be torn down until the
/// access has finished; otherwise the later steps are refused and what the
/// access leaves on the suspended thread's stack becomes the callback's
/// result.
/// </summary>
public sealed class AsyncCallbackWaitTests
{
    // Reading any key runs Luau code that calls the host function probe and
    // then returns 99: an access of several checked steps with host code in
    // the middle of it.
    const string ProbedTable = "return setmetatable({}, { __index = function() probe() return 99 end })";

    sealed class AccessOutcome
    {
        internal int? Value;
        internal Exception? Failure;
        internal int? StackDelta;
        internal Exception? TopFailure;
    }

    /// <summary>
    /// Reads <c>table.x</c>, with the root's stack top before and after it,
    /// in one piece of work.
    /// </summary>
    static AccessOutcome MeasureAccess(LuauState root, LuauTable table)
    {
        var outcome = new AccessOutcome();
        var top = root.GetTop();
        try
        {
            outcome.Value = table["x"].Read<int>();
        }
        catch (Exception exception)
        {
            outcome.Failure = exception;
        }

        try
        {
            outcome.StackDelta = root.GetTop() - top;
        }
        catch (Exception exception)
        {
            outcome.TopFailure = exception;
        }

        return outcome;
    }

    static void AssertAccessFinished(AccessOutcome access, bool checkStack = true)
    {
        Assert.Null(access.Failure);
        Assert.Equal(99, access.Value);
        Assert.Null(access.TopFailure);
        if (checkStack)
        {
            Assert.Equal(0, access.StackDelta);
        }
    }

    static LuauTable Retain(LuauState root, string source)
    {
        using var results = root.DoString(source);
        return results[0].Read<LuauTable>().Retain();
    }

    // The next operation runs, and nothing was left on the root's stack.
    static (int Value, int Top) RunNext(LuauState root)
    {
        int value;
        using (var results = root.DoString("return 2"))
        {
            value = results[0].Read<int>();
        }

        return (value, root.GetTop());
    }

    /// <summary>
    /// Runs <paramref name="action"/> on a thread of its own and waits for
    /// it. No synchronization context or task scheduler is current there, so
    /// a task completed there runs its <c>ConfigureAwait(false)</c>
    /// continuations inline: the runner goes as far as it can without the
    /// scheduler before this returns.
    /// </summary>
    static bool RunOnNewThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        { IsBackground = true };
        thread.Start();
        return thread.Join(TestBudgets.WaitTimeout) && failure == null;
    }

    // --- The callback completes on another thread ------------------------------

    [Fact]
    public async Task AnAdmittedAccessFinishesWhenTheCallbackCompletesOnAnotherThread()
    {
        // The reviewer's case, made deterministic: the access's host code
        // completes the callback on another thread and waits until the runner
        // there has gone as far as it can. The runner used to end the wait
        // there, so the access's remaining steps were refused and the value
        // it left on the stack became the callback's result (99).
        using var loop = new SingleThreadLoop();
        var root = await loop.CreateRootAsync();
        try
        {
            // Without RunContinuationsAsynchronously: the runner continues
            // inline on the completing thread.
            var source = new TaskCompletionSource();
            var completed = false;
            LuauTable? table = null;
            var run = await loop.Run(() =>
            {
                root.OpenBaseLibrary();
                root["probe"] = root.CreateFunction("probe", _ => completed = RunOnNewThread(source.SetResult));
                table = Retain(root, ProbedTable);
                root["wait"] = root.CreateAsyncFunction("wait", context =>
                {
                    context.Return(42);
                    return new ValueTask(source.Task);
                });
                // The callback runs, and the wait begins, before this returns.
                return ReadAll(root.DoStringAsync("return wait()"));
            });

            // Unrelated code on the scheduler, in its own context.
            var access = await loop.Run(() => MeasureAccess(root, table!));

            Assert.True(completed);
            AssertAccessFinished(access);
            Assert.Equal(new object?[] { 42 }, await run);
            Assert.Equal((2, 0), await loop.Run(() => RunNext(root)));
            await loop.Run(table!.Dispose);
        }
        finally
        {
            await loop.Run(root.Dispose);
        }
    }

    // --- The callback completes inline, inside the access ---------------------

    [Fact]
    public async Task AnAdmittedAccessFinishesWhenItCompletesTheCallbackInline()
    {
        // No context is current on this scheduler's thread, so completing the
        // source there runs the callback's continuation, and the runner's,
        // inside the access. The runner used to end the wait, resume the
        // script, and end the operation there, from a host callback of the
        // root's host operation thread; the access then put back a stack top
        // that no longer existed.
        using var loop = new SingleThreadLoop(currentContext: false);
        var root = await loop.CreateRootAsync();
        try
        {
            var source = new TaskCompletionSource();
            Task<object?[]>? run = null;
            bool? endedInsideAccess = null;
            LuauTable? table = null;
            await loop.Run(() =>
            {
                root.OpenBaseLibrary();
                root["probe"] = root.CreateFunction("probe", _ =>
                {
                    source.SetResult();
                    endedInsideAccess = run!.IsCompleted;
                });
                table = Retain(root, ProbedTable);
                root["wait"] = root.CreateAsyncFunction("wait", context =>
                {
                    context.Return(42);
                    return new ValueTask(source.Task);
                });
                run = ReadAll(root.DoStringAsync("return wait()"));
            });

            var access = await loop.Run(() => MeasureAccess(root, table!));

            Assert.False(endedInsideAccess);
            AssertAccessFinished(access);
            Assert.Equal(new object?[] { 42 }, await run!.WaitAsync(TestBudgets.WaitTimeout));
            Assert.Equal((2, 0), await loop.Run(() => RunNext(root)));
            await loop.Run(table!.Dispose);
        }
        finally
        {
            await loop.Run(root.Dispose);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheCallbacksOwnAccessFinishesWhenItCompletesTheCallbackInline(bool scheduled)
    {
        // The callback's own work carries its marker, so this needs no
        // scheduler and happened before the scheduler rule existed: without
        // one, the operation ended inside the access and the access left a
        // value on the root's stack for good.
        using var loop = new SingleThreadLoop(currentContext: false);
        var root = scheduled ? await loop.CreateRootAsync() : TestBudgets.CreateRoot();
        Task<T> OnOwner<T>(Func<T> action) => scheduled ? loop.Run(action) : Task.Run(action);
        try
        {
            var source = new TaskCompletionSource();
            var accessed = new TaskCompletionSource<AccessOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var started = new ManualResetEventSlim();
            Task<object?[]>? run = null;
            bool? endedInsideAccess = null;
            LuauTable? table = null;
            await OnOwner(() =>
            {
                root.OpenBaseLibrary();
                root["probe"] = root.CreateFunction("probe", _ =>
                {
                    source.SetResult();
                    endedInsideAccess = run!.IsCompleted;
                });
                table = Retain(root, ProbedTable);
                root["wait"] = root.CreateAsyncFunction("wait", async context =>
                {
                    context.Return(42);
                    // The callback's own work, in its flow: on the scheduler,
                    // or on the thread pool without one.
                    var flow = ExecutionContext.Capture()!;
                    void Access()
                    {
                        Assert.True(started.Wait(TestBudgets.WaitTimeout));
                        accessed.TrySetResult(MeasureAccess(root, table!));
                    }

                    if (scheduled)
                    {
                        loop.Post(_ => ExecutionContext.Run(flow, _ => Access(), null), null);
                    }
                    else
                    {
                        _ = Task.Run(Access);
                    }

                    await source.Task.ConfigureAwait(false);
                });
                run = ReadAll(root.DoStringAsync("return wait()"));
                started.Set();
                return 0;
            });

            var access = await accessed.Task.WaitAsync(TestBudgets.WaitTimeout);
            Assert.False(endedInsideAccess);
            // Without a scheduler the runner may resume once the access has
            // left the gate, before the second stack read: the root's stack
            // afterwards is checked instead.
            AssertAccessFinished(access, checkStack: scheduled);
            Assert.Equal(new object?[] { 42 }, await run!.WaitAsync(TestBudgets.WaitTimeout));
            Assert.Equal((2, 0), await OnOwner(() => RunNext(root)));
            await OnOwner(() =>
            {
                table!.Dispose();
                return 0;
            });
        }
        finally
        {
            await OnOwner(() =>
            {
                root.Dispose();
                return 0;
            });
        }
    }

    [Fact]
    public async Task ACallbackCompletedOnTheSchedulerOutsideAnAccessContinuesWithoutAPost()
    {
        // The usual path: the runner continues on the scheduler, outside any
        // access, and ends the wait there without another turn of the loop
        // (on Unity a posted continuation can cost a frame).
        using var loop = new SingleThreadLoop(currentContext: false);
        var root = await loop.CreateRootAsync();
        try
        {
            var source = new TaskCompletionSource();
            Task<object?[]>? run = null;
            await loop.Run(() =>
            {
                root["wait"] = root.CreateAsyncFunction("wait", context =>
                {
                    context.Return(42);
                    return new ValueTask(source.Task);
                });
                run = ReadAll(root.DoStringAsync("return wait()"));
            });

            var (completedInline, posts) = await loop.Run(() =>
            {
                var before = loop.PostCount;
                source.SetResult();
                return (run!.IsCompleted, loop.PostCount - before);
            });

            Assert.True(completedInline);
            Assert.Equal(0, posts);
            Assert.Equal(new object?[] { 42 }, await run!);
        }
        finally
        {
            await loop.Run(root.Dispose);
        }
    }

    // --- Hard stops during an admitted access ----------------------------------

    public enum HardStop
    {
        Cancellation,
        WallClock,
        Disposal,
    }

    sealed class StopOutcome
    {
        internal bool CallbackRan;
        internal bool InsideWait;
        internal bool Released;
        internal int CloseCountDuringAccess = -1;
        internal AccessOutcome? Access;
        internal Exception? Run;
        internal bool StopperFinished = true;
        internal (int Value, int Top)? Next;
        internal int CloseCountAfterwards = -1;
    }

    [Theory]
    [InlineData(HardStop.Cancellation)]
    [InlineData(HardStop.WallClock)]
    [InlineData(HardStop.Disposal)]
    public async Task AHardStopDuringAnAdmittedAccessTakesEffectAfterIt(HardStop stop)
    {
        using var loop = new SingleThreadLoop();
        // Only the wall-clock limit depends on time. An attempt whose limit
        // passed before the access began proves nothing; the next one gets a
        // longer limit, and the test fails if none lands.
        const int Attempts = 6;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var outcome = await RunStopDuringAccessAsync(loop, stop, TimeSpan.FromMilliseconds(100 << attempt));
            if (!outcome.InsideWait)
            {
                Assert.Equal(HardStop.WallClock, stop);
                continue;
            }

            // The stop completed the callback while the access ran, and the
            // root stayed open until the access had finished.
            Assert.True(outcome.Released);
            Assert.Equal(0, outcome.CloseCountDuringAccess);
            Assert.True(outcome.StopperFinished);

            // The access ran Luau code under the waiting operation, so the
            // stop reached it, as its own failure, and nothing refused it.
            var access = outcome.Access!;
            Assert.Null(access.Value);
            switch (stop)
            {
                case HardStop.Cancellation:
                    Assert.IsType<LuauExecutionCanceledException>(access.Failure);
                    Assert.IsType<LuauExecutionCanceledException>(outcome.Run);
                    break;
                case HardStop.WallClock:
                    Assert.Equal(
                        LuauExecutionBudgetKind.WallClock,
                        Assert.IsType<LuauExecutionBudgetException>(access.Failure).BudgetKind);
                    Assert.Equal(
                        LuauExecutionBudgetKind.WallClock,
                        Assert.IsType<LuauExecutionBudgetException>(outcome.Run).BudgetKind);
                    break;
                case HardStop.Disposal:
                    Assert.IsType<ObjectDisposedException>(access.Failure);
                    Assert.IsType<ObjectDisposedException>(access.TopFailure);
                    Assert.IsType<ObjectDisposedException>(outcome.Run);
                    Assert.Equal(1, outcome.CloseCountAfterwards);
                    return;
            }

            Assert.Null(access.TopFailure);
            Assert.Equal(0, access.StackDelta);
            Assert.Equal((2, 0), outcome.Next);
            return;
        }

        Assert.Fail($"No attempt's access ran while the operation waited for its callback in {Attempts} attempts.");
    }

    static async Task<StopOutcome> RunStopDuringAccessAsync(SingleThreadLoop loop, HardStop stop, TimeSpan wallClockLimit)
    {
        var outcome = new StopOutcome();
        var root = await loop.CreateRootAsync();
        using var cancellation = new CancellationTokenSource();
        using var released = new ManualResetEventSlim();
        var accessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread? stopper = null;
        Task<object?[]>? run = null;
        LuauTable? table = null;
        try
        {
            await loop.Run(() =>
            {
                root.OpenBaseLibrary();
                root["probe"] = root.CreateFunction("probe", _ =>
                {
                    outcome.InsideWait = !run!.IsCompleted;
                    if (stop == HardStop.Cancellation)
                    {
                        stopper = new Thread(cancellation.Cancel) { IsBackground = true };
                        stopper.Start();
                    }
                    else if (stop == HardStop.Disposal)
                    {
                        // Disposal waits for the access before it touches the
                        // VM, so this thread finishes only after it.
                        stopper = new Thread(root.Dispose) { IsBackground = true };
                        stopper.Start();
                    }

                    // The stop completes the callback on its own thread, and
                    // the runner goes as far as it can there before this is set.
                    outcome.Released = released.Wait(TestBudgets.WaitTimeout);
                    outcome.CloseCountDuringAccess = root.Context.CloseCount;
                });
                table = Retain(root, ProbedTable);
                root["wait"] = root.CreateAsyncFunction("wait", context =>
                {
                    outcome.CallbackRan = true;
                    context.Return(42);
                    // Unrelated work, queued before anything the stop can queue.
                    loop.Post(_ =>
                    {
                        outcome.Access = MeasureAccess(root, table!);
                        accessed.TrySetResult();
                    }, null);
                    var source = new TaskCompletionSource();
                    context.CancellationToken.Register(() =>
                    {
                        source.TrySetResult();
                        released.Set();
                    });
                    return new ValueTask(source.Task);
                });
                run = ReadAll(root.DoStringAsync(
                    "return wait()",
                    cancellationToken: cancellation.Token,
                    executionOptions: stop == HardStop.WallClock
                        ? LuauExecutionOptions.Unbounded with { WallClockLimit = wallClockLimit }
                        : null));
            });

            try
            {
                await run!.WaitAsync(TestBudgets.WaitTimeout);
            }
            catch (Exception exception)
            {
                outcome.Run = exception;
            }

            if (outcome.CallbackRan)
            {
                await accessed.Task.WaitAsync(TestBudgets.WaitTimeout);
            }

            if (stopper != null)
            {
                outcome.StopperFinished = stopper.Join(TestBudgets.WaitTimeout);
            }

            if (stop != HardStop.Disposal)
            {
                outcome.Next = await loop.Run(() => RunNext(root));
            }

            outcome.CloseCountAfterwards = root.Context.CloseCount;
            return outcome;
        }
        finally
        {
            await loop.Run(() =>
            {
                table?.Dispose();
                root.Dispose();
            });
        }
    }

    // --- Accesses racing the completion ----------------------------------------

    [Fact]
    public async Task AccessesRacingTheCallbacksCompletionFromTheThreadPoolLeaveTheOperationIntact()
    {
        // The callback completes from a thread-pool thread at a different
        // moment of the access each time. Every access runs inside the wait
        // (the wait can end only after the loop's current work), so each
        // must finish, leave the stack as it found it, and leave the
        // callback's result alone.
        const int Repetitions = 300;
        using var loop = new SingleThreadLoop();
        var root = await loop.CreateRootAsync();
        try
        {
            LuauTable? table = null;
            TaskCompletionSource? source = null;
            await loop.Run(() =>
            {
                root.OpenBaseLibrary();
                table = Retain(
                    root,
                    "return setmetatable({}, { __index = function() local s = 0 for i = 1, 2000 do s += i end return 99 end })");
                root["wait"] = root.CreateAsyncFunction("wait", context =>
                {
                    context.Return(42);
                    return new ValueTask(source!.Task);
                });
            });

            for (var repetition = 0; repetition < Repetitions; repetition++)
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                source = completion;
                var run = await loop.Run(() => ReadAll(root.DoStringAsync("return wait()")));
                var delay = repetition % 40 * 25;
                var access = await loop.Run(() =>
                {
                    ThreadPool.UnsafeQueueUserWorkItem(_ => completion.SetResult(), null);
                    Thread.SpinWait(delay);
                    return MeasureAccess(root, table!);
                });

                AssertAccessFinished(access);
                Assert.Equal(new object?[] { 42 }, await run.WaitAsync(TestBudgets.WaitTimeout));
                Assert.Equal(0, await loop.Run(root.GetTop));
            }

            await loop.Run(table!.Dispose);
        }
        finally
        {
            await loop.Run(root.Dispose);
        }
    }

    // --- An async host function reached from inside an access -----------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAsyncHostFunctionThatAnAccessReachesInACoroutineFailsInPlace(bool callbacksOwnFlow)
    {
        // Inside a coroutine that the access's Luau code creates, an async
        // host function is yieldable. Queuing it used to end the wait from
        // inside the access, so the access's remaining steps were refused,
        // and its failure was charged to the waiting script. The callback's
        // own flow could do this before the scheduler rule existed.
        using var loop = new SingleThreadLoop();
        var root = await loop.CreateRootAsync();
        try
        {
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostAsyncRan = false;
            ExecutionContext? flow = null;
            LuauTable? table = null;
            var run = await loop.Run(() =>
            {
                root.OpenBaseLibrary();
                root.OpenCoroutineLibrary();
                root["hostAsync"] = root.CreateAsyncFunction("hostAsync", context =>
                {
                    hostAsyncRan = true;
                    context.Return(5);
                    return default;
                });
                table = Retain(
                    root,
                    "return setmetatable({}, { __index = function() " +
                    "return coroutine.wrap(function() return hostAsync() end)() end })");
                root["wait"] = root.CreateAsyncFunction("wait", async context =>
                {
                    context.Return(42);
                    flow = ExecutionContext.Capture();
                    pending.TrySetResult();
                    await release.Task;
                });
                return ReadAll(root.DoStringAsync("return wait()"));
            });
            await pending.Task.WaitAsync(TestBudgets.WaitTimeout);

            var access = await loop.Run(() =>
            {
                AccessOutcome? observed = null;
                if (callbacksOwnFlow)
                {
                    ExecutionContext.Run(flow!, _ => observed = MeasureAccess(root, table!), null);
                }
                else
                {
                    observed = MeasureAccess(root, table!);
                }

                return observed!;
            });

            var failure = Assert.IsType<LuauManagedCallbackException>(access.Failure);
            Assert.Equal("hostAsync", failure.CallbackName);
            Assert.Equal(
                "An asynchronous managed callback cannot run in Luau code that a direct host operation runs.",
                Assert.IsType<InvalidOperationException>(failure.InnerException).Message);
            Assert.Null(access.TopFailure);
            Assert.Equal(0, access.StackDelta);
            Assert.False(hostAsyncRan);

            release.TrySetResult();
            Assert.Equal(new object?[] { 42 }, await run);
            Assert.Equal((2, 0), await loop.Run(() => RunNext(root)));
            await loop.Run(table!.Dispose);
        }
        finally
        {
            await loop.Run(root.Dispose);
        }
    }

    // A scheduler that runs its queue only when asked, on the thread that
    // created it, so a host function can run a nested message loop.
    sealed class PumpedScheduler : ILuauContinuationScheduler
    {
        readonly int owner = Environment.CurrentManagedThreadId;
        readonly System.Collections.Concurrent.ConcurrentQueue<Action> queue = new();

        public bool CheckAccess() => Environment.CurrentManagedThreadId == owner;

        public void Post(Action continuation) => queue.Enqueue(continuation);

        internal int Count => queue.Count;

        // Runs queued work until the queue is empty or limit items have run.
        internal int Drain(int limit)
        {
            var delivered = 0;
            while (delivered < limit && queue.TryDequeue(out var work))
            {
                delivered++;
                work();
            }

            return delivered;
        }
    }

    [Fact]
    public void ANestedMessageLoopInsideAnAccessDoesNotSpinOnTheDeferredEndOfTheWait()
    {
        // The access's host function completes the callback and then runs the
        // scheduler's queue until it is empty, as a nested message loop does.
        // The end of the wait is delivered there, inside the access, where it
        // cannot run. It must not be posted straight back: the loop would
        // deliver it again for ever, and the access could never end.
        //
        // The runner continues inline in the host function only when the
        // completing thread has no synchronization context, so the test
        // removes xunit's for its duration; with one, the continuation goes
        // to the thread pool and may miss the nested loop.
        const int DrainLimit = 1000;
        var testContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            RunNestedMessageLoopScenario(DrainLimit);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(testContext);
        }
    }

    static void RunNestedMessageLoopScenario(int DrainLimit)
    {
        var scheduler = new PumpedScheduler();
        using var root = LuauState.Create(TestBudgets.StateOptions with
        {
            DefaultExecutionOptions = TestBudgets.Execution with { ContinuationScheduler = scheduler },
        });
        root.OpenBaseLibrary();
        var source = new TaskCompletionSource();
        var nestedDeliveries = -1;
        var leftInQueue = -1;
        using var probe = root.CreateFunction("probe", _ =>
        {
            source.SetResult();
            nestedDeliveries = scheduler.Drain(DrainLimit);
            leftInQueue = scheduler.Count;
        });
        root["probe"] = LuauValue.FromFunction(probe);
        using var setup = root.DoString(ProbedTable);
        using var table = setup[0].Read<LuauTable>().Retain();
        using var wait = root.CreateAsyncFunction("wait", context =>
        {
            context.Return(42);
            return new ValueTask(source.Task);
        });
        root["wait"] = LuauValue.FromFunction(wait);

        var before = root.GetTop();
        var run = root.DoStringAsync("return wait()").AsTask();
        // The callback's result is on the stack while it is pending.
        var pending = root.GetTop();
        Assert.Equal(99, table["x"].Read<int>());
        Assert.Equal(pending, root.GetTop());

        // The end of the wait was delivered to the nested loop, which then
        // came to an end with an empty queue.
        Assert.InRange(nestedDeliveries, 1, DrainLimit - 1);
        Assert.Equal(0, leftInQueue);

        // The end of the wait arrives once the access is over.
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!run.IsCompleted)
        {
            Assert.True(waited.Elapsed < TestBudgets.WaitTimeout, "The operation did not finish after the access ended.");
            if (scheduler.Drain(DrainLimit) == 0)
            {
                Thread.Sleep(1);
            }
        }

        using (var results = run.GetAwaiter().GetResult())
        {
            Assert.Equal(42, Assert.Single(results).Read<int>());
        }

        Assert.Equal(before, root.GetTop());
        using var next = root.DoString("return 1 + 1");
        Assert.Equal(2, Assert.Single(next).Read<int>());
    }
}
