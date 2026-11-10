namespace Luau.Tests;

public sealed class BytecodeCapabilityTests
{
    [Fact]
    public void CoverageIsOptInAndChangesCompilerOutputIdentity()
    {
        const string sourceText = "local function answer() return 42 end; return answer()";
        var source = System.Text.Encoding.UTF8.GetBytes(sourceText);
        var defaults = new LuauCompileOptions();
        var instrumentedOptions = LuauCompileOptions.Default with { CoverageLevel = 2 };

        var ordinary = LuauCompiler.Compile(source, defaults);
        var instrumented = LuauCompiler.Compile(source, instrumentedOptions);

        Assert.Equal(0, LuauCompileOptions.Default.CoverageLevel);
        Assert.Equal(0, defaults.CoverageLevel);
        Assert.Equal(0, ordinary.CompileOptions.CoverageLevel);
        Assert.Equal(2, instrumented.CompileOptions.CoverageLevel);
        Assert.Equal(ordinary.SourceSha256, instrumented.SourceSha256);
        Assert.NotEqual(ordinary.BytecodeSha256, instrumented.BytecodeSha256);
        Assert.False(ordinary.ToBytecodeArray().AsSpan().SequenceEqual(instrumented.ToBytecodeArray()));
    }

    [Fact]
    public void CompilerOutputIsSuccessOnlyOpaqueAndDefensivelyCopied()
    {
        var options = new LuauCompileOptions
        {
            OptimizationLevel = 2,
            DebugLevel = 0,
        };

        var output = LuauCompiler.Compile("return 42"u8, options);

        Assert.True(output.BytecodeLength > 0);
        Assert.Equal(2, output.CompileOptions.OptimizationLevel);
        Assert.Equal(64, output.SourceSha256.Length);
        Assert.Equal(64, output.BytecodeSha256.Length);
        Assert.NotEqual(0ul, output.UpstreamRevisionHash);
        Assert.NotEqual(0ul, output.HostBuildFingerprint);

        var expected = output.ToBytecodeArray();
        var mutated = output.ToBytecodeArray();
        mutated[^1] ^= 0xff;
        Assert.Equal(expected, output.ToBytecodeArray());
        Assert.NotEqual(mutated[^1], output.ToBytecodeArray()[^1]);
    }

    [Fact]
    public void SyntaxErrorsAreTypedCompilerDiagnosticsNotLoadableOutput()
    {
        var exception = Assert.Throws<LuauCompilationException>(() =>
            LuauCompiler.Compile("local value ="u8));

        Assert.Contains("Expected", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PersistentArtifactDefensivelyCopiesPayloadAndProvenance()
    {
        var output = LuauCompiler.Compile("return 7"u8);
        var bytecode = output.ToBytecodeArray();
        byte[] provenance = [1, 2, 3, 4];
        var artifact = new LuauBytecodeArtifact(
            LuauBytecodeArtifact.CurrentSchemaVersion,
            bytecode,
            output.CompileOptions,
            output.UpstreamRevisionHash,
            output.HostBuildFingerprint,
            "tests/persistent-artifact.luau",
            output.SourceSha256,
            output.BytecodeSha256,
            "first-party/build-manifest",
            provenance);

        bytecode[^1] ^= 0xff;
        provenance[0] = 0xff;

        Assert.Equal(output.ToBytecodeArray(), artifact.ToBytecodeArray());
        Assert.Equal([1, 2, 3, 4], artifact.GetProvenanceData());
        var copiedProvenance = artifact.GetProvenanceData();
        copiedProvenance[0] = 0xff;
        Assert.Equal([1, 2, 3, 4], artifact.GetProvenanceData());
    }

    [Fact]
    public void ArtifactConstructorRejectsWrongSchemaAndCompilerDiagnosticPayload()
    {
        var output = LuauCompiler.Compile("return 7"u8);

        Assert.Throws<ArgumentOutOfRangeException>(() => new LuauBytecodeArtifact(
            LuauBytecodeArtifact.CurrentSchemaVersion + 1,
            output.ToBytecodeArray(),
            output.CompileOptions,
            output.UpstreamRevisionHash,
            output.HostBuildFingerprint,
            "tests/wrong-schema.luau",
            output.SourceSha256,
            output.BytecodeSha256,
            "tests/wrong-schema"));

        Assert.Throws<ArgumentException>(() => new LuauBytecodeArtifact(
            LuauBytecodeArtifact.CurrentSchemaVersion,
            [0, 1],
            output.CompileOptions,
            output.UpstreamRevisionHash,
            output.HostBuildFingerprint,
            "tests/compiler-diagnostic.luau",
            output.SourceSha256,
            output.BytecodeSha256,
            "tests/compiler-diagnostic"));
    }

    [Fact]
    public void RuntimeIdentityMismatchWinsBeforeProvenanceValidation()
    {
        var output = LuauCompiler.Compile("return 7"u8);
        var artifact = new LuauBytecodeArtifact(
            LuauBytecodeArtifact.CurrentSchemaVersion,
            output.ToBytecodeArray(),
            output.CompileOptions,
            output.UpstreamRevisionHash,
            output.HostBuildFingerprint + 1,
            "tests/wrong-runtime.luau",
            output.SourceSha256,
            output.BytecodeSha256,
            "tests/wrong-runtime");
        var validator = new CountingValidator(accept: true);
        using var state = LuauState.Create(TestBudgets.StateOptions with
        {
            BytecodePolicy = LuauBytecodePolicy.RequireValidator,
            BytecodeValidator = validator,
        });

        var exception = Assert.Throws<LuauException>(() =>
            state.ExecuteVerifiedBytecode(artifact, "@tests/wrong-runtime.luau"));

        Assert.Contains("different Luau runtime build", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, validator.CallCount);
    }

    [Fact]
    public async Task PreCanceledArtifactExecutionSkipsProvenanceValidation()
    {
        var output = LuauCompiler.Compile("return 7"u8);
        var artifact = LuauBytecodeArtifact.Create(
            output,
            "tests/pre-canceled.luau",
            "tests/pre-canceled");
        var validator = new CountingValidator(accept: true);
        using var state = LuauState.Create(TestBudgets.StateOptions with
        {
            BytecodePolicy = LuauBytecodePolicy.RequireValidator,
            BytecodeValidator = validator,
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<LuauExecutionCanceledException>(async () =>
            await state.ExecuteVerifiedBytecodeAsync(
                artifact,
                "@tests/pre-canceled.luau".AsMemory(),
                cancellation.Token));
        Assert.Equal(0, validator.CallCount);
    }

    sealed class CountingValidator(bool accept) : ILuauBytecodeValidator
    {
        public int CallCount { get; private set; }

        public bool IsValid(LuauBytecodeArtifact artifact, ReadOnlySpan<byte> bytecode)
        {
            CallCount++;
            return accept;
        }
    }
}
