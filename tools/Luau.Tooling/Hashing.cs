using System.Security.Cryptography;
using System.Text;

namespace Luau.Tooling;

internal static class Hashing
{
    public static string FileSha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    public static byte[] CanonicalUtf8Bytes(string path)
    {
        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var text = strictUtf8.GetString(File.ReadAllBytes(path));
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
    }
}
