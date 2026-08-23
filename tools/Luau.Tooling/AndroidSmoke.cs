using System.Text.RegularExpressions;

namespace Luau.Tooling;

internal static class AndroidSmoke
{
    private sealed record Device(string Serial, string State, string Abi, string Model, string Manufacturer, string Qemu);

    public static async Task RunAsync(
        RepositoryContext repository,
        UnityEditor editor,
        string? explicitAdb,
        string? explicitSerial,
        string targetKind,
        string apk,
        string packageName,
        string log,
        int timeoutSeconds)
    {
        var adb = ResolveAdb(repository, editor, explicitAdb);
        var device = await SelectDeviceAsync(adb, explicitSerial, targetKind, repository.Root);
        Console.WriteLine($"Selected Android target: {device.Serial} model={device.Model} abi={device.Abi}");
        var installed = false;
        try
        {
            await ProcessRunner.RequireAsync(adb, ["-s", device.Serial, "install", "-r", "-t", "-d", apk], repository.Root);
            installed = true;
            await ProcessRunner.RequireAsync(adb, ["-s", device.Serial, "shell", "am", "force-stop", packageName], repository.Root);
            var boundary = "LUAU_HOST_BOUNDARY_" + Guid.NewGuid().ToString("N");
            await ProcessRunner.RequireAsync(adb, ["-s", device.Serial, "shell", "log", "-t", "LuauHost", boundary], repository.Root);
            var launch = await ProcessRunner.RequireAsync(
                adb,
                ["-s", device.Serial, "shell", "monkey", "-p", packageName, "-c", "android.intent.category.LAUNCHER", "1"],
                repository.Root,
                echo: false);
            if (!launch.CombinedOutput.Contains("Events injected: 1", StringComparison.Ordinal))
            {
                throw new ToolingException($"Android smoke launch did not inject a launcher event on {device.Serial}.");
            }

            await WaitForMarkerAsync(adb, device.Serial, boundary, log, timeoutSeconds, repository.Root);
            Console.WriteLine($"Android {targetKind} IL2CPP smoke passed.");
        }
        finally
        {
            if (installed)
            {
                await ProcessRunner.RunAsync(adb, ["-s", device.Serial, "shell", "am", "force-stop", packageName], repository.Root, echo: false);
                await ProcessRunner.RunAsync(adb, ["-s", device.Serial, "uninstall", packageName], repository.Root, echo: false);
            }
        }
    }

    private static string ResolveAdb(RepositoryContext repository, UnityEditor editor, string? explicitPath)
    {
        var executable = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            candidates.Add(explicitPath);
        }
        candidates.Add(Path.Combine(
            Path.GetDirectoryName(editor.Executable)!, "Data", "PlaybackEngines", "AndroidPlayer", "SDK", "platform-tools", executable));
        foreach (var variable in new[] { "ANDROID_SDK_ROOT", "ANDROID_HOME" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } sdk)
            {
                candidates.Add(Path.Combine(sdk, "platform-tools", executable));
            }
        }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", executable));
        if (Environment.GetEnvironmentVariable("PATH") is { } path)
        {
            candidates.AddRange(path.Split(Path.PathSeparator).Select(directory => Path.Combine(directory, executable)));
        }

        foreach (var candidate in candidates)
        {
            var resolved = Path.IsPathRooted(candidate) ? Path.GetFullPath(candidate) : repository.PathOf(candidate);
            if (Directory.Exists(resolved))
            {
                resolved = Path.Combine(resolved, executable);
            }
            if (File.Exists(resolved))
            {
                return resolved;
            }
        }
        throw new ToolingException("adb was not found. Install Unity Android support or pass --adb.");
    }

    private static async Task<Device> SelectDeviceAsync(
        string adb,
        string? explicitSerial,
        string targetKind,
        string root)
    {
        if (!string.IsNullOrWhiteSpace(explicitSerial) && !Regex.IsMatch(explicitSerial, @"^[A-Za-z0-9._:-]+$"))
        {
            throw new ToolingException($"ADB serial contains unsupported characters: {explicitSerial}");
        }

        var inventory = await ProcessRunner.RequireAsync(adb, ["devices", "-l"], root, echo: false);
        var devices = new List<(string Serial, string State)>();
        foreach (var line in inventory.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Regex.Match(line.Trim(), @"^(\S+)\s+(\S+)(?:\s+.*)?$");
            if (match.Success && !line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
            {
                devices.Add((match.Groups[1].Value, match.Groups[2].Value));
            }
        }

        if (!string.IsNullOrWhiteSpace(explicitSerial))
        {
            devices = devices.Where(device => device.Serial == explicitSerial).ToList();
            if (devices.Count != 1 || devices[0].State != "device")
            {
                throw new ToolingException($"ADB serial {explicitSerial} was not found in online state.");
            }
        }
        else
        {
            devices = devices.Where(device => device.State == "device").ToList();
        }

        var details = new List<Device>();
        foreach (var device in devices)
        {
            details.Add(new Device(
                device.Serial,
                device.State,
                await PropertyAsync(adb, device.Serial, "ro.product.cpu.abi", root),
                await PropertyAsync(adb, device.Serial, "ro.product.model", root),
                await PropertyAsync(adb, device.Serial, "ro.product.manufacturer", root),
                await PropertyAsync(adb, device.Serial, "ro.kernel.qemu", root)));
        }

        var eligible = targetKind switch
        {
            "quest-arm64" => details.Where(device =>
                device.Abi.StartsWith("arm64", StringComparison.Ordinal) &&
                (device.Model.Contains("Quest", StringComparison.OrdinalIgnoreCase) ||
                 Regex.IsMatch(device.Manufacturer, "^(Oculus|Meta)$", RegexOptions.IgnoreCase))).ToArray(),
            "emulator-x64" => details.Where(device =>
                device.Abi == "x86_64" &&
                (device.Serial.StartsWith("emulator-", StringComparison.Ordinal) || device.Qemu == "1")).ToArray(),
            _ => throw new ToolingException($"Unknown Android smoke target kind: {targetKind}"),
        };
        if (eligible.Length != 1)
        {
            var summary = details.Count == 0 ? "(none)" : string.Join("; ", details.Select(device =>
                $"{device.Serial} state={device.State} abi={device.Abi} model={device.Model}"));
            throw new ToolingException($"Expected exactly one eligible {targetKind} Android target; found {eligible.Length}. Candidates: {summary}");
        }
        return eligible[0];
    }

    private static async Task<string> PropertyAsync(string adb, string serial, string property, string root) =>
        (await ProcessRunner.RequireAsync(
            adb, ["-s", serial, "shell", "getprop", property], root, echo: false)).StandardOutput.Trim();

    private static async Task WaitForMarkerAsync(
        string adb,
        string serial,
        string boundary,
        string log,
        int timeoutSeconds,
        string root)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var result = await ProcessRunner.RequireAsync(
                adb,
                ["-s", serial, "logcat", "-d", "-v", "threadtime", "LuauHost:I", "Unity:I", "AndroidRuntime:E", "*:S"],
                root,
                echo: false);
            var content = result.StandardOutput;
            var boundaryIndex = content.LastIndexOf(boundary, StringComparison.Ordinal);
            if (boundaryIndex >= 0)
            {
                var scoped = content[(boundaryIndex + boundary.Length)..];
                FileSystem.WriteUtf8(log, scoped);
                if (scoped.Contains(UnityHostCommand.FailedMarkerForAndroid, StringComparison.Ordinal))
                {
                    throw new ToolingException($"Android smoke emitted a failure marker. See {log}");
                }
                if (scoped.Contains(UnityHostCommand.PassedMarkerForAndroid, StringComparison.Ordinal))
                {
                    return;
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        throw new ToolingException($"Android smoke timed out after {timeoutSeconds} seconds. See {log}");
    }
}
