namespace Luau.Tooling;

internal static class HostSoakCommand
{
    public static async Task<int> RunAsync(RepositoryContext repository, CommandLine options)
    {
        var configuration = options.Get("--configuration", "Release");
        var iterations = options.GetInt("--iterations", 25);
        if (iterations is < 1 or > 10_000)
        {
            throw new ToolingException("--iterations must be between 1 and 10000.");
        }

        var nativeHost = options.Get("--native-host") ?? DefaultNativeHost(repository);
        if (!Path.IsPathRooted(nativeHost))
        {
            throw new ToolingException("--native-host must be an absolute path.");
        }

        nativeHost = Path.GetFullPath(nativeHost);
        FileSystem.RequireFile(nativeHost, "Selected luau_host artifact");
        var output = options.Get("--output") ?? repository.PathOf("artifacts", "stage-3-host-soak");
        output = Path.IsPathRooted(output) ? Path.GetFullPath(output) : repository.PathOf(output);
        Directory.CreateDirectory(output);

        Console.WriteLine($"Selected luau_host native artifact: {nativeHost}");
        Console.WriteLine($"Selected luau_host SHA256: {Hashing.FileSha256(nativeHost)}");
        var report = Path.Combine(output, "luau-host.json");
        await ProcessRunner.RequireAsync(
            "dotnet",
            [
                "run", "--project", repository.PathOf("tests", "Luau.HostSoak", "Luau.HostSoak.csproj"),
                "--configuration", configuration,
                "--artifacts-path", Path.Combine(output, "dotnet"),
                $"-p:LuauHostNativePath={nativeHost}",
                "--", "run", "--output", report, "--soak-iterations", iterations.ToString(),
            ],
            repository.Root,
            timeout: TimeSpan.FromMinutes(30));

        Console.WriteLine($"luau_host soak validation passed. Report: {report}");
        return 0;
    }

    private static string DefaultNativeHost(RepositoryContext repository) => OperatingSystem.IsWindows()
        ? repository.PathOf("Luau.Unity", "Runtime", "Plugins", "win-x64", "luau_host.dll")
        : OperatingSystem.IsLinux()
            ? repository.PathOf("native", "luau-host", "out", "install", "linux-x64", "libluau_host.so")
            : throw new ToolingException("No default luau_host artifact is defined for this operating system.");
}
