using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Luau.Tooling;

internal static class UnityHostCommand
{
    private const string PackageName = "com.qll.luau.unity";
    private const string AndroidPackageName = "com.luauunity.host.smoke";
    private const string PassedMarker = "LUAU_PLAYER_SMOKE_PASS";
    private const string FailedMarker = "LUAU_PLAYER_SMOKE_FAIL";
    internal const string PassedMarkerForAndroid = PassedMarker;
    internal const string FailedMarkerForAndroid = FailedMarker;

    public static async Task<int> RunAsync(RepositoryContext repository, CommandLine options)
    {
        var configuration = options.Get("--configuration", "Release");
        if (configuration is not ("Debug" or "Release"))
        {
            throw new ToolingException("--configuration must be Debug or Release.");
        }
        var compile = options.Has("--compile");
        var editMode = options.Has("--editmode");
        var linuxSmoke = options.Has("--linux-smoke");
        var windowsSmoke = options.Has("--windows-smoke");
        var androidArm64Smoke = options.Has("--android-arm64-smoke");
        var androidX64Smoke = options.Has("--android-x64-smoke");
        if (linuxSmoke && !OperatingSystem.IsLinux())
        {
            throw new ToolingException("--linux-smoke can only run on Linux.");
        }
        if (windowsSmoke && !OperatingSystem.IsWindows())
        {
            throw new ToolingException("--windows-smoke can only run on Windows.");
        }
        var runUnity = compile || editMode || linuxSmoke || windowsSmoke || androidArm64Smoke || androidX64Smoke;
        var editor = runUnity ? UnityEditor.Resolve(repository, options) : null;
        var smokeTimeoutSeconds = options.GetInt("--smoke-timeout-seconds", 180);
        if (smokeTimeoutSeconds is < 10 or > 600)
        {
            throw new ToolingException("--smoke-timeout-seconds must be between 10 and 600.");
        }

        var nativeOut = repository.PathOf("native", "luau-host", "out");
        var validationRoot = options.Get("--output") ?? Path.Combine(nativeOut, "unity-host");
        validationRoot = Path.IsPathRooted(validationRoot)
            ? Path.GetFullPath(validationRoot)
            : repository.PathOf(validationRoot);
        if (!PathSafety.IsStrictDescendant(validationRoot, nativeOut))
        {
            throw new ToolingException($"Unity output must be a strict descendant of {nativeOut}: {validationRoot}");
        }

        var project = Path.Combine(validationRoot, "project");
        var logs = Path.Combine(validationRoot, "logs");
        var results = Path.Combine(validationRoot, "results");
        var builds = Path.Combine(validationRoot, "builds");
        var stagedPackage = Path.Combine(project, "Packages", PackageName);
        if (options.Has("--reuse"))
        {
            FileSystem.RequireDirectory(project, "Reusable disposable Unity project");
            Directory.CreateDirectory(logs);
            Directory.CreateDirectory(results);
            Directory.CreateDirectory(builds);
            Console.WriteLine($"Reusing disposable Unity host project at {project}");
        }
        else
        {
            if (Directory.Exists(validationRoot))
            {
                Directory.Delete(validationRoot, recursive: true);
            }

            Directory.CreateDirectory(project);
            Directory.CreateDirectory(logs);
            Directory.CreateDirectory(results);
            Directory.CreateDirectory(builds);

            var sourceProject = repository.PathOf("tests", "Luau.Unity.Integration");
            foreach (var folder in new[] { "Assets", "Packages", "ProjectSettings" })
            {
                FileSystem.CopyDirectory(Path.Combine(sourceProject, folder), Path.Combine(project, folder));
            }

            FileSystem.CopyDirectory(repository.PathOf("Luau.Unity"), stagedPackage);
            var packageManifestMeta = Path.Combine(stagedPackage, "package.json.meta");
            if (File.Exists(packageManifestMeta))
            {
                File.Delete(packageManifestMeta);
            }

            UnityProjectStaging.ImportSamples(stagedPackage, project);
            RemoveGeneratedDirectories(project);
            NormalizeEmbeddedPackage(project);
            if (editor is not null)
            {
                FileSystem.WriteUtf8(
                    Path.Combine(project, "ProjectSettings", "ProjectVersion.txt"),
                    $"m_EditorVersion: {editor.Version}\n");
            }

            var nativeHost = OperatingSystem.IsLinux()
                ? options.Get("--linux-plugin") ?? repository.PathOf("native", "luau-host", "out", "install", "linux-x64", "libluau_host.so")
                : options.Get("--windows-plugin") ?? repository.PathOf("Luau.Unity", "Runtime", "Plugins", "win-x64", "luau_host.dll");
            if (OperatingSystem.IsLinux() || linuxSmoke)
            {
                if (!Path.IsPathRooted(nativeHost))
                {
                    throw new ToolingException("--linux-plugin must be an absolute path.");
                }

                UnityProjectStaging.StageLinuxPlugin(
                    Path.Combine(stagedPackage, "Runtime", "Plugins", "linux-x64"),
                    Path.GetFullPath(nativeHost));
            }

            StagePackagePluginOverride(repository, stagedPackage, options.Get("--windows-plugin"), "win-x64", "luau_host.dll");
            StagePackagePluginOverride(repository, stagedPackage, options.Get("--android-arm64-plugin"), "android-arm64", "libluau_host.so");
            StagePackagePluginOverride(repository, stagedPackage, options.Get("--android-x64-plugin"), "android-x64", "libluau_host.so");

            await ManagedArtifacts.BuildAsync(repository, configuration, ManagedArtifactBuildMode.UnityStaging, nativeHost);
            ManagedArtifacts.CopyOrCheck(repository, configuration, stagedPackage, check: false);
            Console.WriteLine($"Disposable Unity host project prepared at {project}");
        }

        if (androidArm64Smoke || androidX64Smoke)
        {
            SetAndroidApplicationIdentifier(
                Path.Combine(project, "ProjectSettings", "ProjectSettings.asset"), AndroidPackageName);
        }

        if (compile)
        {
            var log = Path.Combine(logs, "compile.log");
            await UnityProcess.RequireAsync(editor!, project, log, ["-quit"], "Compile disposable Unity host project");
            UnityProcess.RejectCompilerErrors(log);
        }

        if (editMode)
        {
            var log = Path.Combine(logs, "editmode-tests.log");
            var result = Path.Combine(results, "editmode-tests.xml");
            await UnityProcess.RequireAsync(
                editor!, project, log,
                ["-runTests", "-testPlatform", "EditMode", "-testResults", result],
                "Run disposable Unity EditMode tests",
                TimeSpan.FromMinutes(options.GetInt("--unity-timeout-minutes", 30)));
            FileSystem.RequireFile(result, "Unity EditMode result");
            var testRun = XDocument.Load(result).Root;
            if (testRun?.Name.LocalName != "test-run" ||
                testRun.Attribute("result")?.Value != "Passed" ||
                testRun.Attribute("failed")?.Value != "0")
            {
                throw new ToolingException($"Unity EditMode tests did not pass. See {result} and {log}");
            }
        }

        if (linuxSmoke)
        {
            var output = Path.Combine(builds, "linux-x64", "LuauSmoke");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await UnityProcess.RequireAsync(
                editor!, project, Path.Combine(logs, "linux-x64-smoke-build.log"),
                [
                    "-buildTarget", "Linux64",
                    "-executeMethod", "Luau.Unity.Editor.LuauPlayerSmokeBuild.BuildLinux64Il2Cpp",
                    "-luauSmokeOutput", output,
                    "-quit",
                ],
                "Build disposable Linux x64 IL2CPP smoke player",
                TimeSpan.FromMinutes(options.GetInt("--unity-timeout-minutes", 30)));
            FileSystem.RequireFile(output, "Linux smoke player");
            await RunDesktopPlayerSmokeAsync(output, Path.Combine(logs, "linux-x64-player.log"), smokeTimeoutSeconds, "Linux x64");
        }

        if (windowsSmoke)
        {
            var output = Path.Combine(builds, "windows-x64", "LuauSmoke.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await UnityProcess.RequireAsync(
                editor!, project, Path.Combine(logs, "windows-x64-smoke-build.log"),
                [
                    "-buildTarget", "Win64",
                    "-executeMethod", "Luau.Unity.Editor.LuauPlayerSmokeBuild.BuildWindows64Il2Cpp",
                    "-luauSmokeOutput", output,
                    "-quit",
                ],
                "Build disposable Windows x64 IL2CPP smoke player",
                TimeSpan.FromMinutes(options.GetInt("--unity-timeout-minutes", 30)));
            FileSystem.RequireFile(output, "Windows smoke player");
            await RunDesktopPlayerSmokeAsync(output, Path.Combine(logs, "windows-x64-player.log"), smokeTimeoutSeconds, "Windows x64");
        }

        if (androidArm64Smoke)
        {
            await BuildAndRunAndroidSmokeAsync(
                repository, options, editor!, project, logs, builds,
                "android-arm64", "BuildAndroidArm64Il2Cpp", "quest-arm64",
                options.Get("--android-arm64-serial"), smokeTimeoutSeconds);
        }

        if (androidX64Smoke)
        {
            await BuildAndRunAndroidSmokeAsync(
                repository, options, editor!, project, logs, builds,
                "android-x64", "BuildAndroidX64Il2Cpp", "emulator-x64",
                options.Get("--android-x64-serial"), smokeTimeoutSeconds);
        }

        Console.WriteLine("Unity host validation completed.");
        return 0;
    }

    private static void NormalizeEmbeddedPackage(string project)
    {
        var manifestPath = Path.Combine(project, "Packages", "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject() ??
            throw new ToolingException($"Invalid Unity manifest: {manifestPath}");
        manifest["dependencies"]!.AsObject()[PackageName] = $"file:{PackageName}";
        FileSystem.WriteUtf8(manifestPath, manifest.ToJsonString(JsonOptions.Indented) + "\n");

        var lockPath = Path.Combine(project, "Packages", "packages-lock.json");
        if (!File.Exists(lockPath))
        {
            return;
        }

        var packageLock = JsonNode.Parse(File.ReadAllText(lockPath))?.AsObject() ??
            throw new ToolingException($"Invalid Unity package lock: {lockPath}");
        var dependencies = packageLock["dependencies"]?.AsObject();
        if (dependencies?[PackageName] is not JsonObject dependency)
        {
            File.Delete(lockPath);
            return;
        }

        dependency["version"] = $"file:{PackageName}";
        dependency["source"] = "embedded";
        FileSystem.WriteUtf8(lockPath, packageLock.ToJsonString(JsonOptions.Indented) + "\n");
    }

    private static void StagePackagePluginOverride(
        RepositoryContext repository,
        string stagedPackage,
        string? source,
        string platform,
        string fileName)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        source = Path.IsPathRooted(source) ? Path.GetFullPath(source) : repository.PathOf(source);
        var destination = Path.Combine(stagedPackage, "Runtime", "Plugins", platform, fileName);
        FileSystem.RequireFile(destination + ".meta", $"Reviewed {platform} Unity importer metadata");
        FileSystem.CopyFile(source, destination);
        if (Hashing.FileSha256(source) != Hashing.FileSha256(destination))
        {
            throw new ToolingException($"Staged {platform} plugin failed SHA256 verification.");
        }

        Console.WriteLine($"Staged disposable {platform} plugin: {destination}");
    }

    private static void SetAndroidApplicationIdentifier(string projectSettings, string packageName)
    {
        FileSystem.RequireFile(projectSettings, "Disposable Unity ProjectSettings.asset");
        var lines = File.ReadAllLines(projectSettings);
        var inApplicationIdentifier = false;
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index] == "  applicationIdentifier:")
            {
                inApplicationIdentifier = true;
                continue;
            }
            if (inApplicationIdentifier && lines[index].StartsWith("    Android: ", StringComparison.Ordinal))
            {
                lines[index] = "    Android: " + packageName;
                File.WriteAllLines(projectSettings, lines, FileSystem.Utf8NoBom);
                return;
            }
            if (inApplicationIdentifier && lines[index].StartsWith("  ", StringComparison.Ordinal) &&
                !lines[index].StartsWith("    ", StringComparison.Ordinal))
            {
                break;
            }
        }

        throw new ToolingException($"Android application identifier was not found in {projectSettings}");
    }

    private static async Task BuildAndRunAndroidSmokeAsync(
        RepositoryContext repository,
        CommandLine options,
        UnityEditor editor,
        string project,
        string logs,
        string builds,
        string platform,
        string buildMethod,
        string targetKind,
        string? serial,
        int smokeTimeoutSeconds)
    {
        var output = Path.Combine(builds, platform, "LuauSmoke.apk");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await UnityProcess.RequireAsync(
            editor, project, Path.Combine(logs, $"{platform}-smoke-build.log"),
            [
                "-buildTarget", "Android",
                "-executeMethod", $"Luau.Unity.Editor.LuauPlayerSmokeBuild.{buildMethod}",
                "-luauSmokeOutput", output,
                "-quit",
            ],
            $"Build disposable {platform} IL2CPP smoke APK",
            TimeSpan.FromMinutes(options.GetInt("--unity-timeout-minutes", 30)));
        FileSystem.RequireFile(output, $"{platform} smoke APK");
        await AndroidSmoke.RunAsync(
            repository, editor, options.Get("--adb"), serial, targetKind, output,
            AndroidPackageName, Path.Combine(logs, $"{platform}-player.log"), smokeTimeoutSeconds);
    }

    private static async Task RunDesktopPlayerSmokeAsync(
        string executable,
        string log,
        int timeoutSeconds,
        string platform)
    {
        Console.WriteLine($"==> Launch {platform} IL2CPP smoke player");
        var result = await ProcessRunner.RunAsync(
            executable,
            ["-batchmode", "-nographics", "-logFile", log],
            Path.GetDirectoryName(executable)!,
            timeout: TimeSpan.FromSeconds(timeoutSeconds));
        var text = File.Exists(log) ? File.ReadAllText(log) : result.CombinedOutput;
        if (result.ExitCode != 0 || !text.Contains(PassedMarker, StringComparison.Ordinal) ||
            text.Contains(FailedMarker, StringComparison.Ordinal))
        {
            throw new ToolingException($"{platform} player smoke failed with exit code {result.ExitCode}. See {log}");
        }

        Console.WriteLine($"{platform} IL2CPP smoke passed.");
    }

    private static void RemoveGeneratedDirectories(string root)
    {
        var directories = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) is "bin" or "obj")
            .OrderByDescending(static path => path.Length)
            .ToArray();
        foreach (var directory in directories)
        {
            Directory.Delete(directory, recursive: true);
            var meta = directory + ".meta";
            if (File.Exists(meta))
            {
                File.Delete(meta);
            }
        }
    }
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    public static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };
}
