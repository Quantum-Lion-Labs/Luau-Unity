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
    static readonly ConcurrentDictionary<long, WeakReference<LuauVmContext>> managedCallbackOwners = new();
    // The operation the current asynchronous flow runs in. Only flows that
    // contain an asynchronous operation carry one; see PushAmbient.
    static readonly AsyncLocal<AmbientOperationMarker?> ambientMarker = new();

    // The innermost synchronous operation running on this thread.
    [ThreadStatic] static ScriptOperation? threadAmbientOperation;
    [ThreadStatic] static long threadAmbientGeneration;
    static readonly LuauHostInterruptPoll interruptCallback = Interrupt;
    static readonly IntPtr interruptPointer = Marshal.GetFunctionPointerForDelegate(interruptCallback);
    static long nextGlobalManagedCallbackId;

    readonly object lifecycleGate = new();
    readonly object nativeGate = new();
    readonly Dictionary<IntPtr, WeakReference<LuauState>> states = [];
    readonly Dictionary<long, LuauManagedCallbackRegistration> managedCallbacks = [];
    readonly Dictionary<long, int> managedCallbackNativeReferences = [];
    readonly HashSet<long> managedCallbackWrapperOwners = [];
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
    // A finished synchronous operation kept for the next one; see EndOperation.
    ScriptOperation? pooledOperation;
    long operationGeneration;
    // The active operation's DeadlineTimestamp, or zero; read by the watchdog.
    long watchdogDeadline;
    WeakReference<LuauVmContext>? watchdogEntry;
    const int OptionsCacheSize = 4;
    readonly ResolvedOptions?[] optionsCache = new ResolvedOptions?[OptionsCacheSize];
    int optionsCacheCursor;
    int lifecycleState;
    int rootSandboxed;
    int bytecodeLoadDepth;
    int releasedReferenceCount;
    int closeCount;
    int interruptInstallCount;
    LuauManagedCallbackRegistration? pendingManagedCallbackNativeReleases;
    // A runnable thread of this root on which direct host operations run
    // Luau code their own thread cannot (maintainer guide section 6.7).
    // Created on first use, retained by a registry reference, and touched
    // only under the native gate.
    IntPtr hostOperationThread;
    int hostOperationThreadReference;
    int hostOperationThreadDepth;

    // Luau bounds recursion per thread, and some redirected operations run on
    // a thread created for that one use, so recursion through them (Luau code
    // the operation reaches calls back into a host function that starts
    // another) is not counted anywhere. Real nesting is one or two deep.
    const int MaximumHostOperationThreadDepth = 16;

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
    internal bool IsClosed => Volatile.Read(ref lifecycleState) == 2;
    internal long WatchdogDeadline => Volatile.Read(ref watchdogDeadline);
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
                return ReadAllocatorFailureLocked();
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

            ResetAllocatorFailureLocked();
        }
    }

    void ResetAllocatorFailureLocked()
    {
        var status = luau_host_memory_reset_failure((LuauHostState*)mainPointer);
        if (status != LuauHostStatus.Ok)
        {
            throw new LuauException(
                $"The Luau host could not reset allocator diagnostics (status {(int)status}).");
        }
    }

    /// <summary>
    /// <see cref="LuauNativeProtection.IsFrameFull"/> for a caller that holds
    /// the native gate on an open root, without re-entering the gate for each
    /// allocator diagnostic call.
    /// </summary>
    internal bool IsFrameFullUnderNativeAccess(LuauHostState* pointer)
    {
        ResetAllocatorFailureLocked();
        var reserved = 0;
        var status = luau_host_stack_check(pointer, 1, &reserved);
        var full = status == LuauHostStatus.Ok &&
            reserved == 0 &&
            ReadAllocatorFailureLocked() == LuauAllocatorFailure.None;
        ResetAllocatorFailureLocked();
        return full;
    }

    LuauAllocatorFailure ReadAllocatorFailureLocked()
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

    /// <summary>
    /// Returns a runnable thread of this root for a direct host operation
    /// whose own thread cannot run Luau code. The caller holds native access.
    /// The handle is borrowed: the root's registry keeps the thread alive
    /// until the root closes or a later call replaces it.
    /// </summary>
    internal LuauHostState* AcquireHostOperationThread(LuauState requester, string operation)
    {
        if (hostOperationThreadDepth >= MaximumHostOperationThreadDepth)
        {
            throw new InvalidOperationException(
                $"Cannot {operation}: direct host operations on a thread that cannot run Luau code are nested " +
                $"more than {MaximumHostOperationThreadDepth} deep. Luau code reached by one of them keeps " +
                "starting another.");
        }

        var thread = GetOrCreateHostOperationThread(requester, operation);
        // Counted only once the thread is in hand: every caller pairs this
        // with ReleaseHostOperationThread in a finally block.
        hostOperationThreadDepth++;
        return thread;
    }

    /// <summary>Ends the use begun by <see cref="AcquireHostOperationThread"/>.</summary>
    internal void ReleaseHostOperationThread()
    {
        hostOperationThreadDepth--;
    }

    LuauHostState* GetOrCreateHostOperationThread(LuauState requester, string operation)
    {
        var current = (LuauHostState*)hostOperationThread;
        // Code that ran on the thread can hand it to the host (for example a
        // metamethod returning coroutine.running()), and a host call can then
        // leave it suspended. Replace it rather than run code there. A nested
        // use finds it running, which is fine.
        if (current != null && luau_host_thread_status(current) == LuauHostStatus.Ok)
        {
            return current;
        }

        return CreateHostOperationThread(requester, operation);
    }

    LuauHostState* CreateHostOperationThread(LuauState requester, string operation)
    {
        // Create it from the root so that it always shares the root's
        // globals; nothing here depends on them, but code a metamethod runs
        // there may.
        var root = (LuauHostState*)mainPointer;
        LuauHostState* created = null;
        LuauNativeProtection.Prepare(this);
        var status = luau_host_thread_create(root, &created);
        if (status != LuauHostStatus.Ok)
        {
            // Reads and pops the error object, then throws.
            LuauNativeProtection.ThrowIfFailed(requester, root, status, operation);
            throw new LuauException($"The Luau host could not create a thread to {operation}.");
        }

        var reference = 0;
        try
        {
            LuauNativeProtection.Prepare(this);
            status = luau_host_reference_create(root, -1, &reference);
            if (status != LuauHostStatus.Ok)
            {
                LuauNativeProtection.ThrowIfFailed(requester, root, status, operation);
                throw new LuauException($"The Luau host could not retain a thread to {operation}.");
            }
        }
        finally
        {
            // Pops the created thread; shrinking needs no slot and cannot fail.
            _ = luau_host_stack_set_top(root, -2);
        }

        var previous = hostOperationThreadReference;
        hostOperationThread = (IntPtr)created;
        hostOperationThreadReference = reference;
        if (previous > 0)
        {
            // Release needs no slot and cannot fail for a live token.
            _ = luau_host_reference_release(root, previous);
        }

        return created;
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
        ScriptOperationMode mode,
        bool resumesThread = false)
    {
        var effectiveOptions = ResolveOperationOptions(options);
        if (effectiveOptions.ContinuationScheduler is { } scheduler && !scheduler.CheckAccess())
        {
            throw new InvalidOperationException(
                "Luau execution must begin on its configured continuation scheduler. " +
                "Dispatch the call to the VM owner thread before compiling, pushing arguments, or resuming it.");
        }

        if (effectiveOptions.WallClockLimit.HasValue && Volatile.Read(ref watchdogEntry) == null)
        {
            RegisterWithWatchdog();
        }

        // Read before the gates, as the operation constructor used to: a child
        // disposed concurrently fails here with ObjectDisposedException.
        var lifetimeToken = state.LifetimeToken;
        ScriptOperation operation;
        long deadline;
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

                // Every top-level entry except Resume starts a new call. Both
                // gates are held and no operation is active, so nothing can
                // resume or reset the thread between this check and the
                // entry; nothing has been pushed yet, so a rejection leaves
                // the thread exactly as it was. The root is never left
                // suspended (ScriptRunner.RejectRootYield resets it), so the
                // per-frame calls on it skip the native read.
                if (mode == ScriptOperationMode.TopLevelResume && !resumesThread && !state.IsMainThread)
                {
                    state.ThrowIfSuspendedUnderNativeAccess(
                        "start a new call",
                        chunkName,
                        includeUnstartedCoroutine: true);
                }

                // Asynchronous operations are never reused: execution contexts
                // captured during one can reference it indefinitely.
                operation = isAsync ? new ScriptOperation(this) : TakePooledOperation();
                operation.Activate(
                    ++operationGeneration,
                    state,
                    chunkName,
                    effectiveOptions,
                    cancellationToken,
                    lifetimeToken,
                    disposalCancellationSource.Token,
                    isAsync,
                    mode);

                try
                {
                    // The native gate is held and the root is open.
                    ResetAllocatorFailureLocked();
                    ConfigureInterruptBudget(operation);
                    // Last: it writes nothing when it fails.
                    PushAmbient(operation);
                }
                catch
                {
                    // Not yet published or registered: nothing else can hold
                    // this life, so the instance can simply be kept.
                    AbandonUnpublishedOperationLocked(operation);
                    throw;
                }

                activeOperation = operation;
                deadline = operation.DeadlineTimestamp;
                if (deadline != 0)
                {
                    // A full fence: see LuauDeadlineWatchdog.EnsureArmed.
                    Interlocked.Exchange(ref watchdogDeadline, deadline);
                }
            }
        }

        try
        {
            if (deadline != 0)
            {
                LuauDeadlineWatchdog.EnsureArmed(deadline);
            }

            if (operation.HasCancelableCallerToken)
            {
                operation.RegisterCallerCancellationSignal();
            }
        }
        catch
        {
            operation.Dispose();
            throw;
        }

        return operation;
    }

    ScriptOperation TakePooledOperation()
    {
        var pooled = pooledOperation;
        pooledOperation = null;
        return pooled ?? new ScriptOperation(this);
    }

    void AbandonUnpublishedOperationLocked(ScriptOperation operation)
    {
        operation.Deactivate();
        if (!operation.IsAsync)
        {
            operation.ClearForReuse();
            pooledOperation = operation;
        }
    }

    void RegisterWithWatchdog()
    {
        // Publish the entry only once the watchdog has it. Another thread that
        // saw it earlier would skip registration, publish a deadline, and arm
        // a timer whose scan could not find this root yet. The watchdog never
        // takes a context lock while holding its own, so this order is safe.
        lock (lifecycleGate)
        {
            if (watchdogEntry != null)
            {
                return;
            }

            var entry = new WeakReference<LuauVmContext>(this);
            LuauDeadlineWatchdog.Register(entry);
            Volatile.Write(ref watchdogEntry, entry);
        }
    }

    /// <summary>
    /// Returns the callback-visible token of life <paramref name="generation"/>
    /// of <paramref name="operation"/>, creating it on first use. The life is
    /// checked and the token read or created under the lifecycle gate, which
    /// <see cref="EndOperation"/> holds while it ends the life and detaches
    /// the source. A request that arrives after the life ended fails as an
    /// expired call context does, even when the instance is already reused.
    /// </summary>
    internal CancellationToken GetOperationCancellationToken(ScriptOperation operation, long generation)
    {
        lock (lifecycleGate)
        {
            if (!ReferenceEquals(activeOperation, operation) || operation.Generation != generation)
            {
                throw LuauCallFrame.CreateExpiredException();
            }

            return operation.GetOrCreateCancellationTokenLocked();
        }
    }

    /// <summary>
    /// Records <paramref name="operation"/> as the innermost operation of the
    /// current flow, for <see cref="ThrowIfNativeAccessDenied"/>. A
    /// synchronous operation begins and ends on one thread, so a
    /// thread-static slot identifies it without touching the execution
    /// context. An asynchronous operation can continue on any thread, so it
    /// uses an <see cref="AsyncLocal{T}"/> marker, as does a synchronous
    /// operation that starts inside a flow that already carries one: there
    /// the marker, not the thread, says which operation is innermost.
    /// </summary>
    void PushAmbient(ScriptOperation operation)
    {
        var currentMarker = ambientMarker.Value;
        if (operation.IsAsync || currentMarker != null)
        {
            var marker = new AmbientOperationMarker(operation, operation.Generation);
            ambientMarker.Value = marker;
            operation.SaveAsyncAmbient(marker, currentMarker);
        }

        if (!operation.IsAsync)
        {
            operation.SaveThreadAmbient(threadAmbientOperation, threadAmbientGeneration);
            threadAmbientOperation = operation;
            threadAmbientGeneration = operation.Generation;
        }
    }

    /// <summary>
    /// Undoes <see cref="PushAmbient"/>; false when the slots no longer name
    /// this life, in which case they are left alone as before.
    /// </summary>
    static bool PopAmbient(ScriptOperation operation, long generation)
    {
        var restored = true;
        if (operation.AsyncAmbientMarker is { } marker)
        {
            if (ReferenceEquals(ambientMarker.Value, marker))
            {
                ambientMarker.Value = operation.PreviousAsyncAmbientMarker;
            }
            else
            {
                restored = false;
            }
        }

        if (operation.PushedThreadAmbient)
        {
            if (ReferenceEquals(threadAmbientOperation, operation) &&
                threadAmbientGeneration == generation)
            {
                threadAmbientOperation = operation.PreviousThreadAmbient;
                threadAmbientGeneration = operation.PreviousThreadAmbientGeneration;
            }
            else
            {
                restored = false;
            }
        }

        return restored;
    }

    static bool IsAmbient(ScriptOperation active)
    {
        var generation = active.Generation;
        var marker = ambientMarker.Value;
        if (marker != null)
        {
            return ReferenceEquals(marker.Operation, active) && marker.Generation == generation;
        }

        return ReferenceEquals(threadAmbientOperation, active) && threadAmbientGeneration == generation;
    }

    /// <summary>
    /// Merges per-operation options into the state defaults. Without options
    /// an operation uses the defaults themselves. Options records are
    /// immutable, so a merge is reused for the same instance: the Unity
    /// scheduler passes one instance per phase on every call. A few entries
    /// cover several phases on one root; a miss only costs the merge it
    /// replaces.
    /// </summary>
    LuauExecutionOptions ResolveOperationOptions(LuauExecutionOptions? requested)
    {
        if (requested == null)
        {
            return Options.DefaultExecutionOptions;
        }

        for (var index = 0; index < optionsCache.Length; index++)
        {
            var cached = Volatile.Read(ref optionsCache[index]);
            if (cached != null && ReferenceEquals(cached.Requested, requested))
            {
                return cached.Effective;
            }
        }

        var effective = LuauExecutionOptions.ResolveForOperation(Options.DefaultExecutionOptions, requested);
        var slot = (int)((uint)Interlocked.Increment(ref optionsCacheCursor) % (uint)optionsCache.Length);
        Volatile.Write(ref optionsCache[slot], new ResolvedOptions(requested, effective));
        return effective;
    }

    sealed class ResolvedOptions
    {
        internal ResolvedOptions(LuauExecutionOptions requested, LuauExecutionOptions effective)
        {
            Requested = requested;
            Effective = effective;
        }

        internal LuauExecutionOptions Requested { get; }
        internal LuauExecutionOptions Effective { get; }
    }

    /// <summary>
    /// Hands the operation's budget to the native interrupt throttle, which
    /// keeps the safepoint count and the clock and polls managed code only
    /// when the count passes the limit, the deadline passes, or a request
    /// is pending. Configuring also resets the count and clears requests, so
    /// it must happen before the operation is published and can be
    /// signaled.
    /// </summary>
    void ConfigureInterruptBudget(ScriptOperation operation)
    {
        var status = luau_host_interrupt_configure(
            (LuauHostState*)mainPointer,
            operation.InterruptCountBudget,
            operation.GetRemainingWallClockNanoseconds());
        if (status != LuauHostStatus.Ok)
        {
            throw new LuauException(
                $"The Luau host could not configure the execution budget (status {(int)status}).");
        }
    }

    /// <summary>
    /// Makes the next interrupt safepoint poll life <paramref name="generation"/>
    /// of <paramref name="operation"/> from any thread. The request is dropped
    /// once that life is no longer active, including when the instance has
    /// since been reused; holding the lifecycle gate keeps the root open for
    /// the native call, since close begins only after the active operation
    /// ends or disposal takes this gate.
    /// </summary>
    internal void RequestInterruptAttention(ScriptOperation operation, long generation)
    {
        lock (lifecycleGate)
        {
            if (ReferenceEquals(activeOperation, operation) && operation.Generation == generation)
            {
                RequestActiveInterruptAttentionLocked();
            }
        }
    }

    /// <summary>
    /// Makes the next safepoint poll whichever operation is active, for
    /// disposal requests that do not know which operation they stop.
    /// </summary>
    internal void RequestActiveInterruptAttention()
    {
        lock (lifecycleGate)
        {
            RequestActiveInterruptAttentionLocked();
        }
    }

    void RequestActiveInterruptAttentionLocked()
    {
        if (activeOperation != null && mainPointer != IntPtr.Zero)
        {
            luau_host_interrupt_request((LuauHostState*)mainPointer);
        }
    }

    /// <summary>
    /// Called by <see cref="LuauDeadlineWatchdog"/> when this root's published
    /// deadline has passed. Signals the active life at most once, and only if
    /// its own wall-clock limit has passed: requests the poll first, then
    /// returns the callback-visible token source for the watchdog to cancel
    /// outside every lock, since its registrations are host code.
    /// </summary>
    internal CancellationTokenSource? SignalWatchdogDeadline()
    {
        lock (lifecycleGate)
        {
            var active = activeOperation;
            if (active == null || active.DeadlineTimestamp == 0 || !active.IsPastWallClockLimit())
            {
                return null;
            }

            // Later scans skip this root until its next operation.
            Interlocked.CompareExchange(ref watchdogDeadline, 0, active.DeadlineTimestamp);
            if (!active.TryMarkDeadlineSignaled())
            {
                return null;
            }

            RequestActiveInterruptAttentionLocked();
            // Read after marking: a source created later sees the mark and
            // cancels itself. Under the gate the life cannot change, so this
            // is null or this life's source.
            return active.TokenSource;
        }
    }

    internal void EndOperation(ScriptOperation operation, long generation)
    {
        IntPtr pointerToClose = IntPtr.Zero;
        CancellationTokenSource? tokenSource;

        lock (nativeGate)
        {
            lock (lifecycleGate)
            {
                if (!ReferenceEquals(activeOperation, operation) || operation.Generation != generation)
                {
                    return;
                }

                activeOperation = null;
                // No token request can pass its life check from here on.
                tokenSource = operation.DetachTokenSourceLocked();
                if (operation.DeadlineTimestamp != 0)
                {
                    Volatile.Write(ref watchdogDeadline, 0);
                }

                var ambientRestored = PopAmbient(operation, generation);

                if (deferredReferences.Count != 0)
                {
                    if (lifecycleState == 0 && mainPointer != IntPtr.Zero)
                    {
                        foreach (var reference in deferredReferences)
                        {
                            luau_host_reference_release((LuauHostState*)mainPointer, reference);
                            Interlocked.Increment(ref releasedReferenceCount);
                        }
                    }

                    deferredReferences.Clear();
                }

                if (lifecycleState == 1 && mainPointer != IntPtr.Zero)
                {
                    pointerToClose = mainPointer;
                    mainPointer = IntPtr.Zero;
                }

                // Reuse only a synchronous life that nothing can still name:
                // no caller registration, no marker in a captured execution
                // context, no ambient slot left pointing at it, and a root
                // that will run again. Its cancellation registration and
                // token source were disposed before this call.
                if (lifecycleState == 0 &&
                    !operation.IsAsync &&
                    !operation.HasCancelableCallerToken &&
                    operation.AsyncAmbientMarker == null &&
                    ambientRestored)
                {
                    operation.ClearForReuse();
                    pooledOperation = operation;
                }
            }

            if (pointerToClose != IntPtr.Zero)
            {
                CloseNative(pointerToClose);
            }
        }

        // Outside the gates: disposing unregisters from the lifetime tokens
        // and can wait for a cancellation callback that needs them.
        tokenSource?.Dispose();
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
            // The active operation and the native root change only while the
            // native gate is held (BeginOperation, EndOperation, DisposeRoot),
            // so holding it is enough to read both.
            if (!ReferenceEquals(Volatile.Read(ref activeOperation), operation) ||
                Volatile.Read(ref mainPointer) == IntPtr.Zero)
            {
                ThrowHelper.ThrowObjectDisposedException(nameof(LuauState));
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
        // Every operation's scheduler is the state's: per-operation options
        // cannot replace it (LuauExecutionOptions.ResolveForOperation).
        var scheduler = Options.DefaultExecutionOptions.ContinuationScheduler;
        if (scheduler != null && !scheduler.CheckAccess())
        {
            throw new InvalidOperationException(
                "The Luau VM can only be accessed from its configured continuation scheduler.");
        }

        var active = Volatile.Read(ref activeOperation);
        if (active == null)
        {
            return;
        }

        if (!IsAmbient(active))
        {
            ThrowHelper.ThrowInvalidOperationException("The Luau VM is executing on another operation.");
        }

        active.ThrowIfAsyncCallbackAccessUnsafe();
    }

    internal ScriptOperation? GetActiveOperation()
    {
        return Volatile.Read(ref activeOperation);
    }

    /// <summary>The finished synchronous operation kept for reuse, for tests.</summary>
    internal ScriptOperation? PooledOperation
    {
        get
        {
            lock (lifecycleGate)
            {
                return pooledOperation;
            }
        }
    }

    internal long RegisterManagedCallback(
        string? name,
        Func<LuauState, LuauOperationCancellation, int> callback)
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

    internal long RegisterManagedCallback(
        string? name,
        Func<LuauState, LuauOperationCancellation, ValueTask<int>> callback)
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

    long CompleteManagedCallbackRegistrationLocked(
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

    internal bool TryGetManagedCallback(long id, out LuauManagedCallbackRegistration registration)
    {
        lock (lifecycleGate)
        {
            DrainManagedCallbackNativeReleasesLocked();
            return managedCallbacks.TryGetValue(id, out registration!);
        }
    }

    internal static bool TryGetManagedCallbackOwner(
        long id,
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

    internal IntPtr AddManagedCallbackNativeReference(long id)
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

    internal void ReleaseManagedCallbackWrapper(long id)
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
        // Finalizers and native destructors push onto this list from any
        // thread; skip the gate when there is nothing to drain. A release
        // queued just after this read is drained by the next native access,
        // as it would be if it had arrived just after the drain.
        if (Volatile.Read(ref pendingManagedCallbackNativeReleases) == null)
        {
            return;
        }

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

            // An active operation keeps the root open; its next safepoint
            // now observes the disposal request.
            RequestActiveInterruptAttentionLocked();
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

    // Identifiers are process-wide and never reused, so a native closure that
    // outlives its registration can never reach a later one. 64 bits do not
    // run out: two million registrations a second would take 146,000 years.
    // They used to be 32-bit, and the process could register no callback
    // after its 2,147,483,647th.
    static long NextManagedCallbackId()
    {
        var id = Interlocked.Increment(ref nextGlobalManagedCallbackId);
        if (id <= 0)
        {
            throw new InvalidOperationException(
                "The process exhausted managed Luau callback identifiers.");
        }

        return id;
    }

    bool RemoveManagedCallbackIfUnownedLocked(long id)
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

            operation.PollInterrupt(state);
            return operation.YieldReason != ScriptYieldReason.None ? 1 : 0;
        }
        catch
        {
            // Interrupt callbacks must never throw across the native boundary.
            // The throttled host polls again only on request, so ask for the
            // next safepoint to retry the cancellation and budget checks.
            try
            {
                luau_host_interrupt_request(state);
            }
            catch
            {
            }

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
