using System.Security.Cryptography;

namespace Luau.Tooling;

internal enum ManagedArtifactBuildMode
{
    PackageRefresh,
    UnityStaging,
}

internal static class ManagedArtifacts
{
    private sealed record Artifact(string FileName, bool CanonicalText = false);
    private sealed record Project(string Path, string Framework, bool UsesNativeHost, Artifact[] Artifacts);

    private static readonly Project[] Projects =
    [
        new("src/Luau/Luau.csproj", "netstandard2.1", true,
            [new("Luau.dll"), new("Luau.xml", CanonicalText: true)]),
        new("src/Luau.SourceGenerator/Luau.SourceGenerator.csproj", "netstandard2.0", false,
            [new("Luau.SourceGenerator.dll")]),
    ];

    public static async Task BuildAsync(
        RepositoryContext repository,
        string configuration,
        ManagedArtifactBuildMode mode,
        string? nativeHost = null)
    {
        foreach (var arguments in GetBuildCommands(repository, configuration, mode, nativeHost))
        {
            await ProcessRunner.RequireAsync("dotnet", arguments, repository.Root);
        }
    }

    internal static IEnumerable<string[]> GetBuildCommands(
        RepositoryContext repository,
        string configuration,
        ManagedArtifactBuildMode mode,
        string? nativeHost = null)
    {
        if (configuration is not ("Debug" or "Release"))
        {
            throw new ToolingException("--configuration must be Debug or Release.");
        }
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        if (mode == ManagedArtifactBuildMode.UnityStaging && string.IsNullOrWhiteSpace(nativeHost))
        {
            throw new ToolingException("Unity managed staging requires a native host path.");
        }

        foreach (var project in Projects)
        {
            var arguments = new List<string>
            {
                "build", repository.PathOf(project.Path), "--configuration", configuration, "--nologo",
            };
            if (mode == ManagedArtifactBuildMode.PackageRefresh)
            {
                arguments.Add("--no-restore");
            }
            else if (project.UsesNativeHost)
            {
                arguments.AddRange(["--framework", project.Framework,
                    $"-p:LuauHostNativePath={Path.GetFullPath(nativeHost!, repository.Root)}"]);
            }
            yield return arguments.ToArray();
        }
    }

    public static void CopyOrCheck(
        RepositoryContext repository,
        string configuration,
        string packageRoot,
        bool check)
    {
        foreach (var project in Projects)
        {
            foreach (var artifact in project.Artifacts)
            {
                var source = repository.PathOf(
                    Path.GetDirectoryName(project.Path)!, "bin", configuration, project.Framework, artifact.FileName);
                var destination = Path.Combine(packageRoot, "Runtime", artifact.FileName);
                FileSystem.RequireFile(source, "Managed build artifact");
                FileSystem.RequireFile(destination + ".meta", "Unity artifact metadata");
                var sourceBytes = artifact.CanonicalText ? Hashing.CanonicalUtf8Bytes(source) : File.ReadAllBytes(source);
                var sourceHash = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();

                if (check)
                {
                    FileSystem.RequireFile(destination, "Unity artifact");
                    if (sourceHash != Hashing.FileSha256(destination))
                    {
                        throw new ToolingException($"Stale Unity artifact: {destination} does not match {source}.");
                    }
                    Console.WriteLine($"Current: {destination} (SHA256={sourceHash})");
                }
                else
                {
                    File.WriteAllBytes(destination, sourceBytes);
                    if (sourceHash != Hashing.FileSha256(destination))
                    {
                        throw new ToolingException($"Copied Unity artifact failed SHA256 verification: {destination}");
                    }
                    Console.WriteLine($"Copied {source} -> {destination} (SHA256={sourceHash})");
                }
            }
        }
    }
}
