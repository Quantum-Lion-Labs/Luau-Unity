using System.Buffers.Binary;
using System.Text;
using Luau.Tooling;

namespace Luau.Tooling.Tests;

public sealed class NativeDependencyPolicyTests
{
    // What the Android plugins needed while they used the shared NDK C++
    // runtime, which Unity players without GameActivity do not contain.
    private static readonly string[] SharedRuntimeAndroidNeeded = ["libc++_shared.so", "libm.so", "libdl.so", "libc.so"];

    [Fact]
    public void ReadsNeededLibrariesInDynamicSectionOrder()
    {
        var elf = Elf(["libm.so", "libdl.so", "libc.so"]);

        Assert.Equal(["libm.so", "libdl.so", "libc.so"], NativeDependencyPolicy.ReadNeededLibraries(elf));
        Assert.Empty(NativeDependencyPolicy.ReadNeededLibraries(Elf([])));
    }

    [Theory]
    [InlineData("android-arm64")]
    [InlineData("android-x64")]
    public void RejectsAnAndroidPluginThatNeedsTheSharedCxxRuntime(string platform)
    {
        var exception = Assert.Throws<ToolingException>(() =>
            NativeDependencyPolicy.ValidateElf(platform, "Package plugin 'x'", Elf(SharedRuntimeAndroidNeeded)));

        Assert.Contains("Package plugin 'x' needs libc++_shared.so,", exception.Message);
        Assert.Contains("statically", exception.Message);
    }

    [Theory]
    [InlineData("android-arm64")]
    [InlineData("android-x64")]
    public void AcceptsAnAndroidPluginThatNeedsOnlySystemLibraries(string platform)
    {
        NativeDependencyPolicy.ValidateElf(platform, "plugin", Elf(["libm.so", "libdl.so", "libc.so"]));
        NativeDependencyPolicy.ValidateElf(platform, "plugin", Elf(["libc.so"]));
    }

    [Theory]
    [InlineData("android-arm64", "libc++.so")]
    [InlineData("android-arm64", "liblog.so")]
    [InlineData("android-arm64", "libmain.so")]
    [InlineData("android-x64", "libgame.so")]
    [InlineData("android-arm64", "libc.so.6")]
    [InlineData("linux-x64", "libstdc++.so.6")]
    [InlineData("linux-x64", "libgcc_s.so.1")]
    [InlineData("linux-x64", "libc.so")]
    public void RejectsEveryLibraryOutsideThePlatformAllowlist(string platform, string library)
    {
        var cRuntime = platform == "linux-x64" ? "libc.so.6" : "libc.so";
        var exception = Assert.Throws<ToolingException>(() =>
            NativeDependencyPolicy.Validate(platform, "plugin", [cRuntime, library]));

        Assert.Contains($"needs {library}, which is not allowed for {platform}", exception.Message);
    }

    [Fact]
    public void AcceptsTheLinuxCRuntimeOnly()
    {
        NativeDependencyPolicy.Validate("linux-x64", "plugin", ["libm.so.6", "libc.so.6", "ld-linux-x86-64.so.2"]);
    }

    [Theory]
    [InlineData("android-arm64")]
    [InlineData("linux-x64")]
    public void RequiresTheCRuntimeDependency(string platform)
    {
        var math = platform == "linux-x64" ? "libm.so.6" : "libm.so";
        var exception = Assert.Throws<ToolingException>(() => NativeDependencyPolicy.Validate(platform, "plugin", [math]));

        Assert.Contains("does not declare its C runtime dependency", exception.Message);
        Assert.Throws<ToolingException>(() => NativeDependencyPolicy.Validate(platform, "plugin", []));
    }

    [Fact]
    public void HasNoPolicyForPlatformsThatAreNotElf()
    {
        Assert.Throws<ToolingException>(() => NativeDependencyPolicy.Validate("win-x64", "plugin", ["KERNEL32.dll"]));
    }

    [Fact]
    public void RejectsMalformedElfInsteadOfHidingDependencies()
    {
        var valid = Elf(SharedRuntimeAndroidNeeded);

        AssertMalformed("is not an ELF file", [.. "MZ"u8, .. new byte[100]]);
        AssertMalformed("is not a little-endian ELF64 file", Patched(valid, 4, 1));
        AssertMalformed("is not a little-endian ELF64 file", Patched(valid, 5, 2));
        AssertMalformed("has an invalid program header table", valid[..100]);
        AssertMalformed("has a segment outside the file", valid[..(valid.Length - 8)]);
        AssertMalformed("has no dynamic segment", Elf(SharedRuntimeAndroidNeeded, dynamicType: 6));
        AssertMalformed("has more than one dynamic segment", Elf(SharedRuntimeAndroidNeeded, loadType: 2));
        AssertMalformed("has an unterminated dynamic section", Elf(SharedRuntimeAndroidNeeded, terminate: false));
        AssertMalformed("has no dynamic string table", Elf(SharedRuntimeAndroidNeeded, stringTable: false));
        AssertMalformed("outside its loaded segments", Elf(SharedRuntimeAndroidNeeded, stringTableShift: 0x100000));
        AssertMalformed("names a dependency outside its dynamic string table", Elf(SharedRuntimeAndroidNeeded, neededShift: 0x1000));
        AssertMalformed("names an empty or unterminated dependency", Elf(SharedRuntimeAndroidNeeded, neededShift: -1));
    }

    // The loader reads the dynamic table at its loaded address. A file whose
    // PT_DYNAMIC offset names other bytes could show the audit an allowed list
    // while the loader resolves a forbidden one.
    [Fact]
    public void RejectsADynamicSegmentThatIsNotTheImageOfItsLoadedAddress()
    {
        AssertMalformed(
            "file offset is not the image of its loaded address",
            Elf(["libm.so", "libdl.so", "libc.so"], dynamicAddressShift: 16));
        AssertMalformed(
            "file offset is not the image of its loaded address",
            Elf(["libm.so", "libdl.so", "libc.so"], dynamicAddressShift: 0x100000));
        AssertMalformed(
            "has overlapping loaded segments",
            Elf(["libm.so", "libdl.so", "libc.so"], overlappingLoad: true));
    }

    [Fact]
    public void BoundsTheDependencyListBeforeDecodingIt()
    {
        Assert.Equal(64, NativeDependencyPolicy.ReadNeededLibraries(Elf(Enumerable.Repeat("libc.so", 64).ToArray())).Length);
        AssertMalformed("lists more than 64 dependencies", Elf(Enumerable.Repeat("libc.so", 65).ToArray()));

        Assert.Equal(
            [new string('a', 255)],
            NativeDependencyPolicy.ReadNeededLibraries(Elf([new string('a', 255)])));
        AssertMalformed("or one longer than 255 bytes", Elf([new string('a', 256)]));
    }

    [Fact]
    public void ReadsTheCommittedPluginsLikeReadelf()
    {
        var plugins = Path.Combine(RepositoryRoot(), "Luau.Unity", "Runtime", "Plugins");

        // Stable across plugin refreshes: only the C++ runtime entry changes.
        foreach (var platform in new[] { "android-arm64", "android-x64" })
        {
            var needed = NativeDependencyPolicy.ReadNeededLibraries(File.ReadAllBytes(Path.Combine(plugins, platform, "libluau_host.so")));
            Assert.Equal(["libm.so", "libdl.so", "libc.so"], needed.Where(static name => name != "libc++_shared.so"));
        }

        var linux = NativeDependencyPolicy.ReadNeededLibraries(File.ReadAllBytes(Path.Combine(plugins, "linux-x64", "libluau_host.so")));
        Assert.Equal(["libm.so.6", "libc.so.6", "ld-linux-x86-64.so.2"], linux);
        NativeDependencyPolicy.Validate("linux-x64", "Linux plugin", linux);
    }

    [Fact]
    public void ParsesAndroidNeededLibrariesFromNdkReadelf()
    {
        const string ndk = """
            Dynamic section at offset 0xe9210 contains 29 entries:
              Tag                Type           Name/Value
              0x0000000000000001 (NEEDED)       Shared library: [libc++_shared.so]
              0x0000000000000001 (NEEDED)       Shared library: [libm.so]
              0x0000000000000001 (NEEDED)       Shared library: [libdl.so]
              0x0000000000000001 (NEEDED)       Shared library: [libc.so]
              0x000000000000000e (SONAME)       Library soname: [libluau_host.so]
            """;

        Assert.Equal(SharedRuntimeAndroidNeeded, NativeArtifactsCommand.ParseNeededLibraries(ndk));
    }

    [Fact]
    public void PackageStaticAuditsEveryCommittedElfPlugin()
    {
        var packageRoot = Directory.CreateTempSubdirectory("luau-native-dependencies-");
        try
        {
            void Write(string relative, byte[] bytes)
            {
                var path = Path.Combine(packageRoot.FullName, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
            }

            Write("Runtime/Plugins/android-arm64/libluau_host.so", Elf(["libm.so", "libdl.so", "libc.so"]));
            Write("Runtime/Plugins/android-x64/libluau_host.so", Elf(["libm.so", "libdl.so", "libc.so"]));
            Write("Runtime/Plugins/linux-x64/libluau_host.so", Elf(["libm.so.6", "libc.so.6", "ld-linux-x86-64.so.2"]));
            PackageStaticCommand.ValidateNativeDependencies(packageRoot.FullName);

            foreach (var (platform, relative) in PackageStaticCommand.ElfPlugins.Where(static plugin => plugin.Platform.StartsWith("android-", StringComparison.Ordinal)))
            {
                var original = File.ReadAllBytes(Path.Combine(packageRoot.FullName, relative));
                Write(relative, Elf(SharedRuntimeAndroidNeeded));
                var exception = Assert.Throws<ToolingException>(() => PackageStaticCommand.ValidateNativeDependencies(packageRoot.FullName));
                Assert.Contains($"Package plugin '{relative}' needs libc++_shared.so", exception.Message);
                Write(relative, original);
            }

            File.Delete(Path.Combine(packageRoot.FullName, "Runtime", "Plugins", "android-x64", "libluau_host.so"));
            Assert.Throws<ToolingException>(() => PackageStaticCommand.ValidateNativeDependencies(packageRoot.FullName));
        }
        finally
        {
            packageRoot.Delete(recursive: true);
        }
    }

    private static void AssertMalformed(string expected, byte[] elf)
    {
        var exception = Assert.Throws<ToolingException>(() => NativeDependencyPolicy.ReadNeededLibraries(elf, "plugin"));
        Assert.Contains(expected, exception.Message);
    }

    private static byte[] Patched(byte[] bytes, int offset, byte value)
    {
        var copy = bytes.ToArray();
        copy[offset] = value;
        return copy;
    }

    // A minimal ELF64 shared object: header, a PT_LOAD mapping the whole file
    // at a virtual address that differs from its file offset, a PT_DYNAMIC,
    // the dynamic entries, and the dynamic string table they name.
    private static byte[] Elf(
        IReadOnlyList<string> needed,
        uint loadType = 1,
        uint dynamicType = 2,
        bool terminate = true,
        bool stringTable = true,
        ulong stringTableShift = 0,
        long neededShift = 0,
        ulong dynamicAddressShift = 0,
        bool overlappingLoad = false)
    {
        const int headerSize = 64;
        const int programHeaderSize = 56;
        const ulong baseAddress = 0x10000;
        var programHeaderCount = overlappingLoad ? 3 : 2;
        var dynamicOffset = headerSize + programHeaderCount * programHeaderSize;

        var strings = new MemoryStream();
        strings.WriteByte(0);
        var nameOffsets = new List<long>();
        foreach (var name in needed)
        {
            nameOffsets.Add(strings.Length);
            strings.Write(Encoding.ASCII.GetBytes(name));
            strings.WriteByte(0);
        }

        var entries = new List<(long Tag, ulong Value)>();
        entries.AddRange(nameOffsets.Select(offset => (1L, (ulong)(offset + neededShift))));
        var entryCount = entries.Count + (stringTable ? 2 : 0) + (terminate ? 1 : 0);
        var stringOffset = dynamicOffset + entryCount * 16;
        if (stringTable)
        {
            entries.Add((5, baseAddress + (ulong)stringOffset + stringTableShift));
            entries.Add((10, (ulong)strings.Length));
        }
        if (terminate)
        {
            entries.Add((0, 0));
        }

        var bytes = new byte[stringOffset + strings.Length];
        "\x7f"u8.CopyTo(bytes);
        "ELF"u8.CopyTo(bytes.AsSpan(1));
        bytes[4] = 2;
        bytes[5] = 1;
        bytes[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x10), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x12), 183);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x14), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x20), headerSize);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x34), headerSize);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x36), programHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x38), (ushort)programHeaderCount);

        WriteProgramHeader(bytes.AsSpan(headerSize), loadType, 0, baseAddress, (ulong)bytes.Length);
        WriteProgramHeader(bytes.AsSpan(headerSize + programHeaderSize), dynamicType, (ulong)dynamicOffset,
            baseAddress + (ulong)dynamicOffset + dynamicAddressShift, (ulong)(entries.Count * 16));
        if (overlappingLoad)
        {
            // A second mapping of the same addresses onto different file bytes.
            WriteProgramHeader(bytes.AsSpan(headerSize + 2 * programHeaderSize), 1, 16, baseAddress + 8,
                (ulong)bytes.Length - 16);
        }
        for (var index = 0; index < entries.Count; index++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(dynamicOffset + index * 16), entries[index].Tag);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(dynamicOffset + index * 16 + 8), entries[index].Value);
        }
        strings.ToArray().CopyTo(bytes, stringOffset);
        return bytes;
    }

    private static void WriteProgramHeader(Span<byte> header, uint type, ulong offset, ulong address, ulong size)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(header, type);
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], offset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[16..], address);
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], address);
        BinaryPrimitives.WriteUInt64LittleEndian(header[32..], size);
        BinaryPrimitives.WriteUInt64LittleEndian(header[40..], size);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Luau.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
