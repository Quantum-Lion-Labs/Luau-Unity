using Luau.Internal.Interop;
using static Luau.Internal.Interop.NativeMethods;

namespace Luau;

public unsafe partial class LuauState
{
    /// <summary>
    /// Loads opaque bytecode returned by this process's compiler as a script
    /// closure that managed code may execute through <see cref="LuauFunction.Invoke"/>.
    /// </summary>
    public LuauFunction LoadCompilerOutput(
        LuauCompilerOutput output,
        ReadOnlySpan<char> chunkName = default)
    {
        if (output == null)
        {
            throw new ArgumentNullException(nameof(output));
        }

        ThrowIfDisposed();
        var decodedChunkName = DecodeChunkName(chunkName);
        ValidateCompilerOutput(output, decodedChunkName);
        using var access = EnterNativeAccess();
        var originalTop = luau_host_stack_get_top(l);
        using var chunkUtf8 = new Utf8BufferScope(chunkName);
        try
        {
            LoadAcceptedBytecodeInternal(
                output.Bytecode,
                chunkUtf8.Bytes,
                decodedChunkName);
            var function = ToFunction(-1);
            Pop(1);
            return function;
        }
        finally
        {
            SetTop(originalTop);
        }
    }

    /// <summary>
    /// Loads a persistent artifact after build-identity and configured
    /// provenance validation as a host-invokable script closure. Caller-provided
    /// chunk names are diagnostic only.
    /// </summary>
    public LuauFunction LoadVerifiedBytecode(
        LuauBytecodeArtifact artifact,
        ReadOnlySpan<char> chunkName = default)
    {
        if (artifact == null)
        {
            throw new ArgumentNullException(nameof(artifact));
        }

        ThrowIfDisposed();
        var decodedChunkName = DecodeChunkName(chunkName);
        ValidateArtifact(artifact, decodedChunkName);
        using var access = EnterNativeAccess();
        var originalTop = luau_host_stack_get_top(l);
        using var chunkUtf8 = new Utf8BufferScope(chunkName);
        try
        {
            LoadAcceptedBytecodeInternal(
                artifact.Bytecode,
                chunkUtf8.Bytes,
                decodedChunkName);
            var function = ToFunction(-1);
            Pop(1);
            return function;
        }
        finally
        {
            SetTop(originalTop);
        }
    }

    unsafe void LoadAcceptedBytecodeInternal(
        ReadOnlySpan<byte> bytecode,
        ReadOnlySpan<char> chunkName)
    {
        if (chunkName.IsEmpty)
        {
            LoadAcceptedBytecodeInternal(bytecode, ReadOnlySpan<byte>.Empty);
            return;
        }

        using var chunkUtf8 = new Utf8BufferScope(chunkName);
        LoadAcceptedBytecodeInternal(
            bytecode,
            chunkUtf8.Bytes,
            DecodeChunkName(chunkName));
    }

    /// <summary>
    /// Raw native loader primitive. Callers must establish compiler-output or
    /// persistent-artifact admission before reaching this method.
    /// </summary>
    unsafe void LoadAcceptedBytecodeInternal(
        ReadOnlySpan<byte> bytecode,
        ReadOnlySpan<byte> utf8ChunkName,
        string? decodedChunkName = null,
        bool allowOversizedDiagnostic = false)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();

        decodedChunkName ??= DecodeChunkName(utf8ChunkName);
        if (!allowOversizedDiagnostic || bytecode.IsEmpty || bytecode[0] != 0)
        {
            ValidateBytecodeSize(bytecode.Length, decodedChunkName);
        }
        LuauNativeProtection.Prepare(context);

        ReadOnlySpan<byte> effectiveChunkName = utf8ChunkName.IsEmpty
            ? "main"u8
            : utf8ChunkName;
        using var nameBuffer = new Utf8BufferScope(effectiveChunkName, appendNull: true);
        var nullTerminatedName = nameBuffer.NullTerminatedBytes;

        LuauHostStatus status;
        fixed (byte* bytecodePointer = bytecode)
        fixed (byte* namePointer = nullTerminatedName)
        {
            var loadStatus = LuauHostStatus.Ok;
            LuauHostStatus protectedStatus;
            using (context.BeginBytecodeLoad())
            {
                protectedStatus = luau_host_load(
                    l,
                    namePointer,
                    bytecodePointer,
                    (ulong)bytecode.Length,
                    0,
                    &loadStatus);
                if (protectedStatus == LuauHostStatus.ThreadNotRunnable)
                {
                    // Loading resolves imports, which can run metamethods.
                    protectedStatus = LoadOnThreadSharingGlobals(
                        namePointer,
                        bytecodePointer,
                        (ulong)bytecode.Length,
                        &loadStatus,
                        "load bytecode");
                }
            }
            LuauNativeProtection.ThrowIfFailed(
                this,
                l,
                protectedStatus,
                "load bytecode",
                decodedChunkName);
            status = loadStatus;
        }

        if (status == LuauHostStatus.Ok)
        {
            return;
        }

        var message = ReadAndPopError();
        if (context.AllocatorFailure == LuauAllocatorFailure.QuotaExceeded)
        {
            throw new LuauMemoryLimitException(
                decodedChunkName,
                context.MemoryUsage,
                context.LastAttemptedAllocationBytes);
        }

        if (context.AllocatorFailure == LuauAllocatorFailure.SystemOutOfMemory)
        {
            throw new OutOfMemoryException(
                LuauDiagnosticMessages.WithChunk("The Luau VM could not allocate memory while loading bytecode.", decodedChunkName));
        }

        if (!string.IsNullOrEmpty(decodedChunkName) &&
            message.IndexOf(decodedChunkName, StringComparison.Ordinal) < 0)
        {
            message = LuauDiagnosticMessages.WithChunk(message, decodedChunkName);
        }

        throw new LuauException(message, decodedChunkName);
    }

    internal void ValidateSourceSize(int sourceBytes, string? chunkName)
    {
        if (Options.MaxSourceBytes is { } limit && sourceBytes > limit)
        {
            throw new LuauLoadLimitException(
                chunkName,
                LuauLoadInputKind.Source,
                sourceBytes,
                limit);
        }
    }

    void ValidateCompilerOutput(LuauCompilerOutput output, string? chunkName)
    {
        ValidateBytecodeSize(output.BytecodeLength, chunkName);
        ValidateBytecodeIdentity(
            output.UpstreamRevisionHash,
            output.HostBuildFingerprint,
            chunkName);
    }

    void ValidateArtifact(LuauBytecodeArtifact artifact, string? chunkName)
    {
        ValidateBytecodeSize(artifact.BytecodeLength, chunkName);
        ValidateBytecodeIdentity(
            artifact.UpstreamRevisionHash,
            artifact.HostBuildFingerprint,
            chunkName);

        if (Options.BytecodePolicy == LuauBytecodePolicy.Reject)
        {
            throw new LuauException(
                LuauDiagnosticMessages.WithChunk(
                    "Persistent bytecode artifacts are disabled for this state.",
                    chunkName),
                chunkName);
        }
        if (Options.BytecodePolicy != LuauBytecodePolicy.RequireValidator)
        {
            throw new InvalidOperationException("Unknown bytecode policy.");
        }
        if (!Options.BytecodeValidator!.IsValid(artifact, artifact.Bytecode))
        {
            throw new LuauException(
                LuauDiagnosticMessages.WithChunk(
                    "The persistent bytecode artifact was rejected by the configured validator.",
                    chunkName),
                chunkName);
        }
    }

    void ValidateBytecodeSize(int bytecodeLength, string? chunkName)
    {
        if (Options.MaxBytecodeBytes is { } limit && bytecodeLength > limit)
        {
            throw new LuauLoadLimitException(
                chunkName,
                LuauLoadInputKind.Bytecode,
                bytecodeLength,
                limit);
        }
    }

    static void ValidateBytecodeIdentity(
        ulong upstreamRevisionHash,
        ulong hostBuildFingerprint,
        string? chunkName)
    {
        if (upstreamRevisionHash != LuauNativeProtection.ExpectedUpstreamRevisionHash ||
            hostBuildFingerprint != LuauNativeProtection.ExpectedHostBuildFingerprint)
        {
            throw new LuauException(
                LuauDiagnosticMessages.WithChunk(
                    "The bytecode was produced for a different Luau runtime build.",
                    chunkName),
                chunkName);
        }
    }

    unsafe string ReadAndPopError()
    {
        try
        {
            // Reading an existing string is allocation-free. Do not ask
            // lua_tolstring to coerce an arbitrary error object here: coercion
            // can allocate and must never long-jump across this managed frame.
            if (luau_host_stack_get_top(l) == 0 || (LuauHostType)luau_host_type(l, -1) != LuauHostType.String)
            {
                return "Luau loading failed without a string error message.";
            }

            ulong length = 0;
            var pointer = luau_host_to_string_view(l, -1, &length);
            return pointer == null || length == 0
                ? "Luau loading failed without an error message."
                : BoundedUtf8Decoder.DecodeDiagnostic(
                    pointer,
                    length,
                    Options.MaxDiagnosticBytes);
        }
        finally
        {
            if (luau_host_stack_get_top(l) > 0)
            {
                SetTop(-2);
            }
        }
    }
}
