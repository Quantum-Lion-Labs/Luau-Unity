using System.Runtime.ExceptionServices;
using System.Text;
using Luau.Internal.Interop;
using static Luau.Internal.Interop.NativeMethods;

namespace Luau;

internal static class ScriptRunner
{
    internal static int Run(
        ScriptOperation operation,
        LuauState state,
        int argumentCount,
        Span<LuauValue> destination,
        bool hasFunction = true)
    {
        var resultCount = RunCore(operation, argumentCount, hasFunction);
        if (destination.Length < resultCount)
        {
            DiscardResults(operation, operation.ThreadPointer, resultCount);
            throw new ArgumentException("Destination is too short.", nameof(destination));
        }

        PopResultsInto(operation, state, destination, resultCount);

        return resultCount;
    }

    internal static LuauResultScope Run(
        ScriptOperation operation,
        LuauState state,
        int argumentCount,
        bool hasFunction = true)
    {
        var resultCount = RunCore(operation, argumentCount, hasFunction);
        if (resultCount == 0)
        {
            return new LuauResultScope([]);
        }

        LuauValue[] results;
        try
        {
            results = new LuauValue[resultCount];
        }
        catch
        {
            DiscardResults(operation, operation.ThreadPointer, resultCount);
            throw;
        }

        PopOwnedResults(operation, state, results, resultCount);

        return new LuauResultScope(results);
    }

    internal static void RunVoid(
        ScriptOperation operation,
        int argumentCount,
        bool hasFunction = true)
    {
        var resultCount = RunCore(operation, argumentCount, hasFunction);
        if (resultCount == 0)
        {
            return;
        }

        DiscardResults(operation, operation.ThreadPointer, resultCount);
        throw new LuauResultLimitException(operation.ChunkName, resultCount, limit: 0);
    }

    internal static async ValueTask<int> RunAsync(
        ScriptOperation operation,
        LuauState state,
        int argumentCount,
        Memory<LuauValue> destination,
        bool hasFunction = true)
    {
        var resultCount = await RunAsyncCore(operation, argumentCount, hasFunction).ConfigureAwait(false);
        if (destination.Length < resultCount)
        {
            await LuauContinuationDispatcher.InvokeAsync(
                operation.Options.ContinuationScheduler,
                () => DiscardResults(operation, operation.ThreadPointer, resultCount)).ConfigureAwait(false);
            throw new ArgumentException("Destination is too short.", nameof(destination));
        }

        await LuauContinuationDispatcher.InvokeAsync(
            operation.Options.ContinuationScheduler,
            () =>
            {
                Span<LuauValue> slots;
                try
                {
                    slots = destination.Span;
                }
                catch
                {
                    DiscardResults(operation, operation.ThreadPointer, resultCount);
                    throw;
                }
                PopResultsInto(operation, state, slots, resultCount);
            }).ConfigureAwait(false);

        return resultCount;
    }

    internal static async ValueTask<LuauResultScope> RunAsync(
        ScriptOperation operation,
        LuauState state,
        int argumentCount,
        bool hasFunction = true)
    {
        var resultCount = await RunAsyncCore(operation, argumentCount, hasFunction).ConfigureAwait(false);
        if (resultCount == 0)
        {
            return new LuauResultScope([]);
        }

        LuauValue[] results;
        try
        {
            results = new LuauValue[resultCount];
        }
        catch
        {
            await LuauContinuationDispatcher.InvokeAsync(
                operation.Options.ContinuationScheduler,
                () => DiscardResults(operation, operation.ThreadPointer, resultCount)).ConfigureAwait(false);
            throw;
        }

        await LuauContinuationDispatcher.InvokeAsync(
            operation.Options.ContinuationScheduler,
            () => PopOwnedResults(operation, state, results, resultCount)).ConfigureAwait(false);

        return new LuauResultScope(results);
    }

    internal static async ValueTask RunVoidAsync(
        ScriptOperation operation,
        int argumentCount,
        bool hasFunction = true)
    {
        var resultCount = await RunAsyncCore(operation, argumentCount, hasFunction).ConfigureAwait(false);
        if (resultCount == 0)
        {
            return;
        }

        await LuauContinuationDispatcher.InvokeAsync(
            operation.Options.ContinuationScheduler,
            () => DiscardResults(operation, operation.ThreadPointer, resultCount)).ConfigureAwait(false);
        throw new LuauResultLimitException(operation.ChunkName, resultCount, limit: 0);
    }

    internal static ValueTask<int> RunCountAsync(
        ScriptOperation operation,
        int argumentCount,
        bool hasFunction = true)
    {
        return RunAsyncCore(operation, argumentCount, hasFunction);
    }

    internal static int RunToStack(
        ScriptOperation operation,
        int argumentCount,
        bool hasFunction = true)
    {
        return RunCore(operation, argumentCount, hasFunction);
    }

    static int RunCore(ScriptOperation operation, int argumentCount, bool hasFunction)
    {
        var state = operation.ThreadPointer;
        var from = operation.FromPointer;
        var baseTop = -1;
        var resumeWithError = false;
        var firstResume = true;

        while (true)
        {
            operation.PrepareResume();
            LuauHostStatus status;
            // The stack top right after native control returned, or -1. The
            // checks below change the stack only when they throw, so the
            // result count can use it without another native round trip.
            var topAfterResume = -1;

            if (resumeWithError)
            {
                operation.PrepareCallbackFailureInjection();
                status = ResumeErrorWithCallbackFailure(operation, state, from);
                resumeWithError = false;
            }
            else if (firstResume)
            {
                status = Enter(
                    operation,
                    state,
                    from,
                    argumentCount,
                    argumentCount + (hasFunction ? 1 : 0),
                    out baseTop,
                    out topAfterResume);
            }
            else
            {
                status = Resume(operation, state, from, argumentCount, out topAfterResume);
            }

            firstResume = false;

            if (status == LuauHostStatus.Ok)
            {
                operation.CompleteCoroutineDead();
                operation.ClearInjectedCallbackFailure();
                ThrowIfHardStopped(operation, state);
                ThrowIfUninjectedCallbackFailure(operation, state);
                return GetResultCount(operation, state, baseTop, topAfterResume);
            }

            if (status != LuauHostStatus.Yielded && status != LuauHostStatus.Break)
            {
                ThrowExecutionFailure(operation, state);
            }

            operation.ClearInjectedCallbackFailure();
            ThrowIfHardStopped(operation, state);

            switch (operation.YieldReason)
            {
                case ScriptYieldReason.None:
                    ThrowIfUninjectedCallbackFailure(operation, state);
                    RejectRootYield(operation, state);
                    var yieldedResultCount = GetResultCount(operation, state, baseTop, topAfterResume);
                    operation.CompleteCoroutineSuspended();
                    return yieldedResultCount;
                case ScriptYieldReason.CallbackFailure:
                    resumeWithError = true;
                    argumentCount = 0;
                    continue;
                case ScriptYieldReason.HardStop:
                    ThrowIfHardStopped(operation, state);
                    throw new LuauException("Luau execution stopped without a reported cause.", operation.ChunkName);
                case ScriptYieldReason.AsyncCallback:
                    Abort(operation, state);
                    throw new LuauException(
                        LuauDiagnosticMessages.WithChunk(
                            "An asynchronous managed callback yielded during synchronous execution.",
                            operation.ChunkName),
                        operation.ChunkName);
                default:
                    Abort(operation, state);
                    throw new LuauException("Unknown Luau yield reason.", operation.ChunkName);
            }
        }
    }

    static async ValueTask<int> RunAsyncCore(
        ScriptOperation operation,
        int argumentCount,
        bool hasFunction)
    {
        var state = operation.ThreadPointer;
        var from = operation.FromPointer;
        var scheduler = operation.Options.ContinuationScheduler;
        var baseTop = await LuauContinuationDispatcher.InvokeAsync(
            scheduler,
            () => GetTop(operation, state) - argumentCount - (hasFunction ? 1 : 0)).ConfigureAwait(false);
        var resumeWithError = false;

        while (true)
        {
            operation.PrepareResume();
            LuauHostStatus status;

            if (resumeWithError)
            {
                operation.PrepareCallbackFailureInjection();
                status = await LuauContinuationDispatcher.InvokeAsync(
                    scheduler,
                    () => ResumeErrorWithCallbackFailure(operation, state, from)).ConfigureAwait(false);
                resumeWithError = false;
            }
            else
            {
                var resumeArgumentCount = argumentCount;
                status = await LuauContinuationDispatcher.InvokeAsync(
                    scheduler,
                    () => Resume(operation, state, from, resumeArgumentCount)).ConfigureAwait(false);
            }

            if (operation.PendingCallbackThread != IntPtr.Zero)
            {
                await LuauContinuationDispatcher.InvokeAsync(
                    scheduler,
                    () => ValidatePendingCallbackSuspension(operation, state, status)).ConfigureAwait(false);
            }

            if (status == LuauHostStatus.Ok)
            {
                operation.CompleteCoroutineDead();
                operation.ClearInjectedCallbackFailure();
                return await LuauContinuationDispatcher.InvokeAsync(
                    scheduler,
                    () =>
                    {
                        ThrowIfHardStopped(operation, state);
                        ThrowIfUninjectedCallbackFailure(operation, state);
                        return GetResultCount(operation, state, baseTop);
                    }).ConfigureAwait(false);
            }

            if (status != LuauHostStatus.Yielded && status != LuauHostStatus.Break)
            {
                await LuauContinuationDispatcher.InvokeAsync(
                    scheduler,
                    () => ThrowExecutionFailure(operation, state)).ConfigureAwait(false);
                throw new LuauException("Luau execution failure handling returned unexpectedly.", operation.ChunkName);
            }

            operation.ClearInjectedCallbackFailure();
            await LuauContinuationDispatcher.InvokeAsync(
                scheduler,
                () => ThrowIfHardStopped(operation, state)).ConfigureAwait(false);

            switch (operation.YieldReason)
            {
                case ScriptYieldReason.None:
                    var yieldedResultCount = await LuauContinuationDispatcher.InvokeAsync(
                        scheduler,
                        () =>
                        {
                            ThrowIfUninjectedCallbackFailure(operation, state);
                            RejectRootYield(operation, state);
                            return GetResultCount(operation, state, baseTop);
                        }).ConfigureAwait(false);
                    operation.CompleteCoroutineSuspended();
                    return yieldedResultCount;
                case ScriptYieldReason.CallbackFailure:
                    resumeWithError = true;
                    argumentCount = 0;
                    continue;
                case ScriptYieldReason.HardStop:
                    await LuauContinuationDispatcher.InvokeAsync(
                        scheduler,
                        () => ThrowIfHardStopped(operation, state)).ConfigureAwait(false);
                    throw new LuauException("Luau execution stopped without a reported cause.", operation.ChunkName);
                case ScriptYieldReason.AsyncCallback:
                    operation.MarkAsyncCallbackSuspended();
                    // ValidatePendingCallbackSuspension confirmed this thread is
                    // the resume target, so its frame is the callback's own.
                    var callbackThread = operation.PendingCallbackThread;
                    var pending = operation.TakePendingCallback();
                    var callbackName = operation.TakePendingCallbackName();
                    if (pending?.AsynchronousCallback == null)
                    {
                        operation.RecordCallbackFailure(
                            callbackName,
                            new InvalidOperationException("The pending managed callback was lost."));
                        operation.RecordFailedCallbackSuspension(callbackThread);
                        resumeWithError = true;
                        argumentCount = 0;
                        operation.FinishAsyncCallback();
                        continue;
                    }

                    try
                    {
                        var callback = pending.AsynchronousCallback;
                        var invocation = await LuauContinuationDispatcher.InvokeAsync(
                            scheduler,
                            () => callback(
                                GetCallbackState(operation),
                                new LuauOperationCancellation(operation))).ConfigureAwait(false);
                        argumentCount = await invocation.ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        operation.RecordCallbackFailure(callbackName, exception);
                        argumentCount = 0;
                        resumeWithError = true;
                    }

                    await LuauContinuationDispatcher.InvokeAsync(
                        scheduler,
                        () => ThrowIfHardStopped(operation, state)).ConfigureAwait(false);
                    if (!resumeWithError)
                    {
                        try
                        {
                            var callbackResultCount = argumentCount;
                            await LuauContinuationDispatcher.InvokeAsync(
                                scheduler,
                                () => ValidateResultCount(operation, GetResumeTarget(operation, state), callbackResultCount)).ConfigureAwait(false);
                        }
                        catch (Exception exception)
                        {
                            operation.RecordCallbackFailure(callbackName, exception);
                            argumentCount = 0;
                            resumeWithError = true;
                        }
                    }

                    if (resumeWithError)
                    {
                        operation.RecordFailedCallbackSuspension(callbackThread);
                    }

                    operation.FinishAsyncCallback();
                    continue;
                default:
                    await LuauContinuationDispatcher.InvokeAsync(
                        scheduler,
                        () => Abort(operation, state)).ConfigureAwait(false);
                    throw new LuauException("Unknown Luau yield reason.", operation.ChunkName);
            }
        }
    }

    static void ValidatePendingCallbackSuspension(ScriptOperation operation, IntPtr state, LuauHostStatus status)
    {
        var callbackThread = operation.PendingCallbackThread;
        if (callbackThread == IntPtr.Zero ||
            ((status == LuauHostStatus.Break || status == LuauHostStatus.Yielded) &&
                GetResumeTarget(operation, state) == callbackThread))
        {
            return;
        }

        // A non-yieldable ancestor can reject the child's break and catch the
        // resulting Lua error. A later interrupt may then suspend the parent;
        // dispatching the child's callback there would use the wrong stack.
        // Compare the saved pointer only: the rejected child may be collected.
        operation.TakePendingCallback();
        var callbackName = operation.TakePendingCallbackName();
        operation.FinishAsyncCallback();
        ThrowIfHardStopped(operation, state);
        operation.RecordCallbackFailure(callbackName, new InvalidOperationException(
            "An asynchronous managed callback requires yieldable Luau execution through all parent coroutines."));
        ThrowIfUninjectedCallbackFailure(operation, state);
    }

    static void RejectRootYield(ScriptOperation operation, IntPtr state)
    {
        if (!operation.State.IsMainThread)
        {
            return;
        }

        // Root calls have no continuation owner. Leaving one suspended would
        // make the next invocation resume it instead of the requested function.
        // Reset before result validation, which can itself throw on a value yield.
        Abort(operation, state);
        throw new LuauException(
            LuauDiagnosticMessages.WithChunk(
                "The root Luau state cannot yield. Use a child coroutine for resumable execution.",
                operation.ChunkName),
            operation.ChunkName);
    }

    static int GetResultCount(ScriptOperation operation, IntPtr state, int baseTop, int knownTop = -1)
    {
        var resultCount = (knownTop >= 0 ? knownTop : GetTop(operation, state)) - baseTop;
        if (resultCount < 0)
        {
            Abort(operation, state);
            throw new LuauException("The Luau stack was corrupted while calculating results.");
        }

        if (operation.Options.MaxResultCount is { } limit && resultCount > limit)
        {
            DiscardResultsAbove(operation, state, baseTop);
            throw new LuauResultLimitException(operation.ChunkName, resultCount, limit);
        }

        // Luau can return or yield more values than a host frame holds.
        // Reading a reference result needs one free slot, so reject a full
        // frame before decoding any result rather than partway through.
        if (resultCount > 0 && IsFrameFull(operation, state))
        {
            DiscardResultsAbove(operation, state, baseTop);
            throw new LuauStackLimitException(operation.ChunkName, "return its results");
        }

        return resultCount;
    }

    static unsafe bool IsFrameFull(ScriptOperation operation, IntPtr state)
    {
        using var access = operation.Context.EnterOperationNativeAccess(operation);
        return operation.Context.IsFrameFullUnderNativeAccess((LuauHostState*)state);
    }

    // Shrinking the stack cannot fail natively. If it ever does, reset the
    // thread rather than leave rejected values behind.
    static void DiscardResultsAbove(ScriptOperation operation, IntPtr state, int baseTop)
    {
        try
        {
            SetTop(operation, state, baseTop);
        }
        catch
        {
            Abort(operation, state);
            throw;
        }
    }

    static void ThrowIfUninjectedCallbackFailure(ScriptOperation operation, IntPtr state)
    {
        var failure = operation.TakeUninjectedCallbackFailure();
        if (failure == null)
        {
            return;
        }

        Abort(operation, state);
        throw failure;
    }

    static void DiscardResults(ScriptOperation operation, IntPtr state, int resultCount)
    {
        if (resultCount <= 0)
        {
            return;
        }

        var top = GetTop(operation, state);
        SetTop(operation, state, Math.Max(0, top - resultCount));
    }

    static void PopResultsInto(
        ScriptOperation operation,
        LuauState state,
        Span<LuauValue> destination,
        int resultCount)
    {
        try
        {
            ValidateDestinationSlots(destination, resultCount);
        }
        catch
        {
            // Nothing was read, so the results are still on the stack, and
            // only ReadResults trims it.
            DiscardResults(operation, operation.ThreadPointer, resultCount);
            throw;
        }

        var writtenStart = resultCount;
        try
        {
            ReadResults(operation, state, destination, resultCount, ref writtenStart);
        }
        catch
        {
            DisposeResultValues(destination.Slice(writtenStart, resultCount - writtenStart));
            throw;
        }
    }

    static void PopOwnedResults(
        ScriptOperation operation,
        LuauState state,
        LuauValue[] results,
        int resultCount)
    {
        var writtenStart = resultCount;
        try
        {
            ReadResults(operation, state, results, resultCount, ref writtenStart);
        }
        catch
        {
            DisposeResultValues(results);
            throw;
        }
    }

    /// <summary>
    /// Moves the top <paramref name="resultCount"/> values into
    /// <paramref name="slots"/> under one native access. Values are read in
    /// the order the per-value pop used to read them, last result first, and
    /// a hard stop is still checked after each one. The stack is trimmed to
    /// the results' base whether or not every value was read.
    /// </summary>
    static unsafe void ReadResults(
        ScriptOperation operation,
        LuauState state,
        Span<LuauValue> slots,
        int resultCount,
        ref int writtenStart)
    {
        if (resultCount <= 0)
        {
            return;
        }

        using var access = operation.Context.EnterOperationNativeAccess(operation);
        var pointer = (LuauHostState*)operation.ThreadPointer;
        var baseTop = luau_host_stack_get_top(pointer) - resultCount;
        try
        {
            for (var index = resultCount - 1; index >= 0; index--)
            {
                slots[index] = state.ToValueUnderOperationAccess(baseTop + 1 + index);
                writtenStart = index;
                if (operation.GetHardStopException() is { } hardStop)
                {
                    throw hardStop;
                }
            }
        }
        finally
        {
            // Shrinking the stack cannot fail natively.
            RequireStackSetTopSuccess(luau_host_stack_set_top(pointer, baseTop));
        }
    }

    static void DisposeResultValues(Span<LuauValue> values)
    {
        for (var index = values.Length - 1; index >= 0; index--)
        {
            values[index].DisposeUnpublishedReference();
            values[index] = default;
        }
    }

    static void ValidateDestinationSlots(Span<LuauValue> destination, int resultCount)
    {
        for (var index = 0; index < resultCount; index++)
        {
            if (destination[index].ContainsManagedReferenceWrapper)
            {
                throw new ArgumentException(
                    "A destination slot that will receive a result contains a managed Luau reference wrapper. " +
                    "Release the existing wrapper according to its ownership contract and clear its slot before reuse.",
                    nameof(destination));
            }
        }
    }

    static void ValidateResultCount(ScriptOperation operation, IntPtr state, int resultCount)
    {
        var top = GetTop(operation, state);
        if (resultCount < 0 || resultCount > top)
        {
            throw new LuauException(
                $"Managed callback returned invalid result count {resultCount} for a stack containing {top} values.");
        }
    }

    static void ThrowIfHardStopped(ScriptOperation operation, IntPtr state)
    {
        var exception = operation.GetHardStopException();
        if (exception == null)
        {
            return;
        }

        Abort(operation, state);
        throw exception;
    }

    static void ThrowExecutionFailure(ScriptOperation operation, IntPtr state)
    {
        // A callback can discover a hard stop while accessing the VM and then
        // report that exception through the callback-failure channel. Capture
        // the sticky hard stop independently of the last yield reason so the
        // operation-wide precedence remains hard stop, callback failure, then
        // allocator/native failure.
        var recordedHardStop = operation.GetHardStopException();
        var isInjectedCallbackFailure = IsCallbackFailureToken(operation, state);
        var callbackFailure = isInjectedCallbackFailure
            ? operation.TakeInjectedCallbackFailure()
            : operation.TakeUninjectedCallbackFailure();
        if (!isInjectedCallbackFailure)
        {
            operation.ClearInjectedCallbackFailure();
        }

        string message;
        try
        {
            message = PopError(operation, state);
        }
        catch
        {
            Abort(operation, state);
            throw;
        }

        Abort(operation, state);

        if (recordedHardStop != null)
        {
            throw recordedHardStop;
        }

        if (callbackFailure != null)
        {
            throw callbackFailure;
        }

        if (operation.Context.AllocatorFailure == LuauAllocatorFailure.QuotaExceeded)
        {
            throw new LuauMemoryLimitException(
                operation.ChunkName,
                operation.Context.MemoryUsage,
                operation.Context.LastAttemptedAllocationBytes);
        }

        if (!string.IsNullOrEmpty(operation.ChunkName) &&
            message.IndexOf(operation.ChunkName, StringComparison.Ordinal) < 0)
        {
            message = LuauDiagnosticMessages.WithChunk(message, operation.ChunkName);
        }

        throw new LuauException(message, operation.ChunkName);
    }

    static unsafe LuauHostStatus ResumeErrorWithCallbackFailure(
        ScriptOperation operation,
        IntPtr state,
        IntPtr from)
    {
        using var access = operation.Context.EnterOperationNativeAccess(operation);
        var target = luau_host_resume_target((LuauHostState*)state);
        // When the target is the thread the failed callback suspended on, its
        // frame is that callback's and the token replaces it, so the push has
        // room even when arguments or partial results filled the frame. A
        // pending failure can instead arrive on another frame: a non-yieldable
        // parent can reject the callback's suspension, and a later interrupt
        // then suspends a Lua frame whose registers are live. Compare the saved
        // pointer only; a rejected child may already be collected. Rich
        // callback details remain attached to the managed exception; a Luau
        // pcall receives only this opaque non-nil failure token.
        var failedCallbackThread = operation.TakeFailedCallbackThread();
        LuauNativeProtection.Prepare(operation.Context);
        var pushStatus = LuauNativeProtection.PushCallbackFailure(
            target,
            operation.CallbackFailureToken,
            replaceCallbackFrame: failedCallbackThread != IntPtr.Zero && failedCallbackThread == (IntPtr)target);
        if (pushStatus == LuauHostStatus.Ok)
        {
            return luau_host_resume_error((LuauHostState*)state, (LuauHostState*)from);
        }

        ExceptionDispatchInfo failure;
        try
        {
            LuauNativeProtection.ThrowIfFailed(
                operation.State,
                target,
                pushStatus,
                "inject a managed callback failure",
                operation.ChunkName);
            failure = ExceptionDispatchInfo.Capture(new LuauException(
                "Managed callback failure injection failed without a reported cause.",
                operation.ChunkName));
        }
        catch (Exception pushFailure)
        {
            // The callback failure never reached Luau: the frame it arrived on
            // had no room for the token. An emptied callback frame always has
            // room, so this is a frame the callback did not own. Below a hard
            // stop the callback failure still outranks the failure to inject it.
            var callbackFailure = operation.GetHardStopException() == null
                ? operation.TakeInjectedCallbackFailure()
                : null;
            failure = ExceptionDispatchInfo.Capture(callbackFailure ?? pushFailure);
        }

        Abort(operation, state);
        failure.Throw();
        return default;
    }

    static unsafe bool IsCallbackFailureToken(ScriptOperation operation, IntPtr state)
    {
        using var access = operation.Context.EnterOperationNativeAccess(operation);
        return (LuauHostType)luau_host_type((LuauHostState*)state, -1) == LuauHostType.LightUserdata &&
            (IntPtr)luau_host_to_light_userdata((LuauHostState*)state, -1) == operation.CallbackFailureToken;
    }

    static unsafe string PopError(ScriptOperation operation, IntPtr state)
    {
        using var access = operation.Context.EnterOperationNativeAccess(operation);
        var pointer = (LuauHostState*)state;
        try
        {
            // String coercion may allocate. Execution can
            // arrive here with the allocator exhausted, so only read values
            // that are already strings and never coerce arbitrary error data.
            if ((LuauHostType)luau_host_type(pointer, -1) != LuauHostType.String)
            {
                return "Luau execution failed with a non-string error value.";
            }

            ulong length = 0;
            var text = luau_host_to_string_view(pointer, -1, &length);
            if (text == null || length == 0)
            {
                return "Luau execution failed without an error message.";
            }

            return BoundedUtf8Decoder.DecodeDiagnostic(
                text,
                length,
                operation.Context.Options.MaxDiagnosticBytes);
        }
        finally
        {
            RequireStackSetTopSuccess(luau_host_stack_set_top(pointer, -2));
        }
    }

    static unsafe void Abort(ScriptOperation operation, IntPtr state)
    {
        using var access = operation.Context.EnterOperationNativeAccess(operation);
        var status = luau_host_thread_reset((LuauHostState*)state);
        if (status == LuauHostStatus.Ok)
        {
            return;
        }

        // Protected reset contains the native longjmp but cannot restore a
        // partially reset CallInfo/stack. Capture diagnostics without reading
        // that stack, poison the whole root, and let EndOperation perform the
        // only remaining native action: deferred state close.
        Exception? failure = null;
        try
        {
            failure = CreateTerminalResetFailure(operation, status);
        }
        finally
        {
            try
            {
                (operation.State.From ?? operation.State).Dispose();
            }
            catch
            {
                // The VM is already terminal. Disposal will be retried when
                // the active operation unwinds.
            }
        }

        throw failure;
    }

    internal static void AbortHostOperation(ScriptOperation operation)
    {
        Abort(operation, operation.ThreadPointer);
    }

    static Exception CreateTerminalResetFailure(ScriptOperation operation, LuauHostStatus status)
    {
        if (operation.Context.AllocatorFailure == LuauAllocatorFailure.QuotaExceeded)
        {
            var usage = operation.Context.MemoryUsage;
            var limit = usage.LimitBytes!.Value;
            var attempted = Math.Max(
                limit + 1,
                operation.Context.LastAttemptedAllocationBytes);
            return new LuauMemoryLimitException(operation.ChunkName, usage, attempted);
        }

        if (operation.Context.AllocatorFailure == LuauAllocatorFailure.SystemOutOfMemory ||
            status == LuauHostStatus.SystemOutOfMemory)
        {
            return new OutOfMemoryException(
                LuauDiagnosticMessages.WithChunk(
                    "The Luau VM could not reset after an execution failure and was disposed.",
                    operation.ChunkName));
        }

        return new LuauException(
            LuauDiagnosticMessages.WithChunk(
                $"The Luau VM reset failed with native status {(int)status} and the VM was disposed.",
                operation.ChunkName),
            operation.ChunkName);
    }

    static unsafe int GetTop(ScriptOperation operation, IntPtr state)
    {
        using var access = operation.Context.EnterOperationNativeAccess(operation);
        return luau_host_stack_get_top((LuauHostState*)state);
    }

    static unsafe IntPtr GetResumeTarget(ScriptOperation operation, IntPtr state)
    {
        using var access = operation.Context.EnterOperationNativeAccess(operation);
        return (IntPtr)luau_host_resume_target((LuauHostState*)state);
    }

    static unsafe LuauState GetCallbackState(ScriptOperation operation)
    {
        using var access = operation.Context.EnterOperationNativeAccess(operation);
        var target = luau_host_resume_target((LuauHostState*)operation.ThreadPointer);
        return LuauState.GetCachedState(target, operation.Context);
    }

    static unsafe void SetTop(ScriptOperation operation, IntPtr state, int top)
    {
        using var access = operation.Context.EnterOperationNativeAccess(operation);
        RequireStackSetTopSuccess(luau_host_stack_set_top((LuauHostState*)state, top));
    }

    static void RequireStackSetTopSuccess(LuauHostStatus status)
    {
        if (status != LuauHostStatus.Ok)
        {
            throw new InvalidOperationException(
                $"The Luau host returned status {(int)status} while attempting to set the stack top.");
        }
    }

    static unsafe LuauHostStatus Resume(
        ScriptOperation operation,
        IntPtr state,
        IntPtr from,
        int argumentCount)
    {
        using var access = operation.Context.EnterOperationNativeAccess(operation);
        operation.ArmCoroutineLifecycle();
        return luau_host_resume((LuauHostState*)state, (LuauHostState*)from, argumentCount);
    }

    static unsafe LuauHostStatus Resume(
        ScriptOperation operation,
        IntPtr state,
        IntPtr from,
        int argumentCount,
        out int topAfterResume)
    {
        using var access = operation.Context.EnterOperationNativeAccess(operation);
        operation.ArmCoroutineLifecycle();
        var status = luau_host_resume((LuauHostState*)state, (LuauHostState*)from, argumentCount);
        topAfterResume = ReadTopAfterResume(state, status);
        return status;
    }

    /// <summary>
    /// The first resume of a synchronous run: records the stack base below
    /// the callee and its arguments, then resumes, under one native access.
    /// </summary>
    static unsafe LuauHostStatus Enter(
        ScriptOperation operation,
        IntPtr state,
        IntPtr from,
        int argumentCount,
        int enteredSlots,
        out int baseTop,
        out int topAfterResume)
    {
        using var access = operation.Context.EnterOperationNativeAccess(operation);
        baseTop = luau_host_stack_get_top((LuauHostState*)state) - enteredSlots;
        operation.ArmCoroutineLifecycle();
        var status = luau_host_resume((LuauHostState*)state, (LuauHostState*)from, argumentCount);
        topAfterResume = ReadTopAfterResume(state, status);
        return status;
    }

    static unsafe int ReadTopAfterResume(IntPtr state, LuauHostStatus status) =>
        status is LuauHostStatus.Ok or LuauHostStatus.Yielded or LuauHostStatus.Break
            ? luau_host_stack_get_top((LuauHostState*)state)
            : -1;

}
