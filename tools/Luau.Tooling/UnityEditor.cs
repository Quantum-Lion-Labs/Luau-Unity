using System.Text.RegularExpressions;

namespace Luau.Tooling;

internal sealed record UnityEditor(string Executable, UnityVersion Version)
{
    private static readonly Regex VersionInPath = new(
        @"(?<!\d)(?<version>\d+\.\d+\.\d+[a-z]+\d+)(?!\d)",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    public static UnityEditor Resolve(RepositoryContext repository, CommandLine options)
    {
        var explicitPath = options.Get("--unity") ??
            Environment.GetEnvironmentVariable("UNITY_EXE") ??
            Environment.GetEnvironmentVariable("UNITY_PATH");
        var requestedVersionText = options.Get("--unity-version");
        var requestedVersion = requestedVersionText is null ? (UnityVersion?)null : UnityVersion.Parse(requestedVersionText);

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            candidates.Add(explicitPath);
        }

        var discoveryVersions = new List<string>();
        if (requestedVersion.HasValue)
        {
            discoveryVersions.Add(requestedVersion.Value.ToString());
        }
        else if (string.IsNullOrWhiteSpace(explicitPath))
        {
            var projectVersion = File.ReadAllText(repository.PathOf(
                "tests", "Luau.Unity.Integration", "ProjectSettings", "ProjectVersion.txt"));
            var match = Regex.Match(projectVersion, @"(?m)^m_EditorVersion:\s*(\S+)");
            if (match.Success)
            {
                discoveryVersions.Add(match.Groups[1].Value);
            }

            AddInstalledVersions(discoveryVersions, "/work/unity/editors");
            AddInstalledVersions(discoveryVersions, Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Unity", "Hub", "Editor"));
            if (OperatingSystem.IsWindows())
            {
                AddInstalledVersions(discoveryVersions, Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Unity", "Hub", "Editor"));
            }
        }

        foreach (var versionText in discoveryVersions.Distinct(StringComparer.Ordinal))
        {
            candidates.Add(Path.Combine("/work/unity/editors", versionText, "Editor", "Unity"));
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Unity", "Hub", "Editor", versionText, "Editor", OperatingSystem.IsWindows() ? "Unity.exe" : "Unity"));
            if (OperatingSystem.IsWindows())
            {
                candidates.Add(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Unity", "Hub", "Editor", versionText, "Editor", "Unity.exe"));
            }
        }

        var executable = candidates
            .Select(path => Path.GetFullPath(path, repository.Root))
            .FirstOrDefault(File.Exists);
        if (executable is null)
        {
            throw new ToolingException(
                "Unity was not found. Pass --unity with an editor executable and --unity-version with its version.");
        }

        UnityVersion version;
        if (requestedVersion.HasValue)
        {
            version = requestedVersion.Value;
        }
        else
        {
            var match = VersionInPath.Match(executable);
            if (!match.Success)
            {
                throw new ToolingException("Unable to infer the Unity version from the editor path. Pass --unity-version.");
            }

            version = UnityVersion.Parse(match.Groups["version"].Value);
        }

        if (!version.IsInStream(6000, 3))
        {
            throw new ToolingException($"Unity {version} is outside the supported 6000.3 editor stream.");
        }

        return new UnityEditor(executable, version);
    }

    private static void AddInstalledVersions(List<string> versions, string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        versions.AddRange(Directory.EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .Where(static value => value is not null && value.StartsWith("6000.3.", StringComparison.Ordinal))
            .OrderDescending(StringComparer.Ordinal)!);
    }
}
