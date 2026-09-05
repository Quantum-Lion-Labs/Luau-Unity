using System.Text.RegularExpressions;

namespace Luau.Tooling;

internal static class UnityProcess
{
    public static Task<ProcessResult> RunAsync(
        UnityEditor editor,
        string project,
        string log,
        string[] arguments,
        TimeSpan? timeout = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        return ProcessRunner.RunAsync(
            editor.Executable,
            ["-batchmode", "-nographics", "-projectPath", project, "-logFile", log, .. arguments],
            project,
            UnityEnvironment(),
            timeout: timeout ?? TimeSpan.FromMinutes(30));
    }

    public static async Task RequireAsync(
        UnityEditor editor,
        string project,
        string log,
        string[] arguments,
        string description,
        TimeSpan? timeout = null)
    {
        Console.WriteLine($"==> {description} with Unity {editor.Version}");
        var result = await RunAsync(editor, project, log, arguments, timeout);
        if (result.ExitCode != 0)
        {
            throw new ToolingException($"'{editor.Executable}' exited with code {result.ExitCode}. See {log}");
        }
    }

    public static bool HasCompilerErrors(string text) => Regex.IsMatch(
        text,
        @"error CS\d+|Scripts have compiler errors|Compilation failed",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool HasPackageOrPluginErrors(string text) => Regex.IsMatch(
        text,
        @"Failed to resolve packages?|DllNotFoundException.*luau_host|EntryPointNotFoundException.*luau_host",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static void RejectCompilerErrors(string log)
    {
        FileSystem.RequireFile(log, "Unity compilation log");
        if (HasCompilerErrors(File.ReadAllText(log)))
        {
            throw new ToolingException($"Unity reported compiler errors. See {log}");
        }
    }

    private static IReadOnlyDictionary<string, string?>? UnityEnvironment()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        var compatibilityDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "opt", "unity-linux-compat", "usr", "lib", "x86_64-linux-gnu");
        if (!File.Exists(Path.Combine(compatibilityDirectory, "libxml2.so.2")) ||
            !File.Exists(Path.Combine(compatibilityDirectory, "libicuuc.so.70")))
        {
            return null;
        }

        var existing = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        return new Dictionary<string, string?>
        {
            ["LD_LIBRARY_PATH"] = string.IsNullOrWhiteSpace(existing)
                ? compatibilityDirectory
                : compatibilityDirectory + Path.PathSeparator + existing,
        };
    }
}
