using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Luau.Tooling;

internal static class PackageConsumerCommand
{
    private const string PassedMarker = "LUAU_PACKAGE_CONSUMER_PASS";
    private const string FailedMarker = "LUAU_PACKAGE_CONSUMER_FAIL";

    public static async Task<int> RunAsync(RepositoryContext repository, CommandLine options)
    {
        var editor = UnityEditor.Resolve(repository, options);
        var allowedRoot = repository.PathOf("native", "luau-host", "out");
        var outputRoot = options.Get("--output") ?? allowedRoot;
        outputRoot = Path.IsPathRooted(outputRoot) ? Path.GetFullPath(outputRoot) : repository.PathOf(outputRoot);
        if (outputRoot != allowedRoot && !PathSafety.IsStrictDescendant(outputRoot, allowedRoot))
        {
            throw new ToolingException($"Consumer output root must stay under {allowedRoot}: {outputRoot}");
        }

        Directory.CreateDirectory(outputRoot);
        var project = Path.Combine(outputRoot, "unity-package-consumer-" + Guid.NewGuid().ToString("N"));
        var succeeded = false;
        try
        {
            var assets = Path.Combine(project, "Assets", "ConsumerProbe");
            var packages = Path.Combine(project, "Packages");
            var settings = Path.Combine(project, "ProjectSettings");
            var logs = Path.Combine(project, "Logs");
            Directory.CreateDirectory(assets);
            Directory.CreateDirectory(packages);
            Directory.CreateDirectory(settings);
            Directory.CreateDirectory(logs);
            FileSystem.CopyDirectory(repository.PathOf("tests", "Luau.Unity.PackageConsumerProbe"), assets);

            var reference = options.Get("--package") ?? "file:" + repository.PathOf("Luau.Unity").Replace('\\', '/');
            var expectedCommit = options.Get("--expected-commit");
            var contentRoot = await ResolvePackageContentAsync(repository, project, reference, expectedCommit);
            using var metadata = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(Path.Combine(contentRoot, "package.json")));
            var package = metadata.RootElement;
            var packageName = package.GetProperty("name").GetString();
            var packageVersion = package.GetProperty("version").GetString();
            PackageStaticCommand.Require(packageName == "com.qll.luau.unity", $"Unexpected package identity: {packageName}");
            PackageStaticCommand.Require(packageVersion is not null && Regex.IsMatch(
                    packageVersion, @"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant),
                $"Referenced package has an unsafe or invalid version: {packageVersion}");
            FileSystem.RequireFile(Path.Combine(contentRoot, "Runtime", "Luau.xml"), "Referenced XML documentation");

            var samplesRoot = Path.Combine(project, "Assets", "Samples", "Luau.Unity", packageVersion!);
            var declaredSamples = PackageStaticCommand.ValidateMaintainedSamples(package, "Referenced package");
            foreach (var sample in declaredSamples)
            {
                var displayName = sample.GetProperty("displayName").GetString()!;
                var relativePath = sample.GetProperty("path").GetString()!;
                PackageStaticCommand.Require(relativePath.StartsWith("Samples~/", StringComparison.Ordinal),
                    $"Unsafe package sample path: {relativePath}");
                var source = Path.GetFullPath(Path.Combine(contentRoot, relativePath));
                PackageStaticCommand.Require(source == contentRoot || PathSafety.IsStrictDescendant(source, contentRoot),
                    $"Package sample escapes content root: {relativePath}");
                FileSystem.CopyDirectory(source, Path.Combine(samplesRoot, displayName));
            }

            // Compile the reusable Core in the documented consumer shape. The complete
            // demo is compiled separately by unity-test, which imports every sample asset.
            var demoGame = Path.GetFullPath(Path.Combine(samplesRoot, "Full Luau Scripting Demo", "Demo Game"));
            var demoGameMeta = demoGame + ".meta";
            PackageStaticCommand.Require(PathSafety.IsStrictDescendant(demoGame, project),
                "Imported Full Demo game content escaped the disposable consumer project.");
            FileSystem.RequireDirectory(demoGame, "Imported Full Luau Scripting Demo game content");
            FileSystem.RequireFile(demoGameMeta, "Imported Full Luau Scripting Demo game metadata");
            Directory.Delete(demoGame, recursive: true);
            File.Delete(demoGameMeta);

            FileSystem.WriteUtf8(
                Path.Combine(settings, "ProjectVersion.txt"),
                $"m_EditorVersion: {editor.Version}\n");
            var manifest = new JsonObject
            {
                ["dependencies"] = new JsonObject
                {
                    ["com.qll.luau.unity"] = reference,
                    ["com.unity.inputsystem"] = "1.19.0",
                    ["com.unity.modules.audio"] = "1.0.0",
                    ["com.unity.modules.physics2d"] = "1.0.0",
                },
            };
            FileSystem.WriteUtf8(Path.Combine(packages, "manifest.json"), manifest.ToJsonString(JsonOptions.Indented) + "\n");

            if (OperatingSystem.IsLinux())
            {
                var pluginSource = options.Get("--linux-plugin") ??
                    repository.PathOf("native", "luau-host", "out", "install", "linux-x64", "libluau_host.so");
                if (!Path.IsPathRooted(pluginSource))
                {
                    throw new ToolingException("--linux-plugin must be absolute.");
                }

                var pluginDirectory = Path.Combine(project, "Assets", "Plugins", "linux-x64");
                Directory.CreateDirectory(pluginDirectory);
                FileSystem.WriteUtf8(pluginDirectory + ".meta", UnityHostCommand.LinuxPluginFolderMeta + "\n");
                FileSystem.CopyFile(pluginSource, Path.Combine(pluginDirectory, "libluau_host.so"));
                FileSystem.WriteUtf8(
                    Path.Combine(pluginDirectory, "libluau_host.so.meta"),
                    UnityHostCommand.LinuxPluginMeta + "\n");
            }

            var log = Path.Combine(logs, "package-consumer.log");
            Console.WriteLine($"Running generated package consumer at {project} with Unity {editor.Version} after deleting the demo game and retaining its reusable Core.");
            var result = await ProcessRunner.RunAsync(
                editor.Executable,
                [
                    "-batchmode", "-nographics", "-quit", "-projectPath", project,
                    "-executeMethod", "Luau.Unity.PackageConsumerProbe.RunConsumerProbe.Execute",
                    "-logFile", log,
                ],
                project,
                UnityHostCommand.UnityEnvironment(),
                TimeSpan.FromMinutes(options.GetInt("--unity-timeout-minutes", 20)));
            var logText = File.Exists(log) ? File.ReadAllText(log) : result.CombinedOutput;
            var compileFailure = Regex.IsMatch(
                logText,
                @"error CS\d+|Scripts have compiler errors|Compilation failed|Failed to resolve packages?|DllNotFoundException.*luau_host|EntryPointNotFoundException.*luau_host",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (result.ExitCode != 0 || compileFailure ||
                !logText.Contains(PassedMarker, StringComparison.Ordinal) ||
                logText.Contains(FailedMarker, StringComparison.Ordinal))
            {
                throw new ToolingException(
                    $"Generated Unity package consumer failed with exit code {result.ExitCode}. Diagnostics retained at {project}");
            }

            if (expectedCommit is not null)
            {
                ValidateLock(Path.Combine(packages, "packages-lock.json"), reference, expectedCommit);
            }

            succeeded = true;
            Console.WriteLine(OperatingSystem.IsLinux()
                ? "Generated minimal Unity package consumer compiled samples, loaded the Linux development host, and executed successfully."
                : "Generated minimal Unity package consumer compiled samples, loaded the platform host, and executed successfully.");
            return 0;
        }
        finally
        {
            if (succeeded)
            {
                PathSafety.DeleteDisposableDirectory(project, outputRoot, "unity-package-consumer-");
            }
            else
            {
                Console.Error.WriteLine($"Package consumer diagnostics retained at: {project}");
            }
        }
    }

    private static async Task<string> ResolvePackageContentAsync(
        RepositoryContext repository,
        string project,
        string reference,
        string? expectedCommit)
    {
        if (reference.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            PackageStaticCommand.Require(expectedCommit is null, "--expected-commit requires an exact git reference.");
            var path = reference[5..];
            path = Path.IsPathRooted(path) ? Path.GetFullPath(path) : repository.PathOf(path);
            FileSystem.RequireDirectory(path, "Referenced local package");
            return path;
        }

        var match = Regex.Match(
            reference,
            @"^(?<repository>.+?\.git)(?:\?path=(?<path>[^#]+))?#(?<revision>[A-Za-z0-9._/-]+)$",
            RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);
        if (!match.Success || match.Groups["revision"].Value.Contains("..", StringComparison.Ordinal))
        {
            throw new ToolingException("Package consumer requires a local file package or an exact git package reference.");
        }

        var materialization = Path.Combine(project, "PackageSource");
        Directory.CreateDirectory(materialization);
        await ProcessRunner.RequireAsync("git", ["init", "--quiet", materialization], repository.Root);
        await ProcessRunner.RequireAsync("git", ["-C", materialization, "remote", "add", "origin", match.Groups["repository"].Value], repository.Root);
        await ProcessRunner.RequireAsync("git", ["-C", materialization, "fetch", "--quiet", "--depth", "1", "origin", match.Groups["revision"].Value], repository.Root);
        await ProcessRunner.RequireAsync("git", ["-C", materialization, "checkout", "--quiet", "--detach", "FETCH_HEAD"], repository.Root);
        var commit = (await ProcessRunner.RequireAsync("git", ["-C", materialization, "rev-parse", "HEAD"], repository.Root, echo: false)).StandardOutput.Trim();
        if (expectedCommit is not null && !commit.Equals(expectedCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolingException($"Materialized commit {commit} does not match {expectedCommit}.");
        }

        var relativePath = Uri.UnescapeDataString(match.Groups["path"].Value).Trim('/', '\\');
        var content = string.IsNullOrEmpty(relativePath)
            ? materialization
            : Path.GetFullPath(Path.Combine(materialization, relativePath));
        PackageStaticCommand.Require(content == materialization || PathSafety.IsStrictDescendant(content, materialization),
            "Git package path escapes its materialization.");
        FileSystem.RequireDirectory(content, "Materialized package content");
        return content;
    }

    private static void ValidateLock(string path, string reference, string expectedCommit)
    {
        FileSystem.RequireFile(path, "Generated consumer package lock");
        var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ??
            throw new ToolingException("Generated consumer lock is invalid JSON.");
        var entry = root["dependencies"]?["com.qll.luau.unity"]?.AsObject() ??
            throw new ToolingException("Generated consumer lock is missing com.qll.luau.unity.");
        PackageStaticCommand.Require(
            entry["source"]?.GetValue<string>() == "git" &&
            entry["version"]?.GetValue<string>() == reference &&
            entry["hash"]?.GetValue<string>().Equals(expectedCommit, StringComparison.OrdinalIgnoreCase) == true,
            "Generated consumer lock did not resolve the requested exact git package.");
    }
}
