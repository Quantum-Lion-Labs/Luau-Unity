namespace Luau.Tests;

public sealed class RootYieldTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task YieldingInvocationIsRejectedAndNextFunctionActuallyRuns(bool isAsync, bool yieldsValue)
    {
        using var root = LuauState.Create();
        root.OpenCoroutineLibrary();
        var marks = new List<string>();
        using var mark = root.CreateFunction(context => marks.Add(context.Read<string>(0)));
        root["mark"] = mark;
        using var functions = root.DoString(
            "return function() mark('before'); coroutine.yield(" + (yieldsValue ? "7" : "") +
            "); mark('after') end, function() mark('second') end");
        var first = functions.Read<LuauFunction>(0);
        var second = functions.Read<LuauFunction>(1);

        var exception = isAsync
            ? await Assert.ThrowsAsync<LuauException>(() => first.InvokeVoidAsync().AsTask())
            : Assert.Throws<LuauException>(() => first.InvokeVoid());

        Assert.Contains("yield", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, root.GetTop());
        if (isAsync)
            await second.InvokeVoidAsync();
        else
            second.InvokeVoid();
        Assert.Equal(["before", "second"], marks);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RootSourceYieldIsRejectedAndNextChunkActuallyRuns(bool isAsync, bool yieldsValue)
    {
        using var root = LuauState.Create();
        root.OpenCoroutineLibrary();
        var source = "coroutine.yield(" + (yieldsValue ? "7" : "") + "); return 9";

        var exception = isAsync
            ? await Assert.ThrowsAsync<LuauException>(() => root.DoStringAsync(source).AsTask())
            : Assert.Throws<LuauException>(() => root.DoString(source));

        Assert.Contains("yield", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, root.GetTop());
        using var next = isAsync
            ? await root.DoStringAsync("return 11")
            : root.DoString("return 11");
        Assert.Equal(11, next.Read<int>(0));
    }
}
