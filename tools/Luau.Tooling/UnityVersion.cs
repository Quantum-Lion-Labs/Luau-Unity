using System.Text.RegularExpressions;

namespace Luau.Tooling;

internal readonly record struct UnityVersion(int Major, int Minor, int Patch, string Suffix)
{
    private static readonly Regex Pattern = new(
        @"^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?<suffix>[a-z]+\d+)$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    public static UnityVersion Parse(string value)
    {
        var match = Pattern.Match(value);
        if (!match.Success)
        {
            throw new ToolingException($"Invalid Unity editor version: '{value}'. Expected a value such as 6000.3.20f1.");
        }

        return new UnityVersion(
            int.Parse(match.Groups["major"].Value),
            int.Parse(match.Groups["minor"].Value),
            int.Parse(match.Groups["patch"].Value),
            match.Groups["suffix"].Value);
    }

    public bool IsInStream(int major, int minor) => Major == major && Minor == minor;

    public override string ToString() => $"{Major}.{Minor}.{Patch}{Suffix}";
}
