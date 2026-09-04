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

        var remaining = resultCount;
        var writtenStart = resultCount;
        try
        {
            ValidateDestinationSlots(destination, resultCount);
            for (var i = resultCount - 1; i >= 0; i--)
            {
                destination[i] = state.Pop();
                writtenStart = i;
                remaining--;
            }
        }
        catch
        {
            DisposeResultValues(destination.Slice(writtenStart, resultCount - writtenStart));
            DiscardResults(operation, operation.ThreadPointer, remaining);
            throw;
        }

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

        var remaining = resultCount;
        try
        {
            for (var i = resultCount - 1; i >= 0; i--)
            {
                results[i] = state.Pop();
                remaining--;
            }
        }
        catch
        {
            DisposeResultValues(results);
            DiscardResults(operation, operation.ThreadPointer, remaining);
            throw;
        }

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
                var remaining = resultCount;
                var writtenStart = resultCount;
                try
                {
                    ValidateDestinationSlots(destination.Span, resultCount);
                    for (var i = resultCount - 1; i >= 0; i--)
                    {
                        destination.Span[i] = state.Pop();
                        writtenStart = i;
                        remaining--;
                    }
                }
                catch
                {
                    DisposeResultValues(
                        destination.Span.Slice(writtenStart, resultCount - writtenStart));
                    DiscardResults(operation, operation.ThreadPointer, remaining);
                    throw;
                }
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
            () =>
            {
                var remaining = resultCount;
                try
                {
                    for (var i = resultCount - 1; i >= 0; i--)
                    {
                        results[i] = state.Pop();
                        remaining--;
                    }
                }
                catch
                {
                    DisposeResultValues(results);
                    DiscardResults(operation, operation.ThreadPointer, remaining);
                    throw;
                }
            }).ConfigureAwait(false);

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
        var baseTop = GetTop(operation, state) - argumentCount - (hasFunction ? 1 : 0);
        var resumeWithError = false;

        while (true)
        {
            operation.PrepareResume();
            LuauHostStatus status;

            if (resumeWithError)
            {
                operation.PrepareCallbackFailureInjection();
                status = ResumeErrorWithCallbackFailure(operation, state, from);
                resumeWithError = false;
            }
            else
            {
                status = Resume(operation, state, from, argumentCount);
            }

            if (status == LuauHostStatus.Ok)
            {
                operation.CompleteCoroutineDead();
                operation.ClearInjectedCallbackFailure();
                ThrowIfHardStopped(operation, state);
                ThrowIfUninjectedCallbackFailure(operation, state);
                return GetResultCount(operation, state, baseTop);
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
                    var yieldedResultCount = GetResultCount(operation, state, baseTop);
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
                    var pending = operation.TakePendingCallback();
                    var callbackName = operation.TakePendingCallbackName();
                    if (pending?.AsynchronousCallback == null)
                    {
                        operation.RecordCallbackFailure(
                            callbackName,
                            new InvalidOperationException("The pending managed callback was lost."));
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
                                operation.CancellationToken)).ConfigureAwait(false);
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

    static int GetResultCount(ScriptOperation operation, IntPtr state, int baseTop)
    {
        var resultCount = GetTop(operation, state) - baseTop;
        if (resultCount < 0)
        {
            Abort(operation, state);
            throw new LuauException("The Luau stack was corrupted while calculating results.");
        }

        if (operation.Options.MaxResultCount is { } limit && resultCount > limit)
        {
            SetTop(operation, state, baseTop);
            throw new LuauResultLimitException(operation.ChunkName, resultCount, limit);
        }

        return resultCount;
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
        // The value itself is allocation-free, but the protected bridge also
        // reserves its stack slot inside a native error frame. Rich callback
        // details remain attached to the managed exception; a Luau pcall
        // receives only this opaque non-nil failure token.
        LuauNativeProtection.Prepare(operation.Context);
        var pushStatus = luau_host_push_light_userdata(
            target,
            operation.CallbackFailureToken.ToPointer(),
            0);
        try
        {
            LuauNativeProtection.ThrowIfFailed(
                operation.State,
                target,
                pushStatus,
                "inject a managed callback failure",
                operation.ChunkName);
        }
        catch
        {
            Abort(operation, state);
            throw;
        }

        return luau_host_resume_error((LuauHostState*)state, (LuauHostState*)from);
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
        return luau_host_resume((LuauHostState*)state, (LuauHostState*)from, argumentCount);
    }

}
