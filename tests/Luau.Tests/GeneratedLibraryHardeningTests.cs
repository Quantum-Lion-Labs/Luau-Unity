using Luau;

namespace Luau.Tests;

public sealed class GeneratedLibraryHardeningTests
{
    [Fact]
    public void GeneratedPropertyWritesRequireAPublicSetter()
    {
        var library = new GeneratedHardeningLibrary();
        using var state = TestBudgets.CreateRoot();
        state.OpenBaseLibrary();
        state.OpenLibrary(library);

        var results = state.DoString(
            """
            local ok, failure = pcall(function()
                generatedHardening.privateSetValue = 99
            end)

            generatedHardening.writableValue = 17
            return ok,
                type(failure),
                generatedHardening.privateSetValue,
                generatedHardening.writableValue
            """);

        Assert.Equal(4, results.Length);
        Assert.False(results[0].Read<bool>());
        Assert.Equal("userdata", results[1].Read<string>());
        Assert.Equal(41, results[2].Read<int>());
        Assert.Equal(17, results[3].Read<int>());
        Assert.Equal(41, library.PrivateSetValue);
        Assert.Equal(17, library.WritableValue);
    }

    [Fact]
    public void GeneratedMetatableIsProtectedFromSandboxedChildren()
    {
        using var root = TestBudgets.CreateRoot();
        root.OpenBaseLibrary();
        root.OpenLibrary(new GeneratedHardeningLibrary());
        root.SandboxRoot();
        using var first = root.CreateSandboxedThread();
        using var second = root.CreateSandboxedThread();

        var attackResults = first.DoString(
            """
            local exposed = getmetatable(generatedHardening)
            local ok, failure = pcall(function()
                setmetatable(generatedHardening, {})
            end)

            return exposed, type(exposed), ok, tostring(failure)
            """);
        var siblingResults = second.DoString(
            "return generatedHardening.privateSetValue");

        Assert.Equal(4, attackResults.Length);
        Assert.Equal("protected Luau host library", attackResults[0].Read<string>());
        Assert.Equal("string", attackResults[1].Read<string>());
        Assert.False(attackResults[2].Read<bool>());
        Assert.Contains(
            "protected metatable",
            attackResults[3].Read<string>(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(41, Assert.Single(siblingResults).Read<int>());
    }
}

[LuauLibrary("generatedHardening")]
sealed partial class GeneratedHardeningLibrary
{
    [LuauMember("privateSetValue")]
    public int PrivateSetValue { get; private set; } = 41;

    [LuauMember("writableValue")]
    public int WritableValue { get; set; } = 10;
}
