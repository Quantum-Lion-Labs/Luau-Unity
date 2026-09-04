using System.Diagnostics;

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

internal sealed class ScriptOperation : IDisposable
{
    static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromMilliseconds(int.MaxValue - 1d);
    static long nextCallbackFailureToken;
    readonly CancellationToken callerCancellationToken;
    readonly CancellationTokenSource linkedCancellationSource;
    readonly CancellationTokenSource? deadlineCancellationSource;
    readonly long startedTimestamp;
    readonly ScriptOperation? previousAmbient;

    Timer? deadlineTimer;
    LuauManagedCallbackRegistration? pendingCallback;
    IntPtr pendingCallbackThread;
    string? pendingCallbackName;
    Exception? callbackFailure;
    string? callbackFailureName;
    Exception? injectedCallbackFailure;
    string? injectedCallbackFailureName;
    Exception? hardStopException;
    int yieldReason;
    int asyncCallbackPhase;
    int hardStopReason;
    int disposeState;
    int coroutineLifecycleArmed;
    int coroutineCompletion;
    long interruptCount;
    long decodedBytes;

    internal unsafe ScriptOperation(
        LuauVmContext context,
        LuauState state,
        string? chunkName,
        LuauExecutionOptions options,
        CancellationToken cancellationToken,
        bool isAsync,
        ScriptOperationMode mode,
        ScriptOperation? previousAmbient)
    {
        Context = context;
        State = state;
        ChunkName = string.IsNullOrEmpty(chunkName) ? null : chunkName;
        Options = options;
        IsAsync = isAsync;
        Mode = mode;
        ThreadPointer = (IntPtr)state.PointerUnsafe;
        FromPointer = state.From == null ? IntPtr.Zero : (IntPtr)state.From.PointerUnsafe;
        callerCancellationToken = cancellationToken;
        this.previousAmbient = previousAmbient;
        startedTimestamp = Stopwatch.GetTimestamp();

        CancellationTokenSource? deadlineSource = null;
        Timer? timer = null;
        try
        {
            if (options.WallClockLimit is { } wallClockLimit)
            {
                deadlineSource = new CancellationTokenSource();
                timer = new Timer(
                    static operation => ((ScriptOperation)operation!).SignalDeadline(),
                    this,
                    Timeout.InfiniteTimeSpan,
                    Timeout.InfiniteTimeSpan);
                deadlineCancellationSource = deadlineSource;
                deadlineTimer = timer;
                timer.Change(GetTimerDelay(wallClockLimit), Timeout.InfiniteTimeSpan);
            }
            else
            {
                deadlineCancellationSource = null;
            }

            linkedCancellationSource = deadlineSource == null
                ? CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    state.LifetimeToken,
                    context.DisposalToken)
                : CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    state.LifetimeToken,
                    context.DisposalToken,
                    deadlineSource.Token);
        }
        catch
        {
            timer?.Dispose();
            deadlineSource?.Dispose();
            throw;
        }

        var token = Interlocked.Increment(ref nextCallbackFailureToken);
        CallbackFailureToken = (IntPtr)(token == 0 ? 1 : token);
    }

    internal LuauVmContext Context { get; }
    internal LuauState State { get; }
    internal string? ChunkName { get; }
    internal LuauExecutionOptions Options { get; }
    internal bool IsAsync { get; }
    internal ScriptOperationMode Mode { get; }
    internal IntPtr ThreadPointer { get; }
    internal IntPtr FromPointer { get; }
    internal IntPtr CallbackFailureToken { get; }
    internal CancellationToken CancellationToken => linkedCancellationSource.Token;
    internal ScriptOperation? PreviousAmbient => previousAmbient;
    internal long InterruptCount => Interlocked.Read(ref interruptCount);

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
    }

    internal void MarkAsyncCallbackSuspended()
    {
        Volatile.Write(ref asyncCallbackPhase, (int)AsyncCallbackPhase.Suspended);
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
        var failure = Interlocked.Exchange(ref callbackFailure, null);
        return failure == null
            ? null
            : CreateControlledCallbackFailure(failure, TakeCallbackFailureName());
    }

    internal void ClearInjectedCallbackFailure()
    {
        Interlocked.Exchange(ref injectedCallbackFailure, null);
        injectedCallbackFailureName = null;
    }

    internal void PollInterrupt()
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

        if (DetectHardStop(fromInterrupt: true) != ScriptHardStopReason.None)
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

        var reason = DetectHardStop(fromInterrupt: false);
        if (reason == ScriptHardStopReason.None)
        {
            return null;
        }

        var candidate = CreateHardStopException(reason);
        return Interlocked.CompareExchange(ref hardStopException, candidate, null) ?? candidate;
    }

    ScriptHardStopReason DetectHardStop(bool fromInterrupt)
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
            var observedCount = fromInterrupt
                ? Interlocked.Increment(ref interruptCount)
                : Interlocked.Read(ref interruptCount);
            if (fromInterrupt &&
                Options.InterruptCountLimit is { } interruptLimit &&
                observedCount > interruptLimit)
            {
                candidate = ScriptHardStopReason.InterruptCount;
            }
            else if (Options.WallClockLimit is { } wallClockLimit)
            {
                var elapsed = GetElapsedTime();
                if (deadlineCancellationSource?.IsCancellationRequested == true ||
                    elapsed >= wallClockLimit)
                {
                    candidate = ScriptHardStopReason.WallClock;
                }
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

    TimeSpan GetElapsedTime()
    {
        var elapsedTicks = Stopwatch.GetTimestamp() - startedTimestamp;
        return TimeSpan.FromSeconds((double)elapsedTicks / Stopwatch.Frequency);
    }

    void SignalDeadline()
    {
        var deadlineSource = deadlineCancellationSource;
        if (deadlineSource == null || Volatile.Read(ref disposeState) != 0)
        {
            return;
        }

        var remaining = Options.WallClockLimit!.Value - GetElapsedTime();
        if (remaining > TimeSpan.Zero)
        {
            try
            {
                Volatile.Read(ref deadlineTimer)?.Change(
                    GetTimerDelay(remaining),
                    Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // Operation disposal won the race with a long-deadline rearm.
            }
            return;
        }

        try
        {
            deadlineSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Operation disposal won the race with the timer callback.
        }
        catch
        {
            // Registrations on the callback-visible token are host code. A
            // throwing cancellation callback cannot escape the timer thread;
            // the source is already canceled and the runner will observe it.
        }
    }

    static TimeSpan GetTimerDelay(TimeSpan remaining)
    {
        return remaining <= MaximumTimerDelay ? remaining : MaximumTimerDelay;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref deadlineTimer, null)?.Dispose();
        linkedCancellationSource.Dispose();
        deadlineCancellationSource?.Dispose();
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
        Context.EndOperation(this);
    }
}
