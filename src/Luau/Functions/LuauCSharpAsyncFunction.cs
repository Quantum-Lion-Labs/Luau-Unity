using Luau.Internal.Interop;
using static Luau.Internal.Interop.NativeMethods;

namespace Luau;

internal sealed unsafe class LuauCSharpAsyncFunction : LuauFunction, ILuauManagedCallbackFunction
{
    static readonly LuauHostManagedFunction callback = Call;
    readonly Func<LuauState, LuauOperationCancellation, ValueTask<int>> csharpDelegate;
    readonly LuauVmContext context;
    long registrationId;

    public LuauCSharpAsyncFunction(
        LuauState state,
        Func<LuauState, LuauOperationCancellation, ValueTask<int>> func,
        string? name = null) : base(state)
    {
        csharpDelegate = func;
        context = state.Context;
        registrationId = context.RegisterManagedCallback(name, func);
    }

    long ILuauManagedCallbackFunction.RegistrationId => Volatile.Read(ref registrationId);
    LuauHostManagedFunction ILuauManagedCallbackFunction.Callback => callback;

    [AOT.MonoPInvokeCallback(typeof(LuauHostManagedFunction))]
    static unsafe int Call(LuauHostState* l)
    {
        ScriptOperation? operation = null;
        LuauManagedCallbackRegistration? registration = null;

        try
        {
            var nativeRegistrationId = luau_host_callback_registration_id(l);
            if (nativeRegistrationId == 0 || nativeRegistrationId > long.MaxValue ||
                !LuauVmContext.TryGetManagedCallbackOwner(
                    (long)nativeRegistrationId,
                    out var context,
                    out registration) ||
                (operation = context.GetActiveOperation()) == null)
            {
                return 0;
            }
            if (registration.AsynchronousCallback == null)
            {
                operation.RecordCallbackFailure(
                    registration?.Name,
                    new ObjectDisposedException(nameof(LuauFunction), "The managed callback is no longer registered."));
                return YieldFailureIfPossible(l, operation);
            }

            if (!operation.IsAsync)
            {
                operation.RecordCallbackFailure(
                    registration.Name,
                    new InvalidOperationException(
                        "An asynchronous managed callback cannot run inside a synchronous Luau execution."));
                return YieldFailureIfPossible(l, operation);
            }

            if (luau_host_is_yieldable(l) == 0)
            {
                operation.RecordCallbackFailure(
                    registration.Name,
                    new InvalidOperationException(
                        "An asynchronous managed callback requires yieldable Luau execution."));
                return YieldFailureIfPossible(l, operation);
            }

            if (!operation.IsResumingNatively)
            {
                // Luau code that a direct host operation runs outside the
                // operation's own resume (a metamethod reached while the
                // operation waits for a callback, here inside a coroutine it
                // created). Queuing would end that wait, or replace the
                // operation's pending work, from inside the access, and the
                // suspension cannot reach the runner: fail in place, so the
                // access that ran this code reports it.
                operation.RecordCallbackFailure(
                    registration.Name,
                    new InvalidOperationException(
                        "An asynchronous managed callback cannot run in Luau code that a direct host operation runs."));
                return RaiseFailure(l, operation);
            }

            // Do not invoke managed user code while luau_host_resume is still on the
            // native stack. The runner dispatches the delegate only after it has
            // observed LUA_YIELD, so synchronous portions and fast continuations
            // can safely use the callback state.
            operation.QueueAsyncCallback(registration, (IntPtr)l);
            // A host suspension keeps the argument stack on this coroutine and
            // propagates through coroutine.resume/wrap without exposing a yield.
            return luau_host_suspend(l);
        }
        catch (Exception ex)
        {
            operation?.RecordCallbackFailure(registration?.Name, ex);
            return YieldFailureIfPossible(l, operation);
        }
    }

    static unsafe int YieldFailureIfPossible(LuauHostState* state, ScriptOperation? operation)
    {
        // An asynchronous operation's suspension reaches its runner only
        // from the runner's own resume (ScriptOperation.IsResumingNatively).
        if (luau_host_is_yieldable(state) != 0 &&
            (operation == null || !operation.IsAsync || operation.IsResumingNatively))
        {
            // The runner injects the failure after this suspension. Tell it
            // which thread holds the failed callback's frame.
            operation?.RecordFailedCallbackSuspension((IntPtr)state);
            return luau_host_suspend(state);
        }

        if (operation == null)
        {
            return 0;
        }

        return RaiseFailure(state, operation);
    }

    /// <summary>
    /// Raises the recorded failure as a Luau error from the failed
    /// callback's own frame, without suspending.
    /// </summary>
    static unsafe int RaiseFailure(LuauHostState* state, ScriptOperation operation)
    {
        // This runs inside the failed callback, so the frame is its own.
        var status = LuauNativeProtection.PushCallbackFailure(
            state,
            operation.CallbackFailureToken,
            replaceCallbackFrame: true);
        if (status != LuauHostStatus.Ok)
        {
            // Not expected: Luau keeps free slots in every callback frame,
            // so the emptied frame has room for the token. If the host refuses
            // anyway, return an invalid negative result so the native
            // trampoline unwinds immediately; the managed cause stays recorded
            // so the runner can apply failure precedence after native return.
            return -3;
        }

        operation.PrepareCallbackFailureInjection();
        return -2;
    }

    public override string ToString()
    {
        return $"function: (C# delegate)";
    }

    private protected override void DisposeCore()
    {
        var id = Interlocked.Exchange(ref registrationId, 0);
        if (id != 0)
        {
            context.ReleaseManagedCallbackWrapper(id);
        }
    }

    ~LuauCSharpAsyncFunction()
    {
        try
        {
            var id = Interlocked.Exchange(ref registrationId, 0);
            if (id != 0)
            {
                context.ReleaseManagedCallbackWrapper(id);
            }
        }
        catch
        {
            // Finalizers must never surface registration cleanup failures.
        }
    }
}
