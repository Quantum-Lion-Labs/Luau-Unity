using Luau.Internal.Interop;
using static Luau.Internal.Interop.NativeMethods;

namespace Luau;

internal sealed unsafe class LuauCSharpAsyncFunction : LuauFunction, ILuauManagedCallbackFunction
{
    static readonly LuauHostManagedFunction callback = Call;
    readonly Func<LuauState, CancellationToken, ValueTask<int>> csharpDelegate;
    readonly LuauVmContext context;
    int registrationId;

    public LuauCSharpAsyncFunction(
        LuauState state,
        Func<LuauState, CancellationToken, ValueTask<int>> func,
        string? name = null) : base(state)
    {
        csharpDelegate = func;
        context = state.Context;
        registrationId = context.RegisterManagedCallback(name, func);
    }

    int ILuauManagedCallbackFunction.RegistrationId => Volatile.Read(ref registrationId);
    LuauHostManagedFunction ILuauManagedCallbackFunction.Callback => callback;

    [AOT.MonoPInvokeCallback(typeof(LuauHostManagedFunction))]
    static unsafe int Call(LuauHostState* l)
    {
        ScriptOperation? operation = null;
        LuauManagedCallbackRegistration? registration = null;

        try
        {
            var nativeRegistrationId = luau_host_callback_registration_id(l);
            if (nativeRegistrationId == 0 || nativeRegistrationId > int.MaxValue ||
                !LuauVmContext.TryGetManagedCallbackOwner(
                    (int)nativeRegistrationId,
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
        if (luau_host_is_yieldable(state) != 0)
        {
            return luau_host_suspend(state);
        }

        if (operation == null)
        {
            return 0;
        }

        var status = luau_host_push_light_userdata(
            state,
            (void*)operation.CallbackFailureToken,
            0);
        if (status != LuauHostStatus.Ok)
        {
            // The token push itself can fail under stack or memory pressure.
            // Return an invalid negative result so the native trampoline
            // unwinds immediately; keep the raw managed cause recorded so the
            // runner can apply managed failure precedence after native return.
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
