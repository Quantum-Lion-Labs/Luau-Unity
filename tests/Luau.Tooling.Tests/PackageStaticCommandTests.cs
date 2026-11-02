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

    [Fact]
    public void AcceptsOnlyTheReviewedLinuxPluginImporter()
    {
        var repository = RepositoryRoot();
        var plugins = Path.Combine(repository, "Luau.Unity", "Runtime", "Plugins");
        var linux = File.ReadAllText(Path.Combine(plugins, "linux-x64", "libluau_host.so.meta"));

        Assert.True(PackageStaticCommand.IsLinuxPluginImporter(linux));
        Assert.True(PackageStaticCommand.IsLinuxPluginImporter(linux.Replace("\n", "\r\n", StringComparison.Ordinal)));
        Assert.False(PackageStaticCommand.IsLinuxPluginImporter(
            File.ReadAllText(Path.Combine(plugins, "win-x64", "luau_host.dll.meta"))));
        Assert.False(PackageStaticCommand.IsLinuxPluginImporter(
            File.ReadAllText(Path.Combine(plugins, "android-arm64", "libluau_host.so.meta"))));
        Assert.False(PackageStaticCommand.IsLinuxPluginImporter(linux.Replace("Exclude Win64: 1", "Exclude Win64: 0", StringComparison.Ordinal)));
        Assert.False(PackageStaticCommand.IsLinuxPluginImporter(linux.Replace("OS: Linux", "OS: AnyOS", StringComparison.Ordinal)));
        Assert.False(PackageStaticCommand.IsLinuxPluginImporter(linux.Replace(
            "    Linux64:\n      enabled: 1", "    Linux64:\n      enabled: 0", StringComparison.Ordinal)));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Luau.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
