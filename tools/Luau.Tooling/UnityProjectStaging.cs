using System.Text.Json;

namespace Luau.Tooling;

internal static class UnityProjectStaging
{
    private const string PackageName = "com.qll.luau.unity";

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

    /// <summary>
    /// Replaces a plugin binary inside a disposable copy of the package while
    /// keeping the package's reviewed importer metadata.
    /// </summary>
    public static void OverridePackagePlugin(string stagedPackage, string platform, string fileName, string source)
    {
        PackageStaticCommand.Require(Path.IsPathRooted(source), $"The {platform} plugin override must be an absolute path.");
        FileSystem.RequireFile(source, $"{platform} plugin override");
        var destination = Path.Combine(stagedPackage, "Runtime", "Plugins", platform, fileName);
        FileSystem.RequireFile(destination + ".meta", $"Reviewed {platform} Unity importer metadata");
        if (!Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.Ordinal))
        {
            FileSystem.CopyFile(source, destination);
        }
        if (!Hashing.FileSha256(source).Equals(Hashing.FileSha256(destination), StringComparison.Ordinal))
        {
            throw new ToolingException($"Staged {platform} plugin failed SHA256 verification.");
        }

        Console.WriteLine($"Staged disposable {platform} plugin: {destination}");
    }
}
