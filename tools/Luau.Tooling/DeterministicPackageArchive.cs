using System.Buffers.Binary;
using System.Text;

namespace Luau.Tooling;

internal static class DeterministicPackageArchive
{
    private static readonly uint[] CrcTable = CreateCrcTable();

    public static byte[] Create(string packageRoot, IReadOnlyList<string> relativeFiles)
    {
        using var tar = new MemoryStream();
        foreach (var relativePath in relativeFiles)
        {
            WriteTarEntry(tar, "package/" + relativePath, File.ReadAllBytes(Path.Combine(packageRoot, relativePath)));
        }

        tar.Write(new byte[1024]);
        return StoredGzip(tar.ToArray());
    }

    internal static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc = (crc >> 8) ^ CrcTable[(crc ^ value) & 0xff];
        }
        return crc ^ uint.MaxValue;
    }

    private static byte[] StoredGzip(byte[] bytes)
    {
        using var stream = new MemoryStream();
        stream.Write([0x1f, 0x8b, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xff]);
        var offset = 0;
        Span<byte> blockHeader = stackalloc byte[4];
        do
        {
            var length = Math.Min(65_535, bytes.Length - offset);
            var final = offset + length == bytes.Length;
            stream.WriteByte(final ? (byte)1 : (byte)0);
            BinaryPrimitives.WriteUInt16LittleEndian(blockHeader, (ushort)length);
            BinaryPrimitives.WriteUInt16LittleEndian(blockHeader[2..], (ushort)(ushort.MaxValue - length));
            stream.Write(blockHeader);
            stream.Write(bytes, offset, length);
            offset += length;
        } while (offset < bytes.Length);

        Span<byte> trailer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer, Crc32(bytes));
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[4..], unchecked((uint)bytes.Length));
        stream.Write(trailer);
        return stream.ToArray();
    }

    private static void WriteTarEntry(Stream stream, string archivePath, byte[] content)
    {
        if (Encoding.ASCII.GetByteCount(archivePath) != archivePath.Length || archivePath.Length > 100)
        {
            throw new ToolingException($"Deterministic archive path must be printable ASCII and at most 100 bytes: {archivePath}");
        }

        var header = new byte[512];
        SetAscii(header, 0, 100, archivePath);
        SetOctal(header, 100, 8, 420);
        SetOctal(header, 108, 8, 0);
        SetOctal(header, 116, 8, 0);
        SetOctal(header, 124, 12, content.Length);
        SetOctal(header, 136, 12, 0);
        Array.Fill(header, (byte)' ', 148, 8);
        header[156] = (byte)'0';
        SetAscii(header, 257, 6, "ustar\0");
        SetAscii(header, 263, 2, "00");
        SetAscii(header, 265, 32, "root");
        SetAscii(header, 297, 32, "root");
        var sum = header.Sum(static value => (int)value);
        SetAscii(header, 148, 8, Convert.ToString(sum, 8).PadLeft(6, '0') + "\0 ");
        stream.Write(header);
        stream.Write(content);
        var padding = (512 - content.Length % 512) % 512;
        stream.Write(new byte[padding]);
    }

    private static void SetAscii(byte[] header, int offset, int length, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        if (bytes.Length > length)
        {
            throw new ToolingException($"Tar header value is too long: {value}");
        }
        Array.Copy(bytes, 0, header, offset, bytes.Length);
    }

    private static void SetOctal(byte[] header, int offset, int length, long value) =>
        SetAscii(header, offset, length, Convert.ToString(value, 8).PadLeft(length - 1, '0') + "\0");

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var entry = index;
            for (var bit = 0; bit < 8; bit++)
            {
                entry = (entry & 1) != 0 ? (entry >> 1) ^ 0xedb88320u : entry >> 1;
            }
            table[index] = entry;
        }
        return table;
    }
}
