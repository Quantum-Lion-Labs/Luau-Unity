namespace Luau.Tests;

public sealed class InterruptTrampolineLifetimeTests
{
    [Fact]
    public async Task RootInstallsInterruptTrampolineOnceAcrossRepeatedOperations()
    {
        using var root = LuauState.Create();
        Assert.Equal(1, root.Context.InterruptInstallCount);

        Assert.Equal(1, Assert.Single(root.DoString("return 1")).Read<int>());
        using var functionOwner = root.DoString("return function() end");
        var function = functionOwner.Read<LuauFunction>(0);
        function.InvokeVoid();
        await function.InvokeVoidAsync();
        Assert.Equal(2, Assert.Single(await root.DoStringAsync("return 2")).Read<int>());

        Assert.Equal(1, root.Context.InterruptInstallCount);
    }

    [Fact]
    public void InterruptTrampolinesAreIndependentAndNativeCloseDrainsEachRoot()
    {
        var first = LuauState.Create();
        var second = LuauState.Create();
        var firstContext = first.Context;
        var secondContext = second.Context;

        Assert.Equal(1, firstContext.InterruptInstallCount);
        Assert.Equal(1, secondContext.InterruptInstallCount);
        Assert.Equal(11, Assert.Single(first.DoString("return 11")).Read<int>());
        Assert.Equal(22, Assert.Single(second.DoString("return 22")).Read<int>());

        first.Dispose();
        second.Dispose();

        Assert.Equal(1, firstContext.CloseCount);
        Assert.Equal(1, secondContext.CloseCount);
        Assert.True(firstContext.IsDisposed);
        Assert.True(secondContext.IsDisposed);
    }
}
