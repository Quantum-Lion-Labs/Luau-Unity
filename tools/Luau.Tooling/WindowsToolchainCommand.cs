using System.Diagnostics;

namespace Luau.Tooling;

internal static class WindowsToolchainCommand
{
    private const string ReviewedComponent = "Microsoft.VisualStudio.Component.VC.14.42.17.12.x86.x64";
    private const string ReviewedToolsVersion = "14.42.34433";
    private const string ReviewedCompilerVersion = "19.42.34444.0";
    private const string ReviewedLinkerVersion = "14.42.34444.0";
    private const string ReviewedSdkVersion = "10.0.22621.0";

    public static async Task<int> RunAsync(RepositoryContext repository, CommandLine options)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new ToolingException("windows-toolchain is only supported on Windows.");
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var installerRoot = Path.Combine(programFilesX86, "Microsoft Visual Studio", "Installer");
        var vswhere = Path.Combine(installerRoot, "vswhere.exe");
        FileSystem.RequireFile(vswhere, "Visual Studio locator");
        var installation = (await ProcessRunner.RequireAsync(
            vswhere,
            ["-latest", "-products", "Microsoft.VisualStudio.Product.Enterprise", "-version", "[17.0,18.0)", "-property", "installationPath"],
            repository.Root,
            echo: false)).StandardOutput.Trim();
        FileSystem.RequireDirectory(installation, "Visual Studio 2022 Enterprise installation");

        if (options.Has("--provision"))
        {
            var setup = Path.Combine(installerRoot, "setup.exe");
            FileSystem.RequireFile(setup, "Visual Studio Installer");
            await ProcessRunner.RequireAsync(
                setup,
                ["modify", "--installPath", installation, "--add", ReviewedComponent, "--quiet", "--norestart"],
                repository.Root,
                timeout: TimeSpan.FromMinutes(30));
        }

        Verify(installation, programFilesX86);
        var githubEnvironment = options.Get("--github-env");
        if (!string.IsNullOrWhiteSpace(githubEnvironment))
        {
            File.AppendAllText(githubEnvironment, $"LUAU_HOST_VS_INSTALLATION_PATH={installation}{Environment.NewLine}", FileSystem.Utf8NoBom);
        }

        Console.WriteLine($"Verified MSVC {ReviewedCompilerVersion}, linker {ReviewedLinkerVersion}, toolset {ReviewedToolsVersion}, and Windows SDK {ReviewedSdkVersion}.");
        return 0;
    }

    private static void Verify(string installation, string programFilesX86)
    {
        var tools = Path.Combine(installation, "VC", "Tools", "MSVC", ReviewedToolsVersion, "bin", "Hostx64", "x64");
        var compiler = Path.Combine(tools, "cl.exe");
        var linker = Path.Combine(tools, "link.exe");
        FileSystem.RequireFile(compiler, $"Reviewed MSVC toolset {ReviewedToolsVersion}");
        FileSystem.RequireFile(linker, "Reviewed MSVC linker");
        Require(FileVersionInfo.GetVersionInfo(compiler).FileVersion == ReviewedCompilerVersion,
            $"MSVC compiler version differs from {ReviewedCompilerVersion}.");
        Require(FileVersionInfo.GetVersionInfo(linker).FileVersion == ReviewedLinkerVersion,
            $"MSVC linker version differs from {ReviewedLinkerVersion}.");

        var sdk = Path.Combine(programFilesX86, "Windows Kits", "10");
        foreach (var relative in new[]
        {
            Path.Combine("Include", ReviewedSdkVersion, "um", "Windows.h"),
            Path.Combine("Include", ReviewedSdkVersion, "ucrt", "corecrt.h"),
            Path.Combine("Lib", ReviewedSdkVersion, "um", "x64", "kernel32.lib"),
        })
        {
            FileSystem.RequireFile(Path.Combine(sdk, relative), $"Reviewed Windows SDK {ReviewedSdkVersion} input");
        }
    }

    private static void Require(bool condition, string message) => PackageStaticCommand.Require(condition, message);
}
