namespace Luau.Tooling;

internal static class ManagedHarnessSelectionCommand
{
    public static async Task<int> RunAsync(RepositoryContext repository, CommandLine options)
    {
        var temp = Path.GetFullPath(Path.GetTempPath());
        var scratch = Path.Combine(temp, "luau-managed-harness-selection-" + Guid.NewGuid().ToString("N"));
        var succeeded = false;
        try
        {
            Directory.CreateDirectory(scratch);
            FileSystem.CopyFile(repository.PathOf("Directory.Build.props"), Path.Combine(scratch, "Directory.Build.props"));
            FileSystem.CopyFile(
                repository.PathOf("tools", "harness", "Luau.Interop.csproj"),
                Path.Combine(scratch, "tools", "harness", "Luau.Interop.csproj"));
            foreach (var file in new[] { "AssemblyInfo.cs", "NativeTypes.cs", "NativeMethods.cs" })
            {
                FileSystem.CopyFile(
                    repository.PathOf("Luau.Unity", "Runtime", "Interop", file),
                    Path.Combine(scratch, "Luau.Unity", "Runtime", "Interop", file));
            }

            string selectedSource;
            string selectedDestination;
            string ignoredDestination;
            string outputName;
            if (OperatingSystem.IsWindows())
            {
                selectedSource = repository.PathOf("Luau.Unity", "Runtime", "Plugins", "win-x64", "luau_host.dll");
                selectedDestination = Path.Combine(scratch, "Luau.Unity", "Runtime", "Plugins", "win-x64", "luau_host.dll");
                ignoredDestination = Path.Combine(scratch, "native", "luau-host", "out", "install", "linux-x64", "libluau_host.so");
                outputName = "luau_host.dll";
            }
            else if (OperatingSystem.IsLinux())
            {
                selectedSource = repository.PathOf("native", "luau-host", "out", "install", "linux-x64", "libluau_host.so");
                selectedDestination = Path.Combine(scratch, "native", "luau-host", "out", "install", "linux-x64", "libluau_host.so");
                ignoredDestination = Path.Combine(scratch, "Luau.Unity", "Runtime", "Plugins", "win-x64", "luau_host.dll");
                outputName = "libluau_host.so";
            }
            else
            {
                throw new ToolingException("Managed harness selection is only defined for Windows and Linux.");
            }

            FileSystem.CopyFile(selectedSource, selectedDestination);
            FileSystem.WriteUtf8(ignoredDestination, "ignored native build sentinel\n");
            var selectedHash = Hashing.FileSha256(selectedDestination);
            var ignoredHash = Hashing.FileSha256(ignoredDestination);
            if (selectedHash.Equals(ignoredHash, StringComparison.Ordinal))
            {
                throw new ToolingException("Selected plugin and ignored sentinel unexpectedly have the same hash.");
            }

            await ProcessRunner.RequireAsync(
                "dotnet",
                ["build", "tools/harness/Luau.Interop.csproj", "--configuration", "Release", "--nologo", "--verbosity", "minimal"],
                scratch);

            var output = Path.Combine(scratch, "tools", "harness", "bin", "Release", "netstandard2.1", outputName);
            FileSystem.RequireFile(output, "Disposable harness native output");
            if (!Hashing.FileSha256(output).Equals(selectedHash, StringComparison.Ordinal))
            {
                throw new ToolingException("Harness output did not select the platform-default native artifact.");
            }

            Console.WriteLine($"Managed harness native selection passed: {output} (SHA256={selectedHash}).");
            succeeded = true;
            return 0;
        }
        finally
        {
            if (succeeded)
            {
                PathSafety.DeleteDisposableDirectory(scratch, temp, "luau-managed-harness-selection-");
            }
            else
            {
                Console.Error.WriteLine($"Managed harness selection diagnostics retained at: {scratch}");
            }
        }
    }
}
