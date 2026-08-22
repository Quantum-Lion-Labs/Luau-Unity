using System.Text.RegularExpressions;

namespace Luau.Tooling;

internal sealed class CMakeCache
{
    private readonly string _text;

    public CMakeCache(string path)
    {
        FileSystem.RequireFile(path, "Configured CMake cache");
        _text = File.ReadAllText(path);
    }

    public string Get(string name)
    {
        var match = Regex.Match(
            _text,
            "(?m)^" + Regex.Escape(name) + @":[^=]+=(.+)$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            throw new ToolingException($"CMake cache does not define {name}.");
        }

        return match.Groups[1].Value.Trim();
    }

    public string? TryGet(string name)
    {
        var match = Regex.Match(
            _text,
            "(?m)^" + Regex.Escape(name) + @":[^=]+=(.+)$",
            RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    public static string GetSetValue(string text, string name)
    {
        var match = Regex.Match(
            text,
            "(?m)^set\\(" + Regex.Escape(name) + "\\s+(?:\"(?<quoted>[^\"]*)\"|(?<plain>[^\\)\\r\\n]*))\\)",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            throw new ToolingException($"Unable to derive generated CMake value {name}.");
        }

        return match.Groups["quoted"].Success
            ? match.Groups["quoted"].Value
            : match.Groups["plain"].Value.Trim();
    }
}
