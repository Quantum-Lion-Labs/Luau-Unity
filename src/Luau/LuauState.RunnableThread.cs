using System.Runtime.CompilerServices;
using Luau.Internal.Interop;
using static Luau.Internal.Interop.NativeMethods;

namespace Luau;

// Luau runs code only on a thread whose native status is OK. A direct host
// operation that can reach Luau code (a global or metamethod-aware table
// access, a display conversion, a bytecode load, or a library registration)
// on a thread that yielded, is suspended by a pending async callback, or
// ended with an error is refused by the host with ThreadNotRunnable before
// anything runs or is consumed. The methods here then do the same work on a
// runnable thread of the same root and leave this thread's stack exactly as
// the refused operation would have on success (maintainer guide section 6.7).
// They run only after a refusal, so the common paths pay nothing for them.
public unsafe partial class LuauState
{
    /// <summary>
    /// Reads global <paramref name="name"/> of this thread on a runnable
    /// thread. On success one value is appended to this thread.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    void GetGlobalOnRunnableThread(byte* name, int nameLength, string operation)
    {
        var runner = context.AcquireHostOperationThread(this, operation);
        var runnerTop = luau_host_stack_get_top(runner);
        try
        {
            // This thread's own globals, indexed from the runnable thread.
            LuauNativeProtection.Prepare(context);
            var status = luau_host_global_push(l);
            LuauNativeProtection.ThrowIfFailed(this, l, status, operation);
            MoveValues(l, runner, 1, operation);
            PushKey(runner, name, nameLength, operation);

            var ignoredType = 0;
            LuauNativeProtection.Prepare(context);
            status = luau_host_table_get(runner, -2, &ignoredType);
            LuauNativeProtection.ThrowIfFailed(this, runner, status, operation);
            MoveValues(runner, l, 1, operation);
        }
        finally
        {
            // Shrinking needs no free slot, so it cannot fail.
            _ = luau_host_stack_set_top(runner, runnerTop);
        }
    }

    /// <summary>
    /// Writes this thread's top value to its global <paramref name="name"/>
    /// on a runnable thread, consuming the value.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    void SetGlobalOnRunnableThread(byte* name, int nameLength, string operation)
    {
        var runner = context.AcquireHostOperationThread(this, operation);
        var runnerTop = luau_host_stack_get_top(runner);
        try
        {
            // Move the value first: the globals table then takes its slot,
            // so this needs no more room here than the write itself.
            MoveValues(l, runner, 1, operation);
            LuauNativeProtection.Prepare(context);
            var status = luau_host_global_push(l);
            LuauNativeProtection.ThrowIfFailed(this, l, status, operation);
            MoveValues(l, runner, 1, operation);

            // value, globals -> globals, key, value
            InsertValue(runner, -2, operation);
            PushKey(runner, name, nameLength, operation);
            InsertValue(runner, -2, operation);

            LuauNativeProtection.Prepare(context);
            status = luau_host_table_set(runner, -3);
            LuauNativeProtection.ThrowIfFailed(this, runner, status, operation);
        }
        finally
        {
            _ = luau_host_stack_set_top(runner, runnerTop);
        }
    }

    /// <summary>
    /// Performs <c>luau_host_table_get(this, -2)</c> on a runnable thread:
    /// this thread's top two values, a table and a key, become the table and
    /// its value.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void TableGetOnRunnableThread(string operation)
    {
        var runner = context.AcquireHostOperationThread(this, operation);
        var runnerTop = luau_host_stack_get_top(runner);
        try
        {
            MoveValues(l, runner, 2, operation);
            var ignoredType = 0;
            LuauNativeProtection.Prepare(context);
            var status = luau_host_table_get(runner, -2, &ignoredType);
            LuauNativeProtection.ThrowIfFailed(this, runner, status, operation);
            MoveValues(runner, l, 2, operation);
        }
        finally
        {
            _ = luau_host_stack_set_top(runner, runnerTop);
        }
    }

    /// <summary>
    /// Performs <c>luau_host_table_set(this, -3)</c> on a runnable thread:
    /// this thread's top three values, a table, a key, and a value, become
    /// the table.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void TableSetOnRunnableThread(string operation)
    {
        var runner = context.AcquireHostOperationThread(this, operation);
        var runnerTop = luau_host_stack_get_top(runner);
        try
        {
            MoveValues(l, runner, 3, operation);
            LuauNativeProtection.Prepare(context);
            var status = luau_host_table_set(runner, -3);
            LuauNativeProtection.ThrowIfFailed(this, runner, status, operation);
            MoveValues(runner, l, 1, operation);
        }
        finally
        {
            _ = luau_host_stack_set_top(runner, runnerTop);
        }
    }

    /// <summary>
    /// Formats the value at <paramref name="index"/> on a runnable thread.
    /// This thread's stack is left as it was.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    string ToDisplayStringOnRunnableThread(int index, int? maxUtf8Bytes, out bool truncated, string operation)
    {
        var runner = context.AcquireHostOperationThread(this, operation);
        var runnerTop = luau_host_stack_get_top(runner);
        try
        {
            LuauNativeProtection.Prepare(context);
            var status = luau_host_push_value(l, index);
            LuauNativeProtection.ThrowIfFailed(this, l, status, operation);
            MoveValues(l, runner, 1, operation);

            byte* result = null;
            ulong length = 0;
            LuauNativeProtection.Prepare(context);
            status = luau_host_to_display_string(runner, -1, &result, &length);
            LuauNativeProtection.ThrowIfFailed(this, runner, status, operation);

            // The string is borrowed from the runnable thread's stack.
            return DecodeDisplayString(result, length, maxUtf8Bytes, out truncated);
        }
        finally
        {
            _ = luau_host_stack_set_top(runner, runnerTop);
        }
    }

    /// <summary>
    /// Performs <c>luau_host_load(this, ...)</c> on a new thread created from
    /// this one, which shares this thread's globals: the loaded function's
    /// environment and the imports resolved while loading are the ones a load
    /// here would use. Returns the load's outer status; the value it appends
    /// (a function, a load error, or an error object) is moved to this thread.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    LuauHostStatus LoadOnThreadSharingGlobals(
        byte* chunkName,
        byte* bytecode,
        ulong bytecodeLength,
        LuauHostStatus* loadStatus,
        string operation)
    {
        var runner = context.AcquireHostOperationThread(this, operation);
        var runnerTop = luau_host_stack_get_top(runner);
        try
        {
            var fresh = CreateThreadSharingGlobals(runner, operation, out var status);
            if (fresh == null)
            {
                // The creation failure and its error object stand in for the
                // load's, with the same stack shape.
                return status;
            }

            LuauNativeProtection.Prepare(context);
            status = luau_host_load(fresh, chunkName, bytecode, bytecodeLength, 0, loadStatus);
            if (status == LuauHostStatus.Ok || AppendsErrorObject(status))
            {
                MoveValues(fresh, l, 1, operation);
            }

            return status;
        }
        finally
        {
            _ = luau_host_stack_set_top(runner, runnerTop);
        }
    }

    /// <summary>
    /// Opens a standard library on a new thread created from this one, which
    /// shares this thread's globals. The library's results are discarded.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    void OpenLibraryOnThreadSharingGlobals(LuauHostLibrary library, string operation)
    {
        var runner = context.AcquireHostOperationThread(this, operation);
        var runnerTop = luau_host_stack_get_top(runner);
        try
        {
            var fresh = CreateThreadSharingGlobals(runner, operation, out var status);
            if (fresh == null)
            {
                LuauNativeProtection.ThrowIfFailed(this, l, status, operation);
                return;
            }

            var ignoredResultCount = 0;
            LuauNativeProtection.Prepare(context);
            status = luau_host_open_library(fresh, library, &ignoredResultCount);
            LuauNativeProtection.ThrowIfFailed(this, fresh, status, operation);
        }
        finally
        {
            _ = luau_host_stack_set_top(runner, runnerTop);
        }
    }

    /// <summary>
    /// Creates a thread that inherits this thread's globals and parks it on
    /// <paramref name="runner"/>, which keeps it reachable until the caller
    /// restores the runner's top. Returns null when creation failed; the
    /// failure's error object is then on this thread.
    /// </summary>
    LuauHostState* CreateThreadSharingGlobals(LuauHostState* runner, string operation, out LuauHostStatus status)
    {
        LuauHostState* fresh = null;
        LuauNativeProtection.Prepare(context);
        status = luau_host_thread_create(l, &fresh);
        if (status != LuauHostStatus.Ok)
        {
            return null;
        }

        // Parking it on the runner leaves this thread's frame as it was.
        MoveValues(l, runner, 1, operation);
        return fresh;
    }

    void PushKey(LuauHostState* thread, byte* name, int nameLength, string operation)
    {
        LuauNativeProtection.Prepare(context);
        var status = luau_host_push_string(thread, name, checked((ulong)nameLength));
        LuauNativeProtection.ThrowIfFailed(this, thread, status, operation);
    }

    void InsertValue(LuauHostState* thread, int index, string operation)
    {
        // Insert cannot fail for a valid index.
        var status = luau_host_stack_insert(thread, index);
        LuauNativeProtection.ThrowIfFailed(this, thread, status, operation);
    }

    void MoveValues(LuauHostState* from, LuauHostState* to, int count, string operation)
    {
        LuauNativeProtection.Prepare(context);
        var status = luau_host_stack_move(from, to, count);
        // Both threads belong to this root and the source holds the values,
        // so the only rejected precondition is a full destination frame.
        LuauNativeProtection.ThrowIfFailed(
            this,
            from,
            status == LuauHostStatus.InvalidArgument ? LuauHostStatus.StackLimit : status,
            operation);
    }

    static bool AppendsErrorObject(LuauHostStatus status)
    {
        return status is LuauHostStatus.LuaError
            or LuauHostStatus.MemoryQuota
            or LuauHostStatus.SystemOutOfMemory
            or LuauHostStatus.Canceled;
    }
}
