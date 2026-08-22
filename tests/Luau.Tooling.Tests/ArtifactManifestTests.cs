using System.Buffers.Binary;
using System.Text;
using Luau.Tooling;

namespace Luau.Tooling.Tests;

public sealed class ArtifactManifestTests
{
    [Fact]
    public void ReadsEmbeddedAbiIdentityRecord()
    {
        var bytes = new byte[256];
        var record = bytes.AsSpan(24);
        "LUAUHABI-PROBE1"u8.CopyTo(record);
        BinaryPrimitives.WriteUInt32LittleEndian(record[16..], 149);
        BinaryPrimitives.WriteUInt32LittleEndian(record[20..], 0x4c554155);
        BinaryPrimitives.WriteUInt16LittleEndian(record[24..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(record[26..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(record[28..], 0xfff);
        record[32] = 8;
        record[33] = 8;
        record[34] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(record[36..], 0xc45f010aabf167ac);
        BinaryPrimitives.WriteUInt64LittleEndian(record[44..], 0xe22f181ac247f52a);
        Encoding.ASCII.GetBytes(new string('a', 64)).CopyTo(record[52..]);
        Encoding.ASCII.GetBytes("Release").CopyTo(record[117..]);

        var identity = ArtifactManifestCommand.ReadIdentity(bytes);

        Assert.Equal(149u, identity.RecordSize);
        Assert.Equal(0x4c554155u, identity.AbiMagic);
        Assert.Equal("Release", identity.BuildConfiguration);
        Assert.Equal(new string('a', 64), identity.BuildInputSha256);
    }
}
