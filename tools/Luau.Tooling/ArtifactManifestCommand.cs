using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Luau.Tooling;

internal static class ArtifactManifestCommand
{
    private const string ApprovedBuildInputSha256 = "ecfa17274b44f16e27f0547a2984dd0d245008e547206e84b7b2eb550ef2650d";
    private const ulong ApprovedUpstreamRevisionHash = 0xc45f010aabf167ac;
    private const ulong ApprovedHostBuildFingerprint = 0x2a4ca2b50dc114da;
    private const uint ApprovedFeatureFlags = 0x1fff;

    internal sealed record BinaryIdentity(
        uint RecordSize,
        uint AbiMagic,
        ushort AbiMajor,
        ushort AbiMinor,
        uint FeatureFlags,
        byte PointerSize,
        byte SizeTSize,
        byte LittleEndian,
        byte Reserved,
        ulong UpstreamRevisionHash,
        ulong HostBuildFingerprint,
        string BuildInputSha256,
        string BuildConfiguration);

    public static async Task<int> RunAsync(RepositoryContext repository, CommandLine options)
    {
        var binary = RequiredOption(options, "--binary");
        var platform = RequiredOption(options, "--platform");
        var output = RequiredOption(options, "--output");
        var configuration = options.Get("--configuration", "Release");
        var androidApi = options.GetInt("--android-api", 0);
        var androidNdk = options.Get("--android-ndk");
        await WriteAsync(repository, binary, platform, output, configuration, androidApi, androidNdk);
        return 0;
    }

    public static async Task WriteAsync(
        RepositoryContext repository,
        string binary,
        string platform,
        string output,
        string configuration = "Release",
        int androidApi = 0,
        string? androidNdk = null)
    {
        if (platform is not ("win-x64" or "android-arm64" or "android-x64" or "linux-x64"))
        {
            throw new ToolingException($"Unsupported host platform: {platform}");
        }
        if (configuration != "Release")
        {
            throw new ToolingException("Artifact manifests require Release configuration.");
        }
        if (platform.StartsWith("android-", StringComparison.Ordinal) && (androidApi <= 0 || string.IsNullOrWhiteSpace(androidNdk)))
        {
            throw new ToolingException("Android manifests require --android-api and --android-ndk.");
        }
        if (!platform.StartsWith("android-", StringComparison.Ordinal) && (androidApi != 0 || !string.IsNullOrWhiteSpace(androidNdk)))
        {
            throw new ToolingException("Android metadata may only be supplied for an Android platform.");
        }

        binary = Path.GetFullPath(binary, repository.Root);
        output = Path.GetFullPath(output, repository.Root);
        FileSystem.RequireFile(binary, "Host artifact");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        var hostRoot = repository.PathOf("native", "luau-host");
        var headerPath = Path.Combine(hostRoot, "include", "luau_host.h");
        var sourcePath = Path.Combine(hostRoot, "src", "luau_host.cpp");
        var referencePath = Path.Combine(hostRoot, "src", "reference_tokens.h");
        var allocatorPath = Path.Combine(hostRoot, "src", "tracked_allocation.h");
        var exportsPath = Path.Combine(hostRoot, "exports", "luau_host.exports");
        var cmakePath = Path.Combine(hostRoot, "CMakeLists.txt");
        var header = File.ReadAllText(headerPath);
        var source = File.ReadAllText(sourcePath);
        var cmake = File.ReadAllText(cmakePath);
        var managed = File.ReadAllText(repository.PathOf("src", "Luau", "Internal", "LuauNativeProtection.cs"));

        var abiMagic = Convert.ToUInt32(Match(header, @"LUAU_HOST_ABI_MAGIC\s*=\s*0x([0-9a-fA-F]+)U", "ABI magic"), 16);
        var abiMajor = ushort.Parse(Match(header, @"LUAU_HOST_ABI_MAJOR\s*=\s*(\d+)", "ABI major"));
        var abiMinor = ushort.Parse(Match(header, @"LUAU_HOST_ABI_MINOR\s*=\s*(\d+)", "ABI minor"));
        var featureMatches = Regex.Matches(header, @"LUAU_HOST_FEATURE_([A-Z0-9_]+)\s*=\s*1U\s*<<\s*(\d+)");
        uint featureFlags = 0;
        var features = new JsonArray();
        foreach (Match match in featureMatches)
        {
            var bit = int.Parse(match.Groups[2].Value);
            var flag = 1u << bit;
            featureFlags |= flag;
            features.Add(new JsonObject
            {
                ["name"] = match.Groups[1].Value.ToLowerInvariant(),
                ["bit"] = bit,
                ["flag"] = Hex32(flag),
            });
        }
        Require(featureFlags == ApprovedFeatureFlags, "Native feature flags differ from the approved ABI.");

        var upstreamRevision = Match(cmake, "set\\(LUAU_HOST_UPSTREAM_REVISION\\s+\"([0-9a-fA-F]{40})\"\\)", "upstream revision");
        var actualRevision = (await ProcessRunner.RequireAsync(
            "git", ["-C", repository.PathOf("native", "luau"), "rev-parse", "HEAD"], repository.Root, echo: false)).StandardOutput.Trim();
        Require(actualRevision == upstreamRevision, "Checked-out Luau revision differs from CMake input.");

        var headerHash = Hashing.FileSha256(headerPath);
        var sourceHash = Hashing.FileSha256(sourcePath);
        var referenceHash = Hashing.FileSha256(referencePath);
        var allocatorHash = Hashing.FileSha256(allocatorPath);
        var exportsHash = Hashing.FileSha256(exportsPath);
        var descriptor = $"abi={abiMajor}.{abiMinor};upstream={upstreamRevision};header={headerHash};source={sourceHash};references={referenceHash};allocator={allocatorHash};exports={exportsHash}";
        var buildInputHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(descriptor))).ToLowerInvariant();
        var upstreamHash = Fnv1A64(upstreamRevision);
        var fingerprint = Fnv1A64($"luau-host-inputs;{buildInputHash};{configuration}");
        Require(buildInputHash == ApprovedBuildInputSha256, "Native build inputs differ from the approved release fingerprint.");
        Require(upstreamHash == ApprovedUpstreamRevisionHash, "Upstream revision hash differs from approval.");
        Require(fingerprint == ApprovedHostBuildFingerprint, "Host build fingerprint differs from approval.");

        var bytes = File.ReadAllBytes(binary);
        var architecture = ValidateArchitecture(bytes, platform);
        var identity = ReadIdentity(bytes);
        Require(identity.AbiMagic == abiMagic && identity.AbiMajor == abiMajor && identity.AbiMinor == abiMinor,
            "Binary ABI identity differs from source.");
        Require(identity.FeatureFlags == featureFlags && identity.PointerSize == 8 && identity.SizeTSize == 8 && identity.LittleEndian == 1 && identity.Reserved == 0,
            "Binary ABI feature or data-model identity is invalid.");
        Require(identity.UpstreamRevisionHash == upstreamHash && identity.HostBuildFingerprint == fingerprint &&
                identity.BuildInputSha256 == buildInputHash && identity.BuildConfiguration == configuration,
            "Binary provenance identity differs from reviewed source inputs.");

        Require(Convert.ToUInt32(Match(managed, @"ExpectedAbiMagic\s*=\s*0x([0-9a-fA-F]+)U", "managed ABI magic"), 16) == abiMagic,
            "Managed ABI magic differs from native ABI.");
        Require(ushort.Parse(Match(managed, @"ExpectedAbiMajor\s*=\s*(\d+)", "managed ABI major")) == abiMajor,
            "Managed ABI major differs from native ABI.");
        Require(Convert.ToUInt32(Match(managed, @"ExpectedFeatureFlags\s*=\s*0x([0-9a-fA-F]+)U", "managed feature flags"), 16) == featureFlags,
            "Managed feature flags differ from native ABI.");

        var approvedExports = File.ReadLines(exportsPath)
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();
        Require(approvedExports.Length == 80 && approvedExports.Distinct(StringComparer.Ordinal).Count() == 80,
            "Approved export allowlist must contain 80 unique symbols.");
        await AuditExportsAsync(repository, platform, binary, exportsPath);
        var sourceCommit = (await ProcessRunner.RequireAsync("git", ["rev-parse", "HEAD"], repository.Root, echo: false)).StandardOutput.Trim().ToLowerInvariant();
        var status = await ProcessRunner.RequireAsync("git", ["status", "--porcelain=v1", "--untracked-files=all"], repository.Root, echo: false);
        var toolchain = await ToolchainMetadataAsync(repository, platform);
        if (platform.StartsWith("android-", StringComparison.Ordinal))
        {
            toolchain["android"] = new JsonObject { ["api"] = androidApi, ["ndk_revision"] = androidNdk };
        }

        var manifest = new JsonObject
        {
            ["schema_version"] = 3,
            ["artifact"] = Path.GetFileName(binary),
            ["platform"] = platform,
            ["source_commit"] = sourceCommit,
            ["source_tree_clean"] = string.IsNullOrWhiteSpace(status.StandardOutput),
            ["platform_metadata"] = new JsonObject
            {
                ["os"] = platform == "win-x64" ? "windows" : platform == "linux-x64" ? "linux" : "android",
                ["architecture"] = platform == "android-arm64" ? "arm64" : "x64",
            },
            ["toolchain"] = toolchain,
            ["binary_architecture"] = architecture,
            ["binary_identity"] = new JsonObject
            {
                ["record_size"] = identity.RecordSize,
                ["abi_magic"] = Hex32(identity.AbiMagic),
                ["abi_version"] = $"{identity.AbiMajor}.{identity.AbiMinor}",
                ["feature_flags"] = Hex32(identity.FeatureFlags),
                ["pointer_size"] = identity.PointerSize,
                ["size_t_size"] = identity.SizeTSize,
                ["little_endian"] = identity.LittleEndian == 1,
                ["upstream_revision_hash"] = Hex64(identity.UpstreamRevisionHash),
                ["host_build_fingerprint"] = Hex64(identity.HostBuildFingerprint),
                ["build_input_sha256"] = identity.BuildInputSha256,
                ["build_configuration"] = identity.BuildConfiguration,
            },
            ["sha256"] = Hashing.FileSha256(binary),
            ["bytes"] = new FileInfo(binary).Length,
            ["upstream_revision"] = upstreamRevision,
            ["upstream_revision_hash"] = Hex64(upstreamHash),
            ["build_configuration"] = configuration,
            ["build_input_sha256"] = buildInputHash,
            ["host_build_fingerprint"] = Hex64(fingerprint),
            ["build_inputs"] = new JsonObject
            {
                ["header_sha256"] = headerHash,
                ["source_sha256"] = sourceHash,
                ["reference_tokens_sha256"] = referenceHash,
                ["allocator_sha256"] = allocatorHash,
                ["exports_sha256"] = exportsHash,
                ["aggregate_sha256"] = buildInputHash,
            },
            ["abi"] = new JsonObject
            {
                ["magic"] = Hex32(abiMagic),
                ["version"] = $"{abiMajor}.{abiMinor}",
                ["major"] = abiMajor,
                ["minor"] = abiMinor,
                ["pointer_size"] = 8,
                ["size_t_size"] = 8,
                ["little_endian"] = true,
                ["features"] = new JsonObject { ["flags"] = Hex32(featureFlags), ["required"] = features },
            },
            ["approved_export_count"] = approvedExports.Length,
            ["approved_exports"] = new JsonArray(approvedExports.Select(static value => JsonValue.Create(value)).ToArray()),
        };
        if (platform.StartsWith("android-", StringComparison.Ordinal))
        {
            manifest["android_api"] = androidApi;
            manifest["android_ndk"] = androidNdk;
        }
        FileSystem.WriteUtf8(output, manifest.ToJsonString(JsonOptions.Indented) + "\n");
        Console.WriteLine($"Validated luau_host ABI manifest: {output}");
    }

    private static async Task AuditExportsAsync(RepositoryContext repository, string platform, string binary, string allowlist)
    {
        var preset = Preset(platform);
        var cache = new CMakeCache(repository.PathOf("native", "luau-host", "out", "build", preset, "CMakeCache.txt"));
        var cmake = cache.Get("CMAKE_COMMAND");
        string exportTool;
        string kind;
        if (platform == "win-x64")
        {
            exportTool = Path.Combine(Path.GetDirectoryName(cache.Get("CMAKE_LINKER"))!, "dumpbin.exe");
            kind = "MSVC";
        }
        else
        {
            exportTool = cache.Get("CMAKE_NM");
            kind = "NM";
        }
        await ProcessRunner.RequireAsync(
            cmake,
            [$"-DBINARY={binary}", $"-DALLOWLIST={allowlist}", $"-DEXPORT_TOOL={exportTool}", $"-DEXPORT_TOOL_KIND={kind}", "-P", repository.PathOf("native", "luau-host", "cmake", "AuditExports.cmake")],
            repository.Root);
    }

    private static async Task<JsonObject> ToolchainMetadataAsync(RepositoryContext repository, string platform)
    {
        var build = repository.PathOf("native", "luau-host", "out", "build", Preset(platform));
        var cache = new CMakeCache(Path.Combine(build, "CMakeCache.txt"));
        var cmakePath = cache.Get("CMAKE_COMMAND");
        var cmakeVersion = (await ProcessRunner.RequireAsync(cmakePath, ["--version"], repository.Root, echo: false))
            .StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Replace("cmake version ", "", StringComparison.Ordinal);
        var descriptorPaths = Directory.GetFiles(build, "CMakeCXXCompiler.cmake", SearchOption.AllDirectories);
        Require(descriptorPaths.Length == 1, $"Expected one CMake compiler descriptor under {build}.");
        var descriptor = File.ReadAllText(descriptorPaths[0]);
        var compiler = CMakeCache.GetSetValue(descriptor, "CMAKE_CXX_COMPILER");
        var compilerId = CMakeCache.GetSetValue(descriptor, "CMAKE_CXX_COMPILER_ID");
        var compilerVersion = CMakeCache.GetSetValue(descriptor, "CMAKE_CXX_COMPILER_VERSION");
        var linker = cache.Get("CMAKE_LINKER");
        var linkerId = CMakeCache.GetSetValue(descriptor, "CMAKE_CXX_COMPILER_LINKER_ID");
        var linkerVersion = CMakeCache.GetSetValue(descriptor, "CMAKE_CXX_COMPILER_LINKER_VERSION");
        FileSystem.RequireFile(compiler, "Configured compiler");
        FileSystem.RequireFile(linker, "Configured linker");
        return new JsonObject
        {
            ["cmake"] = new JsonObject
            {
                ["version"] = cmakeVersion,
                ["generator"] = cache.Get("CMAKE_GENERATOR"),
                ["executable_sha256"] = Hashing.FileSha256(cmakePath),
            },
            ["compiler"] = ToolRecord(compiler, compilerId, compilerVersion),
            ["linker"] = ToolRecord(linker, linkerId, linkerVersion),
            ["build_tool"] = cache.TryGet("CMAKE_MAKE_PROGRAM") is { } make && File.Exists(make)
                ? new JsonObject { ["file"] = Path.GetFileName(make), ["sha256"] = Hashing.FileSha256(make) }
                : null,
            ["build_host"] = new JsonObject
            {
                ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                ["architecture"] = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                ["ci_image_os"] = Environment.GetEnvironmentVariable("ImageOS") ?? "",
                ["ci_image_version"] = Environment.GetEnvironmentVariable("ImageVersion") ?? "",
            },
        };
    }

    private static JsonObject ToolRecord(string path, string identity, string version) => new()
    {
        ["file"] = Path.GetFileName(path),
        ["identity"] = identity,
        ["version"] = version,
        ["sha256"] = Hashing.FileSha256(path),
    };

    private static JsonObject ValidateArchitecture(byte[] bytes, string platform)
    {
        if (platform == "win-x64")
        {
            Require(bytes.Length >= 64 && bytes[0] == 'M' && bytes[1] == 'Z', "Windows artifact is not PE.");
            var offset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3c));
            Require(offset >= 0 && offset <= bytes.Length - 6 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)) == 0x00004550 &&
                    BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 4)) == 0x8664,
                "Windows artifact is not x86_64 PE32+.");
            return new JsonObject { ["format"] = "PE32+", ["machine"] = "x86_64", ["pointer_bits"] = 64 };
        }
        Require(bytes.Length >= 20 && bytes.AsSpan(0, 4).SequenceEqual(new byte[] { 0x7f, 0x45, 0x4c, 0x46 }) && bytes[4] == 2 && bytes[5] == 1,
            "Host artifact is not little-endian ELF64.");
        var machine = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(18));
        Require(machine == (platform == "android-arm64" ? 183 : 62), "ELF host artifact architecture mismatch.");
        return new JsonObject { ["format"] = "ELF64", ["machine"] = machine == 183 ? "aarch64" : "x86_64", ["pointer_bits"] = 64 };
    }

    internal static BinaryIdentity ReadIdentity(byte[] bytes)
    {
        var marker = "LUAUHABI-PROBE1"u8;
        var offset = bytes.AsSpan().IndexOf(marker);
        Require(offset >= 0 && bytes.AsSpan(offset + marker.Length).IndexOf(marker) < 0,
            "Host artifact must contain exactly one binary identity record.");
        var record = bytes.AsSpan(offset);
        Require(record.Length >= 20, "Binary identity record is truncated.");
        var size = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
        Require(size == 149 && offset + size <= bytes.Length, "Binary identity record has invalid size.");
        return new BinaryIdentity(
            size,
            BinaryPrimitives.ReadUInt32LittleEndian(record[20..]),
            BinaryPrimitives.ReadUInt16LittleEndian(record[24..]),
            BinaryPrimitives.ReadUInt16LittleEndian(record[26..]),
            BinaryPrimitives.ReadUInt32LittleEndian(record[28..]),
            record[32], record[33], record[34], record[35],
            BinaryPrimitives.ReadUInt64LittleEndian(record[36..]),
            BinaryPrimitives.ReadUInt64LittleEndian(record[44..]),
            NullTerminatedAscii(record.Slice(52, 65)),
            NullTerminatedAscii(record.Slice(117, 32)));
    }

    private static string NullTerminatedAscii(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        Require(end >= 0, "Binary identity text is not terminated.");
        return Encoding.ASCII.GetString(bytes[..end]);
    }

    private static ulong Fnv1A64(string text)
    {
        var value = 14695981039346656037ul;
        foreach (var item in Encoding.UTF8.GetBytes(text))
        {
            value = unchecked((value ^ item) * 1099511628211ul);
        }
        return value;
    }

    private static string Match(string text, string pattern, string description)
    {
        var match = Regex.Match(text, pattern, RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : throw new ToolingException($"Unable to derive {description}.");
    }

    private static string RequiredOption(CommandLine options, string name) =>
        options.Get(name) ?? throw new ToolingException($"{name} is required.");
    private static string Preset(string platform) => platform switch
    {
        "win-x64" => "windows-x64",
        "android-arm64" => "android-arm64",
        "android-x64" => "android-x64",
        "linux-x64" => "linux-x64",
        _ => throw new ToolingException($"Unsupported platform: {platform}"),
    };
    private static string Hex32(uint value) => $"0x{value:x8}";
    private static string Hex64(ulong value) => $"0x{value:x16}";
    private static void Require(bool condition, string message) => PackageStaticCommand.Require(condition, message);
}
