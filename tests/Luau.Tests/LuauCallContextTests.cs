namespace Luau.Tests;

public sealed class LuauCallContextTests
{
    [Fact]
    public void ReadsAndReturnsManagedValuesAndExpiresAfterInvocation()
    {
        using var state = TestBudgets.CreateRoot();
        var retainedContext = default(LuauCallContext);
        using var callback = state.CreateFunction("sum", context =>
        {
            retainedContext = context;
            Assert.Equal(2, context.ArgumentCount);
            context.Return(context.Read<long>(0) + context.Read<long>(1));
        });
        state["sum"] = callback;

        var result = state.DoString("return sum(19, 23)");

        Assert.Equal(42, Assert.Single(result).Read<int>());
        Assert.Throws<InvalidOperationException>(() => retainedContext.Read<int>(0));
        Assert.Throws<InvalidOperationException>(() => retainedContext.Return(1));
        Assert.Throws<InvalidOperationException>(() => _ = retainedContext.State);
    }

    [Fact]
    public void ConversionFailureIdentifiesCallbackAndArgument()
    {
        using var state = TestBudgets.CreateRoot();
        using var callback = state.CreateFunction(
            "needsInteger",
            context => context.Return(context.Read<long>(0)));
        state["needsInteger"] = callback;

        var exception = Assert.Throws<LuauManagedCallbackException>(
            () => state.DoString("return needsInteger('wrong')", "@context/conversion.luau"));

        Assert.Equal("needsInteger", exception.CallbackName);
        Assert.Contains("Argument 0", exception.InnerException?.Message);
    }

    [Fact]
    public async Task AsyncContextCarriesTheOperationCancellationToken()
    {
        using var state = TestBudgets.CreateRoot();
        using var cancellation = new CancellationTokenSource();
        CancellationToken observed = default;
        using var callback = state.CreateAsyncFunction("observeCancellation", async context =>
        {
            observed = context.CancellationToken;
            await Task.Yield();
            context.Return(7);
        });
        state["observeCancellation"] = callback;

        var result = await state.DoStringAsync(
            "return observeCancellation()",
            cancellationToken: cancellation.Token);

        Assert.Equal(7, Assert.Single(result).Read<int>());
        Assert.True(observed.CanBeCanceled);
    }
}
