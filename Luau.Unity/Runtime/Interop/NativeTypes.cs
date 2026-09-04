#pragma warning disable IDE1006

using System;
using System.Runtime.InteropServices;

namespace Luau.Internal.Interop
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal unsafe delegate int LuauHostManagedFunction(LuauHostState* state);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal unsafe delegate int LuauHostInterruptPoll(LuauHostState* state, LuauHostInterruptKind kind);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal unsafe delegate void LuauHostUserdataDestructor(void* userdata);

    internal enum LuauHostType
    {
        Nil = 0,
        Boolean = 1,
        LightUserdata,
        Number,
        Integer,
        Vector,
        String,
        Table,
        Function,
        Userdata,
        Thread,
        Buffer,
        Class,
        Object,
        DeadKey,
        Proto,
        Upvalue,
        Count = DeadKey,
    }

    internal enum LuauHostStatus : int
    {
        Ok = 0,
        LuaError = 1,
        MemoryQuota = 2,
        SystemOutOfMemory = 3,
        Canceled = 4,
        InvalidArgument = 5,
        CompilerError = 6,
        TerminalReset = 7,
        Unsupported = 8,
        Yielded = 9,
        Break = 10,
        ResourceExhausted = 11,
    }

    internal enum LuauHostAllocatorFailure : int
    {
        None = 0,
        Quota = 1,
        System = 2,
    }

    internal enum LuauHostInterruptKind : int
    {
        Execution = -1,
        GarbageCollection = 0,
    }

    internal enum LuauHostLibrary : int
    {
        Base = 0,
        Coroutine = 1,
        Table = 2,
        OS = 3,
        String = 4,
        Bit32 = 5,
        Buffer = 6,
        Utf8 = 7,
        Math = 8,
        Debug = 9,
        Vector = 10,
        Integer = 11,
    }

    [Flags]
    internal enum LuauHostFeature : uint
    {
        SelfDescription = 1U << 0,
        ProtectedOperations = 1U << 1,
        HostBuffer = 1U << 2,
        TrackedAllocator = 1U << 3,
        ManagedCallbacks = 1U << 4,
        Interrupt = 1U << 5,
        TerminalReset = 1U << 6,
        IntegerValues = 1U << 7,
        Sandbox = 1U << 8,
        OpaqueReferenceTokens = 1U << 9,
        DirectCallbackIdentity = 1U << 10,
        ObservationOnlyGcInterrupt = 1U << 11,
        CoroutineSuspension = 1U << 12,
    }

    [Flags]
    internal enum LuauHostStateOptionFlags : ushort
    {
        None = 0,
        TrackMemory = 1 << 0,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct LuauHostState
    {
        internal fixed byte unused[1];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct LuauHostCompileOptions
    {
        internal uint struct_size;
        internal ushort version;
        internal ushort reserved0;
        internal int optimization_level;
        internal int debug_level;
        internal int type_info_level;
        internal int coverage_level;
        internal uint flags;
        internal uint reserved1;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct LuauHostCallbackTable
    {
        internal uint struct_size;
        internal ushort version;
        internal ushort reserved0;
        internal IntPtr userdata;
        internal ulong registration_id;
        internal IntPtr managed_function;
        internal IntPtr interrupt_poll;
        internal IntPtr userdata_destructor;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct LuauHostStateOptions
    {
        internal uint struct_size;
        internal ushort version;
        internal LuauHostStateOptionFlags flags;
        internal ulong memory_limit_bytes;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal unsafe struct LuauHostMemoryInfo
    {
        internal uint struct_size;
        internal LuauHostAllocatorFailure failure;
        internal ulong current_bytes;
        internal ulong peak_bytes;
        internal ulong limit_bytes;
        internal ulong last_attempted_bytes;
        internal byte tracked;
        internal fixed byte reserved[7];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal unsafe struct LuauHostBuffer
    {
        internal byte* data;
        internal ulong size;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct LuauHostAbiInfo
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
}

#pragma warning restore IDE1006
