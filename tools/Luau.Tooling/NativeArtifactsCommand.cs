using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Luau.Tooling;

internal static class NativeArtifactsCommand
{
    private sealed record Artifact(
        string Platform,
        string Preset,
        string Source,
        string Destination,
        bool Strip,
        int AndroidApi);

    private static readonly IReadOnlyDictionary<string, Artifact> Artifacts =
        new Dictionary<string, Artifact>(StringComparer.Ordinal)
        {
            ["win-x64"] = new("win-x64", "windows-x64", "native/luau-host/out/install/windows-x64/luau_host.dll", "win-x64/luau_host.dll", false, 0),
            ["android-arm64"] = new("android-arm64", "android-arm64", "native/luau-host/out/install/android-arm64/libluau_host.so", "android-arm64/libluau_host.so", true, 26),
            ["android-x64"] = new("android-x64", "android-x64", "native/luau-host/out/install/android-x64/libluau_host.so", "android-x64/libluau_host.so", true, 26),
        };

    public static async Task<int> RunAsync(RepositoryContext repository, CommandLine options)
    {
        var requested = options.GetMany("--platform");
        var platforms = requested.Count == 0 ? Artifacts.Keys.ToArray() : requested.ToArray();
        var check = options.Has("--check");
        var policy = ReleasePolicy.Load(repository);
        var ndkRevision = policy.AndroidNdkRevision;
        var sourceCommit = (await ProcessRunner.RequireAsync("git", ["rev-parse", "HEAD"], repository.Root, echo: false))
            .StandardOutput.Trim().ToLowerInvariant();
        Require(Regex.IsMatch(sourceCommit, "^[0-9a-f]{40}$"), "Unable to resolve source commit.");

        foreach (var platform in platforms)
        {
            if (!Artifacts.TryGetValue(platform, out var artifact))
            {
                throw new ToolingException($"Unsupported shipping platform: {platform}");
            }

            await ProcessAsync(repository, policy, artifact, ndkRevision, sourceCommit, check);
        }

        Console.WriteLine(check
            ? "Selected Unity native shipping artifacts are current, within budgets, and independently audited."
            : "Selected Unity native shipping artifacts were refreshed; external symbols are under native/luau-host/out/symbols.");
        return 0;
    }

    private static async Task ProcessAsync(
        RepositoryContext repository,
        ReleasePolicy policy,
        Artifact artifact,
        string ndkRevision,
        string sourceCommit,
        bool check)
    {
        var hostRoot = repository.PathOf("native", "luau-host");
        var outRoot = Path.Combine(hostRoot, "out");
        var source = repository.PathOf(artifact.Source.Split('/'));
        var destination = repository.PathOf("Luau.Unity", "Runtime", "Plugins", artifact.Destination.Replace('/', Path.DirectorySeparatorChar));
        var importerMeta = destination + ".meta";
        FileSystem.RequireFile(source, $"Installed {artifact.Platform} host artifact");
        FileSystem.RequireFile(importerMeta, "Unity plugin importer metadata");
        var packageRelative = "Runtime/Plugins/" + artifact.Destination;
        var budget = GetBudget(policy, packageRelative);

        var shippingDirectory = Path.Combine(outRoot, check ? "shipping-check" : "shipping", artifact.Platform);
        Directory.CreateDirectory(shippingDirectory);
        var shipping = Path.Combine(shippingDirectory, Path.GetFileName(destination));
        var auditManifest = Path.Combine(shippingDirectory, "luau_host.audit.manifest.json");
        var shippingManifest = Path.Combine(shippingDirectory, "luau_host.shipping.manifest.json");
        string[] symbolFiles;

        if (artifact.Strip)
        {
            var cache = new CMakeCache(Path.Combine(outRoot, "build", artifact.Preset, "CMakeCache.txt"));
            var strip = cache.Get("CMAKE_STRIP");
            var objcopy = cache.Get("CMAKE_OBJCOPY");
            var readelf = cache.Get("CMAKE_READELF");
            var ndkRoot = cache.Get("CMAKE_ANDROID_NDK");
            foreach (var tool in new[] { strip, objcopy, readelf })
            {
                FileSystem.RequireFile(tool, "Pinned NDK tool");
            }
            Require(ReadNdkRevision(ndkRoot) == ndkRevision,
                $"Android NDK revision differs from reviewed revision {ndkRevision}.");
            await ProcessRunner.RequireAsync(strip,
                ["--strip-unneeded", "--remove-section=.comment", "-o", shipping, source], repository.Root);

            var repeat = shipping + ".determinism-check";
            try
            {
                await ProcessRunner.RequireAsync(strip,
                    ["--strip-unneeded", "--remove-section=.comment", "-o", repeat, source], repository.Root);
                Require(Hashing.FileSha256(repeat) == Hashing.FileSha256(shipping),
                    $"Deterministic {artifact.Platform} strip output differs.");
            }
            finally
            {
                if (File.Exists(repeat))
                {
                    File.Delete(repeat);
                }
            }

            await AssertAndroidHardeningAsync(readelf, shipping, repository.Root);
            var symbolsDirectory = Path.Combine(outRoot, "symbols", artifact.Platform);
            var unstripped = Path.Combine(symbolsDirectory, "libluau_host.unstripped.so");
            var debug = Path.Combine(symbolsDirectory, "libluau_host.so.debug");
            symbolFiles = [unstripped, debug];
            if (!check)
            {
                Directory.CreateDirectory(symbolsDirectory);
                File.Copy(source, unstripped, true);
                await ProcessRunner.RequireAsync(objcopy, ["--only-keep-debug", source, debug], repository.Root);
            }

            await ArtifactManifestCommand.WriteAsync(
                repository, shipping, artifact.Platform, auditManifest, "Release", artifact.AndroidApi, ndkRevision);
        }
        else
        {
            File.Copy(source, shipping, true);
            await ArtifactManifestCommand.WriteAsync(repository, shipping, artifact.Platform, auditManifest);
            var pdb = Path.Combine(outRoot, "build", "windows-x64", "Release", "luau_host.pdb");
            var symbolsDirectory = Path.Combine(outRoot, "symbols", artifact.Platform);
            var copiedPdb = Path.Combine(symbolsDirectory, "luau_host.pdb");
            symbolFiles = [copiedPdb];
            if (!check)
            {
                FileSystem.RequireFile(pdb, "Windows Release symbols");
                Directory.CreateDirectory(symbolsDirectory);
                File.Copy(pdb, copiedPdb, true);
            }
        }

        var audit = JsonNode.Parse(File.ReadAllText(auditManifest))!.AsObject();
        Require(audit["schema_version"]!.GetValue<int>() == 3 &&
                audit["source_commit"]!.GetValue<string>() == sourceCommit && audit["toolchain"] is JsonObject,
            $"The {artifact.Platform} audit manifest is missing synchronized provenance.");
        var shippingLength = new FileInfo(shipping).Length;
        Require(shippingLength <= budget,
            $"{artifact.Platform} shipping artifact is {shippingLength} bytes; reviewed budget is {budget}.");

        var shippingRecord = new JsonObject
        {
            ["schema_version"] = 2,
            ["platform"] = artifact.Platform,
            ["source_commit"] = sourceCommit,
            ["source_tree_clean"] = audit["source_tree_clean"]!.GetValue<bool>(),
            ["toolchain"] = audit["toolchain"]!.DeepClone(),
            ["deterministic_transform"] = artifact.Strip ? "llvm-strip --strip-unneeded --remove-section=.comment" : "identity-copy",
            ["unstripped_input"] = FileRecord(source),
            ["shipping_output"] = FileRecord(shipping, budget),
            ["unity_importer_meta_sha256"] = Hashing.FileSha256(importerMeta),
            ["audited_manifest_sha256"] = Hashing.FileSha256(auditManifest),
            ["audited_manifest"] = audit.DeepClone(),
        };
        if (artifact.Strip)
        {
            shippingRecord["android_api"] = artifact.AndroidApi;
            shippingRecord["android_ndk_revision"] = ndkRevision;
        }
        FileSystem.WriteUtf8(shippingManifest, shippingRecord.ToJsonString(JsonOptions.Compact) + "\n");

        if (!check)
        {
            var symbols = new JsonArray(symbolFiles.Select(path => (JsonNode)FileRecord(path)).ToArray());
            var symbolManifest = new JsonObject
            {
                ["schema_version"] = 2,
                ["platform"] = artifact.Platform,
                ["source_commit"] = sourceCommit,
                ["source_tree_clean"] = audit["source_tree_clean"]!.GetValue<bool>(),
                ["toolchain"] = audit["toolchain"]!.DeepClone(),
                ["unstripped_input"] = FileRecord(source),
                ["shipping_output"] = FileRecord(shipping),
                ["audited_manifest_sha256"] = Hashing.FileSha256(auditManifest),
                ["symbols"] = symbols,
            };
            FileSystem.WriteUtf8(
                Path.Combine(outRoot, "symbols", artifact.Platform, "luau_host.symbols.manifest.json"),
                symbolManifest.ToJsonString(JsonOptions.Compact) + "\n");
        }

        if (check)
        {
            FileSystem.RequireFile(destination, $"Unity {artifact.Platform} shipping artifact");
            Require(Hashing.FileSha256(destination) == Hashing.FileSha256(shipping),
                $"Unity {artifact.Platform} shipping artifact is stale.");
            Console.WriteLine($"Current audited artifact: {destination} (bytes={shippingLength}, SHA256={Hashing.FileSha256(shipping)})");
        }
        else
        {
            if (!File.Exists(destination) || Hashing.FileSha256(destination) != Hashing.FileSha256(shipping))
            {
                File.Copy(shipping, destination, true);
            }
            Console.WriteLine($"Copied audited shipping artifact -> {destination} (bytes={shippingLength}, SHA256={Hashing.FileSha256(shipping)})");
        }
    }

    private static JsonObject FileRecord(string path, long? maximum = null)
    {
        FileSystem.RequireFile(path, "Manifest input");
        var result = new JsonObject
        {
            ["file"] = Path.GetFileName(path),
            ["bytes"] = new FileInfo(path).Length,
            ["sha256"] = Hashing.FileSha256(path),
        };
        if (maximum.HasValue)
        {
            result["maximum_bytes"] = maximum.Value;
        }
        return result;
    }

    private static long GetBudget(ReleasePolicy policy, string relativePath)
    {
        var matches = policy.Artifacts.Where(artifact => artifact.Path == relativePath).ToArray();
        Require(matches.Length == 1, $"Release policy must contain exactly one budget for {relativePath}.");
        return matches[0].MaximumBytes;
    }

    private static string ReadNdkRevision(string ndkRoot)
    {
        var properties = Path.Combine(ndkRoot, "source.properties");
        FileSystem.RequireFile(properties, "Android NDK source.properties");
        var match = Regex.Match(File.ReadAllText(properties), @"(?m)^Pkg\.Revision\s*=\s*([^\r\n]+)\s*$");
        return match.Success ? match.Groups[1].Value.Trim() : throw new ToolingException("Unable to read Android NDK revision.");
    }

    private static async Task AssertAndroidHardeningAsync(string readelf, string binary, string root)
    {
        var output = (await ProcessRunner.RequireAsync(
            readelf, ["--program-headers", "--dynamic", binary], root, echo: false)).CombinedOutput;
        Require(Regex.IsMatch(output, @"(?m)^\s*GNU_RELRO\s"), "Android shipping artifact is missing GNU_RELRO.");
        var stack = Regex.Match(output, @"(?m)^\s*GNU_STACK\s+.*$");
        Require(stack.Success && Regex.IsMatch(stack.Value, @"\sRW\s") && !Regex.IsMatch(stack.Value, @"\sRWE\s"),
            "Android shipping artifact does not have a non-executable GNU_STACK.");
        Require(Regex.IsMatch(output, @"(?m)\(FLAGS\).*BIND_NOW|\(FLAGS_1\).*\bNOW\b"),
            "Android shipping artifact is missing immediate binding.");
    }

    private static void Require(bool condition, string message) => PackageStaticCommand.Require(condition, message);
}
