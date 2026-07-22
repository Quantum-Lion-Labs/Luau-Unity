using System.Collections;
using System.Diagnostics.CodeAnalysis;
using static Luau.Internal.Interop.NativeMethods;

namespace Luau;

/// <summary>Provides raw, dictionary-style and metamethod-aware access to a Luau table.</summary>
/// <remarks>
/// Enumeration follows Luau's live raw <c>next</c> contract. Mutation during
/// enumeration is unsupported: it is not a snapshot or fail-fast view, and
/// entries may be included or skipped. Hosts must not depend on visitation or
/// ordering after mutation. Dispose the enumerator after early exit or failure.
/// </remarks>
public unsafe sealed class LuauTable : ILuauReference, ILuauCallbackBorrowedReference, IDisposable, IEnumerable<KeyValuePair<LuauValue, LuauValue>>
{
    /// <summary>
    /// Enumerates raw entries. Disposable <see cref="Current"/> members are
    /// owned by the enumerator until the next move, reset, or disposal; call
    /// <see cref="LuauValue.Retain"/> to keep one. Thread members are shared,
    /// cached, non-retainable wrappers and are not owned by the enumerator.
    /// </summary>
    public struct Enumerator(LuauTable table) : IEnumerator<KeyValuePair<LuauValue, LuauValue>>
    {
        KeyValuePair<LuauValue, LuauValue> current;
        bool completed;

        /// <summary>Gets the current raw key/value pair.</summary>
        public KeyValuePair<LuauValue, LuauValue> Current => current;

        object IEnumerator.Current => Current;

        /// <summary>
        /// Advances to the next live raw entry and releases reference wrappers
        /// owned by the previous <see cref="Current"/> value.
        /// </summary>
        public bool MoveNext()
        {
            if (completed)
            {
                return false;
            }

            try
            {
                if (table.TryMoveNext(current.Key, out var next))
                {
                    DisposeCurrent();
                    current = next;
                    return true;
                }

                DisposeCurrent();
                current = default;
                completed = true;
                return false;
            }
            catch
            {
                DisposeCurrent();
                current = default;
                completed = true;
                throw;
            }
        }

        /// <summary>Releases reference wrappers owned by the current entry.</summary>
        public void Dispose()
        {
            DisposeCurrent();
            current = default;
            completed = true;
        }

        /// <summary>
        /// Releases the current entry and restarts live raw enumeration.
        /// Mutation between iterations remains unsupported.
        /// </summary>
        public void Reset()
        {
            DisposeCurrent();
            current = default;
            completed = false;
        }

        void DisposeCurrent()
        {
            current.Value.DisposeOwnedReference();
            current.Key.DisposeOwnedReference();
        }
    }

    LuauState? state;
    int reference;
    int disposeState;
    readonly object lifetimeGate = new();
    readonly LuauCallFrame? borrowedFrame;

    /// <summary>Gets whether this reference is valid only for its callback frame.</summary>
    public bool IsBorrowed => borrowedFrame != null;

    /// <summary>Gets whether this table or its owning root has been disposed.</summary>
    public bool IsDisposed
    {
        get
        {
            if (Volatile.Read(ref disposeState) != 0)
            {
                return true;
            }

            var currentState = Volatile.Read(ref state);
            return currentState == null || currentState.IsDisposed;
        }
    }

    internal LuauState? OriginatingState => Volatile.Read(ref state);

    LuauReferenceAccess ILuauReference.AcquireReference() => AcquireReference();

    /// <summary>
    /// Gets or sets a value using metamethod-aware Luau table semantics. Use
    /// <see cref="RawGet"/> and <see cref="RawSet"/> for dictionary-style access
    /// that does not invoke <c>__index</c> or <c>__newindex</c>. A
    /// disposable getter result is caller-owned and must be disposed. A thread
    /// result is the VM's shared cached child wrapper; dispose it only after all
    /// holders are finished.
    /// </summary>
    public LuauValue this[LuauValue key]
    {
        get
        {
            ValidateDictionaryKey(key);
            using var access = AcquireReference();
            var state = access.State;
            using var hostOperation = new LuauDirectHostOperationScope(state);
            var pointer = state.PointerUnsafe;
            state.Push(this);
            state.Push(key);

            var ignoredType = 0;
            LuauNativeProtection.Prepare(state.Context);
            var status = luau_host_table_get(pointer, -2, &ignoredType);
            LuauNativeProtection.ThrowIfFailed(state, pointer, status, "read a Luau table value");
            var result = state.ToValue(-1);
            try
            {
                hostOperation.CompleteAndRestore(
                    "A direct host table read cannot yield or suspend the Luau thread.");
                return result;
            }
            catch
            {
                result.DisposeUnpublishedReference();
                throw;
            }
        }
        set
        {
            ValidateDictionaryKey(key);
            using var access = AcquireReference();
            var state = access.State;
            using var hostOperation = new LuauDirectHostOperationScope(state);
            var pointer = state.PointerUnsafe;
            state.Push(this);
            state.Push(key);
            state.Push(value);

            LuauNativeProtection.Prepare(state.Context);
            var status = luau_host_table_set(pointer, -3);
            LuauNativeProtection.ThrowIfFailed(state, pointer, status, "write a Luau table value");
            hostOperation.CompleteAndRestore(
                "A direct host table write cannot yield or suspend the Luau thread.");
        }
    }

    /// <summary>
    /// Gets Luau's raw sequence length for this table. This is the value used
    /// by raw <c>#</c>/<c>lua_objlen</c> semantics, not a count of key/value
    /// entries.
    /// </summary>
    public int Length
    {
        get
        {
            using var access = AcquireReference();
            var state = access.State;
            var pointer = state.PointerUnsafe;
            var originalTop = luau_host_stack_get_top(pointer);
            try
            {
                state.Push(this);
                return luau_host_object_length(pointer, -1);
            }
            finally
            {
                state.SetTop(originalTop);
            }
        }
    }

    /// <summary>
    /// Gets the number of raw key/value entries. This operation is O(n) and
    /// does not invoke metamethods.
    /// </summary>
    public int EntryCount
    {
        get
        {
            using var access = AcquireReference();
            var state = access.State;
            var pointer = state.PointerUnsafe;
            var originalTop = luau_host_stack_get_top(pointer);
            try
            {
                state.Push(this);
                state.PushNil();
                var count = 0;
                while (true)
                {
                    var hasNext = 0;
                    LuauNativeProtection.Prepare(state.Context);
                    var status = luau_host_table_next(pointer, -2, &hasNext);
                    LuauNativeProtection.ThrowIfFailed(
                        state,
                        pointer,
                        status,
                        "count raw Luau table entries");
                    if (hasNext == 0) return count;
                    count = checked(count + 1);
                    state.Pop(1); // Keep the key for the next native iteration.
                }
            }
            finally
            {
                state.SetTop(originalTop);
            }
        }
    }

    internal LuauTable(LuauState state, int reference, LuauCallFrame? borrowedFrame = null)
    {
        this.state = state;
        this.reference = reference;
        this.borrowedFrame = borrowedFrame;
        borrowedFrame?.RegisterBorrowed(this);
    }

    /// <summary>
    /// Creates a shallow Luau clone with an independently disposable managed
    /// owner. The clone has distinct table identity from this table.
    /// </summary>
    public LuauTable Clone()
    {
        using var access = AcquireReference();
        var state = access.State;
        var pointer = state.PointerUnsafe;
        var originalTop = luau_host_stack_get_top(pointer);
        try
        {
            state.Push(this);
            LuauNativeProtection.Prepare(state.Context);
            var status = luau_host_table_clone(pointer, -1);
            LuauNativeProtection.ThrowIfFailed(state, pointer, status, "clone a Luau table");
            return state.ToTable(-1);
        }
        finally
        {
            state.SetTop(originalTop);
        }
    }

    /// <summary>Creates an independently disposable owner for this same table.</summary>
    public LuauTable Retain()
    {
        using var access = AcquireReference();
        return new LuauTable(
            access.State,
            LuauReferenceHelper.RetainReference(
                access.State,
                access.Reference,
                "retain a Luau table"));
    }

    /// <summary>
    /// Performs one low-level raw <c>next</c> step. When successful, the caller
    /// owns any disposable wrappers in <paramref name="result"/> and must
    /// dispose them. Thread values are shared cached child wrappers. Prefer
    /// <see cref="GetEnumerator"/> for automatic cleanup of disposable entries.
    /// </summary>
    public bool TryMoveNext(LuauValue key, out KeyValuePair<LuauValue, LuauValue> result)
    {
        using var access = AcquireReference();
        var state = access.State;
        using var hostOperation = new LuauDirectHostOperationScope(state);

        var pointer = state.PointerUnsafe;
        state.Push(this);
        state.Push(key);

        var hasNext = 0;
        LuauNativeProtection.Prepare(state.Context);
        var status = luau_host_table_next(pointer, -2, &hasNext);
        LuauNativeProtection.ThrowIfFailed(
            state,
            pointer,
            status,
            "enumerate a Luau table");
        if (hasNext == 0)
        {
            result = default;
            hostOperation.CompleteAndRestore(
                "Raw Luau table enumeration cannot yield or suspend the Luau thread.");
            return false;
        }

        var value = state.ToValue(-1);
        var nextKey = default(LuauValue);
        try
        {
            nextKey = state.ToValue(-2);
            result = new(nextKey, value);
            hostOperation.CompleteAndRestore(
                "Raw Luau table enumeration cannot yield or suspend the Luau thread.");
            return true;
        }
        catch
        {
            nextKey.DisposeUnpublishedReference();
            value.DisposeUnpublishedReference();
            throw;
        }
    }

    /// <summary>
    /// Adds one raw entry without invoking metamethods. Nil and NaN are invalid
    /// keys, nil is not an addable value, and an existing raw key is rejected.
    /// </summary>
    public void Add(LuauValue key, LuauValue value)
    {
        ValidateDictionaryKey(key);
        if (value.IsNil)
        {
            throw new ArgumentException("Nil removes a Luau table entry and cannot be added as a value.", nameof(value));
        }
        if (RawContainsKey(key))
        {
            throw new ArgumentException("An entry with the same raw Luau key already exists.", nameof(key));
        }
        RawSet(key, value);
    }

    /// <summary>Adds one raw key/value pair using <see cref="Add(LuauValue, LuauValue)"/>.</summary>
    public void Add(KeyValuePair<LuauValue, LuauValue> item)
    {
        Add(item.Key, item.Value);
    }

    /// <summary>Removes every raw entry without invoking metamethods.</summary>
    public void Clear()
    {
        using var access = AcquireReference();
        var state = access.State;
        var pointer = state.PointerUnsafe;
        var originalTop = luau_host_stack_get_top(pointer);
        try
        {
            LuauReferenceHelper.PushReference(state, access.Reference, "clear a Luau table");
            LuauNativeProtection.Prepare(state.Context);
            var status = luau_host_table_clear(pointer, -1);
            LuauNativeProtection.ThrowIfFailed(state, pointer, status, "clear a Luau table");
        }
        finally
        {
            state.SetTop(originalTop);
        }
    }

    /// <summary>Tests raw key presence without invoking <c>__index</c>.</summary>
    public bool ContainsKey(LuauValue key)
    {
        ValidateDictionaryKey(key);
        return RawContainsKey(key);
    }

    /// <summary>
    /// Reads a raw value without invoking <c>__index</c>. The caller owns and
    /// must dispose a disposable wrapper result. A thread result is the VM's
    /// shared cached child wrapper; dispose it only after all holders finish.
    /// </summary>
    public LuauValue RawGet(LuauValue key)
    {
        ValidateDictionaryKey(key);
        using var access = AcquireReference();
        var state = access.State;
        using var hostOperation = new LuauDirectHostOperationScope(state);
        var pointer = state.PointerUnsafe;
        state.Push(this);
        state.Push(key);

        var ignoredType = 0;
        LuauNativeProtection.Prepare(state.Context);
        var status = luau_host_table_raw_get(pointer, -2, &ignoredType);
        LuauNativeProtection.ThrowIfFailed(state, pointer, status, "read a raw Luau table value");
        var result = state.ToValue(-1);
        try
        {
            hostOperation.CompleteAndRestore(
                "A raw Luau table read cannot yield or suspend the Luau thread.");
            return result;
        }
        catch
        {
            result.DisposeUnpublishedReference();
            throw;
        }
    }

    /// <summary>
    /// Writes a raw entry without invoking <c>__newindex</c>. A nil value
    /// removes the raw entry.
    /// </summary>
    public void RawSet(LuauValue key, LuauValue value)
    {
        ValidateDictionaryKey(key);
        using var access = AcquireReference();
        var state = access.State;
        var pointer = state.PointerUnsafe;
        var originalTop = luau_host_stack_get_top(pointer);
        try
        {
            state.Push(this);
            state.Push(key);
            state.Push(value);

            LuauNativeProtection.Prepare(state.Context);
            var status = luau_host_table_raw_set(pointer, -3);
            LuauNativeProtection.ThrowIfFailed(state, pointer, status, "write a raw Luau table value");
        }
        finally
        {
            state.SetTop(originalTop);
        }
    }

    internal void SetReadOnly()
    {
        using var access = AcquireReference();
        var state = access.State;
        var pointer = state.PointerUnsafe;
        var originalTop = luau_host_stack_get_top(pointer);
        try
        {
            state.Push(this);
            LuauNativeProtection.Prepare(state.Context);
            var status = luau_host_table_set_readonly(pointer, -1, 1);
            LuauNativeProtection.ThrowIfFailed(
                state,
                pointer,
                status,
                "make a Luau table read-only");
        }
        finally
        {
            state.SetTop(originalTop);
        }
    }

    /// <summary>
    /// Attempts a raw read. On success, the caller owns any disposable wrapper
    /// in <paramref name="value"/> and must dispose it. A thread value is the
    /// VM's shared cached child wrapper.
    /// </summary>
    public bool TryGetValue(LuauValue key, [MaybeNullWhen(false)] out LuauValue value)
    {
        ValidateDictionaryKey(key);
        value = RawGet(key);
        return !value.IsNil;
    }

    /// <summary>
    /// Removes a raw entry without invoking metamethods and reports whether it
    /// existed. Assigning <see cref="LuauValue.Nil"/> has the same raw effect.
    /// </summary>
    public bool Remove(LuauValue key)
    {
        ValidateDictionaryKey(key);
        if (!RawContainsKey(key)) return false;
        RawSet(key, LuauValue.Nil);
        return true;
    }

    static void ValidateDictionaryKey(LuauValue key)
    {
        if (key.IsNil)
        {
            throw new ArgumentException("Nil is not a valid Luau table key.", nameof(key));
        }
        if (key.Type == LuauType.Number && double.IsNaN(key.Read<double>()))
        {
            throw new ArgumentException("NaN is not a valid Luau table key.", nameof(key));
        }
    }

    bool RawContainsKey(LuauValue key)
    {
        using var access = AcquireReference();
        var state = access.State;
        var pointer = state.PointerUnsafe;
        var originalTop = luau_host_stack_get_top(pointer);
        try
        {
            state.Push(this);
            state.Push(key);
            var resultType = 0;
            LuauNativeProtection.Prepare(state.Context);
            var status = luau_host_table_raw_get(pointer, -2, &resultType);
            LuauNativeProtection.ThrowIfFailed(
                state,
                pointer,
                status,
                "inspect a raw Luau table key");
            return (Luau.Internal.Interop.LuauHostType)resultType !=
                Luau.Internal.Interop.LuauHostType.Nil;
        }
        finally
        {
            state.SetTop(originalTop);
        }
    }

    /// <summary>Returns Luau's diagnostic string for this table reference.</summary>
    public override string ToString()
    {
        using var access = AcquireReference();
        return LuauReferenceHelper.RefToString(access.State, access.Reference);
    }

    /// <summary>
    /// Creates a live raw enumerator that owns each disposable current entry
    /// until it advances or is disposed. Shared thread wrappers are excluded.
    /// </summary>
    public Enumerator GetEnumerator()
    {
        return new(this);
    }

    IEnumerator<KeyValuePair<LuauValue, LuauValue>> IEnumerable<KeyValuePair<LuauValue, LuauValue>>.GetEnumerator()
    {
        return GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    /// <summary>
    /// Releases this managed table owner. Disposal is idempotent and does not
    /// close the owning VM root.
    /// </summary>
    public void Dispose()
    {
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    void DisposeCore()
    {
        LuauState? owningState;
        int currentReference;
        lock (lifetimeGate)
        {
            if (Interlocked.Exchange(ref disposeState, 1) != 0)
            {
                return;
            }

            owningState = Interlocked.Exchange(ref state, null);
            currentReference = Interlocked.Exchange(ref reference, -1);
        }

        if (owningState != null && currentReference >= 0)
        {
            owningState.TryReleaseReference(currentReference);
        }
    }

    /// <summary>Releases an undisposed table owner as a final fallback.</summary>
    ~LuauTable()
    {
        try
        {
            DisposeCore();
        }
        catch
        {
            // Finalizers must not surface cleanup failures.
        }
    }

    LuauReferenceAccess AcquireReference()
    {
        borrowedFrame?.EnsureBorrowedActive();
        var currentState = Volatile.Read(ref state);
        if (currentState == null || currentState.IsDisposed)
        {
            ThrowHelper.ThrowObjectDisposedException(nameof(LuauTable));
        }

        var referenceState = currentState!.GetMainThread();
        var nativeAccess = currentState.EnterNativeAccess();
        Monitor.Enter(lifetimeGate);
        try
        {
            var currentReference = reference;
            if (disposeState != 0 ||
                !ReferenceEquals(state, currentState) ||
                currentReference < 0 ||
                currentState.IsDisposed)
            {
                ThrowHelper.ThrowObjectDisposedException(nameof(LuauTable));
            }

            return new LuauReferenceAccess(referenceState, currentReference, lifetimeGate, nativeAccess);
        }
        catch
        {
            Monitor.Exit(lifetimeGate);
            nativeAccess.Dispose();
            throw;
        }
    }


    void ILuauCallbackBorrowedReference.InvalidateBorrowed() => DisposeCore();
}
