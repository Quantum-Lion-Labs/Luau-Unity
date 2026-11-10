using System.Buffers.Binary;
using System.Text;
using Luau.Tooling;

namespace Luau.Tooling.Tests;

public sealed class NativeDependencyPolicyTests
{
    // What the Android plugins needed while they used the shared NDK C++
    // runtime, which Unity players without GameActivity do not contain.
    private static readonly string[] SharedRuntimeAndroidNeeded = ["libc++_shared.so", "libm.so", "libdl.so", "libc.so"];

    // What the Windows plugin imported while it used the DLL C and C++
    // runtimes, which Unity players do not contain.
    private static readonly string[] DynamicRuntimeWindowsImports =
    [
        "KERNEL32.dll", "MSVCP140.dll", "VCRUNTIME140.dll", "VCRUNTIME140_1.dll", "api-ms-win-crt-runtime-l1-1-0.dll",
        "api-ms-win-crt-heap-l1-1-0.dll", "api-ms-win-crt-string-l1-1-0.dll", "api-ms-win-crt-stdio-l1-1-0.dll",
        "api-ms-win-crt-math-l1-1-0.dll", "api-ms-win-crt-convert-l1-1-0.dll", "api-ms-win-crt-time-l1-1-0.dll",
    ];

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
    public void ChecksEachPlatformWithTheReaderForItsFormat()
    {
        // The Windows plugin is a PE image; the ELF dependency list does not apply.
        Assert.Contains("PE image", Assert.Throws<ToolingException>(() =>
            NativeDependencyPolicy.Validate("win-x64", "plugin", ["KERNEL32.dll"])).Message);
        Assert.Contains("No PE import policy exists for platform linux-x64", Assert.Throws<ToolingException>(() =>
            NativeDependencyPolicy.ValidateWindows("linux-x64", "plugin", new([], [], []))).Message);

        NativeDependencyPolicy.ValidatePlugin("win-x64", "plugin", Pe(["KERNEL32.dll"]));
        NativeDependencyPolicy.ValidatePlugin("android-arm64", "plugin", Elf(["libm.so", "libdl.so", "libc.so"]));
        Assert.Contains("is not a PE image", Assert.Throws<ToolingException>(() =>
            NativeDependencyPolicy.ValidatePlugin("win-x64", "plugin", Elf(["libc.so"]))).Message);
        Assert.Contains("is not an ELF file", Assert.Throws<ToolingException>(() =>
            NativeDependencyPolicy.ValidatePlugin("linux-x64", "plugin", Pe(["KERNEL32.dll"]))).Message);
    }

    [Fact]
    public void ReadsImportsDelayLoadsAndForwardersInTableOrder()
    {
        var dependencies = NativeDependencyPolicy.ReadPeDependencies(
            Pe(["KERNEL32.dll", "USER32.dll"], ["dbghelp.dll", "VCRUNTIME140.dll"], ["NTDLL.RtlAllocateHeap"]));

        Assert.Equal(["KERNEL32.dll", "USER32.dll"], dependencies.Imports);
        Assert.Equal(["dbghelp.dll", "VCRUNTIME140.dll"], dependencies.DelayImports);
        Assert.Equal(["NTDLL.RtlAllocateHeap"], dependencies.Forwarders);

        var empty = NativeDependencyPolicy.ReadPeDependencies(Pe([]));
        Assert.Empty(empty.Imports);
        Assert.Empty(empty.DelayImports);
        Assert.Empty(empty.Forwarders);
    }

    [Theory]
    [InlineData("KERNEL32.dll")]
    [InlineData("kernel32.dll")]
    [InlineData("Kernel32.DLL")]
    public void AcceptsAWindowsPluginThatImportsOnlyKernel32InAnyCase(string kernel32)
    {
        NativeDependencyPolicy.ValidatePe("win-x64", "plugin", Pe([kernel32]));
        NativeDependencyPolicy.ValidatePe("win-x64", "plugin", Pe(["KERNEL32.dll", kernel32]));
        // Delay-loading the operating system is pointless but needs nothing else.
        NativeDependencyPolicy.ValidatePe("win-x64", "plugin", Pe(["KERNEL32.dll"], [kernel32]));
    }

    // The Visual C++ runtime DLLs, the Universal CRT and its API-set
    // forwarders, their debug variants, and other DLLs a Unity player cannot be
    // relied on to provide or that the plugin has no reason to load.
    [Theory]
    [InlineData("MSVCP140.dll")]
    [InlineData("VCRUNTIME140.dll")]
    [InlineData("VCRUNTIME140_1.dll")]
    [InlineData("msvcp140.DLL")]
    [InlineData("vcruntime140_1.DLL")]
    [InlineData("MSVCP140D.dll")]
    [InlineData("VCRUNTIME140D.dll")]
    [InlineData("ucrtbase.dll")]
    [InlineData("ucrtbased.dll")]
    [InlineData("api-ms-win-crt-runtime-l1-1-0.dll")]
    [InlineData("api-ms-win-crt-heap-l1-1-0.dll")]
    [InlineData("api-ms-win-crt-string-l1-1-0.dll")]
    [InlineData("api-ms-win-crt-stdio-l1-1-0.dll")]
    [InlineData("api-ms-win-crt-math-l1-1-0.dll")]
    [InlineData("api-ms-win-crt-convert-l1-1-0.dll")]
    [InlineData("api-ms-win-crt-time-l1-1-0.dll")]
    [InlineData("api-ms-win-core-synch-l1-2-0.dll")]
    [InlineData("msvcrt.dll")]
    [InlineData("KERNELBASE.dll")]
    [InlineData("KERNEL32")]
    public void RejectsEveryImportOutsideTheWindowsAllowlist(string library)
    {
        var imported = Assert.Throws<ToolingException>(() =>
            NativeDependencyPolicy.ValidatePe("win-x64", "Package plugin 'x'", Pe(["KERNEL32.dll", library])));
        Assert.Contains($"Package plugin 'x' imports {library}, which is not allowed for win-x64 (allowed: KERNEL32.dll)", imported.Message);
        Assert.Contains("statically (/MT)", imported.Message);

        var delayLoaded = Assert.Throws<ToolingException>(() =>
            NativeDependencyPolicy.ValidatePe("win-x64", "plugin", Pe(["KERNEL32.dll"], [library])));
        Assert.Contains($"plugin delay-loads {library}, which is not allowed for win-x64", delayLoaded.Message);
    }

    [Fact]
    public void RejectsTheDynamicRuntimeImportsOfTheEarlierWindowsPlugin()
    {
        var exception = Assert.Throws<ToolingException>(() =>
            NativeDependencyPolicy.ValidatePe("win-x64", "plugin", Pe(DynamicRuntimeWindowsImports)));

        Assert.Contains(
            "plugin imports MSVCP140.dll, VCRUNTIME140.dll, VCRUNTIME140_1.dll, api-ms-win-crt-runtime-l1-1-0.dll, " +
            "api-ms-win-crt-heap-l1-1-0.dll, api-ms-win-crt-string-l1-1-0.dll, api-ms-win-crt-stdio-l1-1-0.dll, " +
            "api-ms-win-crt-math-l1-1-0.dll, api-ms-win-crt-convert-l1-1-0.dll, api-ms-win-crt-time-l1-1-0.dll, " +
            "which are not allowed for win-x64",
            exception.Message);
    }

    // A forwarded export makes the loader load the DLL it names when the
    // export is resolved, even forwarding to an allowed DLL is a dependency the
    // audit cannot see in the import directories.
    [Theory]
    [InlineData("MSVCP140.?_Xlength_error@std@@YAXPEBD@Z")]
    [InlineData("KERNEL32.HeapAlloc")]
    [InlineData("NTDLL.#12")]
    public void RejectsAForwardedExport(string forwarder)
    {
        var exception = Assert.Throws<ToolingException>(() =>
            NativeDependencyPolicy.ValidatePe("win-x64", "plugin", Pe(["KERNEL32.dll"], forwarders: [forwarder])));

        Assert.Contains($"plugin forwards exports to {forwarder}.", exception.Message);
    }

    [Fact]
    public void RequiresTheWindowsPluginToImportKernel32()
    {
        foreach (var image in new[] { Pe([]), Pe(["USER32.dll"]), Pe([], ["KERNEL32.dll"]) })
        {
            var exception = Assert.Throws<ToolingException>(() => NativeDependencyPolicy.ValidatePe("win-x64", "plugin", image));
            Assert.Contains("plugin", exception.Message);
        }
        Assert.Contains("does not import KERNEL32.dll, so it is not a win-x64 DLL", Assert.Throws<ToolingException>(() =>
            NativeDependencyPolicy.ValidatePe("win-x64", "plugin", Pe([]))).Message);
        Assert.Contains("does not import KERNEL32.dll", Assert.Throws<ToolingException>(() =>
            NativeDependencyPolicy.ValidatePe("win-x64", "plugin", Pe([], ["KERNEL32.dll"]))).Message);
    }

    [Fact]
    public void RejectsMalformedPeHeadersInsteadOfHidingImports()
    {
        var (valid, layout) = PeWithLayout(["KERNEL32.dll"]);
        NativeDependencyPolicy.ReadPeDependencies(valid);

        AssertMalformedPe("is not a PE image", [.. "\x7fELF"u8, .. new byte[100]]);
        AssertMalformedPe("is not a PE image", valid[..63]);
        AssertMalformedPe("has no PE signature at its header offset", Patched32(valid, 0x3c, 0x7fff_0000));
        AssertMalformedPe("has no PE signature at its header offset", Patched32(valid, 0x3c, 8));
        AssertMalformedPe("has no PE signature at its header offset", Patched(valid, layout.Coff - 2, (byte)'X'));
        AssertMalformedPe("has no PE signature at its header offset", valid[..(layout.Coff + 21)]);
        AssertMalformedPe("is not an x86-64 PE32+ image", Patched16(valid, layout.Coff, 0x014c));
        AssertMalformedPe("is not an x86-64 PE32+ image", Patched16(valid, layout.Coff, 0xaa64));
        AssertMalformedPe("is not an x86-64 PE32+ image", Patched16(valid, layout.Optional, 0x10b));
        AssertMalformedPe("is not a DLL", Patched16(valid, layout.Coff + 18, 0x0022));
        AssertMalformedPe("is not a DLL", Patched16(valid, layout.Coff + 18, 0x2000));
        AssertMalformedPe("has a truncated optional header", Patched16(valid, layout.Coff + 16, 224));
        AssertMalformedPe("has a truncated optional header", valid[..(layout.Optional + 200)]);
        AssertMalformedPe("declares 15 data directories instead of 16", Patched32(valid, layout.Optional + 108, 15));
        AssertMalformedPe("declares 4294967295 data directories", Patched32(valid, layout.Optional + 108, uint.MaxValue));
        AssertMalformedPe("has an invalid section or file alignment", Patched32(valid, layout.Optional + 36, 0x100));
        AssertMalformedPe("has an invalid section or file alignment", Patched32(valid, layout.Optional + 36, 0x300));
        AssertMalformedPe("has an invalid section or file alignment", Patched32(valid, layout.Optional + 32, 0x100));
        AssertMalformedPe("declares 0 sections", Patched16(valid, layout.Coff + 2, 0));
        AssertMalformedPe("declares 97 sections", Patched16(valid, layout.Coff + 2, 97));
        AssertMalformedPe("has a section table outside its headers", Patched16(valid, layout.Coff + 2, 96));
        AssertMalformedPe("has a section table outside its headers", Patched32(valid, layout.Optional + 60, (uint)layout.SectionTable + 20));
        AssertMalformedPe("has a section table outside its headers", Patched32(valid, layout.Optional + 60, (uint)valid.Length + 0x200));
    }

    [Fact]
    public void RejectsSectionsThatDoNotMapToTheFileExactlyOnce()
    {
        var (valid, layout) = PeWithLayout(["KERNEL32.dll"]);
        var header = layout.SectionTable;

        AssertMalformedPe("has a section outside the file", valid[..(valid.Length - 0x200)]);
        AssertMalformedPe("has a section outside the file", Patched32(valid, header + 20, 0));
        AssertMalformedPe("has a section outside the file", Patched32(valid, header + 16, 0x7fff_fe00));
        AssertMalformedPe("not aligned to its file alignment", Patched32(valid, header + 20, (uint)layout.SectionFileOffset + 0x10));
        AssertMalformedPe("not aligned to its file alignment", Patched32(valid, header + 16, 0x1f0));
        AssertMalformedPe("has a section outside its image", Patched32(valid, layout.Optional + 56, layout.SectionAddress));
        AssertMalformedPe("has a section outside its image", Patched32(valid, header + 12, layout.SectionAddress + 0x10));
        AssertMalformedPe("has a section outside its image", Patched32(valid, header + 12, 0));
        AssertMalformedPe("has a section outside its image", Patched32(Patched32(valid, header + 8, 0), header + 16, 0));
        AssertMalformedPe("has a section outside its image", Patched32(valid, header + 8, uint.MaxValue));

        // A second section whose addresses overlap the first, backed by other
        // file bytes, would let one address name two different bytes.
        AssertMalformedPe("has overlapping sections",
            Pe(["KERNEL32.dll"], extraSection: (layout.SectionAddress, 0x10, (uint)layout.SectionFileOffset, 0x200)));
        AssertMalformedPe("has overlapping sections",
            Pe(["KERNEL32.dll"], extraSection: (layout.SectionAddress - 0x1000, 0x1001, (uint)layout.SectionFileOffset, 0x200)));
        NativeDependencyPolicy.ReadPeDependencies(
            Pe(["KERNEL32.dll"], extraSection: (layout.SectionAddress + 0x1000, 0x10, (uint)layout.SectionFileOffset, 0x200)));
    }

    [Fact]
    public void RejectsMalformedImportDirectoriesInsteadOfHidingImports()
    {
        var (valid, layout) = PeWithLayout(["KERNEL32.dll", "USER32.dll"]);
        var import = layout.Directory(1);
        var descriptor = layout.ImportTable;
        // The section maps its content; the file pads it to the file alignment.
        var contentEnd = layout.SectionAddress + BinaryPrimitives.ReadUInt32LittleEndian(valid.AsSpan(layout.SectionTable + 8));
        var rawSize = (uint)(valid.Length - layout.SectionFileOffset);

        AssertMalformedPe("has a bound import directory, which the audit does not read",
            Patched32(Patched32(valid, layout.Directory(11), layout.SectionAddress), layout.Directory(11) + 4, 8));
        AssertMalformedPe("has a bound import directory", Patched32(valid, layout.Directory(11) + 4, 8));
        AssertMalformedPe("has an import directory with an address but no size", Patched32(valid, import + 4, 0));
        AssertMalformedPe("has an import directory with a size but no address", Patched32(valid, import, 0));
        AssertMalformedPe("has its import directory outside its sections", Patched32(valid, import, 0x9000));
        AssertMalformedPe("has its import directory outside its sections", Patched32(valid, import, 0x100));
        AssertMalformedPe("has an unterminated import directory", Patched32(valid, import, contentEnd - 10));
        AssertMalformedPe("has its import directory outside its sections", Patched32(valid, import, contentEnd));
        AssertMalformedPe("declares an import directory of 61 bytes, but its descriptors and terminator take 60",
            Patched32(valid, import + 4, 61));
        AssertMalformedPe("declares an import directory of 40 bytes, but its descriptors and terminator take 60",
            Patched32(valid, import + 4, 40));
        AssertMalformedPe("has an import descriptor without a name or import address table", Patched32(valid, descriptor + 12, 0));
        AssertMalformedPe("has an import descriptor without a name or import address table", Patched32(valid, descriptor + 20 + 16, 0));
        AssertMalformedPe("has an import library name outside its sections", Patched32(valid, descriptor + 12, 0x9000));
        AssertMalformedPe("has an empty or unterminated import library name", Patched32(valid, descriptor + 12, layout.SectionAddress));
        AssertMalformedPe("has an empty or unterminated import library name", Patched32(valid, descriptor + 12, contentEnd - 1));
        var unterminated = Patched32(valid, layout.SectionTable + 8, rawSize);
        "abcd"u8.CopyTo(unterminated.AsSpan(unterminated.Length - 4));
        AssertMalformedPe("has an empty or unterminated import library name",
            Patched32(unterminated, descriptor + 12, layout.SectionAddress + rawSize - 4));

        // The section maps more bytes than the file holds; the loader zero-fills
        // the rest, which a file reader must not mistake for file data.
        var zeroFilled = Patched32(valid, layout.SectionTable + 8, 0x2000);
        AssertMalformedPe("has its import directory outside the file data of its section",
            Patched32(zeroFilled, import, layout.SectionAddress + rawSize + 0x100));
        AssertMalformedPe("has an import library name outside the file data of its section",
            Patched32(zeroFilled, descriptor + 12, layout.SectionAddress + rawSize));
        NativeDependencyPolicy.ReadPeDependencies(zeroFilled);
    }

    [Fact]
    public void BoundsTheImportTablesBeforeDecodingThem()
    {
        var many = Enumerable.Range(0, 64).Select(static index => $"lib{index}.dll").ToArray();
        Assert.Equal(many, NativeDependencyPolicy.ReadPeDependencies(Pe(many)).Imports);
        Assert.Equal(many, NativeDependencyPolicy.ReadPeDependencies(Pe(["KERNEL32.dll"], many)).DelayImports);
        AssertMalformedPe("imports more than 64 libraries", Pe([.. many, "one-too-many.dll"]));
        AssertMalformedPe("delay-loads more than 64 libraries", Pe(["KERNEL32.dll"], [.. many, "one-too-many.dll"]));

        Assert.Equal([new string('a', 255)], NativeDependencyPolicy.ReadPeDependencies(Pe([new string('a', 255)])).Imports);
        AssertMalformedPe("has an empty or unterminated import library name, or one longer than 255 bytes", Pe([new string('a', 256)]));
        AssertMalformedPe("has an empty or unterminated delay-load import library name, or one longer than 255 bytes",
            Pe(["KERNEL32.dll"], [new string('a', 256)]));
        AssertMalformedPe("has an import library name that is not printable ASCII", Pe(["KERNEL32.dll\u0001"]));
        AssertMalformedPe("has an import library name that is not printable ASCII", Pe(["KERNEL\u00b32.dll"]));
        AssertMalformedPe("has a delay-load import library name that is not printable ASCII", Pe(["KERNEL32.dll"], ["\tx.dll"]));
    }

    [Fact]
    public void RejectsMalformedDelayLoadDirectories()
    {
        var (valid, layout) = PeWithLayout(["KERNEL32.dll"], ["VCRUNTIME140.dll"]);
        var delay = layout.Directory(13);
        var descriptor = layout.DelayTable;

        AssertMalformedPe("has a delay-load import directory with an address but no size", Patched32(valid, delay + 4, 0));
        AssertMalformedPe("has a delay-load import directory with a size but no address", Patched32(valid, delay, 0));
        AssertMalformedPe("has its delay-load import directory outside its sections", Patched32(valid, delay, 0x9000));
        AssertMalformedPe("declares a delay-load import directory of 32 bytes, but its descriptors and terminator take 64",
            Patched32(valid, delay + 4, 32));
        AssertMalformedPe("has a delay-load import descriptor that is not RVA-based", Patched32(valid, descriptor, 0));
        AssertMalformedPe("has a delay-load import descriptor that is not RVA-based", Patched32(valid, descriptor, 3));
        AssertMalformedPe("has a delay-load import descriptor without a name or import address table", Patched32(valid, descriptor + 4, 0));
        AssertMalformedPe("has a delay-load import descriptor without a name or import address table", Patched32(valid, descriptor + 12, 0));
        AssertMalformedPe("has a delay-load import library name outside its sections", Patched32(valid, descriptor + 4, 0x9000));
    }

    [Fact]
    public void RejectsMalformedOrOversizedExportTables()
    {
        var (valid, layout) = PeWithLayout(["KERNEL32.dll"], forwarders: ["KERNEL32.HeapAlloc"]);
        var export = layout.Directory(0);
        var table = layout.ExportTable;

        AssertMalformedPe("has an export directory smaller than its table", Patched32(valid, export + 4, 39));
        AssertMalformedPe("has an export directory smaller than its table", Patched32(valid, export, 0));
        AssertMalformedPe("has its export directory outside its sections", Patched32(valid, export, 0x9000));
        AssertMalformedPe("has its export directory that extends past the file data of its section", Patched32(valid, export + 4, 0x10000));
        AssertMalformedPe("exports more than 65536 functions", Patched32(valid, table + 20, 65537));
        AssertMalformedPe("has its export address table outside its sections", Patched32(valid, table + 28, 0x9000));
        AssertMalformedPe("has its export address table that extends past the file data of its section", Patched32(valid, table + 20, 65536));

        var forwarders = Enumerable.Range(0, 64).Select(static index => $"KERNEL32.F{index}").ToArray();
        Assert.Equal(forwarders, NativeDependencyPolicy.ReadPeDependencies(Pe(["KERNEL32.dll"], forwarders: forwarders)).Forwarders);
        AssertMalformedPe("forwards more than 64 exports", Pe(["KERNEL32.dll"], forwarders: [.. forwarders, "KERNEL32.G"]));
        Assert.Equal([new string('a', 1024)],
            NativeDependencyPolicy.ReadPeDependencies(Pe(["KERNEL32.dll"], forwarders: [new string('a', 1024)])).Forwarders);
        AssertMalformedPe("has an empty or unterminated forwarded export, or one longer than 1024 bytes",
            Pe(["KERNEL32.dll"], forwarders: [new string('a', 1025)]));
        AssertMalformedPe("has a forwarded export that is not printable ASCII", Pe(["KERNEL32.dll"], forwarders: ["KERNEL32.\u0080"]));

        // Exports that are not forwarders are never read as text.
        Assert.Empty(NativeDependencyPolicy.ReadPeDependencies(Pe(["KERNEL32.dll"])).Forwarders);
        Assert.Empty(NativeDependencyPolicy.ReadPeDependencies(Patched32(valid, table + 20, 0)).Forwarders);
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

        // The Android plugins link the C++ runtime statically, so they need
        // only the C runtime libraries, in the order readelf lists them.
        foreach (var platform in new[] { "android-arm64", "android-x64" })
        {
            var needed = NativeDependencyPolicy.ReadNeededLibraries(File.ReadAllBytes(Path.Combine(plugins, platform, "libluau_host.so")));
            Assert.Equal(["libm.so", "libdl.so", "libc.so"], needed);
            NativeDependencyPolicy.Validate(platform, $"{platform} plugin", needed);
        }

        var linux = NativeDependencyPolicy.ReadNeededLibraries(File.ReadAllBytes(Path.Combine(plugins, "linux-x64", "libluau_host.so")));
        Assert.Equal(["libm.so.6", "libc.so.6", "ld-linux-x86-64.so.2"], linux);
        NativeDependencyPolicy.Validate("linux-x64", "Linux plugin", linux);
    }

    [Fact]
    public void ReadsTheCommittedWindowsPluginLikeDumpbin()
    {
        var plugin = File.ReadAllBytes(Path.Combine(RepositoryRoot(), "Luau.Unity", "Runtime", "Plugins", "win-x64", "luau_host.dll"));

        // The Windows plugin links the C and C++ runtimes statically, so it
        // imports only KERNEL32.dll, delay-loads nothing, and forwards nothing.
        var dependencies = NativeDependencyPolicy.ReadPeDependencies(plugin);
        Assert.Equal(["KERNEL32.dll"], dependencies.Imports);
        Assert.Empty(dependencies.DelayImports);
        Assert.Empty(dependencies.Forwarders);
        NativeDependencyPolicy.ValidatePe("win-x64", "Windows plugin", plugin);
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
    public void PackageStaticAuditsEveryCommittedPlugin()
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

            Write("Runtime/Plugins/win-x64/luau_host.dll", Pe(["KERNEL32.dll"]));
            Write("Runtime/Plugins/android-arm64/libluau_host.so", Elf(["libm.so", "libdl.so", "libc.so"]));
            Write("Runtime/Plugins/android-x64/libluau_host.so", Elf(["libm.so", "libdl.so", "libc.so"]));
            Write("Runtime/Plugins/linux-x64/libluau_host.so", Elf(["libm.so.6", "libc.so.6", "ld-linux-x86-64.so.2"]));
            PackageStaticCommand.ValidateNativeDependencies(packageRoot.FullName);

            Assert.Equal(
                ["win-x64", "android-arm64", "android-x64", "linux-x64"],
                PackageStaticCommand.NativePlugins.Select(static plugin => plugin.Platform));
            foreach (var (platform, relative) in PackageStaticCommand.NativePlugins.Where(static plugin => plugin.Platform.StartsWith("android-", StringComparison.Ordinal)))
            {
                var original = File.ReadAllBytes(Path.Combine(packageRoot.FullName, relative));
                Write(relative, Elf(SharedRuntimeAndroidNeeded));
                var exception = Assert.Throws<ToolingException>(() => PackageStaticCommand.ValidateNativeDependencies(packageRoot.FullName));
                Assert.Contains($"Package plugin '{relative}' needs libc++_shared.so", exception.Message);
                Write(relative, original);
            }

            const string windows = "Runtime/Plugins/win-x64/luau_host.dll";
            var windowsOriginal = File.ReadAllBytes(Path.Combine(packageRoot.FullName, windows));
            Write(windows, Pe(DynamicRuntimeWindowsImports));
            Assert.Contains($"Package plugin '{windows}' imports MSVCP140.dll", Assert.Throws<ToolingException>(() =>
                PackageStaticCommand.ValidateNativeDependencies(packageRoot.FullName)).Message);
            Write(windows, Pe(["KERNEL32.dll"], ["VCRUNTIME140.dll"]));
            Assert.Contains($"Package plugin '{windows}' delay-loads VCRUNTIME140.dll", Assert.Throws<ToolingException>(() =>
                PackageStaticCommand.ValidateNativeDependencies(packageRoot.FullName)).Message);
            Write(windows, Elf(["libc.so"]));
            Assert.Contains($"Package plugin '{windows}' is not a PE image", Assert.Throws<ToolingException>(() =>
                PackageStaticCommand.ValidateNativeDependencies(packageRoot.FullName)).Message);
            Write(windows, windowsOriginal);
            PackageStaticCommand.ValidateNativeDependencies(packageRoot.FullName);

            File.Delete(Path.Combine(packageRoot.FullName, "Runtime", "Plugins", "win-x64", "luau_host.dll"));
            Assert.Throws<ToolingException>(() => PackageStaticCommand.ValidateNativeDependencies(packageRoot.FullName));
            Write(windows, windowsOriginal);
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

    private static void AssertMalformedPe(string expected, byte[] image)
    {
        var exception = Assert.Throws<ToolingException>(() => NativeDependencyPolicy.ReadPeDependencies(image, "plugin"));
        Assert.Contains(expected, exception.Message);
    }

    private static byte[] Patched(byte[] bytes, int offset, byte value)
    {
        var copy = bytes.ToArray();
        copy[offset] = value;
        return copy;
    }

    private static byte[] Patched16(byte[] bytes, int offset, ushort value)
    {
        var copy = bytes.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(offset), value);
        return copy;
    }

    private static byte[] Patched32(byte[] bytes, int offset, uint value)
    {
        var copy = bytes.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(offset), value);
        return copy;
    }

    // Where PeWithLayout put each structure: file offsets, plus the address
    // the one section is mapped at, so a test can corrupt exactly one field.
    private sealed record PeLayout(
        int Coff,
        int Optional,
        int SectionTable,
        int SectionFileOffset,
        uint SectionAddress,
        int ImportTable,
        int DelayTable,
        int ExportTable)
    {
        public int Directory(int index) => Optional + 112 + index * 8;
    }

    private static byte[] Pe(
        IReadOnlyList<string> imports,
        IReadOnlyList<string>? delayImports = null,
        IReadOnlyList<string>? forwarders = null,
        (uint Address, uint VirtualSize, uint RawOffset, uint RawSize)? extraSection = null) =>
        PeWithLayout(imports, delayImports, forwarders, extraSection).Image;

    // A minimal PE32+ x86-64 DLL as link.exe lays it out: DOS header, PE
    // signature, COFF header, optional header with 16 data directories, and
    // one section at a different address than its file offset that holds the
    // import descriptors, the delay-load descriptors, an export directory with
    // one ordinary export plus any forwarders, and the names they point to.
    private static (byte[] Image, PeLayout Layout) PeWithLayout(
        IReadOnlyList<string> imports,
        IReadOnlyList<string>? delayImports = null,
        IReadOnlyList<string>? forwarders = null,
        (uint Address, uint VirtualSize, uint RawOffset, uint RawSize)? extraSection = null)
    {
        delayImports ??= [];
        forwarders ??= [];
        const int signature = 0x40;
        const int coff = signature + 4;
        const int optional = coff + 20;
        const int optionalSize = 240;
        const int sectionTable = optional + optionalSize;
        const int headersSize = 0x200;
        const int sectionFileOffset = headersSize;
        const uint sectionAddress = 0x3000;

        // Section-relative layout. The first eight bytes are a zero thunk that
        // every import address table and ordinary export points to.
        var importTable = 8;
        var importBytes = imports.Count == 0 ? 0 : (imports.Count + 1) * 20;
        var delayTable = importTable + importBytes;
        var delayBytes = delayImports.Count == 0 ? 0 : (delayImports.Count + 1) * 32;
        var exportTable = delayTable + delayBytes;
        var functionTable = exportTable + 40;
        var forwarderText = functionTable + (1 + forwarders.Count) * 4;
        var forwarderOffsets = new List<int>();
        var cursor = forwarderText;
        foreach (var forwarder in forwarders)
        {
            forwarderOffsets.Add(cursor);
            cursor += Encoding.Latin1.GetByteCount(forwarder) + 1;
        }
        var exportBytes = cursor - exportTable;
        var nameOffsets = new List<int>();
        foreach (var name in imports.Concat(delayImports))
        {
            nameOffsets.Add(cursor);
            cursor += Encoding.Latin1.GetByteCount(name) + 1;
        }
        var contentSize = cursor;
        var rawSize = (contentSize + 0x1ff) & ~0x1ff;

        var bytes = new byte[sectionFileOffset + rawSize];
        uint Address(int offset) => sectionAddress + (uint)offset;
        void Write16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
        void Write32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        void WriteText(int offset, string text) => Encoding.Latin1.GetBytes(text).CopyTo(bytes, sectionFileOffset + offset);

        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        Write32(0x3c, signature);
        "PE\0\0"u8.CopyTo(bytes.AsSpan(signature));
        Write16(coff, 0x8664);
        Write16(coff + 2, (ushort)(extraSection is null ? 1 : 2));
        Write16(coff + 16, optionalSize);
        Write16(coff + 18, 0x2022);
        Write16(optional, 0x20b);
        Write32(optional + 32, 0x1000);
        Write32(optional + 36, 0x200);
        Write32(optional + 56, 0x10000);
        Write32(optional + 60, headersSize);
        Write32(optional + 108, 16);
        var directories = optional + 112;
        Write32(directories, Address(exportTable));
        Write32(directories + 4, (uint)exportBytes);
        if (imports.Count > 0)
        {
            Write32(directories + 8, Address(importTable));
            Write32(directories + 12, (uint)importBytes);
        }
        if (delayImports.Count > 0)
        {
            Write32(directories + 13 * 8, Address(delayTable));
            Write32(directories + 13 * 8 + 4, (uint)delayBytes);
        }

        void WriteSection(int header, uint address, uint virtualSize, uint fileOffset, uint fileSize)
        {
            ".rdata"u8.CopyTo(bytes.AsSpan(header));
            Write32(header + 8, virtualSize);
            Write32(header + 12, address);
            Write32(header + 16, fileSize);
            Write32(header + 20, fileOffset);
            Write32(header + 36, 0x4000_0040);
        }
        WriteSection(sectionTable, sectionAddress, (uint)contentSize, sectionFileOffset, (uint)rawSize);
        if (extraSection is { } extra)
        {
            WriteSection(sectionTable + 40, extra.Address, extra.VirtualSize, extra.RawOffset, extra.RawSize);
        }

        var section = sectionFileOffset;
        for (var index = 0; index < imports.Count; index++)
        {
            var descriptor = section + importTable + index * 20;
            Write32(descriptor, Address(0));
            Write32(descriptor + 12, Address(nameOffsets[index]));
            Write32(descriptor + 16, Address(0));
        }
        for (var index = 0; index < delayImports.Count; index++)
        {
            var descriptor = section + delayTable + index * 32;
            Write32(descriptor, 1);
            Write32(descriptor + 4, Address(nameOffsets[imports.Count + index]));
            Write32(descriptor + 8, Address(0));
            Write32(descriptor + 12, Address(0));
            Write32(descriptor + 16, Address(0));
        }
        Write32(section + exportTable + 16, 1);
        Write32(section + exportTable + 20, (uint)(1 + forwarders.Count));
        Write32(section + exportTable + 28, Address(functionTable));
        Write32(section + functionTable, Address(0));
        for (var index = 0; index < forwarders.Count; index++)
        {
            Write32(section + functionTable + 4 + index * 4, Address(forwarderOffsets[index]));
            WriteText(forwarderOffsets[index], forwarders[index]);
        }
        foreach (var (name, offset) in imports.Concat(delayImports).Zip(nameOffsets))
        {
            WriteText(offset, name);
        }

        return (bytes, new PeLayout(
            coff, optional, sectionTable, sectionFileOffset, sectionAddress,
            section + importTable, section + delayTable, section + exportTable));
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
