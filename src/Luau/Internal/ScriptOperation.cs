using System.Diagnostics;
using Luau.Internal.Interop;
using static Luau.Internal.Interop.NativeMethods;

namespace Luau;

internal enum ScriptYieldReason
{
    None,
    AsyncCallback,
    CallbackFailure,
    HardStop,
}

internal enum ScriptOperationMode
{
    TopLevelResume,
    NestedProtectedCall,
    DirectHostOperation,
}

internal enum AsyncCallbackPhase
{
    None,
    PendingDispatch,
    Suspended,
}

internal enum ScriptHardStopReason
{
    None,
    Disposed,
    Canceled,
    InterruptCount,
    WallClock,
}

/// <summary>
/// Root-level execution state for one operation. A root reuses one
/// synchronous instance for consecutive operations, so an instance has many
/// lives; <see cref="Generation"/> identifies the current one. Anything that
/// can still hold the instance after its life ends (a cancellation
/// registration, the deadline watchdog, an ambient marker, a callback's lazy
/// token) must compare the generation, never the reference alone.
/// </summary>
internal sealed unsafe class ScriptOperation : IDisposable
{
    static long nextCallbackFailureToken;
    readonly LuauVmContext context;

    long generation;
    LuauState? state;
    string? chunkName;
    LuauExecutionOptions options = LuauExecutionOptions.Unbounded;
    bool isAsync;
    ScriptOperationMode mode;
    IntPtr threadPointer;
    IntPtr fromPointer;
    IntPtr callbackFailureToken;
    CancellationToken callerCancellationToken;
    CancellationToken lifetimeToken;
    CancellationToken disposalToken;
    long startedTimestamp;
    long deadlineTimestamp;

    // Ambient bookkeeping, owned by LuauVmContext.PushAmbient/PopAmbient.
    ScriptOperation? previousThreadAmbient;
    long previousThreadAmbientGeneration;
    bool pushedThreadAmbient;
    AmbientOperationMarker? asyncAmbientMarker;
    AmbientOperationMarker? previousAsyncAmbientMarker;

    // The callback-visible token exists only once something asks for it.
    CancellationTokenSource? tokenSource;
    CancellationTokenRegistration callerCancellationRegistration;
    int deadlineSignaled;

    LuauManagedCallbackRegistration? pendingCallback;
    IntPtr pendingCallbackThread;
    string? pendingCallbackName;
    Exception? callbackFailure;
    IntPtr failedCallbackThread;
    string? callbackFailureName;
    Exception? injectedCallbackFailure;
    string? injectedCallbackFailureName;
    Exception? hardStopException;
    int yieldReason;
    int asyncCallbackPhase;
    int hardStopReason;
    int disposeState = 1;
    int coroutineLifecycleArmed;
    int coroutineCompletion;
    long interruptCount;
    long decodedBytes;

    internal ScriptOperation(LuauVmContext context)
    {
        this.context = context;
    }

    /// <summary>
    /// Starts a new life. Called by <see cref="LuauVmContext.BeginOperation"/>
    /// under the root gates, before the operation is published, so nothing
    /// else can observe the fields while they change.
    /// </summary>
    internal void Activate(
        long generation,
        LuauState state,
        string? chunkName,
        LuauExecutionOptions options,
        CancellationToken cancellationToken,
        CancellationToken lifetimeToken,
        CancellationToken disposalToken,
        bool isAsync,
        ScriptOperationMode mode)
    {
        // Publish the new generation first: a late signal from the previous
        // life compares against it and is dropped.
        Volatile.Write(ref this.generation, generation);
        this.state = state;
        this.chunkName = string.IsNullOrEmpty(chunkName) ? null : chunkName;
        this.options = options;
        this.isAsync = isAsync;
        this.mode = mode;
        threadPointer = (IntPtr)state.PointerUnsafe;
        fromPointer = state.From == null ? IntPtr.Zero : (IntPtr)state.From.PointerUnsafe;
        callerCancellationToken = cancellationToken;
        this.lifetimeToken = lifetimeToken;
        this.disposalToken = disposalToken;
        startedTimestamp = Stopwatch.GetTimestamp();
        deadlineTimestamp = options.WallClockLimit is { } limit
            ? ComputeDeadlineTimestamp(startedTimestamp, limit)
            : 0;

        previousThreadAmbient = null;
        previousThreadAmbientGeneration = 0;
        pushedThreadAmbient = false;
        asyncAmbientMarker = null;
        previousAsyncAmbientMarker = null;
        Volatile.Write(ref tokenSource, null);
        callerCancellationRegistration = default;
        Volatile.Write(ref deadlineSignaled, 0);
        Volatile.Write(ref pendingCallback, null);
        pendingCallbackThread = IntPtr.Zero;
        pendingCallbackName = null;
        Volatile.Write(ref callbackFailure, null);
        failedCallbackThread = IntPtr.Zero;
        callbackFailureName = null;
        Volatile.Write(ref injectedCallbackFailure, null);
        injectedCallbackFailureName = null;
        Volatile.Write(ref hardStopException, null);
        Volatile.Write(ref yieldReason, (int)ScriptYieldReason.None);
        Volatile.Write(ref asyncCallbackPhase, (int)AsyncCallbackPhase.None);
        Volatile.Write(ref hardStopReason, (int)ScriptHardStopReason.None);
        Volatile.Write(ref coroutineLifecycleArmed, 0);
        Volatile.Write(ref coroutineCompletion, 0);
        Volatile.Write(ref interruptCount, 0);
        Volatile.Write(ref decodedBytes, 0);

        var token = Interlocked.Increment(ref nextCallbackFailureToken);
        callbackFailureToken = (IntPtr)(token == 0 ? 1 : token);
        Volatile.Write(ref disposeState, 0);
    }

    /// <summary>
    /// Ends a life that was never published, so nothing but the context
    /// holds it; there is no registration or token to release.
    /// </summary>
    internal void Deactivate()
    {
        Volatile.Write(ref disposeState, 1);
    }

    /// <summary>
    /// Drops every reference the finished life held, so a pooled instance
    /// keeps no state, string, exception, or token alive.
    /// </summary>
    internal void ClearForReuse()
    {
        state = null;
        chunkName = null;
        callerCancellationToken = default;
        lifetimeToken = default;
        disposalToken = default;
        previousThreadAmbient = null;
        asyncAmbientMarker = null;
        previousAsyncAmbientMarker = null;
        Volatile.Write(ref tokenSource, null);
        callerCancellationRegistration = default;
        Volatile.Write(ref pendingCallback, null);
        pendingCallbackName = null;
        Volatile.Write(ref callbackFailure, null);
        callbackFailureName = null;
        Volatile.Write(ref injectedCallbackFailure, null);
        injectedCallbackFailureName = null;
        Volatile.Write(ref hardStopException, null);
    }

    internal LuauVmContext Context => context;
    internal long Generation => Volatile.Read(ref generation);
    internal LuauState State => state!;
    internal string? ChunkName => chunkName;
    internal LuauExecutionOptions Options => options;
    internal bool IsAsync => isAsync;
    internal ScriptOperationMode Mode => mode;
    internal IntPtr ThreadPointer => threadPointer;
    internal IntPtr FromPointer => fromPointer;
    internal IntPtr CallbackFailureToken => callbackFailureToken;
    internal long InterruptCount => Interlocked.Read(ref interruptCount);

    /// <summary>
    /// The <see cref="Stopwatch"/> timestamp at which the wall-clock limit has
    /// certainly passed, or zero without one. The watchdog schedules by it;
    /// the limit itself is still decided by <see cref="IsPastWallClockLimit"/>.
    /// </summary>
    internal long DeadlineTimestamp => deadlineTimestamp;

    /// <summary>Whether the caller supplied a token that can be canceled.</summary>
    internal bool HasCancelableCallerToken => callerCancellationToken.CanBeCanceled;

    internal ScriptOperation? PreviousThreadAmbient => previousThreadAmbient;
    internal long PreviousThreadAmbientGeneration => previousThreadAmbientGeneration;
    internal bool PushedThreadAmbient => pushedThreadAmbient;
    internal AmbientOperationMarker? AsyncAmbientMarker => asyncAmbientMarker;
    internal AmbientOperationMarker? PreviousAsyncAmbientMarker => previousAsyncAmbientMarker;

    internal void SaveThreadAmbient(ScriptOperation? previous, long previousGeneration)
    {
        previousThreadAmbient = previous;
        previousThreadAmbientGeneration = previousGeneration;
        pushedThreadAmbient = true;
    }

    internal void SaveAsyncAmbient(AmbientOperationMarker marker, AmbientOperationMarker? previous)
    {
        asyncAmbientMarker = marker;
        previousAsyncAmbientMarker = previous;
    }

    internal void ReserveDecodedBytes(ulong byteCount)
    {
        var limit = Context.Options.MaxDecodedBytesPerOperation;
        if (!limit.HasValue || byteCount == 0)
        {
            return;
        }

        var increment = byteCount > long.MaxValue ? long.MaxValue : (long)byteCount;
        long observed;
        long total;
        do
        {
            observed = Interlocked.Read(ref decodedBytes);
            total = observed > long.MaxValue - increment
                ? long.MaxValue
                : observed + increment;
        }
        while (Interlocked.CompareExchange(ref decodedBytes, total, observed) != observed);

        if (total > limit.Value)
        {
            throw new LuauDecodedResultLimitException(
                ChunkName,
                LuauDecodedResultLimitKind.OperationBytes,
                total,
                limit.Value);
        }
    }

    internal ScriptYieldReason YieldReason => (ScriptYieldReason)Volatile.Read(ref yieldReason);

    internal void ArmCoroutineLifecycle()
    {
        // Admission and argument preparation do not change the native
        // coroutine. Commit lifecycle tracking only when execution enters it.
        if (Mode != ScriptOperationMode.TopLevelResume || State.IsMainThread)
        {
            return;
        }

        Volatile.Write(ref coroutineLifecycleArmed, 1);
        State.MarkCoroutineRunning();
    }

    internal void CompleteCoroutineSuspended()
    {
        Volatile.Write(ref coroutineCompletion, 1);
    }

    internal void CompleteCoroutineDead()
    {
        Volatile.Write(ref coroutineCompletion, 2);
    }

    internal void PrepareResume()
    {
        Volatile.Write(ref yieldReason, (int)ScriptYieldReason.None);
        Volatile.Write(ref asyncCallbackPhase, (int)AsyncCallbackPhase.None);
    }

    internal IntPtr PendingCallbackThread => pendingCallbackThread;

    internal void QueueAsyncCallback(LuauManagedCallbackRegistration callback, IntPtr thread)
    {
        pendingCallbackName = callback.Name;
        if (Interlocked.CompareExchange(ref pendingCallback, callback, null) != null)
        {
            pendingCallbackName = null;
            RecordCallbackFailure(
                callback.Name,
                new InvalidOperationException("Only one managed async callback can be pending in a Luau operation."));
            return;
        }

        pendingCallbackThread = thread;
        Volatile.Write(ref asyncCallbackPhase, (int)AsyncCallbackPhase.PendingDispatch);
        Volatile.Write(ref yieldReason, (int)ScriptYieldReason.AsyncCallback);
        RequestInterruptAttentionFromOwner();
    }

    internal void MarkAsyncCallbackSuspended()
    {
        Volatile.Write(ref asyncCallbackPhase, (int)AsyncCallbackPhase.Suspended);
        // The runner has acted on the suspension. While the callback runs,
        // its direct host operations can run Luau code (a metamethod or
        // __tostring), and an interrupt poll there must report only new
        // work; a stale reason would stop that code as a hard stop.
        Volatile.Write(ref yieldReason, (int)ScriptYieldReason.None);
    }

    internal void FinishAsyncCallback()
    {
        Volatile.Write(ref asyncCallbackPhase, (int)AsyncCallbackPhase.None);
    }

    internal void ThrowIfAsyncCallbackAccessUnsafe()
    {
        var phase = (AsyncCallbackPhase)Volatile.Read(ref asyncCallbackPhase);
        if (phase == AsyncCallbackPhase.PendingDispatch)
        {
            throw new InvalidOperationException(
                "A managed async callback cannot access its Luau state until the VM has yielded.");
        }
    }

    internal LuauManagedCallbackRegistration? TakePendingCallback()
    {
        pendingCallbackThread = IntPtr.Zero;
        return Interlocked.Exchange(ref pendingCallback, null);
    }

    internal string? TakePendingCallbackName()
    {
        var name = pendingCallbackName;
        pendingCallbackName = null;
        return name;
    }

    internal void RecordCallbackFailure(string? callbackName, Exception exception)
    {
        // This method is called from reverse P/Invoke catch blocks. Keep it to
        // no-throw reference stores only; allocating or formatting an exception
        // here could itself escape across the unmanaged callback boundary.
        callbackFailureName = callbackName;
        Interlocked.Exchange(ref callbackFailure, exception);
        Volatile.Write(ref yieldReason, (int)ScriptYieldReason.CallbackFailure);
        RequestInterruptAttentionFromOwner();
    }

    /// <summary>
    /// Makes the next interrupt safepoint on this root poll managed state.
    /// Pending work must reach Luau at the very next safepoint: the pointer
    /// check in <see cref="RecordFailedCallbackSuspension"/> relies on no
    /// instruction that could switch coroutines running in between.
    /// </summary>
    void RequestInterruptAttentionFromOwner()
    {
        // Only the thread running this operation calls this, while the
        // operation is active, so its root cannot close concurrently and its
        // thread handle stays reachable. The request is a lock-free native
        // store; this also runs from reverse P/Invoke catch blocks, so it
        // must never throw.
        try
        {
            luau_host_interrupt_request((LuauHostState*)ThreadPointer);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Registers the cross-thread signal for caller cancellation. Called
    /// once the operation is active and outside the context locks: a token
    /// that is already canceled runs the request inline, and disposing the
    /// registration waits for a concurrent callback that may need those locks.
    /// </summary>
    internal void RegisterCallerCancellationSignal()
    {
        if (!callerCancellationToken.CanBeCanceled)
        {
            return;
        }

        callerCancellationRegistration = callerCancellationToken.Register(
            static operation => ((ScriptOperation)operation!).OnCallerCanceled(),
            this);
    }

    void OnCallerCanceled()
    {
        try
        {
            // The registration is disposed before this life ends, and an
            // instance is reused only after that, so this is still the life
            // that registered. Ask for a poll before canceling the
            // callback-visible token: its registrations are host code, and one
            // that blocks on the VM must not delay the stop it waits for.
            var life = Volatile.Read(ref generation);
            context.RequestInterruptAttention(this, life);
            var source = Volatile.Read(ref tokenSource);
            if (source != null && Volatile.Read(ref generation) == life)
            {
                source.Cancel();
            }
        }
        catch
        {
            // Cancellation callbacks must not fail. Disposal or the end of the
            // operation already won the race, or a host registration threw;
            // the caller token is canceled either way and the poll observes it.
        }
    }

    /// <summary>
    /// Returns the callback-visible token of life <paramref name="expectedGeneration"/>,
    /// creating it on first use. It is canceled by caller cancellation,
    /// wall-clock expiry, and state or root disposal; a token created after
    /// one of those already happened is returned canceled.
    /// </summary>
    internal CancellationToken GetCancellationToken(long expectedGeneration) =>
        context.GetOperationCancellationToken(this, expectedGeneration);

    /// <summary>
    /// Reads or creates this life's token. The caller holds the context's
    /// lifecycle gate and has checked that this life is the active operation,
    /// so the life cannot end, and the instance cannot be reused, in between:
    /// a request from one life can neither return nor publish into the next.
    /// </summary>
    internal CancellationToken GetOrCreateCancellationTokenLocked()
    {
        var source = Volatile.Read(ref tokenSource);
        return source != null ? source.Token : CreateCancellationToken();
    }

    /// <summary>
    /// Takes the token source when the life ends, under the same gate, for
    /// the context to dispose once it has left its locks.
    /// </summary>
    internal CancellationTokenSource? DetachTokenSourceLocked() =>
        Interlocked.Exchange(ref tokenSource, null);

    CancellationToken CreateCancellationToken()
    {
        // Disposal is linked: both tokens are canceled before their sources are
        // disposed, and linking an already canceled token cancels at once.
        var created = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken, disposalToken);
        var existing = Interlocked.CompareExchange(ref tokenSource, created, null);
        if (existing != null)
        {
            created.Dispose();
            return existing.Token;
        }

        // Caller cancellation and the deadline cancel the published source
        // themselves (OnCallerCanceled, LuauVmContext.SignalWatchdogDeadline).
        // Each publishes its own state before reading the source, and this
        // reads that state after publishing the source, so one of the two
        // always cancels it. Nothing has registered on the new token yet.
        if (callerCancellationToken.IsCancellationRequested ||
            Volatile.Read(ref deadlineSignaled) != 0 ||
            IsPastWallClockLimit())
        {
            try
            {
                created.Cancel();
            }
            catch
            {
            }
        }

        return created.Token;
    }

    /// <summary>The callback-visible token source, if this life created one.</summary>
    internal CancellationTokenSource? TokenSource => Volatile.Read(ref tokenSource);

    /// <summary>
    /// Records that the watchdog saw the deadline pass; true only for the
    /// first signal of a life. Set before the token source is read.
    /// </summary>
    internal bool TryMarkDeadlineSignaled() =>
        Interlocked.CompareExchange(ref deadlineSignaled, 1, 0) == 0;

    /// <summary>
    /// Whether the wall-clock limit has passed. Compares timestamps against
    /// <see cref="DeadlineTimestamp"/>, which is rounded up so that the
    /// elapsed time a budget exception reports is never below the limit.
    /// </summary>
    internal bool IsPastWallClockLimit() =>
        deadlineTimestamp != 0 && Stopwatch.GetTimestamp() >= deadlineTimestamp;

    /// <summary>
    /// The native interrupt budget for this operation: the count limit and
    /// the wall-clock time left, in nanoseconds; zero disables either.
    /// </summary>
    internal ulong InterruptCountBudget =>
        Options.InterruptCountLimit is { } limit ? (ulong)limit : 0UL;

    internal ulong GetRemainingWallClockNanoseconds()
    {
        if (Options.WallClockLimit is not { } limit)
        {
            return 0;
        }

        var remainingTicks = (limit - GetElapsedTime()).Ticks;
        if (remainingTicks <= 0)
        {
            // Already due: the first clock read must observe it.
            return 1;
        }

        const long nanosecondsPerTick = 100;
        return remainingTicks > long.MaxValue / nanosecondsPerTick
            ? ulong.MaxValue
            : (ulong)(remainingTicks * nanosecondsPerTick);
    }

    /// <summary>
    /// Records the thread a failed callback suspended on. A pending failure
    /// can also reach Luau through an interrupt on some other frame, for
    /// example after a non-yieldable parent rejected this suspension; only
    /// this thread's suspended frame is the failed callback's own.
    /// </summary>
    internal void RecordFailedCallbackSuspension(IntPtr thread)
    {
        // Called from reverse P/Invoke paths: a plain store only.
        failedCallbackThread = thread;
    }

    internal IntPtr TakeFailedCallbackThread()
    {
        var thread = failedCallbackThread;
        failedCallbackThread = IntPtr.Zero;
        return thread;
    }

    internal void PrepareCallbackFailureInjection()
    {
        // This method also runs from the non-yieldable reverse P/Invoke path.
        // Move only references here; construct the managed wrapper after the
        // native operation has returned to managed code.
        var failure = Interlocked.Exchange(ref callbackFailure, null);
        if (failure != null)
        {
            injectedCallbackFailureName = TakeCallbackFailureName();
            Interlocked.Exchange(ref injectedCallbackFailure, failure);
        }

        // A non-yieldable callback injects its error immediately. Luau can
        // still report the enclosing resumable frame as yielded while a pcall
        // handles that error, so do not schedule a second error injection.
        Volatile.Write(ref yieldReason, (int)ScriptYieldReason.None);
    }

    internal LuauManagedCallbackException? TakeInjectedCallbackFailure()
    {
        var failure = Interlocked.Exchange(ref injectedCallbackFailure, null);
        if (failure == null)
        {
            return null;
        }

        var callbackName = injectedCallbackFailureName;
        injectedCallbackFailureName = null;
        return CreateControlledCallbackFailure(failure, callbackName);
    }

    internal LuauManagedCallbackException? TakeUninjectedCallbackFailure()
    {
        // Checked after every successful protected call; skip the exchange
        // when nothing is recorded.
        if (Volatile.Read(ref callbackFailure) == null)
        {
            return null;
        }

        var failure = Interlocked.Exchange(ref callbackFailure, null);
        return failure == null
            ? null
            : CreateControlledCallbackFailure(failure, TakeCallbackFailureName());
    }

    internal void ClearInjectedCallbackFailure()
    {
        if (Volatile.Read(ref injectedCallbackFailure) != null)
        {
            Interlocked.Exchange(ref injectedCallbackFailure, null);
        }

        injectedCallbackFailureName = null;
    }

    internal void PollInterrupt(LuauHostState* state)
    {
        if (Volatile.Read(ref pendingCallback) != null)
        {
            Volatile.Write(ref yieldReason, (int)ScriptYieldReason.AsyncCallback);
            return;
        }

        if (Volatile.Read(ref callbackFailure) != null)
        {
            Volatile.Write(ref yieldReason, (int)ScriptYieldReason.CallbackFailure);
            return;
        }

        if (DetectHardStop(state) != ScriptHardStopReason.None)
        {
            Volatile.Write(ref yieldReason, (int)ScriptYieldReason.HardStop);
        }
    }

    string? TakeCallbackFailureName()
    {
        var name = callbackFailureName;
        callbackFailureName = null;
        return name;
    }

    LuauManagedCallbackException CreateControlledCallbackFailure(Exception? failure, string? callbackName)
    {
        failure ??= new InvalidOperationException("A managed callback failed without reporting an exception.");
        return failure as LuauManagedCallbackException
            ?? new LuauManagedCallbackException(ChunkName, callbackName, failure);
    }

    internal Exception? GetHardStopException()
    {
        var existing = Volatile.Read(ref hardStopException);
        if (existing != null)
        {
            return existing;
        }

        var reason = DetectHardStop(interruptState: null);
        if (reason == ScriptHardStopReason.None)
        {
            return null;
        }

        var candidate = CreateHardStopException(reason);
        return Interlocked.CompareExchange(ref hardStopException, candidate, null) ?? candidate;
    }

    /// <summary>
    /// Checks the hard stops in precedence order. From an interrupt poll,
    /// <paramref name="interruptState"/> is the polling thread, and the
    /// native host's safepoint count decides the interrupt budget.
    /// </summary>
    ScriptHardStopReason DetectHardStop(LuauHostState* interruptState)
    {
        var existing = (ScriptHardStopReason)Volatile.Read(ref hardStopReason);
        if (existing != ScriptHardStopReason.None)
        {
            return existing;
        }

        var candidate = ScriptHardStopReason.None;

        if (Context.IsDisposalRequested || State.IsDisposed)
        {
            candidate = ScriptHardStopReason.Disposed;
        }
        else if (callerCancellationToken.IsCancellationRequested)
        {
            candidate = ScriptHardStopReason.Canceled;
        }
        else
        {
            if (interruptState != null &&
                Options.InterruptCountLimit is { } interruptLimit &&
                ReadNativeInterruptCount(interruptState) > (ulong)interruptLimit)
            {
                candidate = ScriptHardStopReason.InterruptCount;
            }
            else if (deadlineTimestamp != 0 &&
                (Volatile.Read(ref deadlineSignaled) != 0 || IsPastWallClockLimit()))
            {
                candidate = ScriptHardStopReason.WallClock;
            }
        }

        if (candidate == ScriptHardStopReason.None)
        {
            return candidate;
        }

        var winner = (ScriptHardStopReason)Interlocked.CompareExchange(
            ref hardStopReason,
            (int)candidate,
            (int)ScriptHardStopReason.None);
        return winner == ScriptHardStopReason.None ? candidate : winner;
    }

    Exception CreateHardStopException(ScriptHardStopReason reason)
    {
        return reason switch
        {
            ScriptHardStopReason.Disposed => new ObjectDisposedException(
                nameof(LuauState),
                LuauDiagnosticMessages.WithChunk("The owning Luau state was disposed during execution.", ChunkName)),
            ScriptHardStopReason.Canceled => new LuauExecutionCanceledException(ChunkName, callerCancellationToken),
            ScriptHardStopReason.InterruptCount => new LuauExecutionBudgetException(
                ChunkName,
                Options.InterruptCountLimit!.Value,
                Interlocked.Read(ref interruptCount)),
            ScriptHardStopReason.WallClock => new LuauExecutionBudgetException(
                ChunkName,
                Options.WallClockLimit!.Value,
                GetElapsedTime()),
            _ => new LuauException("Luau execution stopped without a reported cause.", ChunkName),
        };
    }

    ulong ReadNativeInterruptCount(LuauHostState* interruptState)
    {
        // The host counts safepoints itself and polls only when the count
        // passes the limit or other work is due. Its count is the authority
        // and is compared unclamped; the latest observation is kept for the
        // budget exception, which reports a signed count.
        var nativeCount = luau_host_interrupt_count(interruptState);
        Interlocked.Exchange(ref interruptCount, nativeCount > long.MaxValue ? long.MaxValue : (long)nativeCount);
        return nativeCount;
    }

    TimeSpan GetElapsedTime()
    {
        // Whole TimeSpan ticks, truncated. TimeSpan.FromSeconds(double) rounds
        // to whole milliseconds on Mono's class library.
        var elapsed = Stopwatch.GetTimestamp() - startedTimestamp;
        return TimeSpan.FromTicks((long)(elapsed * TimeSpanTicksPerStopwatchTick));
    }

    static readonly double TimeSpanTicksPerStopwatchTick = (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency;

    static long ComputeDeadlineTimestamp(long started, TimeSpan limit)
    {
        // Round up and add one Stopwatch tick, so that GetElapsedTime() has
        // certainly reached the limit at this timestamp and a budget exception
        // never reports less than the limit. Zero means "no deadline".
        var limitTicks = Math.Ceiling(limit.Ticks / TimeSpanTicksPerStopwatchTick) + 1d;
        var remaining = (double)(long.MaxValue - started);
        return limitTicks >= remaining ? long.MaxValue : started + (long)limitTicks;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0)
        {
            return;
        }

        // Dispose waits for a running cancellation callback, which may need
        // the context locks; no lock is held here.
        callerCancellationRegistration.Dispose();

        if (Volatile.Read(ref coroutineLifecycleArmed) != 0)
        {
            if (Volatile.Read(ref coroutineCompletion) == 1)
            {
                State.MarkCoroutineSuspended();
            }
            else
            {
                State.MarkCoroutineDead();
            }
        }

        // Last: once the context ends this life it may hand the instance to
        // the next operation on this root.
        Context.EndOperation(this, Volatile.Read(ref generation));
    }
}

/// <summary>
/// The value an asynchronous flow carries to identify the operation it runs
/// in. Immutable, so an execution context captured during one life keeps
/// naming that life after the operation instance is reused.
/// </summary>
internal sealed class AmbientOperationMarker
{
    internal AmbientOperationMarker(ScriptOperation operation, long generation)
    {
        Operation = operation;
        Generation = generation;
    }

    internal ScriptOperation Operation { get; }
    internal long Generation { get; }
}

/// <summary>
/// A callback's handle on its operation's cancellation token. The token is
/// created only when a callback reads it, so an operation whose callbacks
/// never ask for one allocates no cancellation machinery.
/// </summary>
internal readonly struct LuauOperationCancellation
{
    readonly ScriptOperation? operation;
    readonly long generation;

    internal LuauOperationCancellation(ScriptOperation operation)
    {
        this.operation = operation;
        generation = operation.Generation;
    }

    internal CancellationToken Token =>
        operation == null ? default : operation.GetCancellationToken(generation);
}
