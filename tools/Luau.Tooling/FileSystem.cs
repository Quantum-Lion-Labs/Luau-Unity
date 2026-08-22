using System.Text;

namespace Luau.Tooling;

internal static class FileSystem
{
    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void RequireFile(string path, string description)
    {
        if (!File.Exists(path))
        {
            throw new ToolingException($"{description} is missing: {path}");
        }
    }

    public static void RequireDirectory(string path, string description)
    {
        if (!Directory.Exists(path))
        {
            throw new ToolingException($"{description} is missing: {path}");
        }
    }

    public static void CopyFile(string source, string destination, bool overwrite = true)
    {
        RequireFile(source, "Required source file");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite);
    }

    public static void CopyDirectory(string source, string destination)
    {
        RequireDirectory(source, "Required source directory");
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            CopyFile(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }

    public static void WriteUtf8(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, Utf8NoBom);
    }
}
