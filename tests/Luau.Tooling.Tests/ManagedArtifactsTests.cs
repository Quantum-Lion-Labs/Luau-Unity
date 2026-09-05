using Luau.Tooling;

namespace Luau.Tooling.Tests;

public sealed class ManagedArtifactsTests
{
    [Fact]
    public void RefreshAndStagingPreserveTheirBuildModes()
    {
        var repository = new RepositoryContext(Path.Combine(Path.GetTempPath(), "luau build modes"));
        var nativeHost = repository.PathOf("native host", "libluau_host.so");
        var refresh = ManagedArtifacts.GetBuildCommands(
            repository, "Release", ManagedArtifactBuildMode.PackageRefresh).ToArray();
        var staging = ManagedArtifacts.GetBuildCommands(
            repository, "Release", ManagedArtifactBuildMode.UnityStaging, nativeHost).ToArray();

        Assert.All(refresh, arguments =>
        {
            Assert.Contains("--no-restore", arguments);
            Assert.DoesNotContain("--framework", arguments);
            Assert.DoesNotContain(arguments, argument => argument.StartsWith("-p:LuauHostNativePath="));
        });
        Assert.All(staging, arguments => Assert.DoesNotContain("--no-restore", arguments));
        var runtime = Assert.Single(staging, arguments => arguments[1] == repository.PathOf("src/Luau/Luau.csproj"));
        Assert.Contains("--framework", runtime);
        Assert.Contains("netstandard2.1", runtime);
        Assert.Contains($"-p:LuauHostNativePath={nativeHost}", runtime);
        var generator = Assert.Single(staging, arguments => arguments[1] == repository.PathOf("src/Luau.SourceGenerator/Luau.SourceGenerator.csproj"));
        Assert.DoesNotContain("--framework", generator);
        Assert.DoesNotContain(generator, argument => argument.StartsWith("-p:LuauHostNativePath="));
    }

    [Fact]
    public void BothDestinationsUseCanonicalBytesAndChecksPreserveStaleFilesAndMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "luau-managed-artifacts-" + Guid.NewGuid().ToString("N"));
        var repository = new RepositoryContext(root);
        try
        {
            FileSystem.WriteUtf8(repository.PathOf("src/Luau/bin/Release/netstandard2.1/Luau.dll"), "runtime bytes");
            FileSystem.WriteUtf8(repository.PathOf("src/Luau/bin/Release/netstandard2.1/Luau.xml"), "<doc>\r\n</doc>\r");
            FileSystem.WriteUtf8(repository.PathOf("src/Luau.SourceGenerator/bin/Release/netstandard2.0/Luau.SourceGenerator.dll"), "generator bytes");
            var artifacts = new[] { "Luau.dll", "Luau.xml", "Luau.SourceGenerator.dll" };
            var package = repository.PathOf("Luau.Unity");
            var staged = repository.PathOf("disposable/Packages/com.qll.luau.unity");
            foreach (var destination in new[] { package, staged })
            {
                foreach (var name in artifacts)
                {
                    FileSystem.WriteUtf8(Path.Combine(destination, "Runtime", name + ".meta"), "preserved metadata: " + name);
                }
                ManagedArtifacts.CopyOrCheck(repository, "Release", destination, check: false);
                ManagedArtifacts.CopyOrCheck(repository, "Release", destination, check: true);
                foreach (var name in artifacts)
                {
                    Assert.Equal("preserved metadata: " + name, File.ReadAllText(Path.Combine(destination, "Runtime", name + ".meta")));
                }
            }

            foreach (var name in artifacts)
            {
                Assert.Equal(File.ReadAllBytes(Path.Combine(package, "Runtime", name)),
                    File.ReadAllBytes(Path.Combine(staged, "Runtime", name)));
            }
            Assert.Equal("<doc>\n</doc>\n", File.ReadAllText(Path.Combine(staged, "Runtime", "Luau.xml")));

            var stale = Path.Combine(staged, "Runtime", "Luau.xml");
            File.WriteAllText(stale, "stale documentation");
            Assert.Throws<ToolingException>(() => ManagedArtifacts.CopyOrCheck(repository, "Release", staged, check: true));
            Assert.Equal("stale documentation", File.ReadAllText(stale));
            Assert.Equal("<doc>\n</doc>\n", File.ReadAllText(Path.Combine(package, "Runtime", "Luau.xml")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
