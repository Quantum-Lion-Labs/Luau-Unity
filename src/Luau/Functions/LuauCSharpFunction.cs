using Luau.Internal.Interop;
using static Luau.Internal.Interop.NativeMethods;

namespace Luau;

internal sealed unsafe class LuauCSharpFunction : LuauFunction, ILuauManagedCallbackFunction
{
    static readonly LuauHostManagedFunction callback = Call;
    readonly Func<LuauState, CancellationToken, int> csharpDelegate;
    readonly LuauVmContext context;
    int registrationId;

    public LuauCSharpFunction(
        LuauState state,
        Func<LuauState, CancellationToken, int> func,
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
            if (registration.SynchronousCallback == null)
            {
                operation.RecordCallbackFailure(
                    registration?.Name,
                    new ObjectDisposedException(nameof(LuauFunction), "The managed callback is no longer registered."));
                return YieldFailureIfPossible(l, operation);
            }

            // Import resolution may call __index while loading sandboxed bytecode.
            // Host callbacks are dynamic: leave the import nil so Luau performs
            // the lookup during execution instead of caching a getter's value.
            if (context.IsLoadingBytecode)
            {
                return 0;
            }

            var state = LuauState.GetCachedState(l, context);
            var resultCount = registration.SynchronousCallback(state, operation.CancellationToken);
            if (resultCount < 0 || resultCount > luau_host_stack_get_top(l))
            {
                throw new InvalidOperationException(
                    $"Managed callback returned invalid result count {resultCount} for a stack containing {luau_host_stack_get_top(l)} values.");
            }

            return resultCount;
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

    ~LuauCSharpFunction()
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
