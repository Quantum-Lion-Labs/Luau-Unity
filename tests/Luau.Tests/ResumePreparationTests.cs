namespace Luau.Tests;

public sealed class ResumePreparationTests
{
    public static IEnumerable<object[]> PreparationFailures()
    {
        foreach (var api in new[] { "Resume", "ResumeInto", "ResumeAsync", "ResumeIntoAsync" })
        foreach (var crossRoot in new[] { false, true })
        foreach (var yielded in new[] { false, true })
            yield return [api, crossRoot, yielded];
    }

    [Theory]
    [MemberData(nameof(PreparationFailures))]
    public async Task InvalidArgumentPreservesStackAndLifecycleAndAllowsRetry(
        string api, bool crossRoot, bool yielded)
    {
        using var root = LuauState.Create();
        using var foreign = LuauState.Create();
        root.OpenCoroutineLibrary();
        using var owner = root.DoString(yielded
            ? "return coroutine.create(function() local value = coroutine.yield(); return value end)"
            : "return coroutine.create(function(value) return value end)");
        using var coroutine = owner.Read<LuauState>(0);
        if (yielded)
        {
            using var suspension = coroutine.Resume();
            Assert.Empty(suspension);
        }
        using var invalid = (crossRoot ? foreign : root).CreateTable();
        if (!crossRoot)
            invalid.Dispose();
        var originalTop = coroutine.GetTop();
        var originalStatus = coroutine.GetStatus();

        var exception = await Record.ExceptionAsync(() => InvokeResume(
            coroutine, api, [1d, LuauValue.FromTable(invalid)]));

        if (crossRoot)
            Assert.IsType<InvalidOperationException>(exception);
        else
            Assert.IsType<ObjectDisposedException>(exception);
        Assert.Equal(originalTop, coroutine.GetTop());
        Assert.Equal(originalStatus, coroutine.GetStatus());
        Assert.Equal(42, await InvokeResume(coroutine, api, [42d]));
        Assert.Equal(0, coroutine.GetTop());
        Assert.Equal(LuauThreadStatus.Dead, coroutine.GetStatus());
    }

    static async Task<int> InvokeResume(LuauState coroutine, string api, LuauValue[] arguments)
    {
        if (api is "Resume" or "ResumeAsync")
        {
            using var result = api == "Resume"
                ? coroutine.Resume(arguments)
                : await coroutine.ResumeAsync(arguments);
            return Assert.Single(result).Read<int>();
        }

        var destination = new LuauValue[1];
        var count = api == "ResumeInto"
            ? coroutine.ResumeInto(arguments, destination)
            : await coroutine.ResumeIntoAsync(arguments, destination);
        Assert.Equal(1, count);
        return destination[0].Read<int>();
    }
}
