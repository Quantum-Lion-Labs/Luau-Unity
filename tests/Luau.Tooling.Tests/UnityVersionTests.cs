using Luau.Tooling;

namespace Luau.Tooling.Tests;

public sealed class UnityVersionTests
{
    [Theory]
    [InlineData("6000.3.0f1")]
    [InlineData("6000.3.19f1")]
    [InlineData("6000.3.20f1")]
    public void AcceptsAnyPatchInSupportedStream(string value)
    {
        var version = UnityVersion.Parse(value);

        Assert.True(version.IsInStream(6000, 3));
        Assert.Equal(value, version.ToString());
    }

    [Fact]
    public void RejectsMalformedVersion()
    {
        Assert.Throws<ToolingException>(() => UnityVersion.Parse("6000.3"));
    }
}
