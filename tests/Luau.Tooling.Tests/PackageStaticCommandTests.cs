using Luau.Tooling;

namespace Luau.Tooling.Tests;

public sealed class PackageStaticCommandTests
{
    [Theory]
    [InlineData("m_EditorVersion: 6000.3.19f1\n")]
    [InlineData("m_EditorVersion: 6000.3.19f1\r\n")]
    public void AcceptsCanonicalIntegrationVersionWithPlatformLineEndings(string content)
    {
        Assert.True(PackageStaticCommand.HasCanonicalIntegrationVersion(content));
    }
}
