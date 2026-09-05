namespace Luau.Tooling;

internal static class ManagedArtifactsCommand
{
    public static async Task<int> RunAsync(RepositoryContext repository, CommandLine options)
    {
        var configuration = options.Get("--configuration", "Release");
        await ManagedArtifacts.BuildAsync(repository, configuration, ManagedArtifactBuildMode.PackageRefresh);
        ManagedArtifacts.CopyOrCheck(repository, configuration, repository.PathOf("Luau.Unity"), options.Has("--check"));

        Console.WriteLine(options.Has("--check")
            ? "Unity managed artifacts are current."
            : "Unity package managed artifacts updated.");
        return 0;
    }
}
