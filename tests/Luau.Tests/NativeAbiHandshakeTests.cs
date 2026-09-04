namespace Luau.Tests;

public sealed unsafe class NativeAbiHandshakeTests
{
    [Fact]
    public void CurrentPluginReportsMatchingHostAbiLayoutFeaturesTagsAndFingerprints()
    {
        var info = LuauNativeProtection.AbiVerifier.Info;

        Assert.Equal(112, sizeof(LuauNativeAbiInfo));
        Assert.Equal(LuauNativeProtection.ExpectedAbiRecordSize, info.struct_size);
        Assert.Equal(LuauNativeProtection.ExpectedAbiMagic, info.magic);
        Assert.Equal(LuauNativeProtection.ExpectedAbiMajor, info.abi_major);
        Assert.Equal(LuauNativeProtection.MinimumAbiMinor, info.abi_minor);
        Assert.Equal(
            LuauNativeProtection.ExpectedFeatureFlags,
            info.feature_flags & LuauNativeProtection.ExpectedFeatureFlags);
        Assert.Equal((byte)sizeof(void*), info.pointer_size);
        Assert.Equal((byte)sizeof(nuint), info.size_t_size);
        Assert.Equal((byte)1, info.little_endian);
        Assert.Equal((uint)32, info.compile_options_size);
        Assert.Equal((uint)(IntPtr.Size == 8 ? 48 : 40), info.callback_table_size);
        Assert.Equal((uint)16, info.state_options_size);
        Assert.Equal((uint)48, info.memory_info_size);
        Assert.Equal((uint)16, info.buffer_size);
        Assert.Equal(LuauNativeProtection.ExpectedUpstreamRevisionHash, info.upstream_revision_hash);
        Assert.Equal(LuauNativeProtection.ExpectedHostBuildFingerprint, info.host_build_fingerprint);

        LuauNativeAbiVerifier.Validate(info);
    }

    [Fact]
    public void RootCreationRejectsWrongHostAbiMajorBeforeNativeStateCreation()
    {
        var info = CreateMatchingInfo();
        info.abi_major++;
        var verifier = CreateVerifier(info);

        var exception = Assert.Throws<PlatformNotSupportedException>(
            () => LuauState.Create(LuauStateOptions.Default, verifier));

        Assert.Contains("host ABI", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Upstream fingerprint", exception.Message, StringComparison.Ordinal);
        Assert.Contains("host build fingerprint", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RootCreationRejectsMissingRequiredFeatureBeforeNativeStateCreation()
    {
        var info = CreateMatchingInfo();
        info.feature_flags &= ~(1U << 2);
        var verifier = CreateVerifier(info);

        var exception = Assert.Throws<PlatformNotSupportedException>(
            () => LuauState.Create(LuauStateOptions.Default, verifier));

        Assert.Contains("host-owned compiler buffers", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RootCreationRejectsHostsWithoutCoroutineSuspension()
    {
        var info = CreateMatchingInfo();
        info.feature_flags &= ~(1U << 12);
        var verifier = CreateVerifier(info);

        var exception = Assert.Throws<PlatformNotSupportedException>(
            () => LuauState.Create(LuauStateOptions.Default, verifier));

        Assert.Contains("coroutine suspension", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("integer")]
    [InlineData("class")]
    [InlineData("object")]
    public void RootCreationRejectsWrongHostTypeTagBeforeNativeStateCreation(string tag)
    {
        var info = CreateMatchingInfo();
        switch (tag)
        {
            case "integer": info.type_integer++; break;
            case "class": info.type_class++; break;
            case "object": info.type_object++; break;
            default: throw new ArgumentOutOfRangeException(nameof(tag));
        }
        var verifier = CreateVerifier(info);

        var exception = Assert.Throws<PlatformNotSupportedException>(
            () => LuauState.Create(LuauStateOptions.Default, verifier));

        Assert.Contains($"{tag} type tag", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifierRejectsWrongPointerSizeWithFingerprintDiagnostics()
    {
        var info = CreateMatchingInfo();
        info.pointer_size = info.pointer_size == 8 ? (byte)4 : (byte)8;
        var verifier = CreateVerifier(info);

        var exception = Assert.Throws<PlatformNotSupportedException>(verifier.EnsureAvailable);

        Assert.Contains("pointer size", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"{LuauNativeProtection.ExpectedHostBuildFingerprint:x16}",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void VerifierRejectsWrongOfficialUpstreamFingerprint()
    {
        var info = CreateMatchingInfo();
        info.upstream_revision_hash++;
        var verifier = CreateVerifier(info);

        var exception = Assert.Throws<PlatformNotSupportedException>(verifier.EnsureAvailable);

        Assert.Contains("upstream fingerprint", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            $"{LuauNativeProtection.ExpectedUpstreamRevisionHash:x16}",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void VerifierRejectsWrongHostBuildFingerprint()
    {
        var info = CreateMatchingInfo();
        info.host_build_fingerprint++;
        var verifier = CreateVerifier(info);

        var exception = Assert.Throws<PlatformNotSupportedException>(verifier.EnsureAvailable);

        Assert.Contains("build fingerprint", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            $"{LuauNativeProtection.ExpectedHostBuildFingerprint:x16}",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StandaloneCompilerRejectsWrongMagicBeforeNativeCompilation()
    {
        var info = CreateMatchingInfo();
        info.magic++;
        var verifier = CreateVerifier(info);

        var exception = Assert.Throws<PlatformNotSupportedException>(
            () => LuauCompiler.Compile("return 42"u8, options: null, verifier));

        Assert.Contains("ABI magic", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifierRejectsUndersizedSelfDescriptionRecord()
    {
        var info = CreateMatchingInfo();
        info.struct_size--;
        var verifier = CreateVerifier(info);

        var exception = Assert.Throws<PlatformNotSupportedException>(verifier.EnsureAvailable);

        Assert.Contains("ABI record", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidQueryStatusReportsCallerAndHostRecordSizes()
    {
        var verifier = new LuauNativeAbiVerifier((output, _) =>
        {
            output->struct_size = 144;
            output->upstream_revision_hash = LuauNativeProtection.ExpectedUpstreamRevisionHash;
            output->host_build_fingerprint = 1;
            return LuauNativeProtection.AbiQueryInvalidArgument;
        });

        var exception = Assert.Throws<PlatformNotSupportedException>(verifier.EnsureAvailable);

        Assert.Contains("112-byte caller record", exception.Message, StringComparison.Ordinal);
        Assert.Contains("144 bytes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MatchingHandshakePrecedesStandaloneCompilationAndRootCreation()
    {
        LuauNativeProtection.EnsureAvailable();

        Assert.True(LuauCompiler.Compile("return 42"u8).BytecodeLength > 0);
        using var state = LuauState.Create();
        Assert.False(state.IsDisposed);
    }

    [Fact]
    public void ConcurrentIndependentRootCreationRunsInjectedHandshakeOnce()
    {
        var info = CreateMatchingInfo();
        var queryCount = 0;
        var verifier = CreateVerifier(info, () =>
        {
            Interlocked.Increment(ref queryCount);
            Thread.Sleep(25);
        });

        Parallel.For(0, 16, _ =>
        {
            using var state = LuauState.Create(LuauStateOptions.Default, verifier);
            Assert.True(state.IsMainThread);
        });

        Assert.Equal(1, queryCount);
    }

    static LuauNativeAbiVerifier CreateVerifier(
        LuauNativeAbiInfo info,
        Action? beforeQuery = null)
    {
        return new LuauNativeAbiVerifier((output, outputSize) =>
        {
            beforeQuery?.Invoke();
            if (output == null || outputSize < sizeof(LuauNativeAbiInfo))
            {
                return LuauNativeProtection.AbiQueryInvalidArgument;
            }

            *output = info;
            return LuauNativeProtection.AbiQueryOk;
        });
    }

    static LuauNativeAbiInfo CreateMatchingInfo()
    {
        return new LuauNativeAbiInfo
        {
            struct_size = checked((uint)sizeof(LuauNativeAbiInfo)),
            magic = LuauNativeProtection.ExpectedAbiMagic,
            abi_major = LuauNativeProtection.ExpectedAbiMajor,
            abi_minor = LuauNativeProtection.MinimumAbiMinor,
            feature_flags = LuauNativeProtection.ExpectedFeatureFlags,
            pointer_size = checked((byte)sizeof(void*)),
            size_t_size = checked((byte)sizeof(nuint)),
            little_endian = BitConverter.IsLittleEndian ? (byte)1 : (byte)0,
            compile_options_size = 32,
            callback_table_size = checked((uint)(IntPtr.Size == 8 ? 48 : 40)),
            state_options_size = 16,
            memory_info_size = 48,
            buffer_size = 16,
            type_nil = (int)LuauType.Nil,
            type_boolean = (int)LuauType.Boolean,
            type_lightuserdata = (int)LuauType.LightUserData,
            type_number = (int)LuauType.Number,
            type_integer = (int)LuauType.Integer,
            type_vector = (int)LuauType.Vector,
            type_string = (int)LuauType.String,
            type_table = (int)LuauType.Table,
            type_function = (int)LuauType.Function,
            type_userdata = (int)LuauType.UserData,
            type_thread = (int)LuauType.Thread,
            type_buffer = (int)LuauType.Buffer,
            type_class = 12,
            type_object = 13,
            upstream_revision_hash = LuauNativeProtection.ExpectedUpstreamRevisionHash,
            host_build_fingerprint = LuauNativeProtection.ExpectedHostBuildFingerprint,
        };
    }
}
