using System.Diagnostics;

namespace Luau.Tests;

public sealed class RuntimeHardeningBehaviorTests
{
    static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task IndependentRootsCanAwaitDistinctManagedCallbacksConcurrently()
    {
        using var firstRoot = LuauState.Create();
        using var secondRoot = LuauState.Create();
        var firstEntered = NewSignal();
        var secondEntered = NewSignal();
        var releaseFirst = NewSignal();
        var releaseSecond = NewSignal();

        firstRoot["waitForHost"] = firstRoot.CreateAsyncFunction(
            "waitForHost",
            async context =>
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task.ConfigureAwait(false);
                context.Return(11);
            });
        secondRoot["waitForHost"] = secondRoot.CreateAsyncFunction(
            "waitForHost",
            async context =>
            {
                secondEntered.TrySetResult();
                await releaseSecond.Task.ConfigureAwait(false);
                context.Return(22);
            });

        var firstExecution = firstRoot
            .DoStringAsync("return waitForHost()", "@concurrency/first.luau")
            .AsTask();
        var secondExecution = secondRoot
            .DoStringAsync("return waitForHost()", "@concurrency/second.luau")
            .AsTask();

        try
        {
            await Task.WhenAll(firstEntered.Task, secondEntered.Task).WaitAsync(TestTimeout);

            releaseFirst.TrySetResult();
            var firstResult = await firstExecution.WaitAsync(TestTimeout);
            Assert.Equal(11, Assert.Single(firstResult).Read<int>());
            Assert.False(secondExecution.IsCompleted);

            releaseSecond.TrySetResult();
            var secondResult = await secondExecution.WaitAsync(TestTimeout);
            Assert.Equal(22, Assert.Single(secondResult).Read<int>());
        }
        finally
        {
            releaseFirst.TrySetResult();
            releaseSecond.TrySetResult();
        }
    }

    [Fact]
    public void YieldedChildrenCanBeInterleavedWithinOneRoot()
    {
        using var root = LuauState.Create();
        root.OpenCoroutineLibrary();
        using var first = root.CreateThread();
        using var second = root.CreateThread();

        var firstYield = first.DoString(
            "coroutine.yield(101); return 102",
            "@coroutines/first.luau");
        var secondYield = second.DoString(
            "coroutine.yield(201); return 202",
            "@coroutines/second.luau");

        Assert.Equal(101, Assert.Single(firstYield).Read<int>());
        Assert.Equal(201, Assert.Single(secondYield).Read<int>());

        var secondResult = second.Resume();
        var firstResult = first.Resume();

        Assert.Equal(202, Assert.Single(secondResult).Read<int>());
        Assert.Equal(102, Assert.Single(firstResult).Read<int>());
    }

    [Fact]
    public async Task ScriptFunctionYieldedByChildInvokesOnRootWithoutResumingChild()
    {
        using var root = LuauState.Create();
        root.OpenCoroutineLibrary();
        using var child = root.CreateThread();
        using var function = Assert.Single(child.DoString(
            "coroutine.yield(function(value) return value * 2 end); return 99",
            "@references/yielded-function.luau")).Read<LuauFunction>();

        Assert.Equal(LuauThreadStatus.Suspended, child.GetStatus());
        Assert.Same(root, function.State);

        var invoked = await function.InvokeAsync(new LuauValue[] { 21d });
        Assert.Equal(42, Assert.Single(invoked).Read<int>());
        Assert.Equal(LuauThreadStatus.Suspended, child.GetStatus());
        Assert.Equal(99, Assert.Single(child.Resume()).Read<int>());
    }

    [Fact]
    public async Task ManagedCallbackFunctionsCannotBeInvokedDirectly()
    {
        using var root = LuauState.Create();
        using var callback = root.CreateFunction(context => context.Return(42));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await callback.InvokeAsync(Array.Empty<LuauValue>()));

        Assert.Contains("only be invoked by Luau", exception.Message);
        root["callback"] = callback;
        Assert.Equal(42, Assert.Single(root.DoString("return callback()")).Read<int>());
    }

    [Fact]
    public void NonYieldableManagedCallbackFailureStopsTheCurrentLuaCallImmediately()
    {
        using var root = LuauState.Create();
        root.OpenBaseLibrary();
        root.OpenTableLibrary();
        root["marker"] = 0;
        root["fail"] = root.CreateFunction(
            "fail",
            _ => throw new InvalidOperationException("expected callback failure"));

        Assert.Throws<LuauManagedCallbackException>(() => root.DoString(
            "local values = { 2, 1 }; table.sort(values, fail); marker = 1"));

        Assert.Equal(0, root["marker"].Read<int>());
    }

    [Fact]
    public async Task AsyncCallbackCanReadReferenceArgumentWhileOwnerCoroutineIsSuspended()
    {
        using var root = LuauState.Create();
        using var child = root.CreateThread();
        child["inspect"] = child.CreateAsyncFunction(
            "inspect",
            async context =>
            {
                using var table = context.Read<LuauTable>(0);
                await Task.Yield();
                context.Return(table["value"].Read<int>() + 1);
            });

        var results = await child.DoStringAsync(
            "return inspect({ value = 41 })",
            "@references/async-callback-table.luau");

        Assert.Equal(42, Assert.Single(results).Read<int>());
        Assert.Equal(8, Assert.Single(child.DoString("return 8")).Read<int>());
    }

    [Fact]
    public async Task CancellationWaitsForPendingCallbackThenResetsStateSafely()
    {
        using var state = LuauState.Create(LuauStateOptions.UnboundedResources with
        {
            DefaultExecutionOptions = LuauExecutionOptions.Unbounded with
            {
                WallClockLimit = TimeSpan.FromSeconds(5),
            },
        });
        using var cancellation = new CancellationTokenSource();
        var callbackEntered = NewSignal();
        var allowLateCompletion = NewSignal();

        state["pending"] = state.CreateAsyncFunction(
            "pending",
            async _ =>
            {
                callbackEntered.TrySetResult();
                await allowLateCompletion.Task.ConfigureAwait(false);
            });

        var execution = state.DoStringAsync(
            "pending(); return 1",
            "@cancellation/pending.luau",
            cancellationToken: cancellation.Token).AsTask();

        try
        {
            await callbackEntered.Task.WaitAsync(TestTimeout);
            cancellation.Cancel();
            await Task.Delay(25);
            Assert.False(execution.IsCompleted);

            allowLateCompletion.TrySetResult();
            var exception = await Assert.ThrowsAsync<LuauExecutionCanceledException>(
                () => execution.WaitAsync(TestTimeout));

            Assert.Equal("@cancellation/pending.luau", exception.ChunkName);

            var recovery = state.DoString("return 7", "@cancellation/recovery.luau");
            Assert.Equal(7, Assert.Single(recovery).Read<int>());
        }
        finally
        {
            allowLateCompletion.TrySetResult();
        }
    }

    [Fact]
    public async Task DisposalBeforeWallClockDeadlineWinsAndDefersCloseUntilCallbackReturns()
    {
        var state = LuauState.Create(LuauStateOptions.UnboundedResources with
        {
            DefaultExecutionOptions = LuauExecutionOptions.Unbounded with
            {
                WallClockLimit = TimeSpan.FromSeconds(5),
            },
        });
        var callbackEntered = NewSignal();
        var allowLateCompletion = NewSignal();
        var lateAccessFailure = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        state["pending"] = state.CreateAsyncFunction(
            "pending",
            async context =>
            {
                callbackEntered.TrySetResult();
                await allowLateCompletion.Task.ConfigureAwait(false);
                lateAccessFailure.TrySetResult(Record.Exception(() => context.Return(1)));
            });

        var execution = state.DoStringAsync(
            "pending(); return 1",
            "@disposal/pending.luau").AsTask();

        try
        {
            await callbackEntered.Task.WaitAsync(TestTimeout);
            state.Dispose();
            Assert.False(execution.IsCompleted);

            allowLateCompletion.TrySetResult();

            Assert.IsType<ObjectDisposedException>(
                await lateAccessFailure.Task.WaitAsync(TestTimeout));
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => execution.WaitAsync(TestTimeout));
        }
        finally
        {
            allowLateCompletion.TrySetResult();
            state.Dispose();
        }
    }

    [Fact]
    public async Task DisposalSelectedAfterDeadlineSignalStillHasHardStopPrecedence()
    {
        var state = LuauState.Create(LuauStateOptions.UnboundedResources with
        {
            DefaultExecutionOptions = LuauExecutionOptions.Unbounded with
            {
                WallClockLimit = TimeSpan.FromMilliseconds(75),
            },
        });
        var callbackEntered = NewSignal();
        var deadlineObserved = NewSignal();
        var allowCallbackReturn = NewSignal();

        state["pendingDeadline"] = state.CreateAsyncFunction(
            "pendingDeadline",
            async context =>
            {
                callbackEntered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
                {
                    deadlineObserved.TrySetResult();
                    await allowCallbackReturn.Task.ConfigureAwait(false);
                }
            });

        var execution = state.DoStringAsync(
            "return pendingDeadline()",
            "@disposal/deadline-precedence.luau").AsTask();

        try
        {
            await callbackEntered.Task.WaitAsync(TestTimeout);
            await deadlineObserved.Task.WaitAsync(TestTimeout);
            state.Dispose();
            allowCallbackReturn.TrySetResult();

            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => execution.WaitAsync(TestTimeout));
        }
        finally
        {
            allowCallbackReturn.TrySetResult();
            state.Dispose();
        }
    }

    [Fact]
    public void SynchronousManagedCallbackFailureIsContainedWithChunkAndInnerException()
    {
        using var state = LuauState.Create();
        var cause = new InvalidOperationException("sync callback exploded");
        state["explode"] = state.CreateFunction("explode", _ => throw cause);

        var exception = Assert.Throws<LuauManagedCallbackException>(
            () => state.DoString("explode()", "@callbacks/sync.luau"));

        Assert.Equal("@callbacks/sync.luau", exception.ChunkName);
        Assert.Equal("explode", exception.CallbackName);
        Assert.Same(cause, exception.InnerException);
    }

    [Fact]
    public async Task AsynchronousManagedCallbackFailureIsContainedWithChunkAndInnerException()
    {
        using var state = LuauState.Create();
        var cause = new ApplicationException("async callback exploded");
        state["explodeAsync"] = state.CreateAsyncFunction(
            "explodeAsync",
            async _ =>
            {
                await Task.Yield();
                throw cause;
            });

        var exception = await Assert.ThrowsAsync<LuauManagedCallbackException>(
            () => state.DoStringAsync(
                    "explodeAsync()",
                    "@callbacks/async.luau")
                .AsTask()
                .WaitAsync(TestTimeout));

        Assert.Equal("@callbacks/async.luau", exception.ChunkName);
        Assert.Equal("explodeAsync", exception.CallbackName);
        Assert.Same(cause, exception.InnerException);
    }

    [Fact]
    public void ManagedCallbackFailureCanBeCaughtByLuauPcall()
    {
        using var state = LuauState.Create();
        state.OpenBaseLibrary();
        state["explode"] = state.CreateFunction("explode", _ => throw new InvalidOperationException("caught"));

        var results = state.DoString(
            "local ok, failure = pcall(explode); return ok, type(failure)",
            "@callbacks/pcall-sync.luau");

        Assert.False(results[0].Read<bool>());
        Assert.Equal("userdata", results[1].Read<string>());
    }

    [Fact]
    public async Task AsynchronousManagedCallbackFailureCanBeCaughtByLuauPcall()
    {
        using var state = LuauState.Create();
        state.OpenBaseLibrary();
        state["explode"] = state.CreateAsyncFunction(
            "explode",
            async _ =>
            {
                await Task.Yield();
                throw new InvalidOperationException("caught async");
            });

        var results = await state.DoStringAsync(
            "local ok, failure = pcall(explode); return ok, type(failure)",
            "@callbacks/pcall-async.luau");

        Assert.False(results[0].Read<bool>());
        Assert.Equal("userdata", results[1].Read<string>());
    }

    [Fact]
    public void CaughtCallbackFailureDoesNotMaskLaterScriptError()
    {
        using var state = LuauState.Create();
        state.OpenBaseLibrary();
        state["explode"] = state.CreateFunction("explode", _ => throw new InvalidOperationException("managed cause"));

        var exception = Assert.Throws<LuauException>(() => state.DoString(
            "pcall(explode); error('later script failure')",
            "@callbacks/later-error.luau"));

        Assert.IsNotType<LuauManagedCallbackException>(exception);
        Assert.Contains("later script failure", exception.Message);
        Assert.Equal("@callbacks/later-error.luau", exception.ChunkName);
    }

    [Fact]
    public void NonStringScriptErrorIsNotCoercedAndStateRecovers()
    {
        using var state = LuauState.Create();
        state.OpenBaseLibrary();
        var exception = Assert.Throws<LuauException>(() => state.DoString(
            "error({ code = 42 })",
            "@errors/non-string.luau"));

        Assert.Contains("non-string error value", exception.Message);
        Assert.Equal("@errors/non-string.luau", exception.ChunkName);
        Assert.Equal(6, state.DoString("return 2 * 3").Single().Read<int>());
    }

    [Fact]
    public void TableMoveOverHugeSparseRangeCannotOutliveTheWallClockBudget()
    {
        // Luau before 0.743 copied every index in the range inside one C
        // function with no interrupt, so this one-line script ran for tens of
        // seconds past any wall-clock limit.
        using var root = LuauState.Create();
        root.OpenBaseLibrary();
        root.OpenTableLibrary();
        root.SandboxRoot();
        using var child = root.CreateSandboxedThread();
        var stopwatch = Stopwatch.StartNew();

        var results = child.DoString(
            """
            local moved = table.move({}, 1, 0x7FFFFFFE, 2, {})
            local source = { [1] = "a", [1000000] = "b" }
            local sparse = table.move(source, 1, 0x7FFFFFFE, 2, {})
            return next(moved) == nil, sparse[2], sparse[1000001]
            """,
            "@hardening/table-move.luau",
            executionOptions: new LuauExecutionOptions { WallClockLimit = TimeSpan.FromMilliseconds(250) });

        Assert.True(results[0].Read<bool>());
        Assert.Equal("a", results[1].Read<string>());
        Assert.Equal("b", results[2].Read<string>());
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void WallClockBudgetStopsSynchronousInfiniteLoop()
    {
        var limit = TimeSpan.FromMilliseconds(20);
        using var state = LuauState.Create(new LuauStateOptions
        {
            DefaultExecutionOptions = LuauExecutionOptions.Unbounded with
            {
                WallClockLimit = limit,
            },
        });
        var stopwatch = Stopwatch.StartNew();

        var exception = Assert.Throws<LuauExecutionBudgetException>(() => state.DoString(
            "while true do end",
            "@budgets/wall-clock.luau"));

        Assert.Equal(LuauExecutionBudgetKind.WallClock, exception.BudgetKind);
        Assert.Equal("@budgets/wall-clock.luau", exception.ChunkName);
        Assert.Equal(limit, exception.WallClockLimit);
        Assert.True(exception.Elapsed >= limit);
        Assert.True(stopwatch.Elapsed < TestTimeout);
    }

    [Fact]
    public async Task WallClockDeadlineCancelsCooperativeAsyncCallback()
    {
        var limit = TimeSpan.FromMilliseconds(75);
        using var state = LuauState.Create(LuauStateOptions.UnboundedResources with
        {
            DefaultExecutionOptions = LuauExecutionOptions.Unbounded with
            {
                WallClockLimit = limit,
            },
        });
        var entered = NewSignal();
        state["waitForever"] = state.CreateAsyncFunction(
            "waitForever",
            async context =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken)
                    .ConfigureAwait(false);
            });

        var execution = state.DoStringAsync(
            "return waitForever()",
            "@budgets/cooperative-deadline.luau").AsTask();
        await entered.Task.WaitAsync(TestTimeout);
        var exception = await Assert.ThrowsAsync<LuauExecutionBudgetException>(
            () => execution.WaitAsync(TestTimeout));

        Assert.Equal(LuauExecutionBudgetKind.WallClock, exception.BudgetKind);
        Assert.Equal(limit, exception.WallClockLimit);
        Assert.Equal("@budgets/cooperative-deadline.luau", exception.ChunkName);
        Assert.True(exception.Elapsed >= limit);
        Assert.Equal(7, Assert.Single(state.DoString("return 7")).Read<int>());
    }

    [Fact]
    public async Task CallbackCannotCatchDeadlineCancellationAndTurnItIntoSuccess()
    {
        var limit = TimeSpan.FromMilliseconds(75);
        using var state = LuauState.Create(LuauStateOptions.UnboundedResources with
        {
            DefaultExecutionOptions = LuauExecutionOptions.Unbounded with
            {
                WallClockLimit = limit,
            },
        });
        var caughtCancellation = false;
        state["catchDeadline"] = state.CreateAsyncFunction(
            "catchDeadline",
            async context =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
                {
                    caughtCancellation = true;
                    context.Return(42);
                }
            });

        var exception = await Assert.ThrowsAsync<LuauExecutionBudgetException>(async () =>
            await state.DoStringAsync(
                "return catchDeadline()",
                "@budgets/caught-deadline.luau").AsTask().WaitAsync(TestTimeout));

        Assert.True(caughtCancellation);
        Assert.Equal(LuauExecutionBudgetKind.WallClock, exception.BudgetKind);
        Assert.Equal("@budgets/caught-deadline.luau", exception.ChunkName);
    }

    [Fact]
    public void DeadlineDetectedDuringCallbackArgumentReadKeepsHardStopPrecedence()
    {
        var limit = TimeSpan.FromMilliseconds(25);
        using var state = LuauState.Create(LuauStateOptions.UnboundedResources with
        {
            DefaultExecutionOptions = LuauExecutionOptions.Unbounded with
            {
                WallClockLimit = limit,
            },
        });
        state["readAfterDeadline"] = state.CreateFunction(
            "readAfterDeadline",
            context =>
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(75));
                _ = context.Read<string>(0);
            });

        var exception = Assert.Throws<LuauExecutionBudgetException>(() =>
            state.DoString(
                "return readAfterDeadline('value')",
                "@budgets/callback-read-after-deadline.luau"));

        Assert.Equal(LuauExecutionBudgetKind.WallClock, exception.BudgetKind);
        Assert.Equal(limit, exception.WallClockLimit);
        Assert.Equal("@budgets/callback-read-after-deadline.luau", exception.ChunkName);
        Assert.Equal(7, Assert.Single(state.DoString("return 7")).Read<int>());
    }

    [Fact]
    public async Task CallerCancellationBeforeDeadlineKeepsCallerCancellationPrecedence()
    {
        using var state = LuauState.Create(LuauStateOptions.UnboundedResources with
        {
            DefaultExecutionOptions = LuauExecutionOptions.Unbounded with
            {
                WallClockLimit = TimeSpan.FromSeconds(5),
            },
        });
        using var cancellation = new CancellationTokenSource();
        var entered = NewSignal();
        state["waitForCancellation"] = state.CreateAsyncFunction(
            "waitForCancellation",
            async context =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken)
                    .ConfigureAwait(false);
            });

        var execution = state.DoStringAsync(
            "return waitForCancellation()",
            "@cancellation/before-deadline.luau",
            cancellationToken: cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TestTimeout);
        cancellation.Cancel();

        var exception = await Assert.ThrowsAsync<LuauExecutionCanceledException>(
            () => execution.WaitAsync(TestTimeout));
        Assert.Equal("@cancellation/before-deadline.luau", exception.ChunkName);
    }

    [Fact]
    public async Task CompletedOperationDisposesDeadlineBeforeLaterOperation()
    {
        using var state = LuauState.Create(LuauStateOptions.UnboundedResources);
        var shortDeadline = LuauExecutionOptions.Unbounded with
        {
            WallClockLimit = TimeSpan.FromMilliseconds(50),
        };
        Assert.Equal(
            1,
            Assert.Single(state.DoString(
                "return 1",
                executionOptions: shortDeadline)).Read<int>());

        await Task.Delay(TimeSpan.FromMilliseconds(125));
        state["later"] = state.CreateAsyncFunction(
            "later",
            async context =>
            {
                await Task.Delay(25, context.CancellationToken).ConfigureAwait(false);
                context.Return(context.CancellationToken.IsCancellationRequested);
            });
        var results = await state.DoStringAsync(
            "return later()",
            "@budgets/later-operation.luau",
            executionOptions: LuauExecutionOptions.Unbounded with
            {
                WallClockLimit = TimeSpan.FromSeconds(1),
            });

        Assert.False(Assert.Single(results).Read<bool>());
    }

    [Fact]
    public void LongDeadlineIsScheduledWithoutTimerRangeFailure()
    {
        using var state = LuauState.Create(LuauStateOptions.UnboundedResources);

        var result = state.DoString(
            "return 42",
            executionOptions: LuauExecutionOptions.Unbounded with
            {
                WallClockLimit = TimeSpan.FromDays(100),
            });

        Assert.Equal(42, Assert.Single(result).Read<int>());
    }

    [Fact]
    public void InterruptBudgetStopsSynchronousInfiniteLoop()
    {
        using var state = LuauState.Create();

        var exception = Assert.Throws<LuauExecutionBudgetException>(() => state.DoString(
            "while true do end",
            "@budgets/interrupts.luau",
            executionOptions: new LuauExecutionOptions { InterruptCountLimit = 1 }));

        Assert.Equal(LuauExecutionBudgetKind.InterruptCount, exception.BudgetKind);
        Assert.Equal("@budgets/interrupts.luau", exception.ChunkName);
        Assert.Equal(1, exception.InterruptCountLimit);
        Assert.True(exception.ObservedInterruptCount > exception.InterruptCountLimit);
    }

    [Fact]
    public void InterruptBudgetSurvivesNonYieldableNativeLibrarySentinel()
    {
        using var state = LuauState.Create();
        state.OpenStringLibrary();
        var exception = Assert.Throws<LuauExecutionBudgetException>(() => state.DoString(
            "return string.match(string.rep('a', 100000), '^.*b$')",
            "@budgets/native-pattern.luau",
            executionOptions: new LuauExecutionOptions { InterruptCountLimit = 1 }));

        Assert.Equal(LuauExecutionBudgetKind.InterruptCount, exception.BudgetKind);
        Assert.Equal("@budgets/native-pattern.luau", exception.ChunkName);
        Assert.True(exception.ObservedInterruptCount > exception.InterruptCountLimit);
        Assert.Equal(9, state.DoString("return 4 + 5").Single().Read<int>());
    }

    [Fact]
    public void ResultLimitRejectsAndRemovesUntrustedResults()
    {
        using var state = LuauState.Create();
        var exception = Assert.Throws<LuauResultLimitException>(() => state.DoString(
            "return 1, 2, 3",
            "@results/limited.luau",
            executionOptions: new LuauExecutionOptions { MaxResultCount = 2 }));

        Assert.Equal("@results/limited.luau", exception.ChunkName);
        Assert.Equal(3, exception.ActualCount);
        Assert.Equal(2, exception.Limit);
        Assert.Equal(7, Assert.Single(state.DoString("return 7")).Read<int>());
    }

    [Fact]
    public void ShortDestinationRemovesRejectedResults()
    {
        using var state = LuauState.Create();
        var destination = new LuauValue[1];

        Assert.Throws<ArgumentException>(() => state.DoStringInto(
            "return 1, 2",
            destination,
            "@results/short-destination.luau"));

        Assert.Equal(8, Assert.Single(state.DoString("return 8")).Read<int>());
    }

    [Fact]
    public async Task PerCallRunnerReuseRemainsCleanAcrossManyAsyncExecutions()
    {
        using var state = LuauState.Create();
        state["tick"] = state.CreateAsyncFunction(
            "tick",
            async _ =>
            {
                await Task.Yield();
            });

        for (var i = 0; i < 200; i++)
        {
            var result = await state.DoStringAsync(
                $"tick(); return {i}",
                $"@runner/reuse-{i}.luau");

            Assert.Equal(i, Assert.Single(result).Read<int>());
        }
    }

    [Fact]
    public void DisposingManagedCallbackWrappersKeepsNativeClosuresAliveUntilLuauGc()
    {
        using var state = LuauState.Create();

        for (var i = 0; i < 1_000; i++)
        {
            using var callback = state.CreateFunction(
                $"callback-{i}",
                context => context.Return(0));
            state["callback"] = callback;
        }

        Assert.Equal(0, state.RegisteredDisposableCount);
        Assert.Equal(0, Assert.Single(state.DoString("return callback()", "@callbacks/retained.luau")).Read<int>());

        state["callback"] = LuauValue.Nil;
        state.CollectGarbage();
        ForceManagedFinalizers();

        Assert.Equal(0, state.Context.ManagedCallbackCount);
    }

    [Fact]
    public void NativeGcReleasesUnreachableManagedCallbackRegistrations()
    {
        using var state = LuauState.Create();

        PushTransientCallbacks(state, 500);

        state["callback"] = LuauValue.Nil;
        state.CollectGarbage();
        ForceManagedFinalizers();

        Assert.Equal(0, state.Context.ManagedCallbackCount);
        Assert.Equal(0, state.RegisteredDisposableCount);
    }

    [Fact]
    public void CollectingOneOfTwoNativeClosuresKeepsSharedCallbackAlive()
    {
        using var state = LuauState.Create();
        var wrapper = PushCallbackIntoTwoGlobals(state);

        state["first"] = LuauValue.Nil;
        state.CollectGarbage();
        ForceManagedFinalizers();

        Assert.False(wrapper.IsAlive);
        Assert.Equal(1, state.Context.ManagedCallbackCount);
        Assert.Equal(42, Assert.Single(state.DoString("return second()" )).Read<int>());

        state["second"] = LuauValue.Nil;
        state.CollectGarbage();
        Assert.Equal(0, state.Context.ManagedCallbackCount);
    }

    [Fact]
    public void NeverPushedCallbackIsReleasedByManagedFinalization()
    {
        using var state = LuauState.Create();
        var wrapper = CreateAbandonedCallback(state);
        Assert.Equal(1, state.Context.ManagedCallbackCount);

        ForceManagedFinalizers();

        Assert.False(wrapper.IsAlive);
        Assert.Equal(0, state.Context.ManagedCallbackCount);
    }

    [Fact]
    public async Task AsyncCallbackCanUseStateImmediatelyAfterNativeYieldCompletes()
    {
        using var state = LuauState.Create();
        state["immediate"] = state.CreateAsyncFunction(
            "immediate",
            context =>
            {
                context.Return(2);
                return ValueTask.CompletedTask;
            });

        for (var i = 0; i < 500; i++)
        {
            var results = await state.DoStringAsync(
                "return immediate()",
                $"@callbacks/yield-handshake-{i}.luau");
            Assert.Equal(2, Assert.Single(results).Read<int>());
        }
    }

    [Fact]
    public async Task DisposingActiveChildCancelsItsPendingCallbackAndPreservesRoot()
    {
        using var root = LuauState.Create();
        var child = root.CreateThread();
        var entered = NewSignal();
        child["wait"] = child.CreateAsyncFunction(
            "wait",
            async context =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken).ConfigureAwait(false);
            });

        var execution = child.DoStringAsync("wait()", "@callbacks/child-dispose.luau").AsTask();
        await entered.Task.WaitAsync(TestTimeout);
        child.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => execution.WaitAsync(TestTimeout));
        Assert.Equal(9, Assert.Single(root.DoString("return 9")).Read<int>());
    }

    [Fact]
    public async Task ConcurrentExecutionOnSameRootIsRejectedWhileCallbackIsPending()
    {
        using var state = LuauState.Create();
        var callbackEntered = NewSignal();
        var releaseCallback = NewSignal();
        state["pending"] = state.CreateAsyncFunction(
            "pending",
            async _ =>
            {
                callbackEntered.TrySetResult();
                await releaseCallback.Task.ConfigureAwait(false);
            });

        var running = state.DoStringAsync(
            "pending(); return 1",
            "@serialization/first.luau").AsTask();

        try
        {
            await callbackEntered.Task.WaitAsync(TestTimeout);

            var exception = Assert.Throws<InvalidOperationException>(
                () => state.DoString("return 2", "@serialization/second.luau"));
            Assert.Contains("execut", exception.Message, StringComparison.OrdinalIgnoreCase);

            releaseCallback.TrySetResult();
            var result = await running.WaitAsync(TestTimeout);
            Assert.Equal(1, Assert.Single(result).Read<int>());
        }
        finally
        {
            releaseCallback.TrySetResult();
        }
    }

    [Fact]
    public void ReentrantExecutionOnSameRootIsRejectedThroughCallbackBoundary()
    {
        using var state = LuauState.Create();
        state["reenter"] = state.CreateFunction(
            "reenter",
            context =>
            {
                context.State.DoString("return 99", "@serialization/nested.luau");
            });

        var exception = Assert.Throws<LuauManagedCallbackException>(
            () => state.DoString("reenter()", "@serialization/outer.luau"));

        Assert.Equal("@serialization/outer.luau", exception.ChunkName);
        Assert.Equal("reenter", exception.CallbackName);
        var cause = Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("already executing", cause.Message, StringComparison.OrdinalIgnoreCase);

        var recovery = state.DoString("return 3", "@serialization/recovery.luau");
        Assert.Equal(3, Assert.Single(recovery).Read<int>());
    }

    static TaskCompletionSource NewSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static WeakReference PushCallbackIntoTwoGlobals(LuauState state)
    {
        var callback = state.CreateFunction("shared", context => context.Return(42));
        state["first"] = callback;
        state["second"] = callback;
        return new WeakReference(callback);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static WeakReference CreateAbandonedCallback(LuauState state)
    {
        return new WeakReference(state.CreateFunction("abandoned", _ => { }));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void PushTransientCallbacks(LuauState state, int count)
    {
        for (var i = 0; i < count; i++)
        {
            state["callback"] = state.CreateFunction($"transient-{i}", _ => { });
        }
    }

    static void ForceManagedFinalizers()
    {
        for (var i = 0; i < 5; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }
}
