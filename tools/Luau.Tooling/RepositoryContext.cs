namespace Luau.Tooling;

internal sealed record RepositoryContext(string Root)
{
    public static RepositoryContext Discover(string? start = null)
    {
        var candidates = new[]
        {
            start,
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
        };

        foreach (var candidate in candidates.Where(static path => !string.IsNullOrWhiteSpace(path)))
        {
            var directory = new DirectoryInfo(Path.GetFullPath(candidate!));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Luau.slnx")) &&
                    Directory.Exists(Path.Combine(directory.FullName, "Luau.Unity")))
                {
                    return new RepositoryContext(directory.FullName);
                }

                directory = directory.Parent;
            }
        }

        throw new ToolingException(
            "Unable to locate the Luau.Unity repository root. Run this command from within the checkout.");
    }

    public string PathOf(params string[] segments)
    {
        var parts = new string[segments.Length + 1];
        parts[0] = Root;
        Array.Copy(segments, 0, parts, 1, segments.Length);
        return Path.GetFullPath(Path.Combine(parts));
    }
}
