using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Luau.Tooling;

internal static class PackageStaticCommand
{
    internal static readonly (string DisplayName, string Path)[] MaintainedSamples =
    [
        ("Getting Started", "Samples~/Getting Started"),
        ("Full Luau Scripting Demo", "Samples~/Full Luau Scripting Demo"),
    ];

    private static readonly string[] ExpectedTopLevel =
    [
        "CHANGELOG.md", "CHANGELOG.md.meta", "Documentation~", "Editor", "Editor.meta",
        "LICENSE.md", "LICENSE.md.meta", "package.json", "package.json.meta", "README.md",
        "README.md.meta", "Runtime", "Runtime.meta", "Samples~", "Tests", "Tests.meta",
        "Third Party Notices.md", "Third Party Notices.md.meta",
    ];

    private static readonly string[] ExpectedDirectories =
    [
        "Documentation~", "Editor", "Runtime", "Runtime/Interop", "Runtime/Plugins",
        "Runtime/Plugins/android-arm64", "Runtime/Plugins/android-x64", "Runtime/Plugins/win-x64",
        "Samples~", "Samples~/Full Luau Scripting Demo", "Samples~/Full Luau Scripting Demo/Core",
        "Samples~/Full Luau Scripting Demo/Demo Game", "Samples~/Full Luau Scripting Demo/Demo Game/Art",
        "Samples~/Full Luau Scripting Demo/Demo Game/Audio", "Samples~/Full Luau Scripting Demo/Demo Game/Prefabs",
        "Samples~/Full Luau Scripting Demo/Demo Game/Scenes", "Samples~/Full Luau Scripting Demo/Demo Game/Scripts",
        "Samples~/Getting Started", "Tests", "Tests/EditMode",
    ];

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".asmdef", ".cs", ".dll", ".json", ".luau", ".md", ".meta", ".png", ".prefab", ".so", ".unity", ".xml",
    };

    private static readonly HashSet<string> ArtifactExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".so", ".xml", ".pdb", ".mdb", ".a", ".aar", ".lib", ".exe", ".jar",
    };

    public static Task<int> RunAsync(RepositoryContext repository, CommandLine options)
    {
        Validate(repository);
        Console.WriteLine("Unity package static validation passed.");
        return Task.FromResult(0);
    }

    public static void Validate(RepositoryContext repository)
    {
        var packageRoot = repository.PathOf("Luau.Unity");
        var policy = ReleasePolicy.Load(repository);
        using var packageDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(packageRoot, "package.json")));
        var package = packageDocument.RootElement;
        Require(package.GetProperty("name").GetString() == policy.PackageId, "Package ID does not match release policy.");
        Require(package.GetProperty("version").GetString() == policy.PackageVersion, "Package version does not match release policy.");
        Require(package.GetProperty("unity").GetString() == "6000.3", "Package Unity floor must be the 6000.3 stream.");
        Require(package.GetProperty("unityRelease").GetString() == "0f1", "Package Unity release floor must be 0f1.");
        ValidateMaintainedSamples(package, "Package");
        Require(!package.TryGetProperty("dependencies", out var dependencies) ||
                dependencies.ValueKind == JsonValueKind.Object && !dependencies.EnumerateObject().Any(),
            "The shipping package must not rely on development-project dependencies.");
        Require(policy.SchemaVersion == 1 && policy.ArchiveFormat == "ustar+gzip-stored-v1", "Unsupported release policy schema or archive format.");

        AssertSequence(
            Directory.EnumerateFileSystemEntries(packageRoot).Select(Path.GetFileName).Order(StringComparer.Ordinal),
            ExpectedTopLevel.Order(StringComparer.Ordinal),
            "Package top-level allowlist");
        AssertSequence(
            Directory.EnumerateDirectories(packageRoot, "*", SearchOption.AllDirectories)
                .Select(path => Relative(packageRoot, path)).Order(StringComparer.Ordinal),
            ExpectedDirectories.Order(StringComparer.Ordinal),
            "Package directory allowlist");

        var forbiddenDirectories = new HashSet<string>(
            ["Assets", "Packages", "ProjectSettings", "Library", "Temp", "Logs", "Builds", "UserSettings", "bin", "obj", "Verification", "Sandbox", "URP"],
            StringComparer.OrdinalIgnoreCase);
        var guidOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFileSystemEntries(packageRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Relative(packageRoot, path);
            var attributes = File.GetAttributes(path);
            Require(!attributes.HasFlag(FileAttributes.ReparsePoint), $"Symbolic links/reparse points cannot ship: {relative}");
            if (Directory.Exists(path))
            {
                Require(!forbiddenDirectories.Contains(Path.GetFileName(path)), $"Generated/project directory ships: {relative}");
                if (relative is not ("Documentation~" or "Samples~"))
                {
                    Require(File.Exists(path + ".meta"), $"Package directory is missing Unity metadata: {relative}");
                }
                continue;
            }

            Require(AllowedExtensions.Contains(Path.GetExtension(path)), $"Unexpected package file type: {relative}");
            Require(!Regex.IsMatch(relative, "(PlayerSmoke|IntegrationSmoke|ConsumerProbe|Verification)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                $"Integration/consumer smoke content ships: {relative}");
            if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                Require(File.Exists(path[..^5]) || Directory.Exists(path[..^5]), $"Orphan Unity metadata: {relative}");
                var matches = Regex.Matches(File.ReadAllText(path), @"(?m)^guid:\s*([0-9a-fA-F]{32})\s*$");
                Require(matches.Count == 1, $"Unity metadata must contain one GUID: {relative}");
                var guid = matches[0].Groups[1].Value.ToLowerInvariant();
                Require(!guidOwners.TryGetValue(guid, out var owner), $"Duplicate Unity GUID {guid} in {relative} and {owner}.");
                guidOwners.Add(guid, relative);
            }
            else if (relative != "package.json")
            {
                Require(File.Exists(path + ".meta"), $"Package asset is missing Unity metadata: {relative}");
            }
        }

        var policyPaths = policy.Artifacts.Select(static artifact => artifact.Path).ToArray();
        var actualArtifacts = Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories)
            .Where(path => ArtifactExtensions.Contains(Path.GetExtension(path)))
            .Select(path => Relative(packageRoot, path))
            .Order(StringComparer.Ordinal);
        AssertSequence(actualArtifacts, policyPaths.Order(StringComparer.Ordinal), "Managed/native/XML artifact allowlist");
        foreach (var artifact in policy.Artifacts)
        {
            var path = Path.Combine(packageRoot, artifact.Path);
            FileSystem.RequireFile(path, "Required package artifact");
            Require(new FileInfo(path).Length <= artifact.MaximumBytes,
                $"Package artifact '{artifact.Path}' exceeds its {artifact.MaximumBytes}-byte budget.");
        }

        Require(!Directory.Exists(Path.Combine(packageRoot, "Runtime", "Plugins", "linux-x64")),
            "Linux development plugins must never be committed to the shipping package.");

        foreach (var relative in new[]
        {
            "Samples~/Getting Started/GettingStartedSample.cs",
            "Samples~/Getting Started/GettingStartedTarget.cs",
            "Samples~/Getting Started/GettingStarted.luau",
            "Samples~/Getting Started/README.md",
            "Samples~/Full Luau Scripting Demo/README.md",
            "Samples~/Full Luau Scripting Demo/Core/FullLuauScriptingDemo.Core.asmdef",
            "Samples~/Full Luau Scripting Demo/Core/LuauBehaviourRuntime.cs",
            "Samples~/Full Luau Scripting Demo/Core/LuauBehaviour.cs",
            "Samples~/Full Luau Scripting Demo/Core/LuauUnityCapabilities.cs",
            "Samples~/Full Luau Scripting Demo/Core/LuauUnityTableValues.cs",
            "Samples~/Full Luau Scripting Demo/Core/LuauQuaternionLibrary.cs",
            "Samples~/Full Luau Scripting Demo/Core/LuauInputLibrary.cs",
            "Samples~/Full Luau Scripting Demo/Core/README.md",
            "Samples~/Full Luau Scripting Demo/Demo Game/Prefabs/Bird.prefab",
            "Samples~/Full Luau Scripting Demo/Demo Game/Prefabs/PipePair.prefab",
            "Samples~/Full Luau Scripting Demo/Demo Game/Scenes/FlappyBird.unity",
            "Samples~/Full Luau Scripting Demo/Demo Game/Scripts/GameController.luau",
            "Samples~/Full Luau Scripting Demo/Demo Game/Scripts/PlayerController.luau",
            "Samples~/Full Luau Scripting Demo/Demo Game/Scripts/PipeController.luau",
            "Samples~/Full Luau Scripting Demo/Demo Game/Art/Bird.png",
            "Samples~/Full Luau Scripting Demo/Demo Game/Art/Pipe.png",
            "Samples~/Full Luau Scripting Demo/Demo Game/Art/Ground.png",
            "Samples~/Full Luau Scripting Demo/Demo Game/Audio/README.md",
            "Samples~/Full Luau Scripting Demo/Demo Game/README.md",
        })
        {
            FileSystem.RequireFile(Path.Combine(packageRoot, relative), "Required maintained sample file");
        }

        var demoGame = Path.Combine(packageRoot, "Samples~", "Full Luau Scripting Demo", "Demo Game");
        Require(!Directory.EnumerateFiles(demoGame, "*.cs", SearchOption.AllDirectories).Any(),
            "Full Luau Scripting Demo gameplay must remain C#-free.");
        using var demoAssemblyDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            packageRoot, "Samples~", "Full Luau Scripting Demo", "Core", "FullLuauScriptingDemo.Core.asmdef")));
        var demoAssembly = demoAssemblyDocument.RootElement;
        Require(demoAssembly.GetProperty("name").GetString() == "Luau.Unity.Samples.FullLuauScriptingDemo.Core",
            "Full Demo Core assembly name changed unexpectedly.");
        Require(demoAssembly.GetProperty("rootNamespace").GetString() == "Luau.Unity.Samples.FullLuauScriptingDemo",
            "Full Demo Core root namespace changed unexpectedly.");
        AssertSequence(
            demoAssembly.GetProperty("references").EnumerateArray().Select(static value => value.GetString()),
            ["GUID:c727d2ef8dd2e4846ab81fbe6ca1f508", "Unity.InputSystem"],
            "Full Demo Core assembly references");

        var windowsMeta = File.ReadAllText(Path.Combine(packageRoot, "Runtime", "Plugins", "win-x64", "luau_host.dll.meta"));
        Require(windowsMeta.Contains("OS: Windows", StringComparison.Ordinal) && windowsMeta.Contains("Exclude Linux64: 1", StringComparison.Ordinal),
            "Windows plugin importer must remain Windows-only.");
        foreach (var platform in new[] { "android-arm64", "android-x64" })
        {
            var meta = File.ReadAllText(Path.Combine(packageRoot, "Runtime", "Plugins", platform, "libluau_host.so.meta"));
            Require(meta.Contains("Android:\n      enabled: 1", StringComparison.Ordinal) && meta.Contains("Exclude Linux64: 1", StringComparison.Ordinal),
                $"{platform} plugin importer must remain Android-only.");
        }

        var xml = XDocument.Load(Path.Combine(packageRoot, "Runtime", "Luau.xml"));
        Require(xml.Root?.Element("assembly")?.Element("name")?.Value == "Luau", "Runtime/Luau.xml does not describe Luau.");
        var sourceProjectVersion = File.ReadAllText(repository.PathOf("tests", "Luau.Unity.Integration", "ProjectSettings", "ProjectVersion.txt"));
        Require(HasCanonicalIntegrationVersion(sourceProjectVersion),
            "The canonical integration project must pin one exact Unity 6000.3 editor.");
    }

    internal static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    internal static bool HasCanonicalIntegrationVersion(string projectVersion) =>
        Regex.IsMatch(projectVersion, @"(?m)^m_EditorVersion: 6000\.3\.\d+f\d+\r?$");

    internal static JsonElement[] ValidateMaintainedSamples(JsonElement package, string description)
    {
        Require(package.TryGetProperty("samples", out var samples) && samples.ValueKind == JsonValueKind.Array,
            $"{description} is missing its maintained sample declarations.");
        var declared = samples.EnumerateArray().ToArray();
        Require(declared.Length == MaintainedSamples.Length,
            $"{description} must declare exactly the two maintained samples; found {declared.Length}.");
        for (var index = 0; index < MaintainedSamples.Length; index++)
        {
            var expected = MaintainedSamples[index];
            var actualName = declared[index].GetProperty("displayName").GetString();
            var actualPath = declared[index].GetProperty("path").GetString();
            Require(actualName == expected.DisplayName && actualPath == expected.Path,
                $"{description} sample mismatch at index {index}. Expected '{expected.DisplayName}' at '{expected.Path}'; " +
                $"found '{actualName}' at '{actualPath}'.");
        }

        return declared;
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new ToolingException(message);
        }
    }

    internal static void AssertSequence(IEnumerable<string?> actual, IEnumerable<string?> expected, string description)
    {
        var actualArray = actual.ToArray();
        var expectedArray = expected.ToArray();
        if (!actualArray.SequenceEqual(expectedArray, StringComparer.Ordinal))
        {
            throw new ToolingException(
                $"{description} mismatch.\nExpected: {string.Join(", ", expectedArray)}\nActual: {string.Join(", ", actualArray)}");
        }
    }
}
