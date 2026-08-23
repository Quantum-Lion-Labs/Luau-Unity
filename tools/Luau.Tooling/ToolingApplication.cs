namespace Luau.Tooling;

internal static class ToolingApplication
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            {
                PrintHelp();
                return 0;
            }

            var repository = RepositoryContext.Discover();
            var options = new CommandLine(args.Skip(1));
            return args[0] switch
            {
                "doctor" => await DoctorCommand.RunAsync(repository, options),
                "managed-artifacts" => await ManagedArtifactsCommand.RunAsync(repository, options),
                "host-soak" => await HostSoakCommand.RunAsync(repository, options),
                "managed-harness-selection" => await ManagedHarnessSelectionCommand.RunAsync(repository, options),
                "unity-test" => await UnityHostCommand.RunAsync(repository, options),
                "package-static" => await PackageStaticCommand.RunAsync(repository, options),
                "package-release" => await PackageReleaseCommand.RunAsync(repository, options),
                "package-consumer" => await PackageConsumerCommand.RunAsync(repository, options),
                "artifact-manifest" => await ArtifactManifestCommand.RunAsync(repository, options),
                "native-artifacts" => await NativeArtifactsCommand.RunAsync(repository, options),
                "windows-toolchain" => await WindowsToolchainCommand.RunAsync(repository, options),
                "release-source" => await ReleaseCiCommand.RequireSourceAsync(repository, options),
                "release-metadata" => await ReleaseCiCommand.WriteMetadataAsync(repository, options),
                "validate-linux" => await ValidateLinuxCommand.RunAsync(repository, options),
                _ => throw new ToolingException($"Unknown command '{args[0]}'. Run with --help for usage."),
            };
        }
        catch (ToolingException exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Luau.Unity cross-platform repository tooling");
        Console.WriteLine();
        Console.WriteLine("Usage: dotnet run --project tools/Luau.Tooling -- <command> [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  doctor   Validate local development prerequisites");
        Console.WriteLine("  managed-artifacts          Refresh or check managed package artifacts");
        Console.WriteLine("  host-soak                  Run the native host lifetime/fault soak");
        Console.WriteLine("  managed-harness-selection  Prove platform-native harness selection");
        Console.WriteLine("  unity-test                 Prepare/test disposable Unity projects and player smokes");
        Console.WriteLine("  package-static             Validate shipping package boundaries");
        Console.WriteLine("  package-release            Build/check deterministic release package");
        Console.WriteLine("  package-consumer           Validate a generated minimal Unity consumer");
        Console.WriteLine("  artifact-manifest          Audit a shipping host and write provenance");
        Console.WriteLine("  native-artifacts           Refresh/check audited shipping native plugins");
        Console.WriteLine("  windows-toolchain          Provision/verify the reviewed Windows toolchain");
        Console.WriteLine("  release-source             Validate canonical release source ancestry");
        Console.WriteLine("  release-metadata           Validate tag and export package metadata");
        Console.WriteLine("  validate-linux             Run the complete local Linux acceptance suite");
    }
}
