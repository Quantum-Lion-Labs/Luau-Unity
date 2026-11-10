using Luau.Internal.Interop;
using static Luau.Internal.Interop.NativeMethods;

namespace Luau;

unsafe partial class LuauState
{
    /// <summary>Creates an empty table and returns an independently disposable owner.</summary>
    public LuauTable CreateTable()
    {
        return CreateTable(0, 0);
    }

    /// <summary>
    /// Creates an empty table with array and hash capacity hints and returns an
    /// independently disposable owner.
    /// </summary>
    /// <param name="nArr">The non-negative array-capacity hint.</param>
    /// <param name="nRec">The non-negative hash-capacity hint.</param>
    public LuauTable CreateTable(int nArr, int nRec)
    {
        ThrowIfDisposed();
        if (nArr < 0) throw new ArgumentOutOfRangeException(nameof(nArr));
        if (nRec < 0) throw new ArgumentOutOfRangeException(nameof(nRec));
        using var access = EnterNativeAccess();

        LuauNativeProtection.Prepare(context);
        var status = luau_host_table_create(l, nArr, nRec);
        LuauNativeProtection.ThrowIfFailed(this, l, status, "create a table");

        try
        {
            return new(this, ReferenceTopValue("retain a table"));
        }
        finally
        {
            SetTop(-2);
        }
    }

    /// <summary>
    /// Creates a one-based array table from <paramref name="values"/> and
    /// returns an independently disposable owner.
    /// </summary>
    /// <param name="values">Values borrowed for the duration of table construction.</param>
    public LuauTable CreateTable(ReadOnlySpan<LuauValue> values)
    {
        var table = CreateTable(values.Length, 0);
        try
        {
            for (int i = 0; i < values.Length; i++)
            {
                // Array slots are Luau number keys, matching script literals.
                table.RawSet(LuauValue.FromNumber(i + 1), values[i]);
            }
            return table;
        }
        catch
        {
            table.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates a table from a span without intermediate collections. When the
    /// span contains the same raw Luau key more than once, the later value wins.
    /// </summary>
    public LuauTable CreateTable(ReadOnlySpan<KeyValuePair<LuauValue, LuauValue>> values)
    {
        var table = CreateTable(0, values.Length);
        try
        {
            for (var index = 0; index < values.Length; index++)
            {
                table.RawSet(values[index].Key, values[index].Value);
            }
            return table;
        }
        catch
        {
            table.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates a table from one collection-friendly entry sequence. When the
    /// sequence contains the same raw Luau key more than once, the later value
    /// wins.
    /// </summary>
    public LuauTable CreateTable(IEnumerable<KeyValuePair<LuauValue, LuauValue>> values)
    {
        if (values == null) throw new ArgumentNullException(nameof(values));
        var capacity = values is IReadOnlyCollection<KeyValuePair<LuauValue, LuauValue>> collection
            ? collection.Count
            : 0;
        var table = CreateTable(0, capacity);
        try
        {
            foreach (var pair in values)
            {
                table.RawSet(pair.Key, pair.Value);
            }
            return table;
        }
        catch
        {
            table.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates a managed callback capability that Luau code can call. Unlike a
    /// script closure returned by a load or value conversion, this capability
    /// cannot be invoked directly through <see cref="LuauFunction.Invoke"/>.
    /// </summary>
    public LuauFunction CreateFunction(Action<LuauCallContext> callback)
    {
        return CreateFunction(name: null, callback);
    }

    /// <summary>
    /// Creates a named managed callback capability that Luau code can call.
    /// Managed callers cannot invoke the returned capability directly.
    /// </summary>
    public LuauFunction CreateFunction(string? name, Action<LuauCallContext> callback)
    {
        ThrowIfDisposed();
        if (callback == null) throw new ArgumentNullException(nameof(callback));

        return CreateRawFunction(
            name,
            (state, cancellation) =>
            {
                var frame = new LuauCallFrame(state, name, state.GetTop(), cancellation);
                try
                {
                    callback(new LuauCallContext(frame));
                    return frame.Complete();
                }
                finally
                {
                    frame.Invalidate();
                }
            });
    }

    /// <summary>
    /// Creates an asynchronous managed callback capability for Luau code.
    /// Managed callers cannot invoke the returned capability directly.
    /// </summary>
    public LuauFunction CreateAsyncFunction(Func<LuauCallContext, ValueTask> callback)
    {
        return CreateAsyncFunction(name: null, callback);
    }

    /// <summary>
    /// Creates a named asynchronous managed callback capability for Luau code.
    /// Managed callers cannot invoke the returned capability directly.
    /// </summary>
    public LuauFunction CreateAsyncFunction(
        string? name,
        Func<LuauCallContext, ValueTask> callback)
    {
        ThrowIfDisposed();
        if (callback == null) throw new ArgumentNullException(nameof(callback));

        return CreateRawAsyncFunction(
            name,
            (state, cancellation) => LuauManagedCallbackInvoker.InvokeAsync(
                state,
                name,
                callback,
                cancellation));
    }

    internal LuauFunction CreateRawFunction(
        string? name,
        Func<LuauState, LuauOperationCancellation, int> callback)
    {
        ThrowIfDisposed();
        if (callback == null) throw new ArgumentNullException(nameof(callback));
        return new LuauCSharpFunction(this, callback, name);
    }

    internal LuauFunction CreateRawAsyncFunction(
        string? name,
        Func<LuauState, LuauOperationCancellation, ValueTask<int>> callback)
    {
        ThrowIfDisposed();
        if (callback == null) throw new ArgumentNullException(nameof(callback));
        return new LuauCSharpAsyncFunction(this, callback, name);
    }

    /// <summary>
    /// Creates a coroutine retained by this VM root. The returned wrapper may
    /// be disposed independently; root disposal always invalidates it.
    /// </summary>
    public unsafe LuauState CreateThread()
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();

        LuauHostState* threadPtr = null;
        LuauNativeProtection.Prepare(context);
        var status = luau_host_thread_create(l, &threadPtr);
        LuauNativeProtection.ThrowIfFailed(this, l, status, "create a Luau thread");

        try
        {
            return context.GetOrCreateThread(l, threadPtr, -1);
        }
        finally
        {
            SetTop(-2);
        }
    }

    /// <summary>Creates a zero-filled buffer and returns an independently disposable owner.</summary>
    /// <param name="size">The non-negative buffer length in bytes.</param>
    public unsafe LuauBuffer CreateBuffer(int size)
    {
        ThrowIfDisposed();
        if (size < 0) ThrowHelper.ThrowArgumentException(nameof(size), "Buffer size must be non-negative");
        using var access = EnterNativeAccess();

        void* data = null;
        LuauNativeProtection.Prepare(context);
        var status = luau_host_buffer_create(l, (ulong)size, &data);
        LuauNativeProtection.ThrowIfFailed(this, l, status, "create a buffer");

        try
        {
            return new LuauBuffer(this, ReferenceTopValue("retain a buffer"));
        }
        finally
        {
            SetTop(-2);
        }
    }

    /// <summary>
    /// Creates a buffer by copying <paramref name="str"/> and returns an
    /// independently disposable owner.
    /// </summary>
    public unsafe LuauBuffer CreateBuffer(ReadOnlySpan<byte> str)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();
        void* data = null;
        LuauNativeProtection.Prepare(context);
        var status = luau_host_buffer_create(l, (ulong)str.Length, &data);
        LuauNativeProtection.ThrowIfFailed(this, l, status, "create a buffer");

        try
        {
            str.CopyTo(new Span<byte>(data, str.Length));

            return new LuauBuffer(this, ReferenceTopValue("retain a buffer"));
        }
        finally
        {
            SetTop(-2);
        }
    }

    unsafe int ReferenceTopValue(string operation)
    {
        var reference = -1;
        LuauNativeProtection.Prepare(context);
        var status = luau_host_reference_create(l, -1, &reference);
        LuauNativeProtection.ThrowIfFailed(this, l, status, operation);
        return reference;
    }
}

internal static class LuauManagedCallbackInvoker
{
    internal static async ValueTask<int> InvokeAsync(
        LuauState state,
        string? name,
        Func<LuauCallContext, ValueTask> callback,
        LuauOperationCancellation cancellation)
    {
        var frame = new LuauCallFrame(state, name, state.GetTop(), cancellation);
        try
        {
            await callback(new LuauCallContext(frame)).ConfigureAwait(false);
            return frame.Complete();
        }
        finally
        {
            frame.Invalidate();
        }
    }
}
