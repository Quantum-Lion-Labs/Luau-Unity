using System.Runtime.CompilerServices;
using Luau.Internal.Interop;
using static Luau.Internal.Interop.NativeMethods;

namespace Luau;

public unsafe partial class LuauState : IDisposable, ILuauReference
{
    LuauHostState* l;
    readonly LuauVmContext context;
    LuauState? root;
    readonly bool isMainThread;
    int reference;
    int disposeState;
    WeakReference<LuauState>? cacheEntry;
    readonly DisposableBag disposables = new();
    readonly CancellationTokenSource lifetimeCancellationSource = new();
    int managedResourcesDisposeState;
    int coroutineStatus;

    /// <summary>Gets whether this wrapper or its VM root has been disposed.</summary>
    public bool IsDisposed => Volatile.Read(ref disposeState) != 0 || context.IsDisposed;

    /// <summary>Gets whether this wrapper owns the VM root rather than a child coroutine.</summary>
    public bool IsMainThread => isMainThread;

    /// <summary>Gets the immutable options snapshot applied when the root was created.</summary>
    public LuauStateOptions Options => context.Options;

    /// <summary>Gets the root allocator's current, peak, and configured-limit byte counts.</summary>
    public LuauMemoryUsageSnapshot MemoryUsage => context.MemoryUsage;

    LuauReferenceAccess ILuauReference.AcquireReference()
    {
        var access = EnterNativeAccess();
        return new LuauReferenceAccess(GetRoot(), reference, lifetimeGate: null, access);
    }

    internal LuauState? From => isMainThread ? null : root;
    internal LuauVmContext Context => context;
    internal LuauHostState* PointerUnsafe => l;
    internal int RegisteredDisposableCount => disposables.Count;
    internal CancellationToken LifetimeToken => lifetimeCancellationSource.Token;

    internal void RegisterDisposable(IDisposable disposable)
    {
        disposables.Add(disposable);
    }

    internal void UnregisterDisposable(IDisposable disposable)
    {
        disposables.Remove(disposable);
    }

    /// <summary>Creates a root VM with finite safe defaults. The caller owns the returned state.</summary>
    public static LuauState Create()
    {
        return Create(LuauStateOptions.Default);
    }

    /// <summary>
    /// Creates a root VM from a validated snapshot of <paramref name="options"/>.
    /// The caller owns the returned state and must dispose it.
    /// </summary>
    /// <param name="options">Resource, trust, and execution policy for the new root.</param>
    /// <exception cref="LuauMemoryLimitException">The configured allocator ceiling prevents VM creation.</exception>
    /// <exception cref="PlatformNotSupportedException">The native host ABI is unavailable or incompatible.</exception>
    public static LuauState Create(LuauStateOptions options)
    {
        return Create(options, LuauNativeProtection.AbiVerifier);
    }

    internal static LuauState Create(LuauStateOptions options, LuauNativeAbiVerifier abiVerifier)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }
        if (abiVerifier == null)
        {
            throw new ArgumentNullException(nameof(abiVerifier));
        }
        options = options.Snapshot();
        options.Validate();
        abiVerifier.EnsureAvailable();

        var nativeOptions = default(LuauHostStateOptions);
        LuauHostStateOptions* nativeOptionsPointer = null;
        if (options.MemoryLimitBytes is { } memoryLimitBytes)
        {
            if ((ulong)memoryLimitBytes >= (ulong)(~(nuint)0))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    memoryLimitBytes,
                    "The memory limit cannot be represented by the native allocator on this platform.");
            }

            nativeOptions = new LuauHostStateOptions
            {
                struct_size = checked((uint)sizeof(LuauHostStateOptions)),
                version = 1,
                flags = LuauHostStateOptionFlags.TrackMemory,
                memory_limit_bytes = checked((ulong)memoryLimitBytes),
            };
            nativeOptionsPointer = &nativeOptions;
        }

        LuauHostState* statePointer = null;
        var failureInfo = new LuauHostMemoryInfo
        {
            struct_size = checked((uint)sizeof(LuauHostMemoryInfo)),
        };
        var status = luau_host_state_create(nativeOptionsPointer, &statePointer, &failureInfo);
        if (status != LuauHostStatus.Ok || statePointer == null)
        {
            if (status == LuauHostStatus.MemoryQuota ||
                failureInfo.failure == LuauHostAllocatorFailure.Quota)
            {
                var limit = options.MemoryLimitBytes!.Value;
                var usage = new LuauMemoryUsageSnapshot(
                    LuauVmContext.ToDiagnosticByteCount(failureInfo.current_bytes),
                    LuauVmContext.ToDiagnosticByteCount(failureInfo.peak_bytes),
                    limit);
                var attempted = Math.Max(
                    limit + 1,
                    LuauVmContext.ToDiagnosticByteCount(failureInfo.last_attempted_bytes));
                throw new LuauMemoryLimitException(null, usage, attempted);
            }

            if (status == LuauHostStatus.SystemOutOfMemory ||
                failureInfo.failure == LuauHostAllocatorFailure.System ||
                (status == LuauHostStatus.Ok && statePointer == null))
            {
                throw new OutOfMemoryException("Unable to create a Luau state.");
            }

            if (status == LuauHostStatus.InvalidArgument)
            {
                throw new PlatformNotSupportedException(
                    "The Luau host rejected the managed root-state options after ABI validation.");
            }

            throw new LuauException(
                $"The Luau host could not create a root state (status {(int)status}).");
        }

        return CreateStateInternal(statePointer, options);
    }

    internal static LuauState GetCachedState(LuauHostState* l)
    {
        if (l == null)
        {
            ThrowHelper.ThrowObjectDisposedException(nameof(LuauState));
        }

        if (LuauVmContext.TryGetState(l, out var state))
        {
            return state;
        }

        var main = luau_host_main_thread(l);
        if (!LuauVmContext.TryGetState(main, out var root))
        {
            ThrowHelper.ThrowInvalidOperationException("The Luau VM is not owned by a managed LuauState");
        }

        using var access = root.context.EnterNativeAccess(root);
        var originalTop = luau_host_stack_get_top(l);
        try
        {
            var ignoredIsMainThread = 0;
            LuauNativeProtection.Prepare(root.context);
            var status = luau_host_push_thread(l, &ignoredIsMainThread);
            LuauNativeProtection.ThrowIfFailed(root, l, status, "retain the current Luau thread");
            return root.context.GetOrCreateThread(l, l, -1);
        }
        finally
        {
            LuauNativeProtection.Prepare(root.context);
            var status = luau_host_stack_set_top(l, originalTop);
            LuauNativeProtection.ThrowIfFailed(root, l, status, "restore the callback stack");
        }
    }

    internal static LuauState GetCachedState(LuauHostState* l, LuauVmContext context)
    {
        if (l == null)
        {
            ThrowHelper.ThrowObjectDisposedException(nameof(LuauState));
        }
        if (context.TryGetOwnedState(l, out var state))
        {
            return state;
        }

        var main = luau_host_main_thread(l);
        if (!context.TryGetOwnedState(main, out var root))
        {
            ThrowHelper.ThrowInvalidOperationException("The Luau VM is not owned by a managed LuauState");
        }

        using var access = context.EnterNativeAccess(root);
        var originalTop = luau_host_stack_get_top(l);
        try
        {
            var ignoredIsMainThread = 0;
            LuauNativeProtection.Prepare(context);
            var status = luau_host_push_thread(l, &ignoredIsMainThread);
            LuauNativeProtection.ThrowIfFailed(root, l, status, "retain the current Luau thread");
            return context.GetOrCreateThread(l, l, -1);
        }
        finally
        {
            LuauNativeProtection.Prepare(context);
            var status = luau_host_stack_set_top(l, originalTop);
            LuauNativeProtection.ThrowIfFailed(root, l, status, "restore the callback stack");
        }
    }

    internal static LuauState CreateStateInternal(LuauHostState* l)
    {
        return CreateStateInternal(l, LuauStateOptions.Default);
    }

    static LuauState CreateStateInternal(LuauHostState* l, LuauStateOptions options)
    {
        if (l == null)
        {
            throw new OutOfMemoryException("Unable to create a Luau state");
        }

        if (LuauVmContext.TryGetState(l, out _))
        {
            ThrowHelper.ThrowInvalidOperationException("The Luau state is already registered");
        }

        var context = new LuauVmContext(l, options);
        LuauState? state = null;
        try
        {
            state = new LuauState(l, context, root: null, reference: -1, isMainThread: true);
            context.RegisterRoot(state);
            context.InstallInterruptTrampoline();
            return state;
        }
        catch
        {
            if (state == null)
            {
                luau_host_state_close(l);
            }
            else
            {
                state.Dispose();
            }
            throw;
        }
    }

    internal LuauState(LuauHostState* l, LuauVmContext context, LuauState? root, int reference, bool isMainThread)
    {
        this.l = l;
        this.context = context;
        this.root = root ?? this;
        this.reference = reference;
        this.isMainThread = isMainThread;
        coroutineStatus = (int)(isMainThread
            ? LuauThreadStatus.Running
            : LuauThreadStatus.Suspended);
    }

    /// <summary>
    /// Gets the managed lifecycle state of a child coroutine. Root-state use
    /// is invalid because coroutine lifecycle does not describe the root VM.
    /// </summary>
    public LuauThreadStatus GetStatus()
    {
        ThrowIfDisposed();
        if (IsMainThread)
        {
            ThrowHelper.ThrowInvalidOperationException(
                "Coroutine status is only available for child Luau threads, not the root state.");
        }

        return (LuauThreadStatus)Volatile.Read(ref coroutineStatus);
    }

    internal LuauHostStatus GetNativeOperationStatus()
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();
        return luau_host_thread_status(l);
    }

    internal void MarkCoroutineRunning()
    {
        if (!isMainThread)
        {
            Volatile.Write(ref coroutineStatus, (int)LuauThreadStatus.Running);
        }
    }

    internal void MarkCoroutineSuspended()
    {
        if (!isMainThread)
        {
            Volatile.Write(ref coroutineStatus, (int)LuauThreadStatus.Suspended);
        }
    }

    internal void MarkCoroutineDead()
    {
        if (!isMainThread)
        {
            Volatile.Write(ref coroutineStatus, (int)LuauThreadStatus.Dead);
        }
    }

    /// <summary>
    /// Gets the root wrapper that owns this coroutine. On a root state, returns
    /// this instance. The returned root is not a newly retained owner.
    /// </summary>
    public LuauState GetMainThread()
    {
        ThrowIfDisposed();
        return GetRoot();
    }

    /// <summary>Returns the Luau textual representation of this root or coroutine.</summary>
    public override unsafe string ToString()
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();
        return LuauReferenceHelper.RefToString(this, reference);
    }

    /// <summary>
    /// Releases this wrapper. Disposing a root cancels active operations and
    /// invalidates all child threads and VM-backed values; disposing a child
    /// releases only that coroutine wrapper.
    /// </summary>
    public void Dispose()
    {
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    void DisposeCore()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0)
        {
            return;
        }

        RequestLifetimeCancellation();
        try
        {
            if (isMainThread)
            {
                context.DisposeRoot(this);
            }
            else
            {
                context.ReleaseChild(this, l, Interlocked.Exchange(ref reference, -1), cacheEntry);
            }
        }
        catch
        {
            InvalidateNativeState();
            DisposeManagedResources();
        }
    }

    /// <summary>Releases the native state if its owner did not dispose it deterministically.</summary>
    ~LuauState()
    {
        try
        {
            DisposeCore();
        }
        catch
        {
            // Finalizers must never surface managed or native cleanup failures.
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ThrowIfDisposed()
    {
        if (IsDisposed) ThrowHelper.ThrowObjectDisposedException(nameof(LuauState));
        context.ThrowIfNativeAccessDenied();
    }

    internal LuauNativeAccess EnterNativeAccess()
    {
        return context.EnterNativeAccess(this);
    }

    internal void TryReleaseReference(int reference)
    {
        context.TryReleaseReference(reference);
    }

    internal void SetCacheEntry(WeakReference<LuauState> entry)
    {
        cacheEntry = entry;
    }

    internal void InvalidateFromRoot()
    {
        Interlocked.Exchange(ref disposeState, 1);
        Interlocked.Exchange(ref reference, -1);
        InvalidateNativeState();
        GC.SuppressFinalize(this);
    }

    internal void InvalidateNativeState()
    {
        l = null;
        cacheEntry = null;
        if (!isMainThread)
        {
            root = null;
        }
    }

    internal void DisposeManagedResources()
    {
        if (Interlocked.Exchange(ref managedResourcesDisposeState, 1) != 0)
        {
            return;
        }

        disposables.Dispose();
        lifetimeCancellationSource.Dispose();
    }

    internal void RequestLifetimeCancellation()
    {
        try
        {
            lifetimeCancellationSource.Cancel();
        }
        catch
        {
            // Host cancellation registrations cannot prevent native teardown.
        }
    }

    LuauState GetRoot()
    {
        ThrowIfDisposed();
        return root!;
    }
}
