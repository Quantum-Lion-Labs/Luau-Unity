using System.Text.Json;

namespace Luau.Tooling;

internal sealed record ReleaseArtifactPolicy(string Path, long MaximumBytes);

internal sealed record ReleasePolicy(
    int SchemaVersion,
    string PackageId,
    string PackageVersion,
    string ReleaseTag,
    string ArchiveFormat,
    string AndroidNdkRevision,
    long MaximumArchiveBytes,
    IReadOnlyList<ReleaseArtifactPolicy> Artifacts)
{
    public static ReleasePolicy Load(RepositoryContext repository)
    {
        var path = repository.PathOf("tools", "UnityPackageReleasePolicy.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        return new ReleasePolicy(
            root.GetProperty("schemaVersion").GetInt32(),
            root.GetProperty("packageId").GetString()!,
            root.GetProperty("packageVersion").GetString()!,
            root.GetProperty("releaseTag").GetString()!,
            root.GetProperty("archiveFormat").GetString()!,
            root.GetProperty("androidNdkRevision").GetString()!,
            root.GetProperty("maximumArchiveBytes").GetInt64(),
            root.GetProperty("artifacts").EnumerateArray()
                .Select(static artifact => new ReleaseArtifactPolicy(
                    artifact.GetProperty("path").GetString()!,
                    artifact.GetProperty("maximumBytes").GetInt64()))
                .ToArray());
    }
}
