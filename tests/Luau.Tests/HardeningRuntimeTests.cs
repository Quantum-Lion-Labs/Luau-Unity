namespace Luau.Tests;

public sealed class HardeningRuntimeTests
{
    [Fact]
    public void AllocationBombStopsAtConfiguredLimitAndRetainsChunkName()
    {
        const long memoryLimit = 1_048_576;
        const string chunkName = "@mods/allocation-bomb.luau";
        using var state = LuauState.Create(TestBudgets.StateOptions with
        {
            MemoryLimitBytes = memoryLimit,
            BytecodePolicy = LuauBytecodePolicy.Reject,
        });
        state.OpenBaseLibrary();
        state.OpenStringLibrary();

        var exception = Assert.Throws<LuauMemoryLimitException>(() => state.DoString(
            """
            local values = {}
            for index = 1, 100000 do
                values[index] = string.rep("x", 256) .. tostring(index)
            end
            return #values
            """,
            chunkName));

        Assert.Equal(chunkName, exception.ChunkName);
        Assert.Equal(memoryLimit, exception.LimitBytes);
        Assert.Equal(memoryLimit, exception.Usage.LimitBytes);
        Assert.InRange(exception.Usage.CurrentBytes, 1, memoryLimit);
        Assert.InRange(exception.Usage.PeakBytes, exception.Usage.CurrentBytes, memoryLimit);
        Assert.True(exception.AttemptedBytes > memoryLimit);
        Assert.Contains(chunkName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MemoryUsageTracksCurrentPeakAndConfiguredLimit()
    {
        const long memoryLimit = 4_194_304;
        using var state = LuauState.Create(TestBudgets.StateOptions with
        {
            MemoryLimitBytes = memoryLimit,
            BytecodePolicy = LuauBytecodePolicy.Reject,
        });
        state.OpenStringLibrary();

        var before = state.MemoryUsage;
        var results = state.DoString(
            "local payload = string.rep('x', 65536); return #payload",
            "@mods/memory-snapshot.luau");
        var after = state.MemoryUsage;

        Assert.Single(results);
        Assert.Equal(65_536, results[0].Read<int>());
        Assert.True(before.IsLimited);
        Assert.Equal(memoryLimit, before.LimitBytes);
        Assert.InRange(before.CurrentBytes, 1, memoryLimit);
        Assert.InRange(before.PeakBytes, before.CurrentBytes, memoryLimit);
        Assert.Equal(memoryLimit, after.LimitBytes);
        Assert.InRange(after.CurrentBytes, 1, memoryLimit);
        Assert.InRange(after.PeakBytes, after.CurrentBytes, memoryLimit);
        Assert.True(after.PeakBytes > before.PeakBytes);
    }

    [Fact]
    public void LimitedStatesCanBeCreatedRunAndDisposedRepeatedly()
    {
        for (var iteration = 0; iteration < 32; iteration++)
        {
            using var state = LuauState.Create(TestBudgets.StateOptions with
            {
                MemoryLimitBytes = 1_048_576,
                BytecodePolicy = LuauBytecodePolicy.Reject,
            });

            var results = state.DoString(
                $"return {iteration} + 1",
                $"@mods/stress-{iteration}.luau");

            Assert.Single(results);
            Assert.Equal(iteration + 1, results[0].Read<int>());
            Assert.Equal(1_048_576, state.MemoryUsage.LimitBytes);
            Assert.InRange(state.MemoryUsage.CurrentBytes, 1, 1_048_576);
            Assert.InRange(
                state.MemoryUsage.PeakBytes,
                state.MemoryUsage.CurrentBytes,
                1_048_576);
        }
    }

    [Fact]
    public void MemoryUsageRetainsFinalTrackedPeakAndLimitAfterDisposal()
    {
        const long memoryLimit = 1_048_576;
        var state = LuauState.Create(TestBudgets.StateOptions with
        {
            MemoryLimitBytes = memoryLimit,
            BytecodePolicy = LuauBytecodePolicy.Reject,
        });
        _ = state.DoString("return 42", "@mods/final-memory-snapshot.luau");
        var beforeDisposal = state.MemoryUsage;

        state.Dispose();

        var afterDisposal = state.MemoryUsage;
        Assert.True(afterDisposal.IsTracked);
        Assert.True(afterDisposal.IsLimited);
        Assert.Equal(0, afterDisposal.CurrentBytes);
        Assert.Equal(memoryLimit, afterDisposal.LimitBytes);
        Assert.True(afterDisposal.PeakBytes >= beforeDisposal.PeakBytes);
    }

    [Fact]
    public void OversizedSourceIsRejectedBeforeCompilationAndRetainsChunkName()
    {
        const string chunkName = "@mods/source-too-large.luau";
        using var state = LuauState.Create(TestBudgets.StateOptions with
        {
            MaxSourceBytes = 8,
            BytecodePolicy = LuauBytecodePolicy.Reject,
        });

        // This input is intentionally invalid Luau. The size error must win over
        // any compiler diagnostic because the source bound is a preflight guard.
        var exception = Assert.Throws<LuauLoadLimitException>(() => state.DoString(
            "this is invalid Luau source",
            chunkName));

        Assert.Equal(chunkName, exception.ChunkName);
        Assert.Equal(LuauLoadInputKind.Source, exception.InputKind);
        Assert.Equal(27, exception.ActualBytes);
        Assert.Equal(8, exception.LimitBytes);
        Assert.Contains(chunkName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizedBytecodeIsRejectedBeforeValidatorOrNativeLoader()
    {
        const string chunkName = "@mods/bytecode-too-large.luau";
        var validator = new RecordingValidator(accept: true);
        using var state = LuauState.Create(TestBudgets.StateOptions with
        {
            MaxBytecodeBytes = 4,
            BytecodePolicy = LuauBytecodePolicy.RequireValidator,
            BytecodeValidator = validator,
        });
        var stackTop = state.GetTop();
        var output = LuauCompiler.Compile("return 1"u8);
        var artifact = LuauBytecodeArtifact.Create(output, chunkName, "tests/oversized");

        var exception = Assert.Throws<LuauLoadLimitException>(() =>
            state.LoadVerifiedBytecode(artifact, chunkName));

        Assert.Equal(chunkName, exception.ChunkName);
        Assert.Equal(LuauLoadInputKind.Bytecode, exception.InputKind);
        Assert.Equal(output.BytecodeLength, exception.ActualBytes);
        Assert.Equal(4, exception.LimitBytes);
        Assert.Equal(0, validator.CallCount);
        Assert.Equal(stackTop, state.GetTop());
    }

    [Fact]
    public void DefaultPolicyBlocksPersistentArtifactsButAllowsCompilerOutputAndSource()
    {
        const string chunkName = "@mods/rejected-bytecode.luau";
        var output = LuauCompiler.Compile("return 41"u8);
        var artifact = LuauBytecodeArtifact.Create(output, chunkName, "tests/default-policy");
        using var state = TestBudgets.CreateRoot();
        var stackTop = state.GetTop();

        var exception = Assert.Throws<LuauException>(() =>
            state.ExecuteVerifiedBytecode(artifact, chunkName));
        var compilerResults = state.ExecuteCompilerOutput(output, "@dev/compiler-output.luau");
        var sourceResults = state.DoString("return 42", "@mods/trusted-source.luau");

        Assert.Equal(chunkName, exception.ChunkName);
        Assert.Contains("disabled", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(stackTop, state.GetTop());
        Assert.Equal(41, Assert.Single(compilerResults).Read<int>());
        Assert.Single(sourceResults);
        Assert.Equal(42, sourceResults[0].Read<int>());
    }

    [Fact]
    public void CompilerOutputStillObservesByteSizeLimit()
    {
        const string chunkName = "@host/oversized-bundle.luau";
        var output = LuauCompiler.Compile("return 1"u8);
        using var state = LuauState.Create(TestBudgets.StateOptions with
        {
            MaxBytecodeBytes = output.BytecodeLength - 1,
        });

        var exception = Assert.Throws<LuauLoadLimitException>(() =>
            state.ExecuteCompilerOutput(output, chunkName));

        Assert.Equal(LuauLoadInputKind.Bytecode, exception.InputKind);
        Assert.Equal(output.BytecodeLength, exception.ActualBytes);
        Assert.Equal(output.BytecodeLength - 1, exception.LimitBytes);
        Assert.Equal(chunkName, exception.ChunkName);
    }

    [Fact]
    public void InternallyCompiledBytecodeIsBoundedBeforeManagedCopyAndLoad()
    {
        const string chunkName = "@mods/compiled-output-too-large.luau";
        using var state = LuauState.Create(TestBudgets.StateOptions with
        {
            MaxSourceBytes = 1_024,
            MaxBytecodeBytes = 8,
        });

        var exception = Assert.Throws<LuauLoadLimitException>(() =>
            state.DoString("return 42", chunkName));

        Assert.Equal(LuauLoadInputKind.Bytecode, exception.InputKind);
        Assert.True(exception.ActualBytes > exception.LimitBytes);
        Assert.Equal(8, exception.LimitBytes);
        Assert.Equal(chunkName, exception.ChunkName);
        Assert.Equal(0, state.GetTop());
    }

    [Fact]
    public void OversizedCompilerDiagnosticIsNotReclassifiedAsBytecodeLimit()
    {
        const string chunkName = "@mods/oversized-diagnostic.luau";
        using var state = LuauState.Create(TestBudgets.StateOptions with
        {
            MaxSourceBytes = 2_048,
            MaxBytecodeBytes = 8,
        });
        var source = "local " + new string('x', 1_024) + " = )";

        var exception = Assert.Throws<LuauException>(() =>
            state.DoString(source, chunkName));

        Assert.Equal(chunkName, exception.ChunkName);
        Assert.Contains(chunkName, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, state.GetTop());
    }

    [Fact]
    public async Task PreCanceledExecutionDoesNotCompileSource()
    {
        using var state = TestBudgets.CreateRoot();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<LuauExecutionCanceledException>(async () =>
            await state.DoStringAsync(
                "this is intentionally invalid source",
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public void CompilerOutputStillObservesExecutionLimit()
    {
        const string chunkName = "@host/bounded-bundle.luau";
        var output = LuauCompiler.Compile("while true do end"u8);
        using var state = LuauState.Create(TestBudgets.StateOptions with
        {
            DefaultExecutionOptions = TestBudgets.Execution with
            {
                InterruptCountLimit = 1,
            },
        });

        var exception = Assert.Throws<LuauExecutionBudgetException>(() =>
            state.ExecuteCompilerOutput(output, chunkName));

        Assert.Equal(LuauExecutionBudgetKind.InterruptCount, exception.BudgetKind);
        Assert.Equal(chunkName, exception.ChunkName);
    }

    [Fact]
    public void ValidatorCanRejectBytecodeBeforeNativeLoading()
    {
        const string chunkName = "@mods/validator-rejected.luau";
        var validator = new RecordingValidator(accept: false);
        var output = LuauCompiler.Compile("return 1"u8);
        var artifact = LuauBytecodeArtifact.Create(
            output,
            chunkName,
            "tests/rejected",
            [1, 2, 3]);
        using var state = LuauState.Create(TestBudgets.StateOptions with
        {
            BytecodePolicy = LuauBytecodePolicy.RequireValidator,
            BytecodeValidator = validator,
        });
        var stackTop = state.GetTop();

        var exception = Assert.Throws<LuauException>(() =>
            state.LoadVerifiedBytecode(artifact, chunkName));

        Assert.Equal(chunkName, exception.ChunkName);
        Assert.Contains("rejected", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, validator.CallCount);
        Assert.Same(artifact, validator.LastArtifact);
        Assert.Equal(output.ToBytecodeArray(), validator.LastBytecode);
        Assert.Equal(stackTop, state.GetTop());
    }

    [Fact]
    public void ValidatorCanAcceptValidBytecode()
    {
        const string chunkName = "@mods/validator-accepted.luau";
        var validator = new RecordingValidator(accept: true);
        var output = LuauCompiler.Compile("return 123"u8);
        var artifact = LuauBytecodeArtifact.Create(output, chunkName, "tests/accepted");
        using var state = LuauState.Create(TestBudgets.StateOptions with
        {
            BytecodePolicy = LuauBytecodePolicy.RequireValidator,
            BytecodeValidator = validator,
        });

        var results = state.ExecuteVerifiedBytecode(artifact, chunkName);

        Assert.Single(results);
        Assert.Equal(123, results[0].Read<int>());
        Assert.Equal(1, validator.CallCount);
        Assert.Same(artifact, validator.LastArtifact);
        Assert.Equal(output.ToBytecodeArray(), validator.LastBytecode);
    }

    [Fact]
    public void SyntaxAndRuntimeErrorsRetainTheirChunkNames()
    {
        using var state = LuauState.Create(TestBudgets.StateOptions with
        {
            BytecodePolicy = LuauBytecodePolicy.Reject,
        });
        state.OpenBaseLibrary();

        var syntax = Assert.Throws<LuauException>(() => state.DoString(
            "local value =",
            "@mods/syntax-error.luau"));
        var runtime = Assert.Throws<LuauException>(() => state.DoString(
            "error('runtime boom')",
            "@mods/runtime-error.luau"));

        Assert.Equal("@mods/syntax-error.luau", syntax.ChunkName);
        Assert.Contains("@mods/syntax-error.luau", syntax.Message, StringComparison.Ordinal);
        Assert.Equal("@mods/runtime-error.luau", runtime.ChunkName);
        Assert.Contains("@mods/runtime-error.luau", runtime.Message, StringComparison.Ordinal);
        Assert.Contains("runtime boom", runtime.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtifactConstructorRejectsTamperedBytecodeBeforeStateValidation()
    {
        var output = LuauCompiler.Compile("return 1"u8);
        var bytecode = output.ToBytecodeArray();
        bytecode[^1] ^= 0xff;

        Assert.Throws<ArgumentException>(() => new LuauBytecodeArtifact(
            LuauBytecodeArtifact.CurrentSchemaVersion,
            bytecode,
            output.CompileOptions,
            output.UpstreamRevisionHash,
            output.HostBuildFingerprint,
            "tests/tampered.luau",
            output.SourceSha256,
            output.BytecodeSha256,
            "tests/tampered"));
    }

    sealed class RecordingValidator(bool accept) : ILuauBytecodeValidator
    {
        public int CallCount { get; private set; }
        public byte[]? LastBytecode { get; private set; }
        public LuauBytecodeArtifact? LastArtifact { get; private set; }

        public bool IsValid(LuauBytecodeArtifact artifact, ReadOnlySpan<byte> bytecode)
        {
            CallCount++;
            LastArtifact = artifact;
            LastBytecode = bytecode.ToArray();
            return accept;
        }
    }
}
