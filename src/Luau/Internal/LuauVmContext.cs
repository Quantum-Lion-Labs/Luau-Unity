using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Luau.Internal.Interop;
using static Luau.Internal.Interop.NativeMethods;

namespace Luau;

internal enum LuauAllocatorFailure
{
    None = 0,
    QuotaExceeded = 1,
    SystemOutOfMemory = 2,
}

internal ref struct LuauBytecodeLoadScope
{
    LuauVmContext? context;

    internal LuauBytecodeLoadScope(LuauVmContext context)
    {
        this.context = context;
    }

    public void Dispose()
    {
        var current = context;
        context = null;
        current?.EndBytecodeLoad();
    }
}

internal sealed unsafe class LuauVmContext
{
    static readonly ConcurrentDictionary<IntPtr, WeakReference<LuauState>> globalStates = new();
    static readonly ConcurrentDictionary<IntPtr, WeakReference<LuauVmContext>> globalContexts = new();
    static readonly ConcurrentDictionary<int, WeakReference<LuauVmContext>> managedCallbackOwners = new();
    static readonly AsyncLocal<ScriptOperation?> ambientOperation = new();
    static readonly LuauHostInterruptPoll interruptCallback = Interrupt;
    static readonly IntPtr interruptPointer = Marshal.GetFunctionPointerForDelegate(interruptCallback);
    static long nextGlobalManagedCallbackId;

    readonly object lifecycleGate = new();
    readonly object nativeGate = new();
    readonly Dictionary<IntPtr, WeakReference<LuauState>> states = [];
    readonly Dictionary<int, LuauManagedCallbackRegistration> managedCallbacks = [];
    readonly Dictionary<int, int> managedCallbackNativeReferences = [];
    readonly HashSet<int> managedCallbackWrapperOwners = [];
    readonly Dictionary<string, LuauValue> moduleCache = new(StringComparer.Ordinal);
    long cachedModuleBytes;
    readonly HashSet<string> loadingModules = new(StringComparer.Ordinal);
    readonly HashSet<IntPtr> sandboxedThreads = [];
    readonly List<int> deferredReferences = [];
    readonly CancellationTokenSource disposalCancellationSource = new();

    IntPtr mainPointer;
    WeakReference<LuauState>? rootEntry;
    WeakReference<LuauVmContext>? globalContextEntry;
    ScriptOperation? activeOperation;
    int lifecycleState;
    int rootSandboxed;
    int bytecodeLoadDepth;
    int releasedReferenceCount;
    int closeCount;
    int interruptInstallCount;
    LuauManagedCallbackRegistration? pendingManagedCallbackNativeReleases;

    LuauHostMemoryInfo finalMemoryInfo;
    bool hasFinalMemoryInfo;

    internal LuauVmContext(LuauHostState* main, LuauStateOptions options)
    {
        mainPointer = (IntPtr)main;
        Options = options;
        ObjectRegistry = new LuauObjectRegistry(options.MaxManagedHandleCount);
    }

    internal LuauStateOptions Options { get; }
    internal LuauObjectRegistry ObjectRegistry { get; }

    internal bool IsDisposed => Volatile.Read(ref lifecycleState) != 0;
    internal bool IsDisposalRequested => Volatile.Read(ref lifecycleState) != 0;
    internal CancellationToken DisposalToken => disposalCancellationSource.Token;
    internal bool IsRootSandboxed => Volatile.Read(ref rootSandboxed) != 0;
    internal bool IsLoadingBytecode => Volatile.Read(ref bytecodeLoadDepth) != 0;

    internal LuauBytecodeLoadScope BeginBytecodeLoad()
    {
        Interlocked.Increment(ref bytecodeLoadDepth);
        return new LuauBytecodeLoadScope(this);
    }

    internal void EndBytecodeLoad()
    {
        if (Interlocked.Decrement(ref bytecodeLoadDepth) < 0)
        {
            throw new InvalidOperationException("The Luau bytecode-load scope was disposed more than once.");
        }
    }

    internal int CachedStateCount
    {
        get
        {
            lock (lifecycleGate)
            {
                return states.Count;
            }
        }
    }

    internal int ReleasedReferenceCount => Volatile.Read(ref releasedReferenceCount);
    internal int CloseCount => Volatile.Read(ref closeCount);
    internal int InterruptInstallCount => Volatile.Read(ref interruptInstallCount);

    internal int ManagedCallbackCount
    {
        get
        {
            lock (lifecycleGate)
            {
                DrainManagedCallbackNativeReleasesLocked();
                return managedCallbacks.Count;
            }
        }
    }

    internal LuauMemoryUsageSnapshot MemoryUsage
    {
        get
        {
            lock (nativeGate)
            {
                var info = ReadMemoryInfo();
                if (info.tracked == 0)
                {
                    return LuauMemoryUsageSnapshot.Untracked;
                }

                long? limit = info.limit_bytes == 0
                    ? null
                    : ToDiagnosticByteCount(info.limit_bytes);
                return new LuauMemoryUsageSnapshot(
                    ToDiagnosticByteCount(info.current_bytes),
                    ToDiagnosticByteCount(info.peak_bytes),
                    limit);
            }
        }
    }

    internal LuauAllocatorFailure AllocatorFailure
    {
        get
        {
            lock (nativeGate)
            {
                return ReadMemoryInfo().failure switch
                {
                    LuauHostAllocatorFailure.None => LuauAllocatorFailure.None,
                    LuauHostAllocatorFailure.Quota => LuauAllocatorFailure.QuotaExceeded,
                    LuauHostAllocatorFailure.System => LuauAllocatorFailure.SystemOutOfMemory,
                    var failure => throw new LuauException(
                        $"The Luau host returned unknown allocator failure {failure}.")
                };
            }
        }
    }

    internal long LastAttemptedAllocationBytes
    {
        get
        {
            lock (nativeGate)
            {
                return ToDiagnosticByteCount(ReadMemoryInfo().last_attempted_bytes);
            }
        }
    }

    internal void ResetAllocatorFailure()
    {
        lock (nativeGate)
        {
            if (mainPointer == IntPtr.Zero)
            {
                return;
            }

            var status = luau_host_memory_reset_failure((LuauHostState*)mainPointer);
            if (status != LuauHostStatus.Ok)
            {
                throw new LuauException(
                    $"The Luau host could not reset allocator diagnostics (status {(int)status}).");
            }
        }
    }

    LuauHostMemoryInfo ReadMemoryInfo()
    {
        if (mainPointer == IntPtr.Zero)
        {
            return hasFinalMemoryInfo ? finalMemoryInfo : default;
        }

        var info = new LuauHostMemoryInfo
        {
            struct_size = checked((uint)sizeof(LuauHostMemoryInfo)),
        };
        var status = luau_host_memory_get((LuauHostState*)mainPointer, &info);
        if (status != LuauHostStatus.Ok)
        {
            throw new LuauException(
                $"The Luau host could not report allocator diagnostics (status {(int)status}).");
        }

        return info;
    }

    internal static long ToDiagnosticByteCount(ulong value)
    {
        return value > long.MaxValue ? long.MaxValue : (long)value;
    }

    internal ScriptOperation BeginOperation(
        LuauState state,
        string? chunkName,
        LuauExecutionOptions? options,
        CancellationToken cancellationToken,
        bool isAsync,
        ScriptOperationMode mode)
    {
        var effectiveOptions = LuauExecutionOptions.ResolveForOperation(
            Options.DefaultExecutionOptions,
            options);
        if (effectiveOptions.ContinuationScheduler is { } scheduler && !scheduler.CheckAccess())
        {
            throw new InvalidOperationException(
                "Luau execution must begin on its configured continuation scheduler. " +
                "Dispatch the call to the VM owner thread before compiling, pushing arguments, or resuming it.");
        }

        lock (nativeGate)
        {
            lock (lifecycleGate)
            {
                if (lifecycleState != 0 || mainPointer == IntPtr.Zero)
                {
                    ThrowHelper.ThrowObjectDisposedException(nameof(LuauState));
                }

                if (activeOperation != null)
                {
                    ThrowHelper.ThrowInvalidOperationException("The Luau VM is already executing.");
                }

                var operation = new ScriptOperation(
                    this,
                    state,
                    chunkName,
                    effectiveOptions,
                    cancellationToken,
                    isAsync,
                    mode,
                    ambientOperation.Value);

                ResetAllocatorFailure();
                activeOperation = operation;
                ambientOperation.Value = operation;
                return operation;
            }
        }
    }

    internal void EndOperation(ScriptOperation operation)
    {
        IntPtr pointerToClose = IntPtr.Zero;

        lock (nativeGate)
        {
            lock (lifecycleGate)
            {
                if (!ReferenceEquals(activeOperation, operation))
                {
                    return;
                }

                activeOperation = null;
                if (ReferenceEquals(ambientOperation.Value, operation))
                {
                    ambientOperation.Value = operation.PreviousAmbient;
                }

                if (lifecycleState == 0 && mainPointer != IntPtr.Zero)
                {
                    foreach (var reference in deferredReferences)
                    {
                        luau_host_reference_release((LuauHostState*)mainPointer, reference);
                        Interlocked.Increment(ref releasedReferenceCount);
                    }
                }

                deferredReferences.Clear();

                if (lifecycleState == 1 && mainPointer != IntPtr.Zero)
                {
                    pointerToClose = mainPointer;
                    mainPointer = IntPtr.Zero;
                }
            }

            if (pointerToClose != IntPtr.Zero)
            {
                CloseNative(pointerToClose);
            }
        }
    }

    internal LuauNativeAccess EnterNativeAccess(LuauState state)
    {
        Monitor.Enter(nativeGate);
        try
        {
            if (Volatile.Read(ref lifecycleState) != 0 ||
                state.IsDisposed ||
                state.PointerUnsafe == null)
            {
                ThrowHelper.ThrowObjectDisposedException(nameof(LuauState));
            }

            ThrowIfNativeAccessDenied();
            return new LuauNativeAccess(this, nativeGate);
        }
        catch
        {
            Monitor.Exit(nativeGate);
            throw;
        }
    }

    internal LuauNativeAccess EnterOperationNativeAccess(ScriptOperation operation)
    {
        Monitor.Enter(nativeGate);
        try
        {
            lock (lifecycleGate)
            {
                if (!ReferenceEquals(activeOperation, operation) || mainPointer == IntPtr.Zero)
                {
                    ThrowHelper.ThrowObjectDisposedException(nameof(LuauState));
                }
            }

            return new LuauNativeAccess(this, nativeGate);
        }
        catch
        {
            Monitor.Exit(nativeGate);
            throw;
        }
    }

    internal void ThrowIfNativeAccessDenied()
    {
        var active = Volatile.Read(ref activeOperation);
        var scheduler = active?.Options.ContinuationScheduler
            ?? Options.DefaultExecutionOptions.ContinuationScheduler;
        if (scheduler != null && !scheduler.CheckAccess())
        {
            throw new InvalidOperationException(
                "The Luau VM can only be accessed from its configured continuation scheduler.");
        }

        if (active != null && !ReferenceEquals(ambientOperation.Value, active))
        {
            ThrowHelper.ThrowInvalidOperationException("The Luau VM is executing on another operation.");
        }

        active?.ThrowIfAsyncCallbackAccessUnsafe();
    }

    internal ScriptOperation? GetActiveOperation()
    {
        return Volatile.Read(ref activeOperation);
    }

    internal int RegisterManagedCallback(
        string? name,
        Func<LuauState, CancellationToken, int> callback)
    {
        lock (lifecycleGate)
        {
            if (lifecycleState != 0)
            {
                ThrowHelper.ThrowObjectDisposedException(nameof(LuauState));
            }

            var id = NextManagedCallbackId();
            var registration = new LuauManagedCallbackRegistration(this, id, name, callback);
            return CompleteManagedCallbackRegistrationLocked(registration);
        }
    }

    internal int RegisterManagedCallback(
        string? name,
        Func<LuauState, CancellationToken, ValueTask<int>> callback)
    {
        lock (lifecycleGate)
        {
            if (lifecycleState != 0)
            {
                ThrowHelper.ThrowObjectDisposedException(nameof(LuauState));
            }

            var id = NextManagedCallbackId();
            var registration = new LuauManagedCallbackRegistration(this, id, name, callback);
            return CompleteManagedCallbackRegistrationLocked(registration);
        }
    }

    int CompleteManagedCallbackRegistrationLocked(
        LuauManagedCallbackRegistration registration)
    {
        try
        {
            managedCallbacks.Add(registration.Id, registration);
            managedCallbackWrapperOwners.Add(registration.Id);
            managedCallbackOwners[registration.Id] = new WeakReference<LuauVmContext>(this);
            return registration.Id;
        }
        catch
        {
            managedCallbackOwners.TryRemove(registration.Id, out _);
            managedCallbackWrapperOwners.Remove(registration.Id);
            managedCallbacks.Remove(registration.Id);
            registration.DisposeNativeReleaseToken();
            throw;
        }
    }

    internal bool TryGetManagedCallback(int id, out LuauManagedCallbackRegistration registration)
    {
        lock (lifecycleGate)
        {
            DrainManagedCallbackNativeReleasesLocked();
            return managedCallbacks.TryGetValue(id, out registration!);
        }
    }

    internal static bool TryGetManagedCallbackOwner(
        int id,
        out LuauVmContext context,
        out LuauManagedCallbackRegistration registration)
    {
        if (id > 0 &&
            managedCallbackOwners.TryGetValue(id, out var owner) &&
            owner.TryGetTarget(out context!) &&
            context.TryGetManagedCallback(id, out registration!))
        {
            return true;
        }

        managedCallbackOwners.TryRemove(id, out _);
        context = null!;
        registration = null!;
        return false;
    }

    internal bool TryGetOwnedState(LuauHostState* pointer, out LuauState state)
    {
        lock (lifecycleGate)
        {
            return TryGetStateLocked((IntPtr)pointer, out state!);
        }
    }

    internal IntPtr AddManagedCallbackNativeReference(int id)
    {
        lock (lifecycleGate)
        {
            DrainManagedCallbackNativeReleasesLocked();
            if (!managedCallbacks.TryGetValue(id, out var registration))
            {
                ThrowHelper.ThrowObjectDisposedException(nameof(LuauFunction));
            }

            managedCallbackNativeReferences.TryGetValue(id, out var count);
            managedCallbackNativeReferences[id] = checked(count + 1);
            return registration.NativeReleaseToken;
        }
    }

    internal void ReleaseManagedCallbackWrapper(int id)
    {
        var removeGlobalOwner = false;
        lock (lifecycleGate)
        {
            DrainManagedCallbackNativeReleasesLocked();
            managedCallbackWrapperOwners.Remove(id);
            removeGlobalOwner = RemoveManagedCallbackIfUnownedLocked(id);
        }

        if (removeGlobalOwner)
        {
            managedCallbackOwners.TryRemove(id, out _);
        }
    }

    internal void EnqueueManagedCallbackNativeRelease(
        LuauManagedCallbackRegistration registration)
    {
        LuauManagedCallbackRegistration? head;
        do
        {
            head = Volatile.Read(ref pendingManagedCallbackNativeReleases);
            registration.SetNextNativeRelease(head);
        }
        while (Interlocked.CompareExchange(
            ref pendingManagedCallbackNativeReleases,
            registration,
            head) != head);
    }

    internal void DrainManagedCallbackNativeReleases()
    {
        lock (lifecycleGate)
        {
            DrainManagedCallbackNativeReleasesLocked();
        }
    }

    void DrainManagedCallbackNativeReleasesLocked()
    {
        var registration = Interlocked.Exchange(
            ref pendingManagedCallbackNativeReleases,
            null);
        while (registration != null)
        {
            var next = registration.TakeNextNativeRelease();
            var releaseCount = registration.DrainNativeReleaseCount();
            var id = registration.Id;

            if (releaseCount > 0 &&
                managedCallbackNativeReferences.TryGetValue(id, out var nativeReferenceCount))
            {
                if (releaseCount >= nativeReferenceCount)
                {
                    managedCallbackNativeReferences.Remove(id);
                }
                else
                {
                    managedCallbackNativeReferences[id] = nativeReferenceCount - releaseCount;
                }
            }

            if (RemoveManagedCallbackIfUnownedLocked(id))
            {
                managedCallbackOwners.TryRemove(id, out _);
            }
            registration = next;
        }
    }

    internal void MarkRootSandboxed()
    {
        if (Interlocked.Exchange(ref rootSandboxed, 1) != 0)
        {
            ThrowHelper.ThrowInvalidOperationException("The Luau root state is already sandboxed.");
        }
    }

    internal bool IsThreadSandboxed(IntPtr pointer)
    {
        lock (lifecycleGate)
        {
            return sandboxedThreads.Contains(pointer);
        }
    }

    internal void MarkThreadSandboxed(IntPtr pointer)
    {
        lock (lifecycleGate)
        {
            if (!sandboxedThreads.Add(pointer))
            {
                ThrowHelper.ThrowInvalidOperationException("The Luau child thread is already sandboxed.");
            }
        }
    }

    internal bool TryGetCachedModule(string key, out LuauValue value)
    {
        lock (lifecycleGate)
        {
            return moduleCache.TryGetValue(key, out value);
        }
    }

    internal void CacheModule(string key, LuauValue value)
    {
        if (value.Type == LuauType.Thread)
        {
            throw new LuauException(
                "Luau thread wrappers cannot be stored in the managed module cache.");
        }

        lock (lifecycleGate)
        {
            if (lifecycleState != 0)
            {
                ThrowHelper.ThrowObjectDisposedException(nameof(LuauState));
            }

            var replacing = moduleCache.TryGetValue(key, out var previous);
            if (!replacing &&
                Options.MaxCachedModuleCount is { } limit &&
                moduleCache.Count >= limit)
            {
                throw new LuauModuleLimitException(
                    LuauModuleLimitKind.CachedResultCount,
                    moduleCache.Count + 1L,
                    limit);
            }

            var retainedBytes = checked(cachedModuleBytes -
                (replacing ? GetCachedModuleBytes(key, previous) : 0) + GetCachedModuleBytes(key, value));
            if (Options.MaxCachedModuleBytes is { } byteLimit && retainedBytes > byteLimit)
            {
                throw new LuauModuleLimitException(
                    LuauModuleLimitKind.CachedResultBytes,
                    retainedBytes,
                    byteLimit);
            }

            // Publish before committing accounting so failed dictionary growth
            // cannot consume budget. Rejected values remain owned by the caller.
            moduleCache[key] = value;
            cachedModuleBytes = retainedBytes;
        }
    }

    static long GetCachedModuleBytes(string key, LuauValue value) =>
        2L * key.Length + (value.Type == LuauType.String ? 2L * value.Read<string>().Length : 0);

    internal void BeginModuleLoad(string key, string requireArgument)
    {
        lock (lifecycleGate)
        {
            if (loadingModules.Contains(key))
            {
                throw new LuauException(
                    $"Circular module dependency detected while requiring '{requireArgument}'.");
            }
            if (Options.MaxModuleDependencyDepth is { } limit &&
                loadingModules.Count >= limit)
            {
                throw new LuauModuleLimitException(
                    LuauModuleLimitKind.DependencyDepth,
                    loadingModules.Count + 1L,
                    limit);
            }

            loadingModules.Add(key);
        }
    }

    internal void EndModuleLoad(string key)
    {
        lock (lifecycleGate)
        {
            loadingModules.Remove(key);
        }
    }

    internal void RegisterRoot(LuauState root)
    {
        lock (lifecycleGate)
        {
            if (lifecycleState != 0)
            {
                ThrowHelper.ThrowObjectDisposedException(nameof(LuauState));
            }

            var pointer = (IntPtr)root.PointerUnsafe;
            if (pointer == IntPtr.Zero || pointer != mainPointer || states.ContainsKey(pointer))
            {
                ThrowHelper.ThrowInvalidOperationException("The Luau state is already registered");
            }

            var entry = new WeakReference<LuauState>(root);
            states.Add(pointer, entry);
            rootEntry = entry;
            root.SetCacheEntry(entry);
            globalStates[pointer] = entry;
            var contextEntry = new WeakReference<LuauVmContext>(this);
            globalContextEntry = contextEntry;
            globalContexts[pointer] = contextEntry;
        }
    }

    internal void InstallInterruptTrampoline()
    {
        lock (nativeGate)
        {
            lock (lifecycleGate)
            {
                if (lifecycleState != 0 || mainPointer == IntPtr.Zero)
                {
                    ThrowHelper.ThrowObjectDisposedException(nameof(LuauState));
                }
                if (rootEntry == null)
                {
                    ThrowHelper.ThrowInvalidOperationException(
                        "The Luau root must be registered before installing its interrupt trampoline.");
                }
                if (interruptInstallCount != 0)
                {
                    ThrowHelper.ThrowInvalidOperationException(
                        "The managed execution interrupt trampoline is already installed for this root.");
                }

                var callbacks = new LuauHostCallbackTable
                {
                    struct_size = (uint)sizeof(LuauHostCallbackTable),
                    version = 1,
                    interrupt_poll = interruptPointer,
                };
                if (luau_host_interrupt_install(
                        (LuauHostState*)mainPointer,
                        &callbacks) != LuauHostStatus.Ok)
                {
                    throw new PlatformNotSupportedException(
                        "The native Luau plugin could not install the managed execution interrupt trampoline.");
                }

                Interlocked.Increment(ref interruptInstallCount);
            }
        }
    }

    internal LuauState GetOrCreateThread(LuauHostState* source, LuauHostState* thread, int stackIndex)
    {
        return GetOrCreateThread(source, thread, stackIndex, out _);
    }

    internal LuauState GetOrCreateThread(
        LuauHostState* source,
        LuauHostState* thread,
        int stackIndex,
        out bool created)
    {
        var pointer = (IntPtr)thread;

        lock (nativeGate)
        {
            lock (lifecycleGate)
            {
                if (lifecycleState != 0)
                {
                    ThrowHelper.ThrowObjectDisposedException(nameof(LuauState));
                }

                if (TryGetStateLocked(pointer, out var cached))
                {
                    created = false;
                    return cached;
                }

                LuauState? root = null;
                rootEntry?.TryGetTarget(out root);
                if (root == null || root.IsDisposed)
                {
                    throw new ObjectDisposedException(nameof(LuauState));
                }

                var reference = -1;
                LuauNativeProtection.Prepare(this);
                var referenceStatus = luau_host_reference_create(source, stackIndex, &reference);
                LuauNativeProtection.ThrowIfFailed(
                    root,
                    source,
                    referenceStatus,
                    "retain a Luau thread");
                var state = new LuauState(thread, this, root, reference, isMainThread: false);
                var entry = new WeakReference<LuauState>(state);

                states[pointer] = entry;
                state.SetCacheEntry(entry);
                globalStates[pointer] = entry;
                created = true;
                return state;
            }
        }
    }

    internal static bool TryGetState(LuauHostState* pointer, out LuauState state)
    {
        var key = (IntPtr)pointer;
        if (globalStates.TryGetValue(key, out var entry) &&
            entry.TryGetTarget(out state!) &&
            !state.IsDisposed)
        {
            return true;
        }

        if (entry != null)
        {
            TryRemoveGlobal(key, entry);
        }

        state = null!;
        return false;
    }

    internal static bool TryGetContext(LuauHostState* pointer, out LuauVmContext context)
    {
        if (pointer != null)
        {
            var mainPointer = (IntPtr)luau_host_main_thread(pointer);
            if (globalContexts.TryGetValue(mainPointer, out var entry) &&
                entry.TryGetTarget(out context!))
            {
                return true;
            }

            if (entry != null)
            {
                TryRemoveGlobalContext(mainPointer, entry);
            }
        }

        context = null!;
        return false;
    }

    internal void ReleaseChild(LuauState child, LuauHostState* pointer, int reference, WeakReference<LuauState>? entry)
    {
        lock (nativeGate)
        {
            lock (lifecycleGate)
            {
                RemoveStateLocked((IntPtr)pointer, entry);
                sandboxedThreads.Remove((IntPtr)pointer);

                if (lifecycleState == 0 && mainPointer != IntPtr.Zero && reference >= 0)
                {
                    if (activeOperation == null)
                    {
                        luau_host_reference_release((LuauHostState*)mainPointer, reference);
                        Interlocked.Increment(ref releasedReferenceCount);
                    }
                    else
                    {
                        deferredReferences.Add(reference);
                    }
                }
            }
        }

        child.InvalidateNativeState();
        child.DisposeManagedResources();
    }

    internal bool TryReleaseReference(int reference)
    {
        if (reference < 0)
        {
            return false;
        }

        lock (nativeGate)
        {
            lock (lifecycleGate)
            {
                if (lifecycleState != 0 || mainPointer == IntPtr.Zero)
                {
                    return false;
                }

                if (activeOperation == null)
                {
                    luau_host_reference_release((LuauHostState*)mainPointer, reference);
                    Interlocked.Increment(ref releasedReferenceCount);
                }
                else
                {
                    deferredReferences.Add(reference);
                }

                return true;
            }
        }
    }

    internal void DisposeRoot(LuauState root)
    {
        List<LuauState> liveStates = [];
        IntPtr pointerToClose = IntPtr.Zero;

        lock (lifecycleGate)
        {
            if (lifecycleState != 0)
            {
                root.InvalidateNativeState();
                root.DisposeManagedResources();
                return;
            }

            Volatile.Write(ref lifecycleState, 1);
        }

        try
        {
            disposalCancellationSource.Cancel();
        }
        catch
        {
            // Cancellation callbacks are host code. VM teardown must continue.
        }

        lock (nativeGate)
        {
            lock (lifecycleGate)
            {
                foreach (var pair in states)
                {
                    TryRemoveGlobal(pair.Key, pair.Value);

                    if (pair.Value.TryGetTarget(out var state))
                    {
                        state.InvalidateFromRoot();
                        liveStates.Add(state);
                    }
                }

                states.Clear();
                rootEntry = null;
                moduleCache.Clear();
                cachedModuleBytes = 0;
                loadingModules.Clear();
                sandboxedThreads.Clear();

                if (!liveStates.Contains(root))
                {
                    root.InvalidateFromRoot();
                    liveStates.Add(root);
                }

                deferredReferences.Clear();

                if (activeOperation == null)
                {
                    pointerToClose = mainPointer;
                    mainPointer = IntPtr.Zero;
                }
            }

            if (pointerToClose != IntPtr.Zero)
            {
                CloseNative(pointerToClose);
            }
        }

        foreach (var state in liveStates)
        {
            state.RequestLifetimeCancellation();
            state.DisposeManagedResources();
        }
    }

    void CloseNative(IntPtr pointer)
    {
        try
        {
            var closingMemoryInfo = new LuauHostMemoryInfo
            {
                struct_size = checked((uint)sizeof(LuauHostMemoryInfo)),
            };
            var preserveMemoryInfo =
                luau_host_memory_get((LuauHostState*)pointer, &closingMemoryInfo) == LuauHostStatus.Ok;
            luau_host_state_close((LuauHostState*)pointer);
            if (preserveMemoryInfo)
            {
                // lua_close releases every VM allocation. Preserve the peak,
                // configured limit, and final diagnostics after the native
                // allocator context itself has been destroyed.
                closingMemoryInfo.current_bytes = 0;
                finalMemoryInfo = closingMemoryInfo;
                hasFinalMemoryInfo = true;
            }
            Interlocked.Increment(ref closeCount);
        }
        finally
        {
            lock (lifecycleGate)
            {
                DrainManagedCallbackNativeReleasesLocked();
                foreach (var pair in managedCallbacks)
                {
                    managedCallbackOwners.TryRemove(pair.Key, out _);
                    pair.Value.DisposeNativeReleaseToken();
                }

                managedCallbacks.Clear();
                managedCallbackNativeReferences.Clear();
                managedCallbackWrapperOwners.Clear();
            }
            var contextEntry = Interlocked.Exchange(ref globalContextEntry, null);
            if (contextEntry != null)
            {
                TryRemoveGlobalContext(pointer, contextEntry);
            }
            try
            {
                ObjectRegistry.Close();
            }
            catch
            {
                // Capability wrapper cleanup cannot prevent VM teardown.
            }
            disposalCancellationSource.Dispose();
            Volatile.Write(ref lifecycleState, 2);
        }
    }

    bool TryGetStateLocked(IntPtr pointer, out LuauState state)
    {
        if (states.TryGetValue(pointer, out var entry))
        {
            if (entry.TryGetTarget(out state!) && !state.IsDisposed)
            {
                return true;
            }

            RemoveStateLocked(pointer, entry);
        }

        state = null!;
        return false;
    }

    void RemoveStateLocked(IntPtr pointer, WeakReference<LuauState>? expectedEntry)
    {
        if (states.TryGetValue(pointer, out var currentEntry) &&
            (expectedEntry == null || ReferenceEquals(currentEntry, expectedEntry)))
        {
            states.Remove(pointer);
            TryRemoveGlobal(pointer, currentEntry);
        }
    }

    static void TryRemoveGlobal(IntPtr pointer, WeakReference<LuauState> expectedEntry)
    {
        ((ICollection<KeyValuePair<IntPtr, WeakReference<LuauState>>>)globalStates)
            .Remove(new KeyValuePair<IntPtr, WeakReference<LuauState>>(pointer, expectedEntry));
    }

    static void TryRemoveGlobalContext(
        IntPtr pointer,
        WeakReference<LuauVmContext> expectedEntry)
    {
        ((ICollection<KeyValuePair<IntPtr, WeakReference<LuauVmContext>>>)globalContexts)
            .Remove(new KeyValuePair<IntPtr, WeakReference<LuauVmContext>>(pointer, expectedEntry));
    }

    static int NextManagedCallbackId()
    {
        while (true)
        {
            var observed = Interlocked.Read(ref nextGlobalManagedCallbackId);
            if (observed >= int.MaxValue)
            {
                throw new InvalidOperationException(
                    "The process exhausted managed Luau callback identifiers.");
            }

            var next = observed + 1;
            if (Interlocked.CompareExchange(
                    ref nextGlobalManagedCallbackId,
                    next,
                    observed) == observed)
            {
                return checked((int)next);
            }
        }
    }

    bool RemoveManagedCallbackIfUnownedLocked(int id)
    {
        if (managedCallbackWrapperOwners.Contains(id) ||
            managedCallbackNativeReferences.ContainsKey(id))
        {
            return false;
        }

        if (managedCallbacks.TryGetValue(id, out var registration))
        {
            if (registration.HasPendingNativeReleases)
            {
                return false;
            }

            managedCallbacks.Remove(id);
            registration.DisposeNativeReleaseToken();
        }
        return true;
    }

    [AOT.MonoPInvokeCallback(typeof(LuauHostInterruptPoll))]
    static int Interrupt(LuauHostState* state, LuauHostInterruptKind kind)
    {
        try
        {
            if (kind != LuauHostInterruptKind.Execution || !TryGetContext(state, out var context))
            {
                return 0;
            }

            var operation = context.GetActiveOperation();
            if (operation == null)
            {
                return 0;
            }

            operation.PollInterrupt();
            return operation.YieldReason != ScriptYieldReason.None ? 1 : 0;
        }
        catch
        {
            // Interrupt callbacks must never throw across the native boundary.
            // A later safepoint will retry cancellation or budget checks.
            return 0;
        }
    }
}

internal readonly ref struct LuauNativeAccess
{
    readonly LuauVmContext context;
    readonly object gate;

    internal LuauNativeAccess(LuauVmContext context, object gate)
    {
        this.context = context;
        this.gate = gate;
    }

    public void Dispose()
    {
        try
        {
            context.DrainManagedCallbackNativeReleases();
        }
        finally
        {
            Monitor.Exit(gate);
        }
    }
}
