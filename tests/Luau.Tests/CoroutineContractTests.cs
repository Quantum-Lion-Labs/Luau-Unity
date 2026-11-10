namespace Luau.Tests;

public sealed class CoroutineContractTests
{
    static readonly TimeSpan TestTimeout = TestBudgets.WaitTimeout;

    [Fact]
    public async Task ArgumentArraysBindToAllocatingResumeApisAndAreNotOverwritten()
    {
        using var root = TestBudgets.CreateRoot();
        root.OpenCoroutineLibrary();
        using var syncCoroutine = Assert.Single(root.DoString(
            "return coroutine.create(function(first, second) return first + second, first * second end)"))
            .Read<LuauState>();
        using var asyncCoroutine = Assert.Single(root.DoString(
            "return coroutine.create(function(first, second) return first - second, second end)"))
            .Read<LuauState>();
        var arguments = new LuauValue[] { 21d, 2d };

        using LuauResultScope syncResults = syncCoroutine.Resume(arguments);
        ValueTask<LuauResultScope> asyncOperation = asyncCoroutine.ResumeAsync(arguments);
        using LuauResultScope asyncResults = await asyncOperation;

        Assert.Equal([21, 2], arguments.Select(value => value.Read<int>()));
        Assert.Equal([23, 42], syncResults.Select(value => value.Read<int>()));
        Assert.Equal([19, 2], asyncResults.Select(value => value.Read<int>()));
    }

    [Fact]
    public void ChildLifecycleReportsOnlyManagedProvableStates()
    {
        using var root = TestBudgets.CreateRoot();
        root.OpenBaseLibrary();
        root.OpenCoroutineLibrary();
        var rootException = Assert.Throws<InvalidOperationException>(() => root.GetStatus());
        Assert.Contains("child", rootException.Message, StringComparison.OrdinalIgnoreCase);
        var resumeRootException = Assert.Throws<InvalidOperationException>(() => root.Resume());
        Assert.Contains("child", resumeRootException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("root", resumeRootException.Message, StringComparison.OrdinalIgnoreCase);

        using var hostChild = root.CreateThread();
        Assert.Equal(LuauThreadStatus.Suspended, hostChild.GetStatus());
        LuauThreadStatus? observedDuringCallback = null;
        hostChild["observeStatus"] = hostChild.CreateFunction(context =>
        {
            observedDuringCallback = context.State.GetStatus();
            context.Return(42);
        });

        Assert.Equal(42, Assert.Single(hostChild.DoString("return observeStatus()" )).Read<int>());
        Assert.Equal(LuauThreadStatus.Running, observedDuringCallback);
        Assert.Equal(LuauThreadStatus.Dead, hostChild.GetStatus());

        // A dead child is reusable for a new host load, and that operation has
        // its own Running -> Dead lifecycle.
        observedDuringCallback = null;
        Assert.Equal(42, Assert.Single(hostChild.DoString("return observeStatus()" )).Read<int>());
        Assert.Equal(LuauThreadStatus.Running, observedDuringCallback);
        Assert.Equal(LuauThreadStatus.Dead, hostChild.GetStatus());

        using var yielded = root.CreateThread();
        Assert.Equal(
            1,
            Assert.Single(yielded.DoString("coroutine.yield(1); return 2")).Read<int>());
        Assert.Equal(LuauThreadStatus.Suspended, yielded.GetStatus());
        Assert.Equal(2, Assert.Single(yielded.Resume()).Read<int>());
        Assert.Equal(LuauThreadStatus.Dead, yielded.GetStatus());

        using var luaCreated = Assert.Single(root.DoString(
            "return coroutine.create(function() return 3 end)" )).Read<LuauState>();
        Assert.Equal(LuauThreadStatus.Suspended, luaCreated.GetStatus());
        Assert.Equal(3, Assert.Single(luaCreated.Resume()).Read<int>());
        Assert.Equal(LuauThreadStatus.Dead, luaCreated.GetStatus());
    }

    [Fact]
    public async Task FailureCancellationAndBudgetStopsLeaveChildDeadAndReusable()
    {
        using var root = LuauState.Create(LuauStateOptions.UnboundedResources);
        root.OpenBaseLibrary();
        using var failed = root.CreateThread();

        Assert.Throws<LuauException>(() => failed.DoString("error('expected failure')"));
        Assert.Equal(LuauThreadStatus.Dead, failed.GetStatus());
        Assert.Equal(7, Assert.Single(failed.DoString("return 7")).Read<int>());
        Assert.Equal(LuauThreadStatus.Dead, failed.GetStatus());

        using var budgeted = root.CreateThread();
        var budgetException = Assert.Throws<LuauExecutionBudgetException>(() => budgeted.DoString(
            "while true do end",
            executionOptions: LuauExecutionOptions.Unbounded with { InterruptCountLimit = 1 }));
        Assert.Equal(LuauExecutionBudgetKind.InterruptCount, budgetException.BudgetKind);
        Assert.Equal(LuauThreadStatus.Dead, budgeted.GetStatus());
        Assert.Equal(8, Assert.Single(budgeted.DoString("return 8")).Read<int>());

        using var canceled = root.CreateThread();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        canceled["wait"] = canceled.CreateAsyncFunction(async context =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken)
                .ConfigureAwait(false);
        });
        var execution = canceled.DoStringAsync(
            "return wait()",
            "@lifecycle/canceled.luau",
            cancellationToken: cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TestTimeout);
        cancellation.Cancel();

        await Assert.ThrowsAsync<LuauExecutionCanceledException>(
            () => execution.WaitAsync(TestTimeout));
        Assert.Equal(LuauThreadStatus.Dead, canceled.GetStatus());
        Assert.Equal(9, Assert.Single(canceled.DoString("return 9")).Read<int>());

        canceled.Dispose();
        Assert.Throws<ObjectDisposedException>(() => canceled.GetStatus());
    }
}
