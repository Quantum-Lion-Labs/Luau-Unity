namespace Luau;

/// <summary>
/// Represents either an owned script closure or a managed callback capability.
/// Script closures and retained copies must be disposed before their root VM;
/// callback-borrowed closures are valid only for their active callback frame.
/// </summary>
/// <remarks>
/// Script invocations run on the root and must complete. An ordinary Luau yield
/// resets the invocation and throws <see cref="LuauException"/>. Asynchronous
/// invocations may still await managed callbacks; use a child coroutine for
/// explicitly resumable Luau execution.
/// </remarks>
public abstract class LuauFunction : IDisposable, ILuauCallbackBorrowedReference
{
    LuauState? state;
    int disposeState;
    readonly object lifetimeGate = new();
    readonly LuauCallFrame? borrowedFrame;

    internal LuauFunction(LuauState state, LuauCallFrame? borrowedFrame = null)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        this.borrowedFrame = borrowedFrame;
        borrowedFrame?.RegisterBorrowed(this);
    }

    /// <summary>Gets whether this reference is valid only for its callback frame.</summary>
    public bool IsBorrowed => borrowedFrame != null;

    /// <summary>
    /// Gets the public owning root. Access fails after this function, its
    /// callback frame, or its root has been disposed.
    /// </summary>
    public LuauState State
    {
        get
        {
            borrowedFrame?.EnsureBorrowedActive();
            lock (lifetimeGate)
            {
                var currentState = state;
                if (disposeState != 0 || currentState == null || currentState.IsDisposed)
                {
                    ThrowHelper.ThrowObjectDisposedException(nameof(LuauFunction));
                }

                return ResolvePublicState(currentState!);
            }
        }
    }

    /// <summary>Gets whether this function or its owning root is disposed.</summary>
    public bool IsDisposed
    {
        get
        {
            if (Volatile.Read(ref disposeState) != 0)
            {
                return true;
            }

            var currentState = Volatile.Read(ref state);
            return currentState == null || currentState.IsDisposed;
        }
    }

    /// <summary>
    /// Creates an independently disposable owner for the same script closure.
    /// Managed callback capabilities already have host-defined ownership and
    /// cannot be retained through this operation.
    /// </summary>
    public LuauFunction Retain()
    {
        if (this is not LuauScriptFunction scriptFunction)
        {
            throw new InvalidOperationException(
                "Managed callback capabilities cannot be retained as script references.");
        }

        return scriptFunction.RetainReference();
    }

    private protected LuauState OwningState => state!;

    /// <summary>
    /// Invokes a script closure synchronously on its owning VM root.
    /// Managed callback functions are host capabilities and cannot be invoked
    /// directly by managed callers. Dispose the returned scope to release its
    /// scope-owned results; dispose returned child threads separately.
    /// </summary>
    public LuauResultScope Invoke(
        ReadOnlySpan<LuauValue> arguments = default,
        LuauExecutionOptions? executionOptions = null)
    {
        return GetScriptFunctionForInvocation().InvokeWithArguments(arguments, executionOptions);
    }

    /// <summary>
    /// Invokes a script closure asynchronously on its owning VM root.
    /// Managed callback functions are host capabilities and cannot be invoked
    /// directly by managed callers. Dispose the returned scope to release its
    /// scope-owned results; dispose returned child threads separately.
    /// </summary>
    public ValueTask<LuauResultScope> InvokeAsync(
        ReadOnlyMemory<LuauValue> arguments = default,
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null)
    {
        return GetScriptFunctionForInvocation().InvokeWithArgumentsAsync(
            arguments,
            cancellationToken,
            executionOptions);
    }

    /// <summary>
    /// Invokes a script closure synchronously and writes its results into
    /// caller-owned storage. The caller owns reference wrappers written to the
    /// destination. Slots that receive results must not already contain managed
    /// reference wrappers.
    /// </summary>
    public int InvokeInto(
        Span<LuauValue> destination,
        LuauExecutionOptions? executionOptions = null)
    {
        return InvokeInto(default, destination, executionOptions);
    }

    /// <summary>
    /// Invokes a script closure synchronously with arguments and writes its
    /// results into distinct caller-owned storage. The caller owns reference
    /// wrappers written to the destination. Slots that receive results must not
    /// already contain managed reference wrappers.
    /// </summary>
    public int InvokeInto(
        ReadOnlySpan<LuauValue> arguments,
        Span<LuauValue> destination,
        LuauExecutionOptions? executionOptions = null)
    {
        return GetScriptFunctionForInvocation().InvokeIntoWithArguments(
            arguments,
            destination,
            executionOptions);
    }

    /// <summary>
    /// Invokes a script closure asynchronously and writes its results into
    /// caller-owned storage. The caller owns reference wrappers written to the
    /// destination. Slots that receive results must not already contain managed
    /// reference wrappers.
    /// </summary>
    public ValueTask<int> InvokeIntoAsync(
        Memory<LuauValue> destination,
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null)
    {
        return InvokeIntoAsync(
            ReadOnlyMemory<LuauValue>.Empty,
            destination,
            cancellationToken,
            executionOptions);
    }

    /// <summary>
    /// Invokes a script closure asynchronously with arguments and writes its
    /// results into distinct caller-owned storage. The caller owns reference
    /// wrappers written to the destination. Slots that receive results must not
    /// already contain managed reference wrappers.
    /// </summary>
    public ValueTask<int> InvokeIntoAsync(
        ReadOnlyMemory<LuauValue> arguments,
        Memory<LuauValue> destination,
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null)
    {
        return GetScriptFunctionForInvocation().InvokeIntoWithArgumentsAsync(
            arguments,
            destination,
            cancellationToken,
            executionOptions);
    }

    /// <summary>
    /// Invokes a script closure synchronously and requires it to return no values.
    /// </summary>
    public void InvokeVoid(LuauExecutionOptions? executionOptions = null)
    {
        GetScriptFunctionForInvocation().InvokeVoidWithArguments(default, executionOptions);
    }

    /// <summary>
    /// Invokes a script closure synchronously with one argument and requires it
    /// to return no values.
    /// </summary>
    public void InvokeVoid(
        LuauValue argument,
        LuauExecutionOptions? executionOptions = null)
    {
        GetScriptFunctionForInvocation().InvokeVoidWithArgument(argument, executionOptions);
    }

    /// <summary>
    /// Invokes a script closure synchronously with arguments and requires it to
    /// return no values.
    /// </summary>
    public void InvokeVoid(
        ReadOnlySpan<LuauValue> arguments,
        LuauExecutionOptions? executionOptions = null)
    {
        GetScriptFunctionForInvocation().InvokeVoidWithArguments(arguments, executionOptions);
    }

    /// <summary>
    /// Invokes a script closure asynchronously and requires it to return no values.
    /// </summary>
    public ValueTask InvokeVoidAsync(
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null)
    {
        return GetScriptFunctionForInvocation().InvokeVoidWithArgumentsAsync(
            ReadOnlyMemory<LuauValue>.Empty,
            cancellationToken,
            executionOptions);
    }

    /// <summary>
    /// Invokes a script closure asynchronously with one argument and requires it
    /// to return no values.
    /// </summary>
    public ValueTask InvokeVoidAsync(
        LuauValue argument,
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null)
    {
        return GetScriptFunctionForInvocation().InvokeVoidWithArgumentAsync(
            argument,
            cancellationToken,
            executionOptions);
    }

    /// <summary>
    /// Invokes a script closure asynchronously with arguments and requires it to
    /// return no values.
    /// </summary>
    public ValueTask InvokeVoidAsync(
        ReadOnlyMemory<LuauValue> arguments,
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null)
    {
        return GetScriptFunctionForInvocation().InvokeVoidWithArgumentsAsync(
            arguments,
            cancellationToken,
            executionOptions);
    }

    internal LuauResultScope InvokeLabeled(
        ReadOnlySpan<LuauValue> arguments,
        string operationLabel,
        LuauExecutionOptions? executionOptions = null)
    {
        return GetScriptFunctionForInvocation().InvokeWithArguments(
            arguments,
            executionOptions,
            operationLabel);
    }

    internal LuauResultScope InvokeOneLabeled(
        LuauValue argument,
        string operationLabel,
        LuauExecutionOptions? executionOptions = null)
    {
        return GetScriptFunctionForInvocation().InvokeWithArgument(
            argument,
            executionOptions,
            operationLabel);
    }

    internal ValueTask<LuauResultScope> InvokeLabeledAsync(
        ReadOnlyMemory<LuauValue> arguments,
        string operationLabel,
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null)
    {
        return GetScriptFunctionForInvocation().InvokeWithArgumentsAsync(
            arguments,
            cancellationToken,
            executionOptions,
            operationLabel);
    }

    internal ValueTask<LuauResultScope> InvokeOneLabeledAsync(
        LuauValue argument,
        string operationLabel,
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null)
    {
        return GetScriptFunctionForInvocation().InvokeWithArgumentAsync(
            argument,
            cancellationToken,
            executionOptions,
            operationLabel);
    }

    internal int InvokeIntoLabeled(
        ReadOnlySpan<LuauValue> arguments,
        Span<LuauValue> destination,
        string operationLabel,
        LuauExecutionOptions? executionOptions = null)
    {
        return GetScriptFunctionForInvocation().InvokeIntoWithArguments(
            arguments,
            destination,
            executionOptions,
            operationLabel);
    }

    internal int InvokeOneIntoLabeled(
        LuauValue argument,
        Span<LuauValue> destination,
        string operationLabel,
        LuauExecutionOptions? executionOptions = null)
    {
        return GetScriptFunctionForInvocation().InvokeIntoWithArgument(
            argument,
            destination,
            executionOptions,
            operationLabel);
    }

    internal ValueTask<int> InvokeIntoLabeledAsync(
        ReadOnlyMemory<LuauValue> arguments,
        Memory<LuauValue> destination,
        string operationLabel,
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null)
    {
        return GetScriptFunctionForInvocation().InvokeIntoWithArgumentsAsync(
            arguments,
            destination,
            cancellationToken,
            executionOptions,
            operationLabel);
    }

    internal ValueTask<int> InvokeOneIntoLabeledAsync(
        LuauValue argument,
        Memory<LuauValue> destination,
        string operationLabel,
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null)
    {
        return GetScriptFunctionForInvocation().InvokeIntoWithArgumentAsync(
            argument,
            destination,
            cancellationToken,
            executionOptions,
            operationLabel);
    }

    internal void InvokeVoidLabeled(
        string operationLabel,
        LuauExecutionOptions? executionOptions = null)
    {
        GetScriptFunctionForInvocation().InvokeVoidWithArguments(
            default,
            executionOptions,
            operationLabel);
    }

    internal void InvokeVoidLabeled(
        LuauValue argument,
        string operationLabel,
        LuauExecutionOptions? executionOptions = null)
    {
        GetScriptFunctionForInvocation().InvokeVoidWithArgument(
            argument,
            executionOptions,
            operationLabel);
    }

    internal void InvokeVoidLabeled(
        ReadOnlySpan<LuauValue> arguments,
        string operationLabel,
        LuauExecutionOptions? executionOptions = null)
    {
        GetScriptFunctionForInvocation().InvokeVoidWithArguments(
            arguments,
            executionOptions,
            operationLabel);
    }

    internal ValueTask InvokeVoidLabeledAsync(
        string operationLabel,
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null)
    {
        return GetScriptFunctionForInvocation().InvokeVoidWithArgumentsAsync(
            ReadOnlyMemory<LuauValue>.Empty,
            cancellationToken,
            executionOptions,
            operationLabel);
    }

    internal ValueTask InvokeVoidLabeledAsync(
        LuauValue argument,
        string operationLabel,
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null)
    {
        return GetScriptFunctionForInvocation().InvokeVoidWithArgumentAsync(
            argument,
            cancellationToken,
            executionOptions,
            operationLabel);
    }

    internal ValueTask InvokeVoidLabeledAsync(
        ReadOnlyMemory<LuauValue> arguments,
        string operationLabel,
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null)
    {
        return GetScriptFunctionForInvocation().InvokeVoidWithArgumentsAsync(
            arguments,
            cancellationToken,
            executionOptions,
            operationLabel);
    }

    /// <summary>
    /// Resolves the stack that callers should use when invoking this function.
    /// Managed callbacks retain their creating state; registry-backed script
    /// functions override this to use the VM root stack so a closure produced
    /// by a yielded coroutine can be invoked without resuming that coroutine.
    /// </summary>
    private protected virtual LuauState ResolvePublicState(LuauState owningState) => owningState;

    private protected virtual void DisposeCore() { }

    /// <summary>
    /// Releases this function owner. Disposal is idempotent and does not close
    /// the owning VM root.
    /// </summary>
    public void Dispose()
    {
        LuauState? owningState;
        lock (lifetimeGate)
        {
            if (Interlocked.Exchange(ref disposeState, 1) != 0)
            {
                return;
            }

            owningState = state;
        }

        try
        {
            DisposeCore();
        }
        finally
        {
            owningState?.UnregisterDisposable(this);
            lock (lifetimeGate)
            {
                state = null;
            }
            GC.SuppressFinalize(this);
        }
    }

    private protected void DisposeFromFinalizer()
    {
        try
        {
            Dispose();
        }
        catch
        {
            // A finalizer cannot safely report cleanup failures.
        }
    }

    private protected void ThrowIfDisposed()
    {
        borrowedFrame?.EnsureBorrowedActive();
        if (IsDisposed) ThrowHelper.ThrowObjectDisposedException(nameof(LuauFunction));
    }

    private protected LuauReferenceAccess AcquireReference(int reference)
    {
        borrowedFrame?.EnsureBorrowedActive();
        var currentState = Volatile.Read(ref state);
        if (currentState == null || currentState.IsDisposed)
        {
            ThrowHelper.ThrowObjectDisposedException(nameof(LuauFunction));
        }

        var referenceState = currentState!.GetMainThread();
        var nativeAccess = currentState.EnterNativeAccess();
        Monitor.Enter(lifetimeGate);
        try
        {
            if (disposeState != 0 ||
                !ReferenceEquals(state, currentState) ||
                reference < 0 ||
                currentState.IsDisposed)
            {
                ThrowHelper.ThrowObjectDisposedException(nameof(LuauFunction));
            }

            return new LuauReferenceAccess(referenceState, reference, lifetimeGate, nativeAccess);
        }
        catch
        {
            Monitor.Exit(lifetimeGate);
            nativeAccess.Dispose();
            throw;
        }
    }

    private protected LuauFunctionAccess AcquireFunctionAccess()
    {
        borrowedFrame?.EnsureBorrowedActive();
        Monitor.Enter(lifetimeGate);
        try
        {
            var currentState = state;
            if (disposeState != 0 || currentState == null || currentState.IsDisposed)
            {
                ThrowHelper.ThrowObjectDisposedException(nameof(LuauFunction));
            }

            return new LuauFunctionAccess(currentState!, lifetimeGate);
        }
        catch
        {
            Monitor.Exit(lifetimeGate);
            throw;
        }
    }

    internal LuauFunctionAccess AcquireForPush()
    {
        return AcquireFunctionAccess();
    }

    static InvalidOperationException CreateHostInvocationCapabilityException()
    {
        return new InvalidOperationException(
            "Managed callback functions are host capabilities that can only be invoked by Luau code.");
    }

    LuauScriptFunction GetScriptFunctionForInvocation()
    {
        return this as LuauScriptFunction ?? throw CreateHostInvocationCapabilityException();
    }

    void ILuauCallbackBorrowedReference.InvalidateBorrowed() => Dispose();
}

internal readonly ref struct LuauFunctionAccess
{
    readonly object lifetimeGate;

    internal LuauState State { get; }

    internal LuauFunctionAccess(LuauState state, object lifetimeGate)
    {
        State = state;
        this.lifetimeGate = lifetimeGate;
    }

    public void Dispose()
    {
        Monitor.Exit(lifetimeGate);
    }
}
