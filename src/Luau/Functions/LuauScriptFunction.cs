using System.Runtime.InteropServices;
using Luau.Internal.Interop;
using static Luau.Internal.Interop.NativeMethods;

namespace Luau;

internal sealed class LuauScriptFunction(
    LuauState state,
    int reference,
    LuauCallFrame? borrowedFrame = null) : LuauFunction(state, borrowedFrame), ILuauReference
{
    static readonly LuauExecutionOptions ZeroResultExecutionOptions =
        LuauExecutionOptions.Unbounded with
        {
            MaxResultCount = 0,
        };

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
        ThrowIfDisposed();
        var state = State;
        using var operation = state.BeginOperation(
            chunkName: operationLabel,
            options: executionOptions,
            cancellationToken: default,
            isAsync: false);

        var baseTop = state.GetTop();
        try
        {
            PushInvocation(state, arguments);
        }
        catch
        {
            state.SetTop(baseTop);
            throw;
        }

        return ScriptRunner.Run(operation, state, arguments.Length);
    }

    internal LuauResultScope InvokeWithArgument(
        LuauValue argument,
        LuauExecutionOptions? executionOptions,
        string? operationLabel)
    {
        ThrowIfDisposed();
        var state = State;
        using var operation = state.BeginOperation(
            chunkName: operationLabel,
            options: executionOptions,
            cancellationToken: default,
            isAsync: false);

        var baseTop = state.GetTop();
        try
        {
            PushInvocation(state, argument);
        }
        catch
        {
            state.SetTop(baseTop);
            throw;
        }

        return ScriptRunner.Run(operation, state, argumentCount: 1);
    }

    internal int InvokeIntoWithArguments(
        ReadOnlySpan<LuauValue> arguments,
        Span<LuauValue> destination,
        LuauExecutionOptions? executionOptions,
        string? operationLabel = null)
    {
        ThrowIfDisposed();
        var state = State;
        using var operation = state.BeginOperation(
            chunkName: operationLabel,
            options: executionOptions,
            cancellationToken: default,
            isAsync: false);

        var baseTop = state.GetTop();
        try
        {
            PushInvocation(state, arguments);
        }
        catch
        {
            state.SetTop(baseTop);
            throw;
        }

        return ScriptRunner.Run(operation, state, arguments.Length, destination);
    }

    internal int InvokeIntoWithArgument(
        LuauValue argument,
        Span<LuauValue> destination,
        LuauExecutionOptions? executionOptions,
        string? operationLabel)
    {
        ThrowIfDisposed();
        var state = State;
        using var operation = state.BeginOperation(
            chunkName: operationLabel,
            options: executionOptions,
            cancellationToken: default,
            isAsync: false);

        var baseTop = state.GetTop();
        try
        {
            PushInvocation(state, argument);
        }
        catch
        {
            state.SetTop(baseTop);
            throw;
        }

        return ScriptRunner.Run(operation, state, argumentCount: 1, destination);
    }

    internal void InvokeVoidWithArguments(
        ReadOnlySpan<LuauValue> arguments,
        LuauExecutionOptions? executionOptions,
        string? operationLabel = null)
    {
        ThrowIfDisposed();
        var state = State;
        using var operation = state.BeginOperation(
            chunkName: operationLabel,
            options: RequireZeroResults(executionOptions),
            cancellationToken: default,
            isAsync: false);

        var baseTop = state.GetTop();
        try
        {
            PushInvocation(state, arguments);
        }
        catch
        {
            state.SetTop(baseTop);
            throw;
        }

        ScriptRunner.RunVoid(operation, arguments.Length);
    }

    internal void InvokeVoidWithArgument(
        LuauValue argument,
        LuauExecutionOptions? executionOptions,
        string? operationLabel = null)
    {
        ThrowIfDisposed();
        var state = State;
        using var operation = state.BeginOperation(
            chunkName: operationLabel,
            options: RequireZeroResults(executionOptions),
            cancellationToken: default,
            isAsync: false);

        var baseTop = state.GetTop();
        try
        {
            PushInvocation(state, argument);
        }
        catch
        {
            state.SetTop(baseTop);
            throw;
        }

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
        ThrowIfDisposed();
        var state = State;
        using var operation = state.BeginOperation(
            chunkName: operationLabel,
            options: executionOptions,
            cancellationToken,
            isAsync: true);

        var baseTop = state.GetTop();
        try
        {
            PushInvocation(state, arguments.Span);
        }
        catch
        {
            state.SetTop(baseTop);
            throw;
        }

        return await ScriptRunner.RunAsync(operation, state, arguments.Length).ConfigureAwait(false);
    }

    internal async ValueTask<LuauResultScope> InvokeWithArgumentAsync(
        LuauValue argument,
        CancellationToken cancellationToken,
        LuauExecutionOptions? executionOptions,
        string? operationLabel)
    {
        ThrowIfDisposed();
        var state = State;
        using var operation = state.BeginOperation(
            chunkName: operationLabel,
            options: executionOptions,
            cancellationToken,
            isAsync: true);

        var baseTop = state.GetTop();
        try
        {
            PushInvocation(state, argument);
        }
        catch
        {
            state.SetTop(baseTop);
            throw;
        }

        return await ScriptRunner.RunAsync(operation, state, argumentCount: 1).ConfigureAwait(false);
    }

    internal async ValueTask<int> InvokeIntoWithArgumentsAsync(
        ReadOnlyMemory<LuauValue> arguments,
        Memory<LuauValue> destination,
        CancellationToken cancellationToken,
        LuauExecutionOptions? executionOptions,
        string? operationLabel = null)
    {
        ThrowIfDisposed();
        var state = State;
        using var operation = state.BeginOperation(
            chunkName: operationLabel,
            options: executionOptions,
            cancellationToken,
            isAsync: true);

        var baseTop = state.GetTop();
        try
        {
            PushInvocation(state, arguments.Span);
        }
        catch
        {
            state.SetTop(baseTop);
            throw;
        }

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
        ThrowIfDisposed();
        var state = State;
        using var operation = state.BeginOperation(
            chunkName: operationLabel,
            options: executionOptions,
            cancellationToken,
            isAsync: true);

        var baseTop = state.GetTop();
        try
        {
            PushInvocation(state, argument);
        }
        catch
        {
            state.SetTop(baseTop);
            throw;
        }

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
        ThrowIfDisposed();
        var state = State;
        using var operation = state.BeginOperation(
            chunkName: operationLabel,
            options: RequireZeroResults(executionOptions),
            cancellationToken,
            isAsync: true);

        var baseTop = state.GetTop();
        try
        {
            PushInvocation(state, arguments.Span);
        }
        catch
        {
            state.SetTop(baseTop);
            throw;
        }

        await ScriptRunner.RunVoidAsync(operation, arguments.Length).ConfigureAwait(false);
    }

    internal async ValueTask InvokeVoidWithArgumentAsync(
        LuauValue argument,
        CancellationToken cancellationToken,
        LuauExecutionOptions? executionOptions,
        string? operationLabel = null)
    {
        ThrowIfDisposed();
        var state = State;
        using var operation = state.BeginOperation(
            chunkName: operationLabel,
            options: RequireZeroResults(executionOptions),
            cancellationToken,
            isAsync: true);

        var baseTop = state.GetTop();
        try
        {
            PushInvocation(state, argument);
        }
        catch
        {
            state.SetTop(baseTop);
            throw;
        }

        await ScriptRunner.RunVoidAsync(operation, argumentCount: 1).ConfigureAwait(false);
    }

    void PushInvocation(LuauState state, ReadOnlySpan<LuauValue> arguments)
    {
        state.Push(this);
        for (var i = 0; i < arguments.Length; i++)
        {
            state.Push(arguments[i]);
        }
    }

    void PushInvocation(LuauState state, LuauValue argument)
    {
        state.Push(this);
        state.Push(argument);
    }

    static LuauExecutionOptions RequireZeroResults(LuauExecutionOptions? executionOptions)
    {
        if (executionOptions == null)
        {
            return ZeroResultExecutionOptions;
        }

        return executionOptions.MaxResultCount == 0
            ? executionOptions
            : executionOptions with { MaxResultCount = 0 };
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
