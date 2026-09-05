using System.Text;

namespace Luau.Tests;

public sealed class ModuleCacheBudgetTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StringResultsAcrossOperationsStopAtRootByteQuota(bool useBundle)
    {
        const int stringLength = 1024;
        const int admittedCount = 16;
        const long byteLimit = 33 * 1024;
        var map = new LuauModuleMap(Enumerable.Range(0, 32).ToDictionary(
            i => $"m{i:D2}", _ => Encoding.UTF8.GetBytes("loaded(); return string.rep('x', 1024)")));
        await using var service = new LuauThreadedCompilationService();
        LuauRequirer requirer = useBundle ? await map.CompileModuleBundleAsync(service) : map;
        using var root = LuauState.Create(LuauStateOptions.Default with
        {
            MemoryLimitBytes = 1024 * 1024,
            MaxCachedModuleBytes = byteLimit,
        });
        root.OpenBaseLibrary();
        root.OpenStringLibrary();
        var loads = 0;
        using var loaded = root.CreateFunction(_ => loads++);
        root["loaded"] = loaded;
        root.OpenRequireLibrary(requirer);
        root.SandboxRoot();

        for (var i = 0; i < admittedCount; i++)
            Assert.Equal(stringLength, LoadLength(root, $"m{i:D2}"));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var failure = Assert.Throws<LuauManagedCallbackException>(() => LoadLength(root, "m16"));
            var limit = Assert.IsType<LuauModuleLimitException>(failure.InnerException);
            Assert.Equal(LuauModuleLimitKind.CachedResultBytes, limit.LimitKind);
            Assert.Equal(byteLimit, limit.Limit);
            Assert.True(limit.Actual > limit.Limit);
            Assert.Equal(0, root.GetTop());
        }

        Assert.Equal(stringLength, LoadLength(root, "m00"));
        Assert.Equal(admittedCount + 2, loads); // Rejected results were never cached; hits add no charge.
        Assert.True(root.MemoryUsage.PeakBytes < root.MemoryUsage.LimitBytes);
    }

    [Fact]
    public void CacheBudgetCountsUtf16KeysAndResultsAtExactBoundary()
    {
        using var root = LuauState.Create(LuauStateOptions.Default with { MaxCachedModuleBytes = 8 });
        // One key char and three value chars (including a surrogate pair).
        root.Context.CacheModule("a", "é🐺");
        var failure = Assert.Throws<LuauModuleLimitException>(() => root.Context.CacheModule("b", 1));
        Assert.Equal(10, failure.Actual);
        Assert.Equal(8, failure.Limit);
        Assert.False(root.Context.TryGetCachedModule("b", out _));
        Assert.True(root.Context.TryGetCachedModule("a", out var result));
        Assert.Equal("é🐺", result.Read<string>());
    }

    [Fact]
    public void ReplacementsAndRejectedPublicationsPreserveRemainingBudget()
    {
        using var root = LuauState.Create(LuauStateOptions.Default with { MaxCachedModuleBytes = 12 });
        root.Context.CacheModule("a", "123"); // Eight bytes.
        Assert.Throws<LuauModuleLimitException>(() => root.Context.CacheModule("a", "123456"));
        Assert.True(root.Context.TryGetCachedModule("a", out var original));
        Assert.Equal("123", original.Read<string>());
        root.Context.CacheModule("b", "x"); // Rejection did not consume the four remaining bytes.
        root.Context.CacheModule("a", "x"); // Replacement releases four bytes.
        root.Context.CacheModule("c", "x");
        Assert.Throws<LuauModuleLimitException>(() => root.Context.CacheModule("d", 1));
    }

    [Fact]
    public void ReferenceResultsKeepTheirPayloadInNativeMemory()
    {
        using var root = LuauState.Create(LuauStateOptions.Default with { MaxCachedModuleBytes = 100 });
        root.OpenBaseLibrary();
        root.OpenStringLibrary();
        root.OpenRequireLibrary(new LuauModuleMap(new Dictionary<string, byte[]>
        {
            ["table"] = "return {payload = string.rep('x', 4096)}"u8.ToArray(),
            ["func"] = "return function() return string.rep('x', 4096) end"u8.ToArray(),
        }));
        using var results = root.DoString("return #require('table').payload, #require('func')()");
        Assert.Equal([4096, 4096], results.Select(value => value.Read<int>()));
    }

    static int LoadLength(LuauState root, string module)
    {
        using var child = root.CreateSandboxedThread();
        using var result = child.DoString($"return #require('{module}')");
        return result.Read<int>(0);
    }
}
