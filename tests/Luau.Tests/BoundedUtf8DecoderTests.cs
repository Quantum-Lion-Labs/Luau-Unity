using System.Text;

namespace Luau.Tests;

public sealed class BoundedUtf8DecoderTests
{
    [Theory]
    [InlineData("FFFF", 0, "", true)]
    [InlineData("FFFF", 2, "", true)]
    [InlineData("FFFF", 3, "\uFFFD", true)]
    [InlineData("FFFF", 6, "\uFFFD\uFFFD", false)]
    [InlineData("61FF62", 3, "a", true)]
    [InlineData("61FF62", 4, "a\uFFFD", true)]
    [InlineData("61FF62", 5, "a\uFFFDb", false)]
    [InlineData("F09F9880FF", 6, "😀", true)]
    [InlineData("F09F9880FF", 7, "😀\uFFFD", false)]
    [InlineData("F09F9880", 3, "", true)]
    [InlineData("E282", 2, "", true)]
    [InlineData("E282", 3, "\uFFFD", false)]
    public unsafe void BoundsDecodedOutputIncludingReplacementFallback(
        string hex, int limit, string expected, bool expectedTruncated)
    {
        var bytes = Convert.FromHexString(hex);
        fixed (byte* pointer = bytes)
        {
            var text = BoundedUtf8Decoder.Decode(pointer, (ulong)bytes.Length, limit, out var truncated);
            Assert.Equal(expected, text);
            Assert.Equal(expectedTruncated, truncated);
            Assert.InRange(Encoding.UTF8.GetByteCount(text), 0, limit);
        }
    }

    [Fact]
    public void DisplayStringReportsTruncationForMalformedUtf8AtInputLimit()
    {
        using var root = LuauState.Create();
        root.OpenStringLibrary();
        using var capture = root.CreateFunction(context =>
        {
            var text = context.ToDisplayString(0, 4096, out var truncated);
            Assert.True(truncated);
            Assert.Equal(new string('\uFFFD', 1365), text);
            Assert.Equal(4095, Encoding.UTF8.GetByteCount(text));
        });
        root["capture"] = capture;
        using var result = root.DoString("capture(string.rep(string.char(255), 4096))");
    }
}
