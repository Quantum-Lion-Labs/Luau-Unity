using System.Text;
using System.Runtime.InteropServices;
using Luau.Internal.Interop;
using static Luau.Internal.Interop.NativeMethods;

namespace Luau;

/// <summary>
/// Verifies and translates the native protected-call ABI. The native bridge
/// owns Luau's setjmp/longjmp boundary; managed callers only receive statuses.
/// </summary>
internal static unsafe class LuauNativeProtection
{
    internal const uint ExpectedAbiMagic = 0x4841554cU;
    internal const ushort ExpectedAbiMajor = 2;
    internal const ushort MinimumAbiMinor = 0;
    internal const uint ExpectedAbiRecordSize = 112;
    internal const uint ExpectedFeatureFlags = 0x1fffU;
    internal const ulong ExpectedUpstreamRevisionHash = 0xc45f010aabf167acUL;
    internal const ulong ExpectedHostBuildFingerprint = 0x6a1565ce91ec38a2UL;
    internal const int AbiQueryOk = (int)LuauHostStatus.Ok;
    internal const int AbiQueryInvalidArgument = (int)LuauHostStatus.InvalidArgument;

    internal static LuauNativeAbiVerifier AbiVerifier { get; } = new(QueryNativeAbiInfo);

    internal static void EnsureAvailable()
    {
        AbiVerifier.EnsureAvailable();
    }

    static int QueryNativeAbiInfo(LuauNativeAbiInfo* info, uint infoSize)
    {
        // This is the sole process-wide compatibility handshake. Calling the
        // raw query here keeps the internal facade free of a second verifier.
        return (int)luau_host_get_abi_info(
            infoSize,
            (LuauHostAbiInfo*)info);
    }

    internal static void Prepare(LuauVmContext context)
    {
        context.ResetAllocatorFailure();
    }

    internal static void ThrowIfFailed(
        LuauState state,
        LuauHostState* pointer,
        LuauHostStatus status,
        string operation,
        string? chunkName = null)
    {
        var context = state.Context;
        var activeOperation = context.GetActiveOperation();
        var isInjectedCallbackFailure =
            status == LuauHostStatus.LuaError &&
            activeOperation != null &&
            IsCallbackFailureToken(pointer, activeOperation.CallbackFailureToken);
        string? nativeMessage = null;
        if (status is LuauHostStatus.LuaError
            or LuauHostStatus.MemoryQuota
            or LuauHostStatus.SystemOutOfMemory
            or LuauHostStatus.Canceled)
        {
            nativeMessage = ReadProtectedError(
                pointer,
                operation,
                state.Options.MaxDiagnosticBytes);
        }

        var hardStop = activeOperation?.GetHardStopException();
        if (hardStop != null)
        {
            throw hardStop;
        }

        var callbackFailure = isInjectedCallbackFailure
            ? activeOperation!.TakeInjectedCallbackFailure()
            : activeOperation?.TakeUninjectedCallbackFailure();
        if (callbackFailure != null)
        {
            throw callbackFailure;
        }

        if (status == LuauHostStatus.Ok)
        {
            return;
        }

        var allocatorFailure = context.AllocatorFailure;

        if (allocatorFailure == LuauAllocatorFailure.QuotaExceeded
            || status == LuauHostStatus.MemoryQuota
            )
        {
            var usage = context.MemoryUsage;
            var limit = usage.LimitBytes!.Value;
            var attempted = Math.Max(limit + 1, context.LastAttemptedAllocationBytes);
            throw new LuauMemoryLimitException(chunkName, usage, attempted);
        }

        if (allocatorFailure == LuauAllocatorFailure.SystemOutOfMemory ||
            status == LuauHostStatus.SystemOutOfMemory)
        {
            throw new OutOfMemoryException(
                LuauDiagnosticMessages.WithChunk(
                    $"The Luau VM could not allocate memory while attempting to {operation}.",
                    chunkName));
        }

        if (status == LuauHostStatus.InvalidArgument)
        {
            throw new InvalidOperationException(
                $"The Luau host rejected the arguments supplied to {operation}.");
        }
        if (status == LuauHostStatus.Unsupported)
        {
            throw new PlatformNotSupportedException(
                $"The Luau host does not support the requested operation to {operation}.");
        }
        if (status == LuauHostStatus.ResourceExhausted)
        {
            throw new LuauReferenceLimitException();
        }

        nativeMessage ??= $"The Luau host failed with status {(int)status} while attempting to {operation}.";
        throw new LuauException(
            LuauDiagnosticMessages.WithChunk(nativeMessage!, chunkName),
            chunkName);
    }

    static bool IsCallbackFailureToken(LuauHostState* pointer, IntPtr token)
    {
        return luau_host_stack_get_top(pointer) > 0 &&
            (LuauHostType)luau_host_type(pointer, -1) == LuauHostType.LightUserdata &&
            (IntPtr)luau_host_to_light_userdata(pointer, -1) == token;
    }

    static string ReadProtectedError(
        LuauHostState* pointer,
        string operation,
        int maxDiagnosticBytes)
    {
        try
        {
            // lua_tolstring may allocate while coercing a number. Native errors
            // may be arbitrary Luau values, so only inspect an existing string.
            if (luau_host_stack_get_top(pointer) > 0 &&
                (LuauHostType)luau_host_type(pointer, -1) == LuauHostType.String)
            {
                ulong length = 0;
                var value = luau_host_to_string_view(pointer, -1, &length);
                if (value != null)
                {
                    return BoundedUtf8Decoder.DecodeDiagnostic(
                        value,
                        length,
                        maxDiagnosticBytes);
                }
            }

            return $"The Luau VM failed while attempting to {operation}.";
        }
        finally
        {
            if (luau_host_stack_get_top(pointer) > 0)
            {
                _ = luau_host_stack_set_top(pointer, -2);
            }
        }
    }
}


[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct LuauNativeAbiInfo
{
    internal uint struct_size;
    internal uint magic;
    internal ushort abi_major;
    internal ushort abi_minor;
    internal uint feature_flags;
    internal byte pointer_size;
    internal byte size_t_size;
    internal byte little_endian;
    internal byte reserved0;
    internal uint compile_options_size;
    internal uint callback_table_size;
    internal uint state_options_size;
    internal uint memory_info_size;
    internal uint buffer_size;
    internal int type_nil;
    internal int type_boolean;
    internal int type_lightuserdata;
    internal int type_number;
    internal int type_integer;
    internal int type_vector;
    internal int type_string;
    internal int type_table;
    internal int type_function;
    internal int type_userdata;
    internal int type_thread;
    internal int type_buffer;
    internal int type_class;
    internal int type_object;
    internal ulong upstream_revision_hash;
    internal ulong host_build_fingerprint;
}

internal unsafe delegate int LuauNativeAbiInfoQuery(LuauNativeAbiInfo* info, uint infoSize);

/// <summary>
/// Performs the process-wide host ABI handshake. The test-visible descriptor
/// mirrors the fixed C record without exposing the internal interop assembly.
/// </summary>
internal sealed unsafe class LuauNativeAbiVerifier
{
    static readonly (LuauHostFeature Feature, string Name)[] RequiredFeatures =
    [
        (LuauHostFeature.SelfDescription, "self-description"),
        (LuauHostFeature.ProtectedOperations, "protected operations"),
        (LuauHostFeature.HostBuffer, "host-owned compiler buffers"),
        (LuauHostFeature.TrackedAllocator, "tracked allocator"),
        (LuauHostFeature.ManagedCallbacks, "managed callbacks"),
        (LuauHostFeature.Interrupt, "interrupt callbacks"),
        (LuauHostFeature.TerminalReset, "terminal reset"),
        (LuauHostFeature.IntegerValues, "integer values"),
        (LuauHostFeature.Sandbox, "sandbox"),
        (LuauHostFeature.CoroutineSuspension, "coroutine suspension"),
    ];

    readonly LuauNativeAbiInfoQuery query;
    readonly Lazy<LuauNativeAbiInfo> verification;

    internal LuauNativeAbiVerifier(LuauNativeAbiInfoQuery query)
    {
        this.query = query ?? throw new ArgumentNullException(nameof(query));
        verification = new Lazy<LuauNativeAbiInfo>(Verify, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal LuauNativeAbiInfo Info => verification.Value;

    internal void EnsureAvailable()
    {
        _ = verification.Value;
    }

    LuauNativeAbiInfo Verify()
    {
        if (sizeof(LuauNativeAbiInfo) != LuauNativeProtection.ExpectedAbiRecordSize)
        {
            throw new PlatformNotSupportedException(
                $"The managed Luau host ABI declaration is {sizeof(LuauNativeAbiInfo)} bytes; " +
                $"the fixed ABI record requires {LuauNativeProtection.ExpectedAbiRecordSize} bytes.");
        }

        var info = default(LuauNativeAbiInfo);
        int status;
        try
        {
            status = query(&info, checked((uint)sizeof(LuauNativeAbiInfo)));
        }
        catch (EntryPointNotFoundException exception)
        {
            throw new PlatformNotSupportedException(
                "The native Luau host does not provide the self-description query required by this managed runtime. " +
                "Rebuild and deploy the matching host plugin.",
                exception);
        }

        if (status != LuauNativeProtection.AbiQueryOk)
        {
            var context = FingerprintContext(info);
            if (status == LuauNativeProtection.AbiQueryInvalidArgument)
            {
                throw new PlatformNotSupportedException(
                    $"The native Luau host rejected an ABI query with a {sizeof(LuauNativeAbiInfo)}-byte caller record; " +
                    $"the host reports {info.struct_size} bytes. {context}");
            }

            throw new PlatformNotSupportedException(
                $"The native Luau host returned unknown ABI query status {status}. {context}");
        }

        Validate(info);
        return info;
    }

    internal static void Validate(LuauNativeAbiInfo info)
    {
        var context = FingerprintContext(info);
        var managedRecordSize = checked((uint)sizeof(LuauNativeAbiInfo));

        if (info.struct_size < managedRecordSize)
        {
            throw new PlatformNotSupportedException(
                $"The native Luau host ABI record is {info.struct_size} bytes; " +
                $"at least {managedRecordSize} bytes are required. {context}");
        }
        if (info.magic != LuauNativeProtection.ExpectedAbiMagic)
        {
            throw new PlatformNotSupportedException(
                $"The native Luau host ABI magic is 0x{info.magic:x8}; " +
                $"expected 0x{LuauNativeProtection.ExpectedAbiMagic:x8}. {context}");
        }
        if (info.abi_major != LuauNativeProtection.ExpectedAbiMajor ||
            info.abi_minor != LuauNativeProtection.MinimumAbiMinor)
        {
            throw new PlatformNotSupportedException(
                $"The native Luau host ABI is {info.abi_major}.{info.abi_minor}; " +
                $"expected the exact ABI {LuauNativeProtection.ExpectedAbiMajor}.{LuauNativeProtection.MinimumAbiMinor}. " +
                $"Managed/native compatibility is exact for this build. {context}");
        }

        ValidateSize("pointer", info.pointer_size, sizeof(void*), context);
        ValidateSize("size_t", info.size_t_size, sizeof(nuint), context);

        if (!BitConverter.IsLittleEndian || info.little_endian != 1)
        {
            var nativeEndianness = info.little_endian == 1 ? "little-endian" : "big-endian";
            var managedEndianness = BitConverter.IsLittleEndian ? "little-endian" : "big-endian";
            throw new PlatformNotSupportedException(
                $"The native Luau host is {nativeEndianness} and the managed runtime is {managedEndianness}; " +
                $"matching little-endian components are required. {context}");
        }

        ValidateSize("compile-options", info.compile_options_size, sizeof(LuauHostCompileOptions), context);
        ValidateSize("callback-table", info.callback_table_size, sizeof(LuauHostCallbackTable), context);
        ValidateSize("state-options", info.state_options_size, sizeof(LuauHostStateOptions), context);
        ValidateSize("memory-info", info.memory_info_size, sizeof(LuauHostMemoryInfo), context);
        ValidateSize("buffer", info.buffer_size, sizeof(LuauHostBuffer), context);

        foreach (var (feature, name) in RequiredFeatures)
        {
            if ((info.feature_flags & (uint)feature) == 0)
            {
                throw new PlatformNotSupportedException(
                    $"The native Luau host ABI is missing the required {name} feature. {context}");
            }
        }

        ValidateTypeTag("nil", info.type_nil, LuauHostType.Nil, context);
        ValidateTypeTag("boolean", info.type_boolean, LuauHostType.Boolean, context);
        ValidateTypeTag("light userdata", info.type_lightuserdata, LuauHostType.LightUserdata, context);
        ValidateTypeTag("number", info.type_number, LuauHostType.Number, context);
        ValidateTypeTag("integer", info.type_integer, LuauHostType.Integer, context);
        ValidateTypeTag("vector", info.type_vector, LuauHostType.Vector, context);
        ValidateTypeTag("string", info.type_string, LuauHostType.String, context);
        ValidateTypeTag("table", info.type_table, LuauHostType.Table, context);
        ValidateTypeTag("function", info.type_function, LuauHostType.Function, context);
        ValidateTypeTag("userdata", info.type_userdata, LuauHostType.Userdata, context);
        ValidateTypeTag("thread", info.type_thread, LuauHostType.Thread, context);
        ValidateTypeTag("buffer", info.type_buffer, LuauHostType.Buffer, context);
        ValidateTypeTag("class", info.type_class, LuauHostType.Class, context);
        ValidateTypeTag("object", info.type_object, LuauHostType.Object, context);

        if (info.upstream_revision_hash != LuauNativeProtection.ExpectedUpstreamRevisionHash)
        {
            throw new PlatformNotSupportedException(
                $"The native Luau host upstream fingerprint is 0x{info.upstream_revision_hash:x16}; " +
                $"the managed runtime requires 0x{LuauNativeProtection.ExpectedUpstreamRevisionHash:x16}. {context}");
        }
        if (info.host_build_fingerprint != LuauNativeProtection.ExpectedHostBuildFingerprint)
        {
            throw new PlatformNotSupportedException(
                $"The native Luau host build fingerprint is 0x{info.host_build_fingerprint:x16}; " +
                $"the managed runtime requires 0x{LuauNativeProtection.ExpectedHostBuildFingerprint:x16}. {context}");
        }
    }

    static void ValidateSize(string name, uint actual, int expected, string context)
    {
        if (actual != (uint)expected)
        {
            throw new PlatformNotSupportedException(
                $"The native Luau host {name} size is {actual} bytes; " +
                $"the managed binding requires {expected} bytes. {context}");
        }
    }

    static void ValidateSize(string name, byte actual, int expected, string context)
    {
        if (actual != expected)
        {
            throw new PlatformNotSupportedException(
                $"The native Luau host {name} size is {actual} bytes; " +
                $"the managed binding requires {expected} bytes. {context}");
        }
    }

    static void ValidateTypeTag(string name, int actual, LuauHostType expected, string context)
    {
        if (actual != (int)expected)
        {
            throw new PlatformNotSupportedException(
                $"The native Luau host {name} type tag is {actual}; " +
                $"the managed binding requires {(int)expected}. {context}");
        }
    }

    static string FingerprintContext(LuauNativeAbiInfo info)
    {
        return $"Upstream fingerprint 0x{info.upstream_revision_hash:x16}; " +
            $"host build fingerprint 0x{info.host_build_fingerprint:x16}.";
    }
}
