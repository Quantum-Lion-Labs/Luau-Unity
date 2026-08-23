using System.Text.Json.Nodes;

namespace Luau.Tooling;

internal static class ReleaseCiCommand
{
    private const string CanonicalRepository = "Quantum-Lion-Labs/Luau-Unity";

    public static async Task<int> RequireSourceAsync(RepositoryContext repository, CommandLine options)
    {
        var actual = options.Get("--repository") ?? Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
        PackageStaticCommand.Require(actual == CanonicalRepository,
            $"Releases must run from '{CanonicalRepository}', not '{actual ?? "<unset>"}'.");
        await ProcessRunner.RequireAsync("git", ["merge-base", "--is-ancestor", "HEAD", "origin/main"], repository.Root);
        Console.WriteLine("Release source is the canonical repository and the tagged commit is on main.");
        return 0;
    }

    public static Task<int> WriteMetadataAsync(RepositoryContext repository, CommandLine options)
    {
        var tag = options.Get("--tag") ?? Environment.GetEnvironmentVariable("GITHUB_REF_NAME")
            ?? throw new ToolingException("--tag is required.");
        var package = JsonNode.Parse(File.ReadAllText(repository.PathOf("Luau.Unity", "package.json")))!.AsObject();
        var name = package["name"]!.GetValue<string>();
        var version = package["version"]!.GetValue<string>();
        PackageStaticCommand.Require(tag == $"v{version}",
            $"Release tag '{tag}' does not match package version '{version}'. Expected 'v{version}'.");
        var output = options.Get("--github-output");
        if (!string.IsNullOrWhiteSpace(output))
        {
            File.AppendAllText(output, $"version={version}{Environment.NewLine}archive_name={name}-{version}.tgz{Environment.NewLine}", FileSystem.Utf8NoBom);
        }
        Console.WriteLine($"Release metadata: version={version}; archive_name={name}-{version}.tgz");
        return Task.FromResult(0);
    }
}
