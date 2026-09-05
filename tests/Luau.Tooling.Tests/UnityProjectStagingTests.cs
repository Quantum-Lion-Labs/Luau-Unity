using Luau.Tooling;

namespace Luau.Tooling.Tests;

public sealed class UnityProjectStagingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "luau-unity-staging-" + Guid.NewGuid().ToString("N"));
    private string Package => Path.Combine(root, "package");
    private string Project => Path.Combine(root, "project");

    public UnityProjectStagingTests()
    {
        Directory.CreateDirectory(Path.Combine(Project, "Assets"));
        FileSystem.WriteUtf8(Path.Combine(Package, "package.json"), """
            {
              "name": "com.qll.luau.unity",
              "version": "1.2.3",
              "samples": [
                { "displayName": "Getting Started", "path": "Samples~/Getting Started" },
                { "displayName": "Full Luau Scripting Demo", "path": "Samples~/Full Luau Scripting Demo" }
              ]
            }
            """);
        FileSystem.WriteUtf8(Path.Combine(Package, "Samples~/Getting Started/Example.cs"), "getting started");
        FileSystem.WriteUtf8(Path.Combine(Package, "Samples~/Full Luau Scripting Demo/Core/Runtime.cs"), "reusable core");
        FileSystem.WriteUtf8(Path.Combine(Package, "Samples~/Full Luau Scripting Demo/Demo Game/Game.cs"), "demo game");
        FileSystem.WriteUtf8(Path.Combine(Package, "Samples~/Full Luau Scripting Demo/Demo Game.meta"), "game metadata");
    }

    [Fact]
    public void ImportsCompleteSamplesWithMetadataAndRejectsOverwrite()
    {
        var samples = UnityProjectStaging.ImportSamples(Package, Project);
        Assert.True(PathSafety.IsStrictDescendant(samples, Path.Combine(Project, "Assets")));
        Assert.Equal("getting started", File.ReadAllText(Path.Combine(samples, "Getting Started/Example.cs")));
        Assert.Equal("reusable core", File.ReadAllText(Path.Combine(samples, "Full Luau Scripting Demo/Core/Runtime.cs")));
        Assert.Equal("demo game", File.ReadAllText(Path.Combine(samples, "Full Luau Scripting Demo/Demo Game/Game.cs")));
        Assert.Equal("game metadata", File.ReadAllText(Path.Combine(samples, "Full Luau Scripting Demo/Demo Game.meta")));
        Assert.Throws<ToolingException>(() => UnityProjectStaging.ImportSamples(Package, Project));
    }

    [Theory]
    [InlineData("1.2.3", "../escape")]
    [InlineData("Samples~/Getting Started", "../outside")]
    [InlineData("com.qll.luau.unity", "com.other.package")]
    public void RejectsInvalidMetadataBeforeCopying(string original, string replacement)
    {
        var metadata = Path.Combine(Package, "package.json");
        File.WriteAllText(metadata, File.ReadAllText(metadata).Replace(original, replacement, StringComparison.Ordinal));
        Assert.Throws<ToolingException>(() => UnityProjectStaging.ImportSamples(Package, Project));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(Project, "Assets")));
    }

    [Fact]
    public void StagesIdenticalDevelopmentPluginAndMetadataInBothProjectLayouts()
    {
        var source = Path.Combine(root, "native", "libluau_host.so");
        FileSystem.WriteUtf8(source, "development plugin bytes");
        var consumer = Path.Combine(Project, "Assets/Plugins/linux-x64");
        var embedded = Path.Combine(Project, "Packages/com.qll.luau.unity/Runtime/Plugins/linux-x64");
        UnityProjectStaging.StageLinuxPlugin(consumer, source);
        UnityProjectStaging.StageLinuxPlugin(embedded, source);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(consumer, "libluau_host.so")));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(embedded, "libluau_host.so")));
        Assert.Equal(File.ReadAllText(consumer + ".meta"), File.ReadAllText(embedded + ".meta"));
        Assert.Equal(File.ReadAllText(Path.Combine(consumer, "libluau_host.so.meta")),
            File.ReadAllText(Path.Combine(embedded, "libluau_host.so.meta")));
        Assert.False(Directory.Exists(Path.Combine(Package, "Runtime/Plugins/linux-x64")));
    }

    [Theory]
    [InlineData("Example.cs(1): error CS1002: ; expected", true, false)]
    [InlineData("Scripts have compiler errors", true, false)]
    [InlineData("Compilation failed", true, false)]
    [InlineData("Failed to resolve packages", false, true)]
    [InlineData("DllNotFoundException: luau_host", false, true)]
    [InlineData("EntryPointNotFoundException: luau_host", false, true)]
    [InlineData("Compilation completed; LUAU_PACKAGE_CONSUMER_PASS", false, false)]
    public void ClassifiesCompilerAndConsumerDependencyFailures(string text, bool compiler, bool dependency)
    {
        Assert.Equal(compiler, UnityProcess.HasCompilerErrors(text));
        Assert.Equal(dependency, UnityProcess.HasPackageOrPluginErrors(text));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
