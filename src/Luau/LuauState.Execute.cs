using System.Buffers;
using System.Text;
using Luau.Internal.Interop;
using static Luau.Internal.Interop.NativeMethods;

namespace Luau;

partial class LuauState
{
    /// <summary>
    /// Executes opaque output produced by this process's compiler and returns
    /// all results under the state's normal execution limits. Dispose the
    /// returned scope to release its scope-owned results; dispose returned
    /// child thread wrappers separately.
    /// </summary>
    public LuauResultScope ExecuteCompilerOutput(
        LuauCompilerOutput output,
        ReadOnlySpan<char> chunkName = default,
        LuauExecutionOptions? executionOptions = null) =>
        ExecuteCompilerOutputCore(output, default, chunkName, executionOptions, hasDestination: false).Results!;

    /// <summary>
    /// Executes compiler output into a caller-owned result span. The caller
    /// owns and must dispose any reference wrappers written to the span. Slots
    /// that receive results must not already contain managed reference wrappers.
    /// </summary>
    public int ExecuteCompilerOutputInto(
        LuauCompilerOutput output,
        Span<LuauValue> destination,
        ReadOnlySpan<char> chunkName = default,
        LuauExecutionOptions? executionOptions = null) =>
        ExecuteCompilerOutputCore(output, destination, chunkName, executionOptions, hasDestination: true).Count;

    /// <summary>
    /// Asynchronously executes compiler output. Dispose the returned scope to
    /// release its scope-owned results; dispose child threads separately.
    /// </summary>
    public async ValueTask<LuauResultScope> ExecuteCompilerOutputAsync(
        LuauCompilerOutput output,
        ReadOnlyMemory<char> chunkName = default,
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null)
    {
        ThrowIfAsyncExecutionCannotStart(chunkName.Span, cancellationToken);
        ValidateCompilerOutputArgument(output, chunkName.Span);
        using var operation = BeginOperation(chunkName.Span, executionOptions, cancellationToken, isAsync: true);
        LoadAcceptedBytecodeInternal(output.Bytecode, chunkName.Span);
        return await ScriptRunner.RunAsync(operation, this, 0).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously executes compiler output into caller-owned memory. The
    /// caller owns reference wrappers written to the destination. Slots that
    /// receive results must not already contain managed reference wrappers.
    /// </summary>
    public async ValueTask<int> ExecuteCompilerOutputIntoAsync(
        LuauCompilerOutput output,
        Memory<LuauValue> destination,
        ReadOnlyMemory<char> chunkName = default,
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null)
    {
        ThrowIfAsyncExecutionCannotStart(chunkName.Span, cancellationToken);
        ValidateCompilerOutputArgument(output, chunkName.Span);
        using var operation = BeginOperation(chunkName.Span, executionOptions, cancellationToken, isAsync: true);
        LoadAcceptedBytecodeInternal(output.Bytecode, chunkName.Span);
        return await ScriptRunner.RunAsync(operation, this, 0, destination).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates and executes a persistent artifact, returning all results.
    /// The chunk name is diagnostic and is never part of provenance validation.
    /// Dispose the returned scope to release its scope-owned results; dispose
    /// returned child thread wrappers separately.
    /// </summary>
    public LuauResultScope ExecuteVerifiedBytecode(
        LuauBytecodeArtifact artifact,
        ReadOnlySpan<char> chunkName = default,
        LuauExecutionOptions? executionOptions = null) =>
        ExecuteVerifiedBytecodeCore(artifact, default, chunkName, executionOptions, hasDestination: false).Results!;

    /// <summary>
    /// Validates and executes an artifact into a caller-owned span. The caller
    /// owns and must dispose any reference wrappers written to the span. Slots
    /// that receive results must not already contain managed reference wrappers.
    /// </summary>
    public int ExecuteVerifiedBytecodeInto(
        LuauBytecodeArtifact artifact,
        Span<LuauValue> destination,
        ReadOnlySpan<char> chunkName = default,
        LuauExecutionOptions? executionOptions = null) =>
        ExecuteVerifiedBytecodeCore(artifact, destination, chunkName, executionOptions, hasDestination: true).Count;

    /// <summary>
    /// Asynchronously validates and executes a persistent artifact. Dispose the
    /// returned scope to release its scope-owned results; dispose returned
    /// child thread wrappers separately.
    /// </summary>
    public async ValueTask<LuauResultScope> ExecuteVerifiedBytecodeAsync(
        LuauBytecodeArtifact artifact,
        ReadOnlyMemory<char> chunkName = default,
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null)
    {
        ThrowIfAsyncExecutionCannotStart(chunkName.Span, cancellationToken);
        ValidateArtifactArgument(artifact, chunkName.Span);
        using var operation = BeginOperation(chunkName.Span, executionOptions, cancellationToken, isAsync: true);
        LoadAcceptedBytecodeInternal(artifact.Bytecode, chunkName.Span);
        return await ScriptRunner.RunAsync(operation, this, 0).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously validates and executes an artifact into caller-owned
    /// memory. The caller owns reference wrappers written to the destination.
    /// Slots that receive results must not already contain managed reference wrappers.
    /// </summary>
    public async ValueTask<int> ExecuteVerifiedBytecodeIntoAsync(
        LuauBytecodeArtifact artifact,
        Memory<LuauValue> destination,
        ReadOnlyMemory<char> chunkName = default,
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null)
    {
        ThrowIfAsyncExecutionCannotStart(chunkName.Span, cancellationToken);
        ValidateArtifactArgument(artifact, chunkName.Span);
        using var operation = BeginOperation(chunkName.Span, executionOptions, cancellationToken, isAsync: true);
        LoadAcceptedBytecodeInternal(artifact.Bytecode, chunkName.Span);
        return await ScriptRunner.RunAsync(operation, this, 0, destination).ConfigureAwait(false);
    }

    (LuauResultScope? Results, int Count) ExecuteCompilerOutputCore(
        LuauCompilerOutput output,
        Span<LuauValue> destination,
        ReadOnlySpan<char> chunkName,
        LuauExecutionOptions? executionOptions,
        bool hasDestination)
    {
        ValidateCompilerOutputArgument(output, chunkName);
        using var operation = BeginOperation(chunkName, executionOptions, default, isAsync: false);
        LoadAcceptedBytecodeInternal(output.Bytecode, chunkName);
        return hasDestination
            ? (null, ScriptRunner.Run(operation, this, 0, destination))
            : (ScriptRunner.Run(operation, this, 0), 0);
    }

    (LuauResultScope? Results, int Count) ExecuteVerifiedBytecodeCore(
        LuauBytecodeArtifact artifact,
        Span<LuauValue> destination,
        ReadOnlySpan<char> chunkName,
        LuauExecutionOptions? executionOptions,
        bool hasDestination)
    {
        ValidateArtifactArgument(artifact, chunkName);
        using var operation = BeginOperation(chunkName, executionOptions, default, isAsync: false);
        LoadAcceptedBytecodeInternal(artifact.Bytecode, chunkName);
        return hasDestination
            ? (null, ScriptRunner.Run(operation, this, 0, destination))
            : (ScriptRunner.Run(operation, this, 0), 0);
    }

    void ValidateCompilerOutputArgument(LuauCompilerOutput output, ReadOnlySpan<char> chunkName)
    {
        if (output == null)
        {
            throw new ArgumentNullException(nameof(output));
        }

        ThrowIfDisposed();
        ValidateCompilerOutput(output, DecodeChunkName(chunkName));
    }

    void ValidateArtifactArgument(LuauBytecodeArtifact artifact, ReadOnlySpan<char> chunkName)
    {
        if (artifact == null)
        {
            throw new ArgumentNullException(nameof(artifact));
        }

        ThrowIfDisposed();
        ValidateArtifact(artifact, DecodeChunkName(chunkName));
    }

    void ThrowIfAsyncExecutionCannotStart(
        ReadOnlySpan<char> chunkName,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (cancellationToken.IsCancellationRequested)
        {
            throw new LuauExecutionCanceledException(DecodeChunkName(chunkName), cancellationToken);
        }
    }

    /// <summary>
    /// Compiles and executes UTF-16 source synchronously. Dispose the returned
    /// scope and dispose any returned child thread wrappers separately.
    /// </summary>
    public LuauResultScope DoString(
        ReadOnlySpan<char> source,
        ReadOnlySpan<char> chunkName = default,
        LuauCompileOptions? options = null,
        LuauExecutionOptions? executionOptions = null)
    {
        using var operation = BeginOperation(chunkName, executionOptions, default, isAsync: false);
        CompileAndLoadString(this, source, chunkName, options);
        return ScriptRunner.Run(operation, this, 0);
    }

    /// <summary>
    /// Compiles and executes UTF-16 source synchronously into caller-owned
    /// storage. The caller owns and must dispose any reference wrappers written
    /// to <paramref name="destination"/>. Slots that receive results must not
    /// already contain managed reference wrappers.
    /// </summary>
    public int DoStringInto(
        ReadOnlySpan<char> source,
        Span<LuauValue> destination,
        ReadOnlySpan<char> chunkName = default,
        LuauCompileOptions? options = null,
        LuauExecutionOptions? executionOptions = null)
    {
        using var operation = BeginOperation(chunkName, executionOptions, default, isAsync: false);
        CompileAndLoadString(this, source, chunkName, options);
        return ScriptRunner.Run(operation, this, 0, destination);
    }

    /// <summary>
    /// Asynchronously compiles and executes string source into caller-owned
    /// storage. The caller owns reference wrappers written to the destination.
    /// Slots that receive results must not already contain managed reference wrappers.
    /// </summary>
    public ValueTask<int> DoStringIntoAsync(
        string source,
        Memory<LuauValue> destination,
        string chunkName = "",
        LuauCompileOptions? options = null,
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null)
    {
        return DoStringIntoAsync(
            source.AsMemory(),
            destination,
            chunkName.AsMemory(),
            options,
            cancellationToken,
            executionOptions);
    }

    /// <summary>
    /// Asynchronously compiles and executes UTF-16 source into caller-owned
    /// storage. The caller owns reference wrappers written to the destination.
    /// Slots that receive results must not already contain managed reference wrappers.
    /// </summary>
    public async ValueTask<int> DoStringIntoAsync(
        ReadOnlyMemory<char> source,
        Memory<LuauValue> destination,
        ReadOnlyMemory<char> chunkName = default,
        LuauCompileOptions? options = null,
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null)
    {
        using var operation = BeginOperation(chunkName.Span, executionOptions, cancellationToken, isAsync: true);
        CompileAndLoadString(this, source.Span, chunkName.Span, options);
        return await ScriptRunner.RunAsync(operation, this, 0, destination).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously compiles and executes string source. Dispose the returned
    /// scope to release its scope-owned results; dispose child threads separately.
    /// </summary>
    public ValueTask<LuauResultScope> DoStringAsync(
        string source,
        string chunkName = "",
        LuauCompileOptions? options = null,
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null)
    {
        return DoStringAsync(
            source.AsMemory(),
            chunkName.AsMemory(),
            options,
            cancellationToken,
            executionOptions);
    }

    /// <summary>
    /// Asynchronously compiles and executes UTF-16 source. Dispose the returned
    /// scope to release its scope-owned results; dispose child threads separately.
    /// </summary>
    public async ValueTask<LuauResultScope> DoStringAsync(
        ReadOnlyMemory<char> source,
        ReadOnlyMemory<char> chunkName = default,
        LuauCompileOptions? options = null,
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null)
    {
        using var operation = BeginOperation(chunkName.Span, executionOptions, cancellationToken, isAsync: true);
        CompileAndLoadString(this, source.Span, chunkName.Span, options);
        return await ScriptRunner.RunAsync(operation, this, 0).ConfigureAwait(false);
    }

    /// <summary>
    /// Compiles and executes UTF-8 source synchronously. Dispose the returned
    /// scope and dispose any returned child thread wrappers separately.
    /// </summary>
    public LuauResultScope DoString(
        ReadOnlySpan<byte> utf8Source,
        ReadOnlySpan<byte> utf8ChunkName = default,
        LuauCompileOptions? options = null,
        LuauExecutionOptions? executionOptions = null)
    {
        using var operation = BeginOperation(utf8ChunkName, executionOptions, default, isAsync: false);
        CompileAndLoadString(this, utf8Source, utf8ChunkName, options);
        return ScriptRunner.Run(operation, this, 0);
    }

    /// <summary>
    /// Compiles and executes UTF-8 source synchronously into caller-owned
    /// storage. The caller owns and must dispose any reference wrappers written
    /// to <paramref name="destination"/>. Slots that receive results must not
    /// already contain managed reference wrappers.
    /// </summary>
    public int DoStringInto(
        ReadOnlySpan<byte> utf8Source,
        Span<LuauValue> destination,
        ReadOnlySpan<byte> utf8ChunkName = default,
        LuauCompileOptions? options = null,
        LuauExecutionOptions? executionOptions = null)
    {
        using var operation = BeginOperation(utf8ChunkName, executionOptions, default, isAsync: false);
        CompileAndLoadString(this, utf8Source, utf8ChunkName, options);
        return ScriptRunner.Run(operation, this, 0, destination);
    }

    /// <summary>
    /// Asynchronously compiles and executes UTF-8 source into caller-owned
    /// storage. The caller owns reference wrappers written to the destination.
    /// Slots that receive results must not already contain managed reference wrappers.
    /// </summary>
    public async ValueTask<int> DoStringIntoAsync(
        ReadOnlyMemory<byte> utf8Source,
        Memory<LuauValue> destination,
        ReadOnlyMemory<byte> utf8ChunkName = default,
        LuauCompileOptions? options = null,
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null)
    {
        using var operation = BeginOperation(utf8ChunkName.Span, executionOptions, cancellationToken, isAsync: true);
        CompileAndLoadString(this, utf8Source.Span, utf8ChunkName.Span, options);
        return await ScriptRunner.RunAsync(operation, this, 0, destination).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously compiles and executes UTF-8 source. Dispose the returned
    /// scope to release its scope-owned results; dispose child threads separately.
    /// </summary>
    public async ValueTask<LuauResultScope> DoStringAsync(
        ReadOnlyMemory<byte> utf8Source,
        ReadOnlyMemory<byte> utf8ChunkName = default,
        LuauCompileOptions? options = null,
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null)
    {
        using var operation = BeginOperation(utf8ChunkName.Span, executionOptions, cancellationToken, isAsync: true);
        CompileAndLoadString(this, utf8Source.Span, utf8ChunkName.Span, options);
        return await ScriptRunner.RunAsync(operation, this, 0).ConfigureAwait(false);
    }

    internal unsafe LuauResultScope DoStringForRequire(
        ReadOnlySpan<byte> utf8Source,
        ReadOnlySpan<byte> utf8ChunkName,
        LuauCompileOptions? options)
    {
        ThrowIfDisposed();
        using var access = EnterNativeAccess();
        var baseTop = GetModuleCallBase(DecodeChunkName(utf8ChunkName));
        CompileAndLoadString(this, utf8Source, utf8ChunkName, options);
        return ExecuteLoadedForRequire(baseTop, DecodeChunkName(utf8ChunkName));
    }

    internal unsafe LuauResultScope DoCompilerOutputForRequire(
        LuauCompilerOutput output,
        ReadOnlySpan<byte> utf8ChunkName)
    {
        if (output == null)
        {
            throw new ArgumentNullException(nameof(output));
        }

        ThrowIfDisposed();
        ValidateCompilerOutput(output, DecodeChunkName(utf8ChunkName));
        using var access = EnterNativeAccess();
        var baseTop = GetModuleCallBase(DecodeChunkName(utf8ChunkName));
        LoadAcceptedBytecodeInternal(output.Bytecode, utf8ChunkName);
        return ExecuteLoadedForRequire(baseTop, DecodeChunkName(utf8ChunkName));
    }

    internal unsafe LuauResultScope DoVerifiedBytecodeForRequire(
        LuauBytecodeArtifact artifact,
        ReadOnlySpan<byte> utf8ChunkName)
    {
        if (artifact == null)
        {
            throw new ArgumentNullException(nameof(artifact));
        }

        ThrowIfDisposed();
        ValidateArtifact(artifact, DecodeChunkName(utf8ChunkName));
        using var access = EnterNativeAccess();
        var baseTop = GetModuleCallBase(DecodeChunkName(utf8ChunkName));
        LoadAcceptedBytecodeInternal(artifact.Bytecode, utf8ChunkName);
        return ExecuteLoadedForRequire(baseTop, DecodeChunkName(utf8ChunkName));
    }

    /// <summary>
    /// The stack base of a module's protected call, read once the thread is
    /// known to be able to run one. A module may run on a thread that is
    /// itself running, inside the operation that called <c>require</c>, but
    /// never on one that is suspended. The caller holds the native gate.
    /// </summary>
    unsafe int GetModuleCallBase(string? chunkName)
    {
        ThrowIfSuspendedUnderNativeAccess("execute a module", chunkName, includeUnstartedCoroutine: false);
        return luau_host_stack_get_top(l);
    }

    unsafe LuauResultScope ExecuteLoadedForRequire(int baseTop, string? chunkName)
    {
        using var nestedOperation = BeginNestedOperationIfNeeded(chunkName);
        using var stack = new LuauStackBoundary(this, baseTop);

        LuauNativeProtection.Prepare(context);
        var status = luau_host_pcall(l, 0, -1, 0);
        LuauNativeProtection.ThrowIfFailed(
            this,
            l,
            status,
            "execute a module",
            chunkName);

        var resultCount = luau_host_stack_get_top(l) - baseTop;
        var resultLimit = nestedOperation.Operation.Options.MaxResultCount;
        if (resultLimit is { } limit && resultCount > limit)
        {
            throw new LuauResultLimitException(chunkName, resultCount, limit);
        }

        // Reading a reference result needs one free slot; a module can return
        // more values than a host frame holds.
        if (resultCount > 0 && LuauNativeProtection.IsFrameFull(context, l))
        {
            throw new LuauStackLimitException(chunkName, "return its results");
        }

        var results = new LuauValue[resultCount];
        try
        {
            for (var i = resultCount - 1; i >= 0; i--)
            {
                results[i] = Pop();
            }
        }
        catch
        {
            for (var index = results.Length - 1; index >= 0; index--)
            {
                results[index].DisposeUnpublishedReference();
                results[index] = default;
            }
            throw;
        }

        stack.Complete();
        return new LuauResultScope(results);
    }

    static void CompileAndLoadString(
        LuauState state,
        ReadOnlySpan<byte> utf8Source,
        ReadOnlySpan<byte> utf8ChunkName,
        LuauCompileOptions? options)
    {
        var chunkName = DecodeChunkName(utf8ChunkName);
        state.ValidateSourceSize(utf8Source.Length, chunkName);
        ThrowIfCompilationStopped(state);
        using var writer = new ArrayPoolBufferWriter(512);
        LuauCompiler.Compile(
            writer,
            utf8Source,
            options,
            LuauNativeProtection.AbiVerifier,
            state.Options.MaxBytecodeBytes,
            chunkName);
        ThrowIfCompilationStopped(state);
        state.LoadAcceptedBytecodeInternal(
            writer.WrittenSpan,
            utf8ChunkName,
            chunkName,
            allowOversizedDiagnostic: true);
    }

    static void CompileAndLoadString(
        LuauState state,
        ReadOnlySpan<char> source,
        ReadOnlySpan<char> chunkName,
        LuauCompileOptions? options)
    {
        var sourceByteCount = Encoding.UTF8.GetByteCount(source);
        state.ValidateSourceSize(sourceByteCount, DecodeChunkName(chunkName));
        using var sourceUtf8 = new Utf8BufferScope(source, sourceByteCount);
        using var chunkUtf8 = new Utf8BufferScope(chunkName);
        CompileAndLoadString(state, sourceUtf8.Bytes, chunkUtf8.Bytes, options);
    }

    static void ThrowIfCompilationStopped(LuauState state)
    {
        var operation = state.Context.GetActiveOperation();
        if (operation?.GetHardStopException() is { } exception)
        {
            throw exception;
        }
    }

    /// <summary>
    /// Resumes this child coroutine with caller-owned arguments. The returned
    /// scope owns its disposable wrapper results and must be disposed. Returned
    /// child threads are caller-managed cached wrappers and must be disposed
    /// separately. Argument storage is never reused as a result destination.
    /// Argument preparation failures preserve the stack and prior lifecycle
    /// across all Resume APIs, allowing a retry with valid arguments.
    /// </summary>
    public LuauResultScope Resume(
        ReadOnlySpan<LuauValue> arguments = default,
        LuauExecutionOptions? executionOptions = null)
    {
        EnsureCoroutine();
        var hasFunction = HasInitialCoroutineFunction();
        using var operation = BeginOperation((string?)null, executionOptions, default, isAsync: false, resumesThread: true);
        PushResumeArguments(operation, arguments);
        return ScriptRunner.Run(operation, this, arguments.Length, hasFunction);
    }

    /// <summary>
    /// Resumes this child coroutine into a caller-owned result span. The caller
    /// owns and must dispose any reference wrappers written to the span. Slots
    /// that receive results must not already contain managed reference wrappers.
    /// </summary>
    public int ResumeInto(
        Span<LuauValue> destination,
        LuauExecutionOptions? executionOptions = null)
    {
        return ResumeInto(default, destination, executionOptions);
    }

    /// <summary>
    /// Resumes this child coroutine with arguments and writes results into a
    /// distinct caller-owned span. The caller owns and must dispose any
    /// reference wrappers written to the span. Slots that receive results must
    /// not already contain managed reference wrappers.
    /// </summary>
    public int ResumeInto(
        ReadOnlySpan<LuauValue> arguments,
        Span<LuauValue> destination,
        LuauExecutionOptions? executionOptions = null)
    {
        EnsureCoroutine();
        var hasFunction = HasInitialCoroutineFunction();
        using var operation = BeginOperation((string?)null, executionOptions, default, isAsync: false, resumesThread: true);
        PushResumeArguments(operation, arguments);
        return ScriptRunner.Run(operation, this, arguments.Length, destination, hasFunction);
    }

    /// <summary>
    /// Asynchronously resumes this child coroutine. Dispose the returned scope;
    /// returned child thread wrappers are caller-managed and disposed separately.
    /// </summary>
    public async ValueTask<LuauResultScope> ResumeAsync(
        ReadOnlyMemory<LuauValue> arguments = default,
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null)
    {
        EnsureCoroutine();
        var hasFunction = HasInitialCoroutineFunction();
        using var operation = BeginOperation((string?)null, executionOptions, cancellationToken, isAsync: true, resumesThread: true);
        PushResumeArguments(operation, arguments.Span);

        return await ScriptRunner.RunAsync(operation, this, arguments.Length, hasFunction).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously resumes into caller-owned result memory. The caller owns
    /// reference wrappers written to the destination. Slots that receive
    /// results must not already contain managed reference wrappers.
    /// </summary>
    public ValueTask<int> ResumeIntoAsync(
        Memory<LuauValue> destination,
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null)
    {
        return ResumeIntoAsync(
            ReadOnlyMemory<LuauValue>.Empty,
            destination,
            cancellationToken,
            executionOptions);
    }

    /// <summary>
    /// Asynchronously resumes with arguments and writes results into distinct
    /// caller-owned memory. The caller owns and must dispose reference wrappers
    /// written to the destination. Slots that receive results must not already
    /// contain managed reference wrappers.
    /// </summary>
    public async ValueTask<int> ResumeIntoAsync(
        ReadOnlyMemory<LuauValue> arguments,
        Memory<LuauValue> destination,
        CancellationToken cancellationToken = default,
        LuauExecutionOptions? executionOptions = null)
    {
        EnsureCoroutine();
        var hasFunction = HasInitialCoroutineFunction();
        using var operation = BeginOperation((string?)null, executionOptions, cancellationToken, isAsync: true, resumesThread: true);
        PushResumeArguments(operation, arguments.Span);

        return await ScriptRunner.RunAsync(
            operation,
            this,
            arguments.Length,
            destination,
            hasFunction).ConfigureAwait(false);
    }

    ScriptOperation BeginOperation(
        ReadOnlySpan<char> chunkName,
        LuauExecutionOptions? options,
        CancellationToken cancellationToken,
        bool isAsync)
    {
        return BeginOperation(DecodeChunkName(chunkName), options, cancellationToken, isAsync);
    }

    ScriptOperation BeginOperation(
        ReadOnlySpan<byte> utf8ChunkName,
        LuauExecutionOptions? options,
        CancellationToken cancellationToken,
        bool isAsync)
    {
        return BeginOperation(DecodeChunkName(utf8ChunkName), options, cancellationToken, isAsync);
    }

    internal ScriptOperation BeginOperation(
        string? chunkName,
        LuauExecutionOptions? options,
        CancellationToken cancellationToken,
        bool isAsync,
        ScriptOperationMode mode = ScriptOperationMode.TopLevelResume,
        bool requireZeroResults = false,
        bool resumesThread = false)
    {
        ThrowIfDisposed();
        if (cancellationToken.IsCancellationRequested)
        {
            throw new LuauExecutionCanceledException(chunkName, cancellationToken);
        }

        return context.BeginOperation(
            this,
            chunkName,
            options,
            cancellationToken,
            isAsync,
            mode,
            requireZeroResults,
            resumesThread);
    }

    /// <summary>
    /// Rejects work that would start a new call on this thread while it holds
    /// a pending continuation. Native resume of a yielded or interrupted
    /// thread continues that continuation and drops the new callee, and a
    /// protected call cannot run on it. With
    /// <paramref name="includeUnstartedCoroutine"/>, a child whose coroutine
    /// function has not started is rejected too: a new call would run above
    /// that function, and the reset after a failed call would discard it. The
    /// caller holds the native gate and has pushed nothing for its call.
    /// </summary>
    internal unsafe void ThrowIfSuspendedUnderNativeAccess(
        string action,
        string? chunkName,
        bool includeUnstartedCoroutine)
    {
        var status = luau_host_thread_status(l);
        if (status is LuauHostStatus.Yielded or LuauHostStatus.Break ||
            (includeUnstartedCoroutine &&
                status == LuauHostStatus.Ok &&
                !isMainThread &&
                luau_host_stack_get_top(l) > 0))
        {
            ThrowSuspendedThread(action, chunkName);
        }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    static void ThrowSuspendedThread(string action, string? chunkName)
    {
        throw new InvalidOperationException(LuauDiagnosticMessages.WithChunk(
            $"Cannot {action} on a suspended Luau thread. The thread has yielded, or holds a coroutine " +
            "function that has not started; resume it until it finishes, or use another thread.",
            chunkName));
    }

    // A host that executes the same chunk every frame passes the same name
    // every time, and one execution decodes it several times. Strings are
    // immutable, so the last decoded name of each encoding is reused.
    [ThreadStatic] static string? lastDecodedChunkName;
    [ThreadStatic] static byte[]? lastUtf8ChunkName;
    [ThreadStatic] static string? lastDecodedUtf8ChunkName;

    internal static string? DecodeChunkName(ReadOnlySpan<char> chunkName)
    {
        if (chunkName.IsEmpty)
        {
            return null;
        }

        var cached = lastDecodedChunkName;
        if (cached != null && chunkName.SequenceEqual(cached.AsSpan()))
        {
            return cached;
        }

        var decoded = new string(chunkName);
        if (chunkName.Length <= 256)
        {
            lastDecodedChunkName = decoded;
        }

        return decoded;
    }

    internal static string? DecodeChunkName(ReadOnlySpan<byte> utf8ChunkName)
    {
        if (utf8ChunkName.IsEmpty)
        {
            return null;
        }

        var cachedBytes = lastUtf8ChunkName;
        if (cachedBytes != null && utf8ChunkName.SequenceEqual(cachedBytes))
        {
            return lastDecodedUtf8ChunkName;
        }

        var decoded = Encoding.UTF8.GetString(utf8ChunkName);
        // Bounded in both encodings: chunk names are diagnostics, and a long
        // one is not worth keeping a copy of.
        if (utf8ChunkName.Length <= 256)
        {
            lastUtf8ChunkName = null;
            lastDecodedUtf8ChunkName = decoded;
            lastUtf8ChunkName = utf8ChunkName.ToArray();
        }

        return decoded;
    }

    void EnsureCoroutine()
    {
        ThrowIfDisposed();
        if (IsMainThread)
        {
            ThrowHelper.ThrowInvalidOperationException(
                "Resume requires a child Luau coroutine; the root state cannot be resumed.");
        }
    }

    unsafe void PushResumeArguments(ScriptOperation operation, ReadOnlySpan<LuauValue> arguments)
    {
        if (arguments.IsEmpty)
        {
            return;
        }

        using var access = context.EnterOperationNativeAccess(operation);
        var baseTop = luau_host_stack_get_top(l);
        try
        {
            for (var i = 0; i < arguments.Length; i++)
            {
                LuauOperationStack.PushArgument(this, l, arguments[i]);
            }

            LuauOperationStack.ThrowIfStoppedBeforeEntry(operation);
        }
        catch
        {
            SetTop(baseTop);
            throw;
        }
    }

    unsafe bool HasInitialCoroutineFunction()
    {
        using var access = EnterNativeAccess();
        return luau_host_thread_status(l) == LuauHostStatus.Ok && luau_host_stack_get_top(l) > 0;
    }
}
