using System.Text;

namespace Luau;

/// <summary>
/// Central UTF-8 decoder for native diagnostics and deliberately truncated
/// display text. It never reads beyond the configured bound and never ends a
/// truncation in the middle of a UTF-8 sequence. Decoded text also fits the
/// bound when encoded as UTF-8, including replacement characters.
/// </summary>
internal static unsafe class BoundedUtf8Decoder
{
    internal static string Decode(byte* value, ulong length, int maxUtf8Bytes, out bool truncated)
    {
        if (maxUtf8Bytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxUtf8Bytes));
        }
        if (value == null || length == 0 || maxUtf8Bytes == 0)
        {
            truncated = length != 0;
            return string.Empty;
        }

        var boundedLength = GetValidPrefixLength(value, length, maxUtf8Bytes);

        truncated = length > (ulong)boundedLength;
        var decoded = boundedLength == 0
            ? string.Empty
            : Encoding.UTF8.GetString(new ReadOnlySpan<byte>(value, boundedLength));

        // Invalid input can expand: one 0xff byte becomes a three-byte U+FFFD.
        // Bound the decoded representation as well as the native read. The
        // decoder produces valid UTF-16, so surrogate pairs must stay together.
        var remainingBytes = maxUtf8Bytes;
        for (var index = 0; index < decoded.Length; index++)
        {
            var character = decoded[index];
            var isPair = char.IsHighSurrogate(character) && index + 1 < decoded.Length &&
                char.IsLowSurrogate(decoded[index + 1]);
            var byteCount = isPair ? 4 : character <= '\u007f' ? 1 : character <= '\u07ff' ? 2 : 3;
            if (byteCount > remainingBytes)
            {
                truncated = true;
                return decoded.Substring(0, index);
            }

            remainingBytes -= byteCount;
            if (isPair)
                index++;
        }

        return decoded;
    }

    internal static string DecodeDiagnostic(byte* value, ulong length, int maxUtf8Bytes)
    {
        return Decode(value, length, maxUtf8Bytes, out _);
    }

    internal static int GetValidPrefixLength(byte* value, ulong length, int maxUtf8Bytes)
    {
        var boundedLength = length > (ulong)maxUtf8Bytes
            ? maxUtf8Bytes
            : checked((int)length);
        return length > (ulong)boundedLength
            ? TrimIncompleteSequence(value, boundedLength)
            : boundedLength;
    }

    static int TrimIncompleteSequence(byte* value, int length)
    {
        if (length == 0)
        {
            return 0;
        }

        var sequenceStart = length - 1;
        while (sequenceStart > 0 &&
               (value[sequenceStart] & 0xc0) == 0x80 &&
               length - sequenceStart < 4)
        {
            sequenceStart--;
        }

        var lead = value[sequenceStart];
        var expected = (lead & 0x80) == 0 ? 1
            : (lead & 0xe0) == 0xc0 ? 2
            : (lead & 0xf0) == 0xe0 ? 3
            : (lead & 0xf8) == 0xf0 ? 4
            : 1;
        return sequenceStart + expected > length ? sequenceStart : length;
    }
}
