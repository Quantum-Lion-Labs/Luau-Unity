namespace Luau.Tooling;

internal static class ValidateLinuxCommand
{
    public static async Task<int> RunAsync(RepositoryContext repository, CommandLine options)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new ToolingException("validate-linux is only supported on Linux.");
        }

        var forwarded = ForwardUnityOptions(options);
        await DoctorCommand.RunAsync(repository, new CommandLine(forwarded));

        var hostRoot = repository.PathOf("native", "luau-host");
        await ProcessRunner.RequireAsync("cmake", ["--preset", "linux-x64"], hostRoot);
        await ProcessRunner.RequireAsync("cmake", ["--build", "--preset", "linux-x64", "--parallel"], hostRoot);
        await ProcessRunner.RequireAsync("ctest", ["--preset", "linux-x64"], hostRoot);
        await ProcessRunner.RequireAsync("cmake", ["--install", "out/build/linux-x64"], hostRoot);

        var nativeHost = repository.PathOf("native", "luau-host", "out", "install", "linux-x64", "libluau_host.so");
        await ArtifactManifestCommand.WriteAsync(
            repository,
            nativeHost,
            "linux-x64",
            repository.PathOf("native", "luau-host", "out", "install", "linux-x64", "luau_host.manifest.json"));
        await ProcessRunner.RequireAsync("dotnet", ["restore", "Luau.slnx"], repository.Root);
        await ProcessRunner.RequireAsync(
            "dotnet",
            ["test", "Luau.slnx", "--no-restore", "--configuration", "Release", $"-p:LuauHostNativePath={nativeHost}"],
            repository.Root,
            timeout: TimeSpan.FromMinutes(20));
        await ProcessRunner.RequireAsync(
            "dotnet",
            [
                "run", "--project", "tests/Luau.Host.AbiFixtureHost/Luau.Host.AbiFixtureHost.csproj",
                "--configuration", "Release", "--", "--fixtures",
                "native/luau-host/out/build/linux-x64/invalid-abi-fixtures",
            ],
            repository.Root);

        await HostSoakCommand.RunAsync(repository, new CommandLine(
            ["--configuration", "Release", "--iterations", options.GetInt("--soak-iterations", 25).ToString(), "--native-host", nativeHost]));
        await ManagedHarnessSelectionCommand.RunAsync(repository, new CommandLine([]));
        await ManagedArtifactsCommand.RunAsync(repository, new CommandLine(["--configuration", "Release", "--check"]));
        await PackageStaticCommand.RunAsync(repository, new CommandLine([]));
        var policy = ReleasePolicy.Load(repository);
        await PackageReleaseCommand.RunAsync(repository, new CommandLine(
            ["--output", $"native/luau-host/out/release/{policy.PackageId}-{policy.PackageVersion}.tgz"]));

        if (!options.Has("--skip-unity"))
        {
            var consumer = new List<string>(forwarded) { "--linux-plugin", nativeHost };
            await PackageConsumerCommand.RunAsync(repository, new CommandLine(consumer));
            var unity = new List<string>(forwarded)
            {
                "--compile", "--editmode", "--linux-smoke", "--linux-plugin", nativeHost,
                "--unity-timeout-minutes", options.GetInt("--unity-timeout-minutes", 30).ToString(),
            };
            await UnityHostCommand.RunAsync(repository, new CommandLine(unity));
        }

        Console.WriteLine(options.Has("--skip-unity")
            ? "Linux partial acceptance passed: native, managed, and package validation are current (Unity skipped)."
            : "Linux development acceptance passed: native, managed, package, and Unity validation are current.");
        return 0;
    }

    private static List<string> ForwardUnityOptions(CommandLine options)
    {
        var arguments = new List<string>();
        foreach (var name in new[] { "--unity", "--unity-version" })
        {
            if (options.Get(name) is { } value)
            {
                arguments.Add(name);
                arguments.Add(value);
            }
        }
        return arguments;
    }
}
