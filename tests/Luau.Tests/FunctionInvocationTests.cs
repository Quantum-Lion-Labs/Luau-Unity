namespace Luau.Tests;

public sealed class FunctionInvocationTests
{
    [Fact]
    public async Task ScriptClosuresSupportFirstClassSyncAndAsyncInvocation()
    {
        using var root = TestBudgets.CreateRoot();
        using var function = Assert.Single(root.DoString(
            "return function(first, second) return first + second, first * second end"))
            .Read<LuauFunction>();

        var syncResults = function.Invoke(new LuauValue[] { 20d, 2d });
        var asyncResults = await function.InvokeAsync(new LuauValue[] { 19d, 3d });

        Assert.Equal([22, 40], syncResults.Select(value => value.Read<int>()));
        Assert.Equal([22, 57], asyncResults.Select(value => value.Read<int>()));
    }

    [Fact]
    public void FunctionInvocationUsesOperationLimitsAndLeavesVmReusable()
    {
        using var root = TestBudgets.CreateRoot();
        using var function = Assert.Single(root.DoString(
            "return function() return 1, 2, 3 end"))
            .Read<LuauFunction>();

        var exception = Assert.Throws<LuauResultLimitException>(() => function.Invoke(
            executionOptions: LuauExecutionOptions.Unbounded with { MaxResultCount = 2 }));

        Assert.Equal(3, exception.ActualCount);
        Assert.Equal(2, exception.Limit);
        Assert.Equal(42, Assert.Single(root.DoString("return 40 + 2")).Read<int>());
    }

    [Fact]
    public void AsyncManagedCallbackReachedBySyncInvokeFailsClearlyAndRecovers()
    {
        using var root = TestBudgets.CreateRoot();
        root["asyncHost"] = root.CreateAsyncFunction(async context =>
        {
            await Task.Yield();
            context.Return(42);
        });
        using var function = Assert.Single(root.DoString(
            "return function() return asyncHost() end"))
            .Read<LuauFunction>();

        var exception = Assert.Throws<LuauManagedCallbackException>(() => function.Invoke());

        Assert.Contains("asynchronous managed callback", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("synchronous Luau execution", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Equal(7, Assert.Single(root.DoString("return 7")).Read<int>());
    }

    [Fact]
    public async Task ManagedCallbackCapabilitiesCannotBeInvokedByManagedCallers()
    {
        using var root = TestBudgets.CreateRoot();
        using var callback = root.CreateFunction(context => context.Return(42));

        var syncException = Assert.Throws<InvalidOperationException>(() => callback.Invoke());
        var asyncException = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await callback.InvokeAsync(Array.Empty<LuauValue>()));

        Assert.Contains("only be invoked by Luau", syncException.Message);
        Assert.Equal(syncException.Message, asyncException.Message);
        root["callback"] = callback;
        Assert.Equal(42, Assert.Single(root.DoString("return callback()" )).Read<int>());
    }
}
