using Luau.Internal.Interop;
using static Luau.Internal.Interop.NativeMethods;

namespace Luau;

public unsafe partial class LuauState
{
    static ReadOnlySpan<byte> SandboxGuardSource => """
        local root = _G
        local rawget_ = rawget
        local rawset_ = rawset
        local error_ = error
        local tostring_ = tostring

        return function(target, key, value)
            if rawget_(root, key) ~= nil then
                error_("attempt to replace protected host global '" .. tostring_(key) .. "'", 2)
            end

            rawset_(target, key, value)
        end
        """u8;

    static ReadOnlySpan<byte> SandboxGuardChunkName => "@luau-dotnet/sandbox-guard"u8;
    static ReadOnlySpan<byte> GlobalEnvironmentName => "_G\0"u8;
    static ReadOnlySpan<byte> RawGetName => "rawget\0"u8;
    static ReadOnlySpan<byte> RawSetName => "rawset\0"u8;
    static ReadOnlySpan<byte> ErrorName => "error\0"u8;
    static ReadOnlySpan<byte> ToStringName => "tostring\0"u8;
    static ReadOnlySpan<byte> NewIndexKey => "__newindex\0"u8;
    static ReadOnlySpan<byte> MetatableKey => "__metatable\0"u8;
    static ReadOnlySpan<byte> MetatableLockValue => "protected Luau sandbox\0"u8;

    /// <summary>
    /// Gets whether the shared root environment has been frozen with
    /// <c>luaL_sandbox</c>.
    /// </summary>
    public bool IsRootSandboxed => context.IsRootSandboxed;

    /// <summary>
    /// Freezes the root globals and opened library tables after the host has
    /// registered its allowed APIs. Environment mutation helpers are removed
    /// so script instances cannot recover and raw-write their proxy table.
    /// The Luau base library must be opened first because isolated child
    /// environments use its primitive functions to protect host globals.
    /// </summary>
    public void SandboxRoot()
    {
        ThrowIfDisposed();
        if (!IsMainThread)
        {
            ThrowHelper.ThrowInvalidOperationException("Only the root Luau state can be root-sandboxed.");
        }

        if (context.IsRootSandboxed)
        {
            ThrowHelper.ThrowInvalidOperationException("The Luau root state is already sandboxed.");
        }

        using var access = EnterNativeAccess();
        EnsureSandboxBaseLibrary();
        this["getfenv"] = LuauValue.Nil;
        this["setfenv"] = LuauValue.Nil;
        LuauNativeProtection.Prepare(context);
        var status = luau_host_sandbox_root(l);
        LuauNativeProtection.ThrowIfFailed(this, l, status, "sandbox the root environment");
        context.MarkRootSandboxed();
    }

    /// <summary>
    /// Creates a child coroutine with an isolated writable global proxy over
    /// the frozen root APIs and a guard that rejects protected-global shadows.
    /// </summary>
    public LuauState CreateSandboxedThread()
    {
        ThrowIfDisposed();
        if (!IsMainThread)
        {
            ThrowHelper.ThrowInvalidOperationException("Sandboxed script threads must be created from the root state.");
        }

        if (!context.IsRootSandboxed)
        {
            ThrowHelper.ThrowInvalidOperationException(
                "Sandbox the root state after registering host APIs before creating script threads.");
        }

        var thread = CreateThread();
        try
        {
            thread.SandboxThread();
            return thread;
        }
        catch
        {
            thread.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Applies an isolated sandbox environment to an existing child thread.
    /// </summary>
    public void SandboxThread()
    {
        ThrowIfDisposed();
        if (IsMainThread)
        {
            ThrowHelper.ThrowInvalidOperationException("Use SandboxRoot on the main Luau state.");
        }

        if (!context.IsRootSandboxed)
        {
            ThrowHelper.ThrowInvalidOperationException("The shared root state must be sandboxed first.");
        }

        var pointer = (IntPtr)l;
        if (context.IsThreadSandboxed(pointer))
        {
            ThrowHelper.ThrowInvalidOperationException("The Luau child thread is already sandboxed.");
        }

        using var access = EnterNativeAccess();
        // Sandboxing replaces this thread's globals and then runs the guard
        // chunk on it. Reject a thread that cannot run Luau code before its
        // globals change, so a rejection leaves it as it was.
        ThrowIfSuspendedUnderNativeAccess("sandbox a thread", chunkName: null, includeUnstartedCoroutine: false);
        if (luau_host_thread_status(l) != LuauHostStatus.Ok)
        {
            ThrowHelper.ThrowInvalidOperationException(
                "Cannot sandbox a Luau thread that ended with an error: Luau code cannot run on it until " +
                "it is reset. Sandbox a new thread from CreateSandboxedThread instead.");
        }
        LuauNativeProtection.Prepare(context);
        var status = luau_host_sandbox_thread(l);
        LuauNativeProtection.ThrowIfFailed(this, l, status, "sandbox a child thread");

        using var guardResults = DoStringForRequire(
            SandboxGuardSource,
            SandboxGuardChunkName,
            options: null);

        if (guardResults.Length != 1 || !guardResults[0].TryRead<LuauFunction>(out var guard))
        {
            throw new LuauException("Unable to create the protected-global sandbox guard.");
        }

        guardResults.Detach(0);
        using (guard)
        {
            var originalTop = luau_host_stack_get_top(l);
            try
            {
                SandboxPushGlobal("inspect the sandboxed global environment");
                if (!SandboxGetMetatable(-1))
                {
                    throw new LuauException("The sandboxed global environment has no proxy metatable.");
                }

                var metatableIndex = luau_host_stack_abs_index(l, -1);
                LuauNativeProtection.Prepare(context);
                var writableStatus = luau_host_table_set_readonly(l, metatableIndex, 0);
                LuauNativeProtection.ThrowIfFailed(
                    this,
                    l,
                    writableStatus,
                    "unlock the sandbox metatable");
                try
                {
                    PushFunction(guard);
                    SandboxSetField(metatableIndex, NewIndexKey, "install the sandbox global guard");

                    SandboxPushLiteral(MetatableLockValue, "create the sandbox metatable lock");
                    SandboxSetField(metatableIndex, MetatableKey, "lock the sandbox metatable");
                }
                finally
                {
                    LuauNativeProtection.Prepare(context);
                    var readonlyStatus = luau_host_table_set_readonly(l, metatableIndex, 1);
                    LuauNativeProtection.ThrowIfFailed(
                        this,
                        l,
                        readonlyStatus,
                        "freeze the sandbox metatable");
                }
            }
            finally
            {
                SetTop(originalTop);
            }
        }

        context.MarkThreadSandboxed(pointer);
    }

    void EnsureSandboxBaseLibrary()
    {
        var originalTop = luau_host_stack_get_top(l);
        bool hasGlobalEnvironment;
        try
        {
            SandboxGetGlobal(GlobalEnvironmentName, "inspect the root global environment");
            hasGlobalEnvironment =
                luau_host_type(l, -1) == (int)LuauHostType.Table &&
                luau_host_is_global(l, -1) != 0;
        }
        finally
        {
            SetTop(originalTop);
        }

        if (!hasGlobalEnvironment ||
            !HasGlobalFunction(RawGetName) ||
            !HasGlobalFunction(RawSetName) ||
            !HasGlobalFunction(ErrorName) ||
            !HasGlobalFunction(ToStringName))
        {
            ThrowHelper.ThrowInvalidOperationException(
                "SandboxRoot requires the Luau base library. Call OpenBaseLibrary() before registering host APIs and applying the sandbox.");
        }
    }

    bool HasGlobalFunction(ReadOnlySpan<byte> name)
    {
        var originalTop = luau_host_stack_get_top(l);
        try
        {
            SandboxGetGlobal(name, "inspect a required sandbox function");
            return luau_host_type(l, -1) == (int)LuauHostType.Function;
        }
        finally
        {
            SetTop(originalTop);
        }
    }

    void SandboxGetGlobal(ReadOnlySpan<byte> name, string operation)
    {
        var resultType = 0;
        fixed (byte* pointer = name)
        {
            LuauNativeProtection.Prepare(context);
            var status = luau_host_global_get(l, pointer, &resultType);
            if (status == LuauHostStatus.ThreadNotRunnable)
            {
                GetGlobalOnRunnableThread(pointer, name.Length - 1, operation);
            }
            else
            {
                LuauNativeProtection.ThrowIfFailed(this, l, status, operation);
            }
        }
    }

    void SandboxPushGlobal(string operation)
    {
        LuauNativeProtection.Prepare(context);
        var status = luau_host_global_push(l);
        LuauNativeProtection.ThrowIfFailed(this, l, status, operation);
    }

    void SandboxPushValue(int index, string operation)
    {
        LuauNativeProtection.Prepare(context);
        var status = luau_host_push_value(l, index);
        LuauNativeProtection.ThrowIfFailed(this, l, status, operation);
    }

    bool SandboxGetMetatable(int index)
    {
        var result = 0;
        LuauNativeProtection.Prepare(context);
        var status = luau_host_metatable_get(l, index, &result);
        LuauNativeProtection.ThrowIfFailed(this, l, status, "inspect a sandbox metatable");
        return result != 0;
    }

    void SandboxPushLiteral(ReadOnlySpan<byte> nullTerminatedValue, string operation)
    {
        fixed (byte* value = nullTerminatedValue)
        {
            LuauNativeProtection.Prepare(context);
            var status = luau_host_push_string(
                l,
                value,
                checked((ulong)(nullTerminatedValue.Length - 1)));
            LuauNativeProtection.ThrowIfFailed(this, l, status, operation);
        }
    }

    void SandboxSetField(int index, ReadOnlySpan<byte> key, string operation)
    {
        var tableIndex = luau_host_stack_abs_index(l, index);
        fixed (byte* pointer = key)
        {
            LuauNativeProtection.Prepare(context);
            var pushStatus = luau_host_push_string(
                l,
                pointer,
                checked((ulong)(key.Length - 1)));
            LuauNativeProtection.ThrowIfFailed(this, l, pushStatus, operation);

            LuauNativeProtection.Prepare(context);
            var insertStatus = luau_host_stack_insert(l, -2);
            LuauNativeProtection.ThrowIfFailed(this, l, insertStatus, operation);

            LuauNativeProtection.Prepare(context);
            var status = luau_host_table_set(l, tableIndex);
            LuauNativeProtection.ThrowIfFailed(this, l, status, operation);
        }
    }
}
