namespace Luau.Tooling;

internal static class DoctorCommand
{
    public static async Task<int> RunAsync(RepositoryContext repository, CommandLine options)
    {
        var unityVersion = UnityVersion.Parse(options.Get("--unity-version", "6000.3.20f1"));
        if (!unityVersion.IsInStream(6000, 3))
        {
            throw new ToolingException(
                $"Unity {unityVersion} is outside the supported 6000.3 editor stream.");
        }

        Console.WriteLine($"Repository: {repository.Root}");
        Console.WriteLine($"Platform: {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}");
        await RequireToolAsync("dotnet", ["--version"], repository.Root, "9.0.306");
        await RequireToolAsync("cmake", ["--version"], repository.Root);
        await RequireToolAsync("ninja", ["--version"], repository.Root);
        await RequireToolAsync("clang-18", ["--version"], repository.Root);
        await RequireToolAsync("clang++-18", ["--version"], repository.Root);

        var submoduleFile = repository.PathOf("native", "luau", "CMakeLists.txt");
        if (!File.Exists(submoduleFile))
        {
            throw new ToolingException("The native/luau submodule is not initialized. Run 'git submodule update --init --recursive'.");
        }

        var unity = options.Get("--unity");
        if (unity is not null)
        {
            if (!File.Exists(unity))
            {
                throw new ToolingException($"Unity editor does not exist: {unity}");
            }

            Console.WriteLine($"Unity editor: {Path.GetFullPath(unity)} ({unityVersion})");
            if (OperatingSystem.IsLinux())
            {
                var editorRoot = Path.GetDirectoryName(Path.GetFullPath(unity));
                var il2Cpp = editorRoot is null ? string.Empty : Path.Combine(
                    editorRoot, "Data", "PlaybackEngines", "LinuxStandaloneSupport", "Variations", "il2cpp");
                if (!Directory.Exists(il2Cpp))
                {
                    throw new ToolingException(
                        "Unity Linux IL2CPP support is not visible to the editor. Install the linux-il2cpp module.");
                }

                Console.WriteLine($"Unity Linux IL2CPP: {il2Cpp}");
                var compatibilityDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "opt", "unity-linux-compat", "usr", "lib", "x86_64-linux-gnu");
                var systemHasLegacyXml = File.Exists("/usr/lib/x86_64-linux-gnu/libxml2.so.2");
                if (!systemHasLegacyXml &&
                    (!File.Exists(Path.Combine(compatibilityDirectory, "libxml2.so.2")) ||
                     !File.Exists(Path.Combine(compatibilityDirectory, "libicuuc.so.70"))))
                {
                    throw new ToolingException(
                        "Unity's Linux IL2CPP linker requires libxml2.so.2. Install the documented user-local Unity compatibility libraries.");
                }

                Console.WriteLine(systemHasLegacyXml
                    ? "Unity Linux linker compatibility: system libxml2.so.2"
                    : $"Unity Linux linker compatibility: {compatibilityDirectory}");
            }
        }
        else
        {
            Console.WriteLine($"Unity editor stream: {unityVersion} (pass --unity to validate an executable)");
        }

        Console.WriteLine("Development prerequisites are available.");
        return 0;
    }

    private static async Task RequireToolAsync(
        string tool,
        string[] arguments,
        string workingDirectory,
        string? requiredFirstLine = null)
    {
        var result = await ProcessRunner.RequireAsync(tool, arguments, workingDirectory, echo: false);
        var firstLine = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
        if (requiredFirstLine is not null && !firstLine.Equals(requiredFirstLine, StringComparison.Ordinal))
        {
            throw new ToolingException($"{tool} reported '{firstLine}'; expected '{requiredFirstLine}'.");
        }

        Console.WriteLine($"{tool}: {firstLine}");
    }
}
