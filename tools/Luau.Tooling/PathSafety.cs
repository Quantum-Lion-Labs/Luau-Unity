namespace Luau.Tooling;

internal static class PathSafety
{
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static bool IsStrictDescendant(string candidate, string parent)
    {
        var parentFullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var candidateFullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        if (candidateFullPath.Equals(parentFullPath, PathComparison))
        {
            return false;
        }

        var prefix = parentFullPath + Path.DirectorySeparatorChar;
        return candidateFullPath.StartsWith(prefix, PathComparison);
    }

    public static void DeleteDisposableDirectory(string candidate, string parent, string requiredLeafPrefix)
    {
        var fullCandidate = Path.GetFullPath(candidate);
        if (!IsStrictDescendant(fullCandidate, parent) ||
            !Path.GetFileName(fullCandidate).StartsWith(requiredLeafPrefix, StringComparison.Ordinal))
        {
            throw new ToolingException($"Refusing to remove unexpected disposable path: {fullCandidate}");
        }

        if (Directory.Exists(fullCandidate))
        {
            Directory.Delete(fullCandidate, recursive: true);
        }
    }
}
