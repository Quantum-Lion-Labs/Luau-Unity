using Luau.Tooling;

namespace Luau.Tooling.Tests;

public sealed class NativeArtifactsCommandTests
{
    [Fact]
    public void ParsesNeededLibrariesFromGnuAndLlvmReadelf()
    {
        const string gnu = """
             0x0000000000000001 (NEEDED)             Shared library: [libm.so.6]
             0x0000000000000001 (NEEDED)             Shared library: [libc.so.6]
             0x000000000000001e (FLAGS)              BIND_NOW
            """;
        const string llvm = """
              0x0000000000000001 (NEEDED)       Shared library: [libm.so.6]
              0x0000000000000001 (NEEDED)       Shared library: [ld-linux-x86-64.so.2]
            """;

        Assert.Equal(["libm.so.6", "libc.so.6"], NativeArtifactsCommand.ParseNeededLibraries(gnu));
        Assert.Equal(["libm.so.6", "ld-linux-x86-64.so.2"], NativeArtifactsCommand.ParseNeededLibraries(llvm));
        Assert.Empty(NativeArtifactsCommand.ParseNeededLibraries("  0x1e (FLAGS) BIND_NOW"));
    }

    [Fact]
    public void ParsesDistinctGlibcVersionsFromGnuAndLlvmReadelf()
    {
        const string gnu = """
            Version needs section '.gnu.version_r' contains 1 entry:
              000000: Version: 1  File: libc.so.6  Cnt: 3
              0x0010:   Name: GLIBC_2.35  Flags: none  Version: 4
              0x0020:   Name: GLIBC_2.2.5  Flags: none  Version: 2
              0x0030:   Name: GLIBC_2.34  Flags: none  Version: 3
            """;
        const string llvm = """
              004:   2 (GLIBC_2.2.5)   2 (GLIBC_2.2.5)   3 (GLIBC_2.14)    4 (GLIBC_2.4)
              008:   5 (GLIBC_2.35)    2 (GLIBC_2.2.5)   0 (*local*)       1 (*global*)
            """;

        Assert.Equal(new Version(2, 35), NativeArtifactsCommand.ParseGlibcVersions(gnu).Max());
        Assert.Equal(3, NativeArtifactsCommand.ParseGlibcVersions(gnu).Length);
        Assert.Equal(new Version(2, 35), NativeArtifactsCommand.ParseGlibcVersions(llvm).Max());
        Assert.Equal(4, NativeArtifactsCommand.ParseGlibcVersions(llvm).Length);
        Assert.Empty(NativeArtifactsCommand.ParseGlibcVersions("GLIBCXX_3.4.30 CXXABI_1.3.13"));
    }

    [Fact]
    public void ParsesImportAndDelayLoadDependenciesFromDumpbin()
    {
        const string kernelOnly = "Dump of file D:\\a\\out\\shipping\\win-x64\\luau_host.dll\r\n\r\nFile Type: DLL\r\n\r\n" +
            "  Image has the following dependencies:\r\n\r\n    KERNEL32.dll\r\n\r\n  Summary\r\n\r\n" +
            "        5000 .data\r\n        A000 .pdata\r\n       2A000 .rdata\r\n        2000 .reloc\r\n       DC000 .text\r\n";
        const string withDelayLoads = """
            Dump of file msdia140.dll

            File Type: DLL

              Image has the following dependencies:

                KERNEL32.dll
                ole32.dll
                SHLWAPI.dll

              Image has the following delay load dependencies:

                ADVAPI32.dll
                OLEAUT32.dll

              Summary

                    1000 .data
            """;

        var (imports, delayImports) = NativeArtifactsCommand.ParseDumpbinDependents(kernelOnly);
        Assert.Equal(["KERNEL32.dll"], imports);
        Assert.Empty(delayImports);

        (imports, delayImports) = NativeArtifactsCommand.ParseDumpbinDependents(withDelayLoads);
        Assert.Equal(["KERNEL32.dll", "ole32.dll", "SHLWAPI.dll"], imports);
        Assert.Equal(["ADVAPI32.dll", "OLEAUT32.dll"], delayImports);

        Assert.Throws<ToolingException>(() => NativeArtifactsCommand.ParseDumpbinDependents("LINK : fatal error LNK1181: cannot open input file"));
    }

    [Fact]
    public async Task ArtifactManifestRejectsPresetForAnotherPlatform()
    {
        var repository = RepositoryContext.Discover();
        var exception = await Assert.ThrowsAsync<ToolingException>(() => ArtifactManifestCommand.WriteAsync(
            repository, "missing.dll", "win-x64", "unused.json", preset: "linux-x64-shipping"));
        Assert.Contains("does not build platform win-x64", exception.Message);
    }
}
