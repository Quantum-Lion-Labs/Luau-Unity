using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Luau.Tooling;

internal static class PackageReleaseCommand
{
    public static async Task<int> RunAsync(RepositoryContext repository, CommandLine options)
    {
        PackageStaticCommand.Validate(repository);
        PackageStaticCommand.Require(
            DeterministicPackageArchive.Crc32("123456789"u8) == 0xcbf43926u,
            "Deterministic gzip CRC32 regression probe failed.");

        var policy = ReleasePolicy.Load(repository);
        var packageRoot = repository.PathOf("Luau.Unity");
        var commit = (await ProcessRunner.RequireAsync(
            "git", ["rev-parse", "HEAD"], repository.Root, echo: false)).StandardOutput.Trim().ToLowerInvariant();
        PackageStaticCommand.Require(
            System.Text.RegularExpressions.Regex.IsMatch(commit, "^[0-9a-f]{40}$"),
            "Unable to resolve package source commit.");

        var tag = options.Get("--tag");
        PackageStaticCommand.Require(!options.Has("--skip-unity-consumer") || tag is not null,
            "--skip-unity-consumer requires --tag so exact-tag consumer validation cannot be bypassed.");
        if (tag is not null)
        {
            await ValidateTagAsync(repository, policy, tag, commit, packageRoot);
        }

        var relativeFiles = Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories)
            .Select(path => PackageStaticCommand.Relative(packageRoot, path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (tag is not null)
        {
            var tree = await ProcessRunner.RequireAsync(
                "git", ["ls-tree", "-r", "--name-only", tag, "--", "Luau.Unity"], repository.Root, echo: false);
            var taggedFiles = tree.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(static path => path["Luau.Unity/".Length..]).Order(StringComparer.Ordinal);
            PackageStaticCommand.AssertSequence(relativeFiles, taggedFiles, "Exact-tag package file inventory");
            foreach (var relativeFile in relativeFiles)
            {
                var taggedObject = (await ProcessRunner.RequireAsync(
                    "git", ["rev-parse", $"{tag}:Luau.Unity/{relativeFile}"], repository.Root, echo: false)).StandardOutput.Trim();
                var workingObject = (await ProcessRunner.RequireAsync(
                    "git", ["hash-object", "--no-filters", "--", Path.Combine(packageRoot, relativeFile)], repository.Root, echo: false)).StandardOutput.Trim();
                PackageStaticCommand.Require(taggedObject.Equals(workingObject, StringComparison.OrdinalIgnoreCase),
                    $"Working package content differs from exact release tag {tag}: {relativeFile}");
            }
        }

        var archive = DeterministicPackageArchive.Create(packageRoot, relativeFiles);
        var second = DeterministicPackageArchive.Create(packageRoot, relativeFiles);
        PackageStaticCommand.Require(archive.AsSpan().SequenceEqual(second), "Package archive is not deterministic.");
        PackageStaticCommand.Require(archive.Length <= policy.MaximumArchiveBytes,
            $"Package archive is {archive.Length} bytes; budget is {policy.MaximumArchiveBytes}.");
        var archiveHash = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();

        var files = new JsonArray();
        foreach (var relativePath in relativeFiles)
        {
            var path = Path.Combine(packageRoot, relativePath);
            files.Add(new JsonObject
            {
                ["path"] = relativePath,
                ["bytes"] = new FileInfo(path).Length,
                ["sha256"] = Hashing.FileSha256(path),
            });
        }

        var manifest = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["packageId"] = policy.PackageId,
            ["packageVersion"] = policy.PackageVersion,
            ["sourceCommit"] = commit,
            ["releasePolicySha256"] = Hashing.FileSha256(repository.PathOf("tools", "UnityPackageReleasePolicy.json")),
            ["archive"] = new JsonObject
            {
                ["name"] = $"{policy.PackageId}-{policy.PackageVersion}.tgz",
                ["format"] = policy.ArchiveFormat,
                ["bytes"] = archive.Length,
                ["sha256"] = archiveHash,
            },
            ["files"] = files,
        };
        var manifestText = manifest.ToJsonString(JsonOptions.Compact) + "\n";

        var output = options.Get("--output");
        if (output is not null)
        {
            output = Path.IsPathRooted(output) ? Path.GetFullPath(output) : repository.PathOf(output);
            PackageStaticCommand.Require(!PathSafety.IsStrictDescendant(output, packageRoot),
                "Release archive must be outside the package tree.");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            if (options.Has("--check"))
            {
                FileSystem.RequireFile(output, "Release archive");
                FileSystem.RequireFile(output + ".manifest.json", "Release manifest");
                PackageStaticCommand.Require(Hashing.FileSha256(output) == archiveHash, $"Release archive is stale: {output}");
                PackageStaticCommand.Require(File.ReadAllText(output + ".manifest.json") == manifestText,
                    $"Release manifest is stale: {output}.manifest.json");
            }
            else
            {
                File.WriteAllBytes(output, archive);
                FileSystem.WriteUtf8(output + ".manifest.json", manifestText);
            }
        }
        else if (options.Has("--check"))
        {
            throw new ToolingException("--check requires --output.");
        }

        if (tag is not null && !options.Has("--skip-unity-consumer"))
        {
            var consumerArguments = new List<string>
            {
                "--package", $"https://github.com/Quantum-Lion-Labs/Luau-Unity.git?path=Luau.Unity#{tag}",
                "--expected-commit", commit,
                "--unity-timeout-minutes", options.GetInt("--consumer-timeout-minutes", 20).ToString(),
            };
            CopyOption(options, consumerArguments, "--unity");
            CopyOption(options, consumerArguments, "--unity-version");
            CopyOption(options, consumerArguments, "--consumer-output", "--output");
            await PackageConsumerCommand.RunAsync(repository, new CommandLine(consumerArguments));
        }

        Console.WriteLine($"Unity package release validation passed. Files: {relativeFiles.Length}; archive: {archive.Length} bytes; SHA256: {archiveHash}");
        return 0;
    }

    private static void CopyOption(CommandLine source, List<string> destination, string sourceName, string? destinationName = null)
    {
        if (source.Get(sourceName) is { } value)
        {
            destination.Add(destinationName ?? sourceName);
            destination.Add(value);
        }
    }

    private static async Task ValidateTagAsync(
        RepositoryContext repository,
        ReleasePolicy policy,
        string tag,
        string commit,
        string packageRoot)
    {
        PackageStaticCommand.Require(tag == policy.ReleaseTag, $"Tag {tag} does not match reviewed tag {policy.ReleaseTag}.");
        var tagCommit = (await ProcessRunner.RequireAsync(
            "git", ["rev-parse", "--verify", $"refs/tags/{tag}^{{commit}}"], repository.Root, echo: false)).StandardOutput.Trim();
        PackageStaticCommand.Require(tagCommit.Equals(commit, StringComparison.OrdinalIgnoreCase),
            $"Current commit is not exact release tag {tag}.");
        var status = await ProcessRunner.RequireAsync(
            "git", ["status", "--porcelain", "--untracked-files=all"], repository.Root, echo: false);
        PackageStaticCommand.Require(string.IsNullOrWhiteSpace(status.StandardOutput), "Working tree must be clean for exact-tag validation.");
        var readme = File.ReadAllText(Path.Combine(packageRoot, "README.md"));
        PackageStaticCommand.Require(
            readme.Contains($"https://github.com/Quantum-Lion-Labs/Luau-Unity.git?path=Luau.Unity#{tag}", StringComparison.Ordinal),
            "Package README does not contain the exact tagged install URL.");
    }
}
