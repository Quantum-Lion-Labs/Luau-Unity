using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Luau.ArtifactFuzz;

static class ArtifactSeedCorpus
{
    const int HeaderLength = 128;
    const int IntegrityLength = 32;
    const int MaximumEnvelopeBytes = 1024 * 1024;
    const int MaximumBytecodeBytes = 768 * 1024;

    // These are the exact identities frozen with ABI 2.0. Keeping the valid
    // parser seed explicit makes an ABI/codec change require a reviewed corpus
    // update instead of silently losing deep parser coverage.
    const ulong UpstreamRevisionHash = 0xc45f010aabf167acUL;
    const ulong HostBuildFingerprint = 0x2a4ca2b50dc114daUL;

    public static IReadOnlyList<ArtifactSeed> CreateStructuralSeeds()
    {
        var valid = CreateValidEnvelope();
        var nearLimitValid = CreateValidEnvelope(MaximumBytecodeBytes);
        var exactEnvelopeLimitGarbage = new byte[MaximumEnvelopeBytes];
        exactEnvelopeLimitGarbage.AsSpan().Fill(0xa5);
        var sourceIdentityLength = Encoding.UTF8.GetByteCount("seed/artifact");

        return
        [
            new("built-in/valid-minimal", valid, RequiresSuccessfulParse: true),
            new(
                "built-in/valid-near-bytecode-limit",
                nearLimitValid,
                RequiresSuccessfulParse: true,
                ParticipatesInMutation: false),
            new(
                "built-in/malformed-exact-envelope-limit",
                exactEnvelopeLimitGarbage,
                RequiresSuccessfulParse: false,
                ParticipatesInMutation: false),
            new("built-in/truncated-header", valid[..127], RequiresSuccessfulParse: false),
            new("built-in/truncated-integrity", valid[..^1], RequiresSuccessfulParse: false),
            new("built-in/trailing-byte", Append(valid, 0xa5), RequiresSuccessfulParse: false),
            new("built-in/integrity-corruption", ChangeByte(valid, valid.Length - 1, 0x80), false),
            new("built-in/negative-source-identity-length", ChangeInt32(valid, 112, -1, false), false),
            new("built-in/max-bytecode-length", ChangeInt32(valid, 124, int.MaxValue, false), false),
            new("built-in/invalid-compile-options", ChangeInt32(valid, 16, int.MaxValue, true), false),
            new("built-in/runtime-identity-mismatch", ChangeUInt64(valid, 40, 0, true), false),
            new("built-in/invalid-source-utf8", ChangeByte(valid, HeaderLength, 0xff, true), false),
            new(
                "built-in/invalid-provenance-utf8",
                ChangeByte(valid, HeaderLength + sourceIdentityLength, 0xff, true),
                false),
            new("built-in/bytecode-hash-mismatch", ChangeByte(valid, 80, 0x01, true), false),
        ];
    }

    static byte[] CreateValidEnvelope(int bytecodeLength = 6)
    {
        var bytecode = new byte[bytecodeLength];
        for (var index = 0; index < bytecode.Length; index++)
        {
            bytecode[index] = (byte)(index * 131 + 17);
        }
        var sourceHash = LowerHex(SHA256.HashData("return 1"u8));
        var bytecodeHash = LowerHex(SHA256.HashData(bytecode));
        var artifact = new LuauBytecodeArtifact(
            LuauBytecodeArtifact.CurrentSchemaVersion,
            bytecode,
            new LuauCompileOptions
            {
                OptimizationLevel = 1,
                DebugLevel = 1,
                TypeInfoLevel = 1,
                CoverageLevel = 0,
            },
            UpstreamRevisionHash,
            HostBuildFingerprint,
            "seed/artifact",
            sourceHash,
            bytecodeHash,
            "fuzz/v1",
            [0x00, 0xff, 0x10, 0x20]);

        return LuauBytecodeArtifactCodec.Write(artifact);
    }

    static string LowerHex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(bytes).ToLowerInvariant();

    static byte[] Append(byte[] source, byte value)
    {
        var result = new byte[source.Length + 1];
        source.CopyTo(result, 0);
        result[^1] = value;
        return result;
    }

    static byte[] ChangeByte(byte[] source, int offset, byte xor, bool refreshIntegrity = false)
    {
        var result = (byte[])source.Clone();
        result[offset] ^= xor;
        if (refreshIntegrity)
        {
            RefreshIntegrity(result);
        }

        return result;
    }

    static byte[] ChangeInt32(byte[] source, int offset, int value, bool refreshIntegrity)
    {
        var result = (byte[])source.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset, sizeof(int)), value);
        if (refreshIntegrity)
        {
            RefreshIntegrity(result);
        }

        return result;
    }

    static byte[] ChangeUInt64(byte[] source, int offset, ulong value, bool refreshIntegrity)
    {
        var result = (byte[])source.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(offset, sizeof(ulong)), value);
        if (refreshIntegrity)
        {
            RefreshIntegrity(result);
        }

        return result;
    }

    static void RefreshIntegrity(byte[] encoded)
    {
        var digest = SHA256.HashData(encoded.AsSpan(0, encoded.Length - IntegrityLength));
        digest.CopyTo(encoded, encoded.Length - IntegrityLength);
    }
}

sealed record ArtifactSeed(
    string Name,
    byte[] Data,
    bool RequiresSuccessfulParse,
    bool ParticipatesInMutation = true);
