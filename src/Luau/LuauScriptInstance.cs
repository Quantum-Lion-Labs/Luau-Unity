using System.Diagnostics.CodeAnalysis;

namespace Luau;

/// <summary>
/// Owns one sandboxed script thread and the table of functions returned by its
/// initialization chunk.
/// </summary>
public sealed class LuauScriptInstance : IDisposable
{
    readonly object lifetimeGate = new();
    readonly LuauState root;
    LuauState? thread;
    LuauTable? exports;
    Dictionary<string, LuauScriptEntrypoint>? entrypoints = new(StringComparer.Ordinal);
    int disposeState;

    LuauScriptInstance(string name, LuauState root, LuauState thread, LuauTable exports)
    {
        Name = name;
        this.root = root;
        this.thread = thread;
        this.exports = exports;
    }

    /// <summary>
    /// Creates an instance by configuring and executing one owned sandboxed
    /// child thread on the root's continuation scheduler.
    /// </summary>
    /// <param name="root">
    /// A live, caller-owned, sandboxed root. Disposing the instance does not
    /// dispose this root.
    /// </param>
    /// <param name="name">A non-empty diagnostic name for the instance.</param>
    /// <param name="loader">
    /// The host loader that executes the script and returns its result scope.
    /// </param>
    /// <param name="configureThread">
    /// Optional capability configuration performed after sandboxing and before
    /// the loader starts.
    /// </param>
    /// <param name="cancellationToken">Cancellation requested by the creating host.</param>
    /// <returns>The fully initialized script instance.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="root"/>, <paramref name="name"/>, or <paramref name="loader"/>
    /// is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="root"/> is not a root state, or <paramref name="name"/>
    /// contains no non-whitespace characters.
    /// </exception>
    /// <exception cref="LuauScriptContractException">
    /// The loader does not return exactly one table.
    /// </exception>
    public static async ValueTask<LuauScriptInstance> CreateAsync(
        LuauState root,
        string name,
        LuauScriptLoader loader,
        Action<LuauState>? configureThread = null,
        CancellationToken cancellationToken = default)
    {
        if (root == null)
        {
            throw new ArgumentNullException(nameof(root));
        }
        if (name == null)
        {
            throw new ArgumentNullException(nameof(name));
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException(
                "A script instance name must contain a non-whitespace character.",
                nameof(name));
        }
        if (loader == null)
        {
            throw new ArgumentNullException(nameof(loader));
        }
        if (!root.IsMainThread)
        {
            throw new ArgumentException(
                "Script instances must be created from a root Luau state.",
                nameof(root));
        }
        if (root.IsDisposed)
        {
            throw new ObjectDisposedException(nameof(root));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var scheduler = root.Options.DefaultExecutionOptions.ContinuationScheduler;
        var scheduledCreation = await LuauContinuationDispatcher.InvokeAsync(
            scheduler,
            () => CreateOnSchedulerAsync(
                root,
                name,
                loader,
                configureThread,
                cancellationToken)).ConfigureAwait(false);
        return await scheduledCreation.ConfigureAwait(false);
    }

    /// <summary>Gets the host-provided diagnostic name.</summary>
    public string Name { get; }

    /// <summary>
    /// Gets the caller-owned VM root. This property preserves identity after
    /// instance disposal and does not transfer ownership.
    /// </summary>
    public LuauState Root => root;

    /// <summary>
    /// Gets whether this instance, its child thread, or its root has been disposed.
    /// </summary>
    public bool IsDisposed
    {
        get
        {
            if (Volatile.Read(ref disposeState) != 0 || root.IsDisposed)
            {
                return true;
            }

            var currentThread = Volatile.Read(ref thread);
            return currentThread == null || currentThread.IsDisposed;
        }
    }

    internal LuauState Thread => Volatile.Read(ref thread)
        ?? throw new ObjectDisposedException(nameof(LuauScriptInstance));

    internal LuauTable ExportTable => Volatile.Read(ref exports)
        ?? throw new ObjectDisposedException(nameof(LuauScriptInstance));

    /// <summary>
    /// Gets or binds a required exported function.
    /// </summary>
    /// <param name="name">The non-empty raw export-table key.</param>
    /// <returns>The cached entrypoint owner.</returns>
    /// <exception cref="LuauScriptContractException">
    /// The export is missing, nil, or not a function.
    /// </exception>
    public LuauScriptEntrypoint GetRequiredEntrypoint(string name)
    {
        ValidateEntrypointName(name);
        return GetEntrypointCore(name, required: true)!;
    }

    /// <summary>
    /// Attempts to get or bind an optional exported function. A missing or nil
    /// export returns <see langword="false"/>; a present non-function is a
    /// contract failure.
    /// </summary>
    /// <param name="name">The non-empty raw export-table key.</param>
    /// <param name="entrypoint">
    /// Receives the cached entrypoint when the export is a function; otherwise
    /// <see langword="null"/>.
    /// </param>
    /// <returns>Whether the requested function was exported.</returns>
    /// <exception cref="LuauScriptContractException">
    /// The export is present, non-nil, and not a function.
    /// </exception>
    public bool TryGetEntrypoint(
        string name,
        [NotNullWhen(true)] out LuauScriptEntrypoint? entrypoint)
    {
        ValidateEntrypointName(name);
        entrypoint = GetEntrypointCore(name, required: false);
        return entrypoint != null;
    }

    LuauScriptEntrypoint? GetEntrypointCore(string name, bool required)
    {
        lock (lifetimeGate)
        {
            ThrowIfDisposedLocked();
            var currentEntrypoints = entrypoints!;
            if (currentEntrypoints.TryGetValue(name, out var cached))
            {
                return cached;
            }

            var value = exports!.RawGet(name);
            try
            {
                if (value.IsNil)
                {
                    if (required)
                    {
                        throw new LuauScriptContractException(
                            Name,
                            name,
                            "The required export is missing or nil.");
                    }

                    return null;
                }

                if (!value.TryRead<LuauFunction>(out var function))
                {
                    throw new LuauScriptContractException(
                        Name,
                        name,
                        $"The export must be a function or nil, but was {value.Type}.");
                }

                var entrypoint = new LuauScriptEntrypoint(this, name, function);
                currentEntrypoints.Add(name, entrypoint);
                value = default;
                return entrypoint;
            }
            finally
            {
                value.DisposeOwnedReference();
            }
        }
    }

    /// <summary>
    /// Invalidates and releases cached entrypoints, then the export table, then
    /// the owned child thread. The root remains caller-owned.
    /// </summary>
    public void Dispose()
    {
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    void DisposeCore()
    {
        LuauScriptEntrypoint[] entrypointSnapshot;
        LuauTable? exportTable;
        LuauState? ownedThread;

        lock (lifetimeGate)
        {
            if (Interlocked.Exchange(ref disposeState, 1) != 0)
            {
                return;
            }

            var currentEntrypoints = entrypoints;
            entrypoints = null;
            entrypointSnapshot = currentEntrypoints == null
                ? []
                : currentEntrypoints.Values.ToArray();
            currentEntrypoints?.Clear();
            exportTable = Interlocked.Exchange(ref exports, null);
            ownedThread = Interlocked.Exchange(ref thread, null);
        }

        try
        {
            for (var index = entrypointSnapshot.Length - 1; index >= 0; index--)
            {
                entrypointSnapshot[index].DisposeFromOwner();
            }
        }
        finally
        {
            try
            {
                exportTable?.Dispose();
            }
            finally
            {
                ownedThread?.Dispose();
            }
        }
    }

    static async ValueTask<LuauScriptInstance> CreateOnSchedulerAsync(
        LuauState root,
        string name,
        LuauScriptLoader loader,
        Action<LuauState>? configureThread,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LuauState? thread = null;
        LuauTable? exportTable = null;
        try
        {
            thread = root.CreateSandboxedThread();
            configureThread?.Invoke(thread);
            cancellationToken.ThrowIfCancellationRequested();

            var loadedResults = await loader(thread, cancellationToken).ConfigureAwait(false);
            using (loadedResults)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (root.IsDisposed || thread.IsDisposed)
                {
                    throw new ObjectDisposedException(nameof(root));
                }

                var results = loadedResults
                    ?? throw new LuauScriptContractException(
                        name,
                        entrypointName: null,
                        "The loader returned a null result scope instead of exactly one export table.");
                if (results.Count != 1)
                {
                    throw new LuauScriptContractException(
                        name,
                        entrypointName: null,
                        $"The script must return exactly one export table, but returned {results.Count} values.");
                }

                var result = results[0];
                if (result.Type != LuauType.Table)
                {
                    throw new LuauScriptContractException(
                        name,
                        entrypointName: null,
                        $"The script must return a table, but returned {result.Type}.");
                }

                var candidateExportTable = result.Read<LuauTable>();
                if (!ReferenceEquals(candidateExportTable.OriginatingState, thread))
                {
                    throw CreateForeignExportTableException(name);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (root.IsDisposed || thread.IsDisposed)
                {
                    throw new ObjectDisposedException(nameof(root));
                }

                results.Detach(0);
                exportTable = candidateExportTable;
                var instance = new LuauScriptInstance(name, root, thread, exportTable);
                thread = null;
                exportTable = null;
                return instance;
            }
        }
        catch
        {
            exportTable?.Dispose();
            thread?.Dispose();
            throw;
        }
    }

    static LuauScriptContractException CreateForeignExportTableException(
        string instanceName,
        Exception? innerException = null)
    {
        return new LuauScriptContractException(
            instanceName,
            entrypointName: null,
            "The export table must be produced by the script instance's owned sandboxed thread " +
            "and belong to its root Luau state.",
            innerException);
    }

    static void ValidateEntrypointName(string name)
    {
        if (name == null)
        {
            throw new ArgumentNullException(nameof(name));
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException(
                "An entrypoint name must contain a non-whitespace character.",
                nameof(name));
        }
    }

    void ThrowIfDisposedLocked()
    {
        if (disposeState != 0 || root.IsDisposed || thread == null || thread.IsDisposed)
        {
            throw new ObjectDisposedException(nameof(LuauScriptInstance));
        }
    }

    /// <summary>Releases an abandoned instance as a final fallback.</summary>
    ~LuauScriptInstance()
    {
        try
        {
            DisposeCore();
        }
        catch
        {
            // Finalizers must not surface cleanup failures.
        }
    }
}
