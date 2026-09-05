using System.Text.Json;

namespace Luau.Tooling;

internal static class UnityProjectStaging
{
    private const string PackageName = "com.qll.luau.unity";

    private const string LinuxPluginMeta = """
        fileFormatVersion: 2
        guid: a26c7812bbd64b52a8cfe2c339e7e481
        PluginImporter:
          externalObjects: {}
          serializedVersion: 3
          iconMap: {}
          executionOrder: {}
          defineConstraints: []
          isPreloaded: 0
          isOverridable: 0
          isExplicitlyReferenced: 0
          validateReferences: 1
          platformData:
            Android:
              enabled: 0
              settings:
                AndroidLibraryDependee: UnityLibrary
                AndroidSharedLibraryType: Executable
                CPU: ARM64
            Any:
              enabled: 0
              settings:
                Exclude Android: 1
                Exclude Editor: 0
                Exclude Linux64: 0
                Exclude OSXUniversal: 1
                Exclude WebGL: 1
                Exclude Win: 1
                Exclude Win64: 1
                Exclude iOS: 1
            Editor:
              enabled: 1
              settings:
                CPU: x86_64
                DefaultValueInitialized: true
                OS: Linux
            Linux64:
              enabled: 1
              settings:
                CPU: x86_64
            OSXUniversal:
              enabled: 0
              settings:
                CPU: None
            Win:
              enabled: 0
              settings:
                CPU: None
            Win64:
              enabled: 0
              settings:
                CPU: None
            iOS:
              enabled: 0
              settings:
                AddToEmbeddedBinaries: false
                CPU: AnyCPU
                CompileFlags:
                FrameworkDependencies:
          userData:
          assetBundleName:
          assetBundleVariant:
        """;

    private const string LinuxPluginFolderMeta = """
        fileFormatVersion: 2
        guid: 63d998da7ae84a97ba2b59484bb660f4
        folderAsset: yes
        DefaultImporter:
          externalObjects: {}
          userData:
          assetBundleName:
          assetBundleVariant:
        """;

    public static string ImportSamples(string packageRoot, string project)
    {
        using var packageDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(packageRoot, "package.json")));
        var package = packageDocument.RootElement;
        PackageStaticCommand.Require(package.GetProperty("name").GetString() == PackageName,
            $"Package has an unexpected identity: {package.GetProperty("name").GetString()}");
        var version = package.GetProperty("version").GetString();
        PackageStaticCommand.Require(version is not null && System.Text.RegularExpressions.Regex.IsMatch(
                version, @"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            $"Package has an unsafe or invalid version: {version}");
        var declaredSamples = PackageStaticCommand.ValidateMaintainedSamples(package, "Package");

        var assets = Path.GetFullPath(Path.Combine(project, "Assets"));
        FileSystem.RequireDirectory(assets, "Disposable Unity Assets directory");
        var importRoot = Path.GetFullPath(Path.Combine(assets, "Samples", "Luau.Unity", version!));
        PackageStaticCommand.Require(PathSafety.IsStrictDescendant(importRoot, assets),
            "Disposable sample import root escaped the Assets tree.");
        PackageStaticCommand.Require(!Directory.Exists(importRoot),
            $"Disposable sample import root already exists: {importRoot}");
        Directory.CreateDirectory(importRoot);

        foreach (var sample in declaredSamples)
        {
            var relativePath = sample.GetProperty("path").GetString()!;
            var displayName = sample.GetProperty("displayName").GetString()!;
            var source = Path.GetFullPath(Path.Combine(packageRoot, relativePath));
            var destination = Path.GetFullPath(Path.Combine(importRoot, displayName));
            PackageStaticCommand.Require(PathSafety.IsStrictDescendant(source, packageRoot),
                $"Declared package sample escapes the package: {relativePath}");
            PackageStaticCommand.Require(PathSafety.IsStrictDescendant(destination, importRoot),
                $"Declared package sample destination escapes the Assets tree: {displayName}");
            FileSystem.RequireDirectory(source, $"Declared package sample '{displayName}'");
            FileSystem.CopyDirectory(source, destination);
        }

        Console.WriteLine($"Imported {declaredSamples.Length} declared package samples into {importRoot}");
        return importRoot;
    }

    public static void StageLinuxPlugin(string directory, string source)
    {
        FileSystem.RequireFile(source, "Linux development host plugin");
        Directory.CreateDirectory(directory);
        FileSystem.WriteUtf8(directory + ".meta", LinuxPluginFolderMeta + "\n");
        var destination = Path.Combine(directory, "libluau_host.so");
        FileSystem.CopyFile(source, destination);
        FileSystem.WriteUtf8(destination + ".meta", LinuxPluginMeta + "\n");
        if (!Hashing.FileSha256(source).Equals(Hashing.FileSha256(destination), StringComparison.Ordinal))
        {
            throw new ToolingException("Staged Linux plugin failed SHA256 verification.");
        }

        Console.WriteLine($"Staged disposable Linux plugin: {destination}");
    }
}
