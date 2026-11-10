using Luau.Internal.Interop;
using static Luau.Internal.Interop.NativeMethods;

namespace Luau;

public unsafe partial class LuauState
{
    /// <summary>
    /// Gets or sets a global by strict UTF-8 name. Disposable reference values
    /// returned by the getter are independently owned. A thread result is the
    /// VM's shared cached child wrapper; dispose it only after all holders are
    /// finished. The setter only borrows its value for this call.
    /// </summary>
    /// <remarks>
    /// Works whatever this thread is doing, including while it has yielded or
    /// waits on an async callback. Luau code the access reaches, such as a
    /// sandboxed thread's write guard or an <c>__index</c> metamethod, then
    /// runs on another thread of the same root and cannot yield; the
    /// suspended continuation is left untouched.
    /// </remarks>
    /// <param name="key">A UTF-8 name containing no NUL byte.</param>
    public LuauValue this[ReadOnlySpan<byte> key]
    {
        get
        {
            ThrowIfDisposed();
            if (key.IndexOf((byte)0) >= 0)
            {
                throw new ArgumentException("Global names cannot contain a NUL byte.", nameof(key));
            }

            using var name = new Utf8BufferScope(key, appendNull: true);
            return GetGlobal(name.NullTerminatedBytes);
        }
        set
        {
            ThrowIfDisposed();
            if (key.IndexOf((byte)0) >= 0)
            {
                throw new ArgumentException("Global names cannot contain a NUL byte.", nameof(key));
            }

            if (IsMainThread && context.IsRootSandboxed)
            {
                ThrowHelper.ThrowInvalidOperationException(
                    "Root globals are read-only after SandboxRoot has been applied.");
            }

            using var name = new Utf8BufferScope(key, appendNull: true);
            SetGlobal(name.NullTerminatedBytes, value);
        }
    }

    /// <summary>
    /// Gets or sets a global by managed name. Disposable reference values
    /// returned by the getter are independently owned. A thread result is the
    /// VM's shared cached child wrapper; dispose it only after all holders are
    /// finished. The setter only borrows its value for this call.
    /// </summary>
    /// <remarks>
    /// Works whatever this thread is doing, including while it has yielded or
    /// waits on an async callback. Luau code the access reaches, such as a
    /// sandboxed thread's write guard or an <c>__index</c> metamethod, then
    /// runs on another thread of the same root and cannot yield; the
    /// suspended continuation is left untouched.
    /// </remarks>
    /// <param name="key">A non-null name containing no NUL character.</param>
    public LuauValue this[string key]
    {
        get
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (key.IndexOf('\0') >= 0)
            {
                throw new ArgumentException("Global names cannot contain a NUL character.", nameof(key));
            }
            using var name = new Utf8BufferScope(key.AsSpan(), appendNull: true);
            return GetGlobal(name.NullTerminatedBytes);
        }
        set
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (key.IndexOf('\0') >= 0)
            {
                throw new ArgumentException("Global names cannot contain a NUL character.", nameof(key));
            }
            if (IsMainThread && context.IsRootSandboxed)
            {
                ThrowHelper.ThrowInvalidOperationException(
                    "Root globals are read-only after SandboxRoot has been applied.");
            }
            using var name = new Utf8BufferScope(key.AsSpan(), appendNull: true);
            SetGlobal(name.NullTerminatedBytes, value);
        }
    }

    LuauValue GetGlobal(ReadOnlySpan<byte> nullTerminatedName)
    {
        using var access = EnterNativeAccess();
        using var hostOperation = new LuauDirectHostOperationScope(this);
        fixed (byte* name = nullTerminatedName)
        {
            var ignoredType = 0;
            LuauNativeProtection.Prepare(context);
            var status = luau_host_global_get(l, name, &ignoredType);
            if (status == LuauHostStatus.ThreadNotRunnable)
            {
                GetGlobalOnRunnableThread(name, nullTerminatedName.Length - 1, "read a Luau global");
            }
            else
            {
                LuauNativeProtection.ThrowIfFailed(this, l, status, "read a Luau global");
            }
        }

        var result = Pop();
        try
        {
            hostOperation.Complete(
                "A direct host global read cannot yield or suspend the Luau thread.");
            return result;
        }
        catch
        {
            result.DisposeUnpublishedReference();
            throw;
        }
    }

    void SetGlobal(ReadOnlySpan<byte> nullTerminatedName, LuauValue value)
    {
        using var access = EnterNativeAccess();
        using var hostOperation = new LuauDirectHostOperationScope(this);
        Push(value);
        fixed (byte* name = nullTerminatedName)
        {
            LuauNativeProtection.Prepare(context);
            var status = luau_host_global_set(l, name);
            if (status == LuauHostStatus.ThreadNotRunnable)
            {
                SetGlobalOnRunnableThread(name, nullTerminatedName.Length - 1, "write a Luau global");
            }
            else
            {
                LuauNativeProtection.ThrowIfFailed(this, l, status, "write a Luau global");
            }
        }

        hostOperation.Complete(
            "A direct host global write cannot yield or suspend the Luau thread.");
    }
}
