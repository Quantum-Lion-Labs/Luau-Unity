namespace Luau.Tooling;

internal static class ManagedArtifactsCommand
{
    private sealed record Artifact(string Source, string Destination, bool CanonicalText = false);

    public static async Task<int> RunAsync(RepositoryContext repository, CommandLine options)
    {
        var configuration = options.Get("--configuration", "Release");
        if (configuration is not ("Debug" or "Release"))
        {
            throw new ToolingException("--configuration must be Debug or Release.");
        }

        foreach (var project in new[]
        {
            "src/Luau/Luau.csproj",
            "src/Luau.SourceGenerator/Luau.SourceGenerator.csproj",
        })
        {
            await ProcessRunner.RequireAsync(
                "dotnet",
                ["build", repository.PathOf(project), "--configuration", configuration, "--nologo", "--no-restore"],
                repository.Root);
        }

        var artifacts = new[]
        {
            new Artifact($"src/Luau/bin/{configuration}/netstandard2.1/Luau.dll", "Runtime/Luau.dll"),
            new Artifact($"src/Luau/bin/{configuration}/netstandard2.1/Luau.xml", "Runtime/Luau.xml", true),
            new Artifact(
                $"src/Luau.SourceGenerator/bin/{configuration}/netstandard2.0/Luau.SourceGenerator.dll",
                "Runtime/Luau.SourceGenerator.dll"),
        };

        foreach (var artifact in artifacts)
        {
            var source = repository.PathOf(artifact.Source);
            var destination = repository.PathOf("Luau.Unity", artifact.Destination);
            FileSystem.RequireFile(source, "Managed build artifact");
            FileSystem.RequireFile(destination + ".meta", "Unity artifact metadata");
            var sourceBytes = artifact.CanonicalText ? Hashing.CanonicalUtf8Bytes(source) : File.ReadAllBytes(source);
            var sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sourceBytes)).ToLowerInvariant();

            if (options.Has("--check"))
            {
                FileSystem.RequireFile(destination, "Unity artifact");
                var destinationHash = Hashing.FileSha256(destination);
                if (!sourceHash.Equals(destinationHash, StringComparison.Ordinal))
                {
                    throw new ToolingException($"Stale Unity artifact: {destination} does not match {source}.");
                }

                Console.WriteLine($"Current: {destination} (SHA256={destinationHash})");
            }
            else
            {
                File.WriteAllBytes(destination, sourceBytes);
                var destinationHash = Hashing.FileSha256(destination);
                if (!sourceHash.Equals(destinationHash, StringComparison.Ordinal))
                {
                    throw new ToolingException($"Copied Unity artifact failed SHA256 verification: {destination}");
                }

                Console.WriteLine($"Copied {source} -> {destination} (SHA256={destinationHash})");
            }
        }

        Console.WriteLine(options.Has("--check")
            ? "Unity managed artifacts are current."
            : "Unity package managed artifacts updated.");
        return 0;
    }
}
