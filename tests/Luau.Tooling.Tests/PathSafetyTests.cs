using Luau.Tooling;

namespace Luau.Tooling.Tests;

public sealed class PathSafetyTests
{
    [Fact]
    public void StrictDescendantAcceptsNestedPath()
    {
        var parent = Path.Combine(Path.GetTempPath(), "luau-parent");
        var child = Path.Combine(parent, "nested", "child");

        Assert.True(PathSafety.IsStrictDescendant(child, parent));
    }

    [Fact]
    public void StrictDescendantRejectsParentAndSiblingPrefix()
    {
        var parent = Path.Combine(Path.GetTempPath(), "luau-parent");

        Assert.False(PathSafety.IsStrictDescendant(parent, parent));
        Assert.False(PathSafety.IsStrictDescendant(parent + "-sibling", parent));
    }
}
