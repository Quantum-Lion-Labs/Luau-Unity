using System.Runtime.InteropServices;
using Luau.Internal.Interop;
using static Luau.Internal.Interop.NativeMethods;

namespace Luau;

internal sealed class LuauScriptFunction(
    LuauState state,
    int reference,
    LuauCallFrame? borrowedFrame = null) : LuauFunction(state, borrowedFrame), ILuauReference
{
    int reference = reference;
    public int Reference => Volatile.Read(ref reference);
    LuauReferenceAccess ILuauReference.AcquireReference() => AcquireReference(Reference);

    private protected override LuauState ResolvePublicState(LuauState owningState) =>
        owningState.GetMainThread();

    internal LuauResultScope InvokeWithArguments(
        ReadOnlySpan<LuauValue> arguments,
        LuauExecutionOptions? executionOptions,
        string? operationLabel = null)
    {
        var state = GetInvocationState();
        using var operation = state.BeginOperation(
            chunkName: operationLabel,
            options: executionOptions,
            cancellationToken: default,
            isAsync: false);

        PushInvocation(operation, state, arguments);

        return ScriptRunner.Run(operation, state, arguments.Length);
    }

    internal LuauResultScope InvokeWithArgument(
        LuauValue argument,
        LuauExecutionOptions? executionOptions,
        string? operationLabel)
    {
        var state = GetInvocationState();
        using var operation = state.BeginOperation(
            chunkName: operationLabel,
            options: executionOptions,
            cancellationToken: default,
            isAsync: false);

        PushInvocation(operation, state, argument);

        return ScriptRunner.Run(operation, state, argumentCount: 1);
    }

    internal int InvokeIntoWithArguments(
        ReadOnlySpan<LuauValue> arguments,
        Span<LuauValue> destination,
        LuauExecutionOptions? executionOptions,
        string? operationLabel = null)
    {
        var state = GetInvocationState();
        using var operation = state.BeginOperation(
            chunkName: operationLabel,
            options: executionOptions,
            cancellationToken: default,
            isAsync: false);

        PushInvocation(operation, state, arguments);

        return ScriptRunner.Run(operation, state, arguments.Length, destination);
    }

    internal int InvokeIntoWithArgument(
        LuauValue argument,
        Span<LuauValue> destination,
        LuauExecutionOptions? executionOptions,
        string? operationLabel)
    {
        var state = GetInvocationState();
        using var operation = state.BeginOperation(
            chunkName: operationLabel,
            options: executionOptions,
            cancellationToken: default,
            isAsync: false);

        PushInvocation(operation, state, argument);

        return ScriptRunner.Run(operation, state, argumentCount: 1, destination);
    }

    internal void InvokeVoidWithArguments(
        ReadOnlySpan<LuauValue> arguments,
        LuauExecutionOptions? executionOptions,
        string? operationLabel = null)
    {
        var state = GetInvocationState();
        using var operation = state.BeginOperation(
            chunkName: operationLabel,
            options: executionOptions,
            cancellationToken: default,
            isAsync: false);

        PushInvocation(operation, state, arguments);

        ScriptRunner.RunVoid(operation, arguments.Length);
    }

    internal void InvokeVoidWithArgument(
        LuauValue argument,
        LuauExecutionOptions? executionOptions,
        string? operationLabel = null)
    {
        var state = GetInvocationState();
        using var operation = state.BeginOperation(
            chunkName: operationLabel,
            options: executionOptions,
            cancellationToken: default,
            isAsync: false);

        PushInvocation(operation, state, argument);

        ScriptRunner.RunVoid(operation, argumentCount: 1);
    }

    internal LuauFunction RetainReference()
    {
        using var access = AcquireReference(Reference);
        return new LuauScriptFunction(
            access.State,
            LuauReferenceHelper.RetainReference(
                access.State,
                access.Reference,
                "retain a Luau function"));
    }

    internal async ValueTask<LuauResultScope> InvokeWithArgumentsAsync(
        ReadOnlyMemory<LuauValue> arguments,
        CancellationToken cancellationToken,
        LuauExecutionOptions? executionOptions,
        string? operationLabel = null)
    {
        var state = GetInvocationState();
        using var operation = state.BeginOperation(
            chunkName: operationLabel,
            options: executionOptions,
            cancellationToken,
            isAsync: true);

        PushInvocation(operation, state, arguments.Span);

        return await ScriptRunner.RunAsync(operation, state, arguments.Length).ConfigureAwait(false);
    }

    internal async ValueTask<LuauResultScope> InvokeWithArgumentAsync(
        LuauValue argument,
        CancellationToken cancellationToken,
        LuauExecutionOptions? executionOptions,
        string? operationLabel)
    {
        var state = GetInvocationState();
        using var operation = state.BeginOperation(
            chunkName: operationLabel,
            options: executionOptions,
            cancellationToken,
            isAsync: true);

        PushInvocation(operation, state, argument);

        return await ScriptRunner.RunAsync(operation, state, argumentCount: 1).ConfigureAwait(false);
    }

    internal async ValueTask<int> InvokeIntoWithArgumentsAsync(
        ReadOnlyMemory<LuauValue> arguments,
        Memory<LuauValue> destination,
        CancellationToken cancellationToken,
        LuauExecutionOptions? executionOptions,
        string? operationLabel = null)
    {
        var state = GetInvocationState();
        using var operation = state.BeginOperation(
            chunkName: operationLabel,
            options: executionOptions,
            cancellationToken,
            isAsync: true);

        PushInvocation(operation, state, arguments.Span);

        return await ScriptRunner.RunAsync(
            operation,
            state,
            arguments.Length,
            destination).ConfigureAwait(false);
    }

    internal async ValueTask<int> InvokeIntoWithArgumentAsync(
        LuauValue argument,
        Memory<LuauValue> destination,
        CancellationToken cancellationToken,
        LuauExecutionOptions? executionOptions,
        string? operationLabel)
    {
        var state = GetInvocationState();
        using var operation = state.BeginOperation(
            chunkName: operationLabel,
            options: executionOptions,
            cancellationToken,
            isAsync: true);

        PushInvocation(operation, state, argument);

        return await ScriptRunner.RunAsync(
            operation,
            state,
            argumentCount: 1,
            destination).ConfigureAwait(false);
    }

    internal async ValueTask InvokeVoidWithArgumentsAsync(
        ReadOnlyMemory<LuauValue> arguments,
        CancellationToken cancellationToken,
        LuauExecutionOptions? executionOptions,
        string? operationLabel = null)
    {
        var state = GetInvocationState();
        using var operation = state.BeginOperation(
            chunkName: operationLabel,
            options: executionOptions,
            cancellationToken,
            isAsync: true);

        PushInvocation(operation, state, arguments.Span);

        await ScriptRunner.RunVoidAsync(operation, arguments.Length).ConfigureAwait(false);
    }

    internal async ValueTask InvokeVoidWithArgumentAsync(
        LuauValue argument,
        CancellationToken cancellationToken,
        LuauExecutionOptions? executionOptions,
        string? operationLabel = null)
    {
        var state = GetInvocationState();
        using var operation = state.BeginOperation(
            chunkName: operationLabel,
            options: executionOptions,
            cancellationToken,
            isAsync: true);

        PushInvocation(operation, state, argument);

        await ScriptRunner.RunVoidAsync(operation, argumentCount: 1).ConfigureAwait(false);
    }

    unsafe void PushInvocation(ScriptOperation operation, LuauState state, ReadOnlySpan<LuauValue> arguments)
    {
        using var access = AcquireReferenceForOperation(operation, Reference);
        var pointer = state.PointerUnsafe;
        var baseTop = luau_host_stack_get_top(pointer);
        try
        {
            PushCallee(state, pointer, access.Reference);
            for (var i = 0; i < arguments.Length; i++)
            {
                LuauOperationStack.PushArgument(state, pointer, arguments[i]);
            }

            LuauOperationStack.ThrowIfStoppedBeforeEntry(operation);
        }
        catch
        {
            state.SetTop(baseTop);
            throw;
        }
    }

    unsafe void PushInvocation(ScriptOperation operation, LuauState state, LuauValue argument)
    {
        using var access = AcquireReferenceForOperation(operation, Reference);
        var pointer = state.PointerUnsafe;
        var baseTop = luau_host_stack_get_top(pointer);
        try
        {
            PushCallee(state, pointer, access.Reference);
            LuauOperationStack.PushArgument(state, pointer, argument);
            LuauOperationStack.ThrowIfStoppedBeforeEntry(operation);
        }
        catch
        {
            state.SetTop(baseTop);
            throw;
        }
    }

    // The invocation pushes run right after BeginOperation, which reset the
    // allocator diagnostics, and run no Luau code, so they need no further
    // reset; a failed push throws before the next one. A failure is
    // translated exactly as the general push path translates it.
    static unsafe void PushCallee(LuauState state, LuauHostState* pointer, int reference)
    {
        var ignoredType = 0;
        var status = luau_host_reference_push(pointer, reference, &ignoredType);
        if (status != LuauHostStatus.Ok)
        {
            LuauNativeProtection.ThrowIfFailed(state, pointer, status, "push a managed Luau reference");
        }
    }

    public override string ToString()
    {
        using var access = AcquireReference(Reference);
        return LuauReferenceHelper.RefToString(access.State, access.Reference);
    }

    private protected override void DisposeCore()
    {
        var currentReference = Interlocked.Exchange(ref reference, -1);
        if (currentReference >= 0)
        {
            OwningState.TryReleaseReference(currentReference);
        }
    }

    ~LuauScriptFunction()
    {
        DisposeFromFinalizer();
    }
}
