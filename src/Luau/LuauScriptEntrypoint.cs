namespace Luau;

/// <summary>
/// Invokes one named function exported by a <see cref="LuauScriptInstance"/>.
/// The entrypoint is owned by its instance and is not independently disposable.
/// </summary>
public sealed class LuauScriptEntrypoint
{
    readonly LuauScriptInstance instance;
    LuauFunction? function;
    int disposeState;

    internal LuauScriptEntrypoint(
        LuauScriptInstance instance,
        string name,
        LuauFunction function)
    {
        this.instance = instance;
        Name = name;
        OperationLabel = $"{instance.Name}:{name}";
        this.function = function;
    }

    internal LuauScriptInstance Instance => instance;
    internal LuauState Root => instance.Root;
    internal string Name { get; }
    internal string OperationLabel { get; }
    internal bool IsDisposed => Volatile.Read(ref disposeState) != 0 || instance.IsDisposed;
    internal LuauFunction Function => Volatile.Read(ref function)
        ?? throw new ObjectDisposedException(nameof(LuauScriptEntrypoint));

    /// <summary>Invokes the export synchronously with no arguments.</summary>
    /// <param name="executionOptions">Optional per-invocation execution limits.</param>
    /// <returns>A caller-owned result scope.</returns>
    public LuauResultScope Invoke(LuauExecutionOptions? executionOptions = null) =>
        GetFunction().InvokeLabeled(
            ReadOnlySpan<LuauValue>.Empty,
            OperationLabel,
            executionOptions);

    /// <summary>Invokes the export synchronously with one argument.</summary>
    /// <param name="argument">The borrowed argument value.</param>
    /// <param name="executionOptions">Optional per-invocation execution limits.</param>
    /// <returns>A caller-owned result scope.</returns>
    public LuauResultScope Invoke(
        LuauValue argument,
        LuauExecutionOptions? executionOptions = null) =>
        GetFunction().InvokeOneLabeled(argument, OperationLabel, executionOptions);

    /// <summary>Invokes the export synchronously with borrowed arguments.</summary>
    /// <param name="arguments">The borrowed argument values.</param>
    /// <param name="executionOptions">Optional per-invocation execution limits.</param>
    /// <returns>A caller-owned result scope.</returns>
    public LuauResultScope Invoke(
        ReadOnlySpan<LuauValue> arguments,
        LuauExecutionOptions? executionOptions = null) =>
        GetFunction().InvokeLabeled(arguments, OperationLabel, executionOptions);

    /// <summary>Invokes the export asynchronously with no arguments.</summary>
    /// <param name="cancellationToken">Cancellation requested by the caller.</param>
    /// <param name="executionOptions">Optional per-invocation execution limits.</param>
    /// <returns>A caller-owned result scope.</returns>
    public ValueTask<LuauResultScope> InvokeAsync(
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null) =>
        GetFunction().InvokeLabeledAsync(
            ReadOnlyMemory<LuauValue>.Empty,
            OperationLabel,
            cancellationToken,
            executionOptions);

    /// <summary>Invokes the export asynchronously with one argument.</summary>
    /// <param name="argument">The borrowed argument value.</param>
    /// <param name="cancellationToken">Cancellation requested by the caller.</param>
    /// <param name="executionOptions">Optional per-invocation execution limits.</param>
    /// <returns>A caller-owned result scope.</returns>
    public ValueTask<LuauResultScope> InvokeAsync(
        LuauValue argument,
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null) =>
        GetFunction().InvokeOneLabeledAsync(
            argument,
            OperationLabel,
            cancellationToken,
            executionOptions);

    /// <summary>Invokes the export asynchronously with borrowed arguments.</summary>
    /// <param name="arguments">The borrowed argument values.</param>
    /// <param name="cancellationToken">Cancellation requested by the caller.</param>
    /// <param name="executionOptions">Optional per-invocation execution limits.</param>
    /// <returns>A caller-owned result scope.</returns>
    public ValueTask<LuauResultScope> InvokeAsync(
        ReadOnlyMemory<LuauValue> arguments,
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null) =>
        GetFunction().InvokeLabeledAsync(
            arguments,
            OperationLabel,
            cancellationToken,
            executionOptions);

    /// <summary>Invokes the export into caller-owned storage with no arguments.</summary>
    /// <param name="destination">Storage that receives owned result values.</param>
    /// <param name="executionOptions">Optional per-invocation execution limits.</param>
    /// <returns>The number of values written.</returns>
    public int InvokeInto(
        Span<LuauValue> destination,
        LuauExecutionOptions? executionOptions = null) =>
        GetFunction().InvokeIntoLabeled(
            ReadOnlySpan<LuauValue>.Empty,
            destination,
            OperationLabel,
            executionOptions);

    /// <summary>Invokes the export into caller-owned storage with one argument.</summary>
    /// <param name="argument">The borrowed argument value.</param>
    /// <param name="destination">Storage that receives owned result values.</param>
    /// <param name="executionOptions">Optional per-invocation execution limits.</param>
    /// <returns>The number of values written.</returns>
    public int InvokeInto(
        LuauValue argument,
        Span<LuauValue> destination,
        LuauExecutionOptions? executionOptions = null) =>
        GetFunction().InvokeOneIntoLabeled(
            argument,
            destination,
            OperationLabel,
            executionOptions);

    /// <summary>Invokes the export into caller-owned storage.</summary>
    /// <param name="arguments">The borrowed argument values.</param>
    /// <param name="destination">Storage that receives owned result values.</param>
    /// <param name="executionOptions">Optional per-invocation execution limits.</param>
    /// <returns>The number of values written.</returns>
    public int InvokeInto(
        ReadOnlySpan<LuauValue> arguments,
        Span<LuauValue> destination,
        LuauExecutionOptions? executionOptions = null) =>
        GetFunction().InvokeIntoLabeled(
            arguments,
            destination,
            OperationLabel,
            executionOptions);

    /// <summary>
    /// Invokes the export asynchronously into caller-owned storage with no arguments.
    /// </summary>
    /// <param name="destination">Storage that receives owned result values.</param>
    /// <param name="cancellationToken">Cancellation requested by the caller.</param>
    /// <param name="executionOptions">Optional per-invocation execution limits.</param>
    /// <returns>The number of values written.</returns>
    public ValueTask<int> InvokeIntoAsync(
        Memory<LuauValue> destination,
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null) =>
        GetFunction().InvokeIntoLabeledAsync(
            ReadOnlyMemory<LuauValue>.Empty,
            destination,
            OperationLabel,
            cancellationToken,
            executionOptions);

    /// <summary>
    /// Invokes the export asynchronously into caller-owned storage with one argument.
    /// </summary>
    /// <param name="argument">The borrowed argument value.</param>
    /// <param name="destination">Storage that receives owned result values.</param>
    /// <param name="cancellationToken">Cancellation requested by the caller.</param>
    /// <param name="executionOptions">Optional per-invocation execution limits.</param>
    /// <returns>The number of values written.</returns>
    public ValueTask<int> InvokeIntoAsync(
        LuauValue argument,
        Memory<LuauValue> destination,
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null) =>
        GetFunction().InvokeOneIntoLabeledAsync(
            argument,
            destination,
            OperationLabel,
            cancellationToken,
            executionOptions);

    /// <summary>Invokes the export asynchronously into caller-owned storage.</summary>
    /// <param name="arguments">The borrowed argument values.</param>
    /// <param name="destination">Storage that receives owned result values.</param>
    /// <param name="cancellationToken">Cancellation requested by the caller.</param>
    /// <param name="executionOptions">Optional per-invocation execution limits.</param>
    /// <returns>The number of values written.</returns>
    public ValueTask<int> InvokeIntoAsync(
        ReadOnlyMemory<LuauValue> arguments,
        Memory<LuauValue> destination,
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null) =>
        GetFunction().InvokeIntoLabeledAsync(
            arguments,
            destination,
            OperationLabel,
            cancellationToken,
            executionOptions);

    /// <summary>Invokes the export synchronously and requires zero results.</summary>
    /// <param name="executionOptions">Optional per-invocation execution limits.</param>
    public void InvokeVoid(LuauExecutionOptions? executionOptions = null) =>
        GetFunction().InvokeVoidLabeled(OperationLabel, executionOptions);

    /// <summary>
    /// Invokes the export synchronously with one argument and requires zero results.
    /// </summary>
    /// <param name="argument">The borrowed argument value.</param>
    /// <param name="executionOptions">Optional per-invocation execution limits.</param>
    public void InvokeVoid(
        LuauValue argument,
        LuauExecutionOptions? executionOptions = null) =>
        GetFunction().InvokeVoidLabeled(argument, OperationLabel, executionOptions);

    /// <summary>
    /// Invokes the export synchronously with borrowed arguments and requires zero results.
    /// </summary>
    /// <param name="arguments">The borrowed argument values.</param>
    /// <param name="executionOptions">Optional per-invocation execution limits.</param>
    public void InvokeVoid(
        ReadOnlySpan<LuauValue> arguments,
        LuauExecutionOptions? executionOptions = null) =>
        GetFunction().InvokeVoidLabeled(arguments, OperationLabel, executionOptions);

    /// <summary>Invokes the export asynchronously and requires zero results.</summary>
    /// <param name="cancellationToken">Cancellation requested by the caller.</param>
    /// <param name="executionOptions">Optional per-invocation execution limits.</param>
    /// <returns>An awaitable invocation.</returns>
    public ValueTask InvokeVoidAsync(
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null) =>
        GetFunction().InvokeVoidLabeledAsync(
            OperationLabel,
            cancellationToken,
            executionOptions);

    /// <summary>
    /// Invokes the export asynchronously with one argument and requires zero results.
    /// </summary>
    /// <param name="argument">The borrowed argument value.</param>
    /// <param name="cancellationToken">Cancellation requested by the caller.</param>
    /// <param name="executionOptions">Optional per-invocation execution limits.</param>
    /// <returns>An awaitable invocation.</returns>
    public ValueTask InvokeVoidAsync(
        LuauValue argument,
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null) =>
        GetFunction().InvokeVoidLabeledAsync(
            argument,
            OperationLabel,
            cancellationToken,
            executionOptions);

    /// <summary>
    /// Invokes the export asynchronously with borrowed arguments and requires zero results.
    /// </summary>
    /// <param name="arguments">The borrowed argument values.</param>
    /// <param name="cancellationToken">Cancellation requested by the caller.</param>
    /// <param name="executionOptions">Optional per-invocation execution limits.</param>
    /// <returns>An awaitable invocation.</returns>
    public ValueTask InvokeVoidAsync(
        ReadOnlyMemory<LuauValue> arguments,
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null) =>
        GetFunction().InvokeVoidLabeledAsync(
            arguments,
            OperationLabel,
            cancellationToken,
            executionOptions);

    internal void DisposeFromOwner()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref function, null)?.Dispose();
    }

    LuauFunction GetFunction()
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(LuauScriptEntrypoint));
        }

        return Volatile.Read(ref function)
            ?? throw new ObjectDisposedException(nameof(LuauScriptEntrypoint));
    }
}
