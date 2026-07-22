namespace Luau.Tests;

public sealed class LuauScriptInstanceTests
{
    [Theory]
    [InlineData("return")]
    [InlineData("return {}, {}")]
    [InlineData("return nil")]
    [InlineData("return 42")]
    public async Task CreationRequiresExactlyOneExportTableAndCleansUp(string source)
    {
        using var root = CreateSandboxedRoot();
        var cachedStateCount = root.Context.CachedStateCount;

        var exception = await Assert.ThrowsAsync<LuauScriptContractException>(() =>
            LuauScriptInstance.CreateAsync(
                    root,
                    "invalid-script",
                    (thread, cancellationToken) => thread.DoStringAsync(
                        source,
                        "@instances/invalid.luau",
                        cancellationToken: cancellationToken))
                .AsTask());

        Assert.Equal("invalid-script", exception.InstanceName);
        Assert.Null(exception.EntrypointName);
        Assert.Equal("invalid-script", exception.ChunkName);
        Assert.Equal(cachedStateCount, root.Context.CachedStateCount);

        using var recoveryThread = root.CreateSandboxedThread();
        using var recovery = recoveryThread.DoString("return 6 * 7");
        Assert.Equal(42, Assert.Single(recovery).Read<int>());
    }

    [Fact]
    public async Task ConfigurationRunsBeforeLoaderAndSuccessfulInstanceOwnsOneChild()
    {
        using var root = CreateSandboxedRoot();
        var configurationRan = false;
        LuauState? loaderThread = null;

        var instance = await LuauScriptInstance.CreateAsync(
            root,
            "configured",
            (thread, cancellationToken) =>
            {
                Assert.True(configurationRan);
                loaderThread = thread;
                return thread.DoStringAsync(
                    "return { read = function() return configuredValue end }",
                    "@instances/configured.luau",
                    cancellationToken: cancellationToken);
            },
            thread =>
            {
                configurationRan = true;
                thread["configuredValue"] = 73;
            });

        Assert.True(configurationRan);
        Assert.Same(loaderThread, instance.Thread);
        Assert.Same(root, instance.Root);
        Assert.Equal("configured", instance.Name);
        Assert.Equal(2, root.Context.CachedStateCount);
        using (var result = instance.GetRequiredEntrypoint("read").Invoke())
        {
            Assert.Equal(73, Assert.Single(result).Read<int>());
        }

        var exportTable = instance.ExportTable;
        var ownedThread = instance.Thread;
        instance.Dispose();
        instance.Dispose();

        Assert.True(instance.IsDisposed);
        Assert.True(exportTable.IsDisposed);
        Assert.True(ownedThread.IsDisposed);
        Assert.False(root.IsDisposed);
        Assert.Equal(1, root.Context.CachedStateCount);
    }

    [Fact]
    public async Task CreationDispatchesConfigurationAndLoaderToRootScheduler()
    {
        var scheduler = new InlineOwnerScheduler();
        using var root = scheduler.Run(() => CreateSandboxedRoot(scheduler));
        var configuredWithAccess = false;
        var loadedWithAccess = false;

        using var instance = await LuauScriptInstance.CreateAsync(
            root,
            "scheduled",
            (thread, _) =>
            {
                loadedWithAccess = scheduler.CheckAccess();
                return new ValueTask<LuauResultScope>(thread.DoString("return {}"));
            },
            _ => configuredWithAccess = scheduler.CheckAccess());

        Assert.True(configuredWithAccess);
        Assert.True(loadedWithAccess);
        Assert.Equal(1, scheduler.PostCount);
    }

    [Fact]
    public async Task CancellationAfterLoaderCompletionDoesNotPublishInstance()
    {
        using var root = CreateSandboxedRoot();
        using var cancellationSource = new CancellationTokenSource();
        LuauTable? returnedTable = null;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LuauScriptInstance.CreateAsync(
                    root,
                    "canceled",
                    (thread, _) =>
                    {
                        var results = thread.DoString("return {}");
                        returnedTable = Assert.Single(results).Read<LuauTable>();
                        cancellationSource.Cancel();
                        return new ValueTask<LuauResultScope>(results);
                    },
                    cancellationToken: cancellationSource.Token)
                .AsTask());

        Assert.NotNull(returnedTable);
        Assert.True(returnedTable.IsDisposed);
        Assert.Equal(1, root.Context.CachedStateCount);
    }

    [Fact]
    public async Task PreCanceledCreationDoesNotConfigureLoadOrCreateChild()
    {
        using var root = CreateSandboxedRoot();
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var configured = false;
        var loaded = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LuauScriptInstance.CreateAsync(
                    root,
                    "pre-canceled",
                    (thread, _) =>
                    {
                        loaded = true;
                        return new ValueTask<LuauResultScope>(thread.DoString("return {}"));
                    },
                    _ => configured = true,
                    cancellationSource.Token)
                .AsTask());

        Assert.False(configured);
        Assert.False(loaded);
        Assert.Equal(1, root.Context.CachedStateCount);
    }

    [Fact]
    public async Task CreationRejectsExportTableFromAnotherRootAndCleansBothDomains()
    {
        using var root = CreateSandboxedRoot();
        using var foreignRoot = CreateSandboxedRoot();
        LuauTable? foreignTable = null;

        var exception = await Assert.ThrowsAsync<LuauScriptContractException>(() =>
            LuauScriptInstance.CreateAsync(
                    root,
                    "foreign-export",
                    (_, _) =>
                    {
                        var foreignResults = foreignRoot.DoString("return {}");
                        foreignTable = Assert.Single(foreignResults).Read<LuauTable>();
                        return new ValueTask<LuauResultScope>(foreignResults);
                    })
                .AsTask());

        Assert.Equal("foreign-export", exception.InstanceName);
        Assert.Null(exception.EntrypointName);
        Assert.Equal("foreign-export", exception.ChunkName);
        Assert.Contains("root Luau state", exception.Message);
        Assert.NotNull(foreignTable);
        Assert.True(foreignTable.IsDisposed);
        Assert.Equal(1, root.Context.CachedStateCount);
        Assert.Equal(1, foreignRoot.Context.CachedStateCount);

        using var rootRecoveryThread = root.CreateSandboxedThread();
        using var rootRecovery = rootRecoveryThread.DoString("return 40 + 2");
        using var foreignRecovery = foreignRoot.DoString("return 6 * 7");
        Assert.Equal(42, Assert.Single(rootRecovery).Read<int>());
        Assert.Equal(42, Assert.Single(foreignRecovery).Read<int>());
    }

    [Fact]
    public async Task CreationRejectsExportTableFromAnotherChildInTheSameRoot()
    {
        using var root = CreateSandboxedRoot();
        using var otherThread = root.CreateSandboxedThread();
        LuauTable? otherTable = null;

        var exception = await Assert.ThrowsAsync<LuauScriptContractException>(() =>
            LuauScriptInstance.CreateAsync(
                    root,
                    "wrong-child-export",
                    (_, _) =>
                    {
                        var otherResults = otherThread.DoString("return {}");
                        otherTable = Assert.Single(otherResults).Read<LuauTable>();
                        return new ValueTask<LuauResultScope>(otherResults);
                    })
                .AsTask());

        Assert.Equal("wrong-child-export", exception.InstanceName);
        Assert.Contains("owned sandboxed thread", exception.Message);
        Assert.NotNull(otherTable);
        Assert.True(otherTable.IsDisposed);
        Assert.Equal(2, root.Context.CachedStateCount);

        using var otherRecovery = otherThread.DoString("return 6 * 7");
        Assert.Equal(42, Assert.Single(otherRecovery).Read<int>());
    }

    [Fact]
    public async Task BindingSupportsRequiredOptionalCachingAndTypeContracts()
    {
        using var root = CreateSandboxedRoot();
        using var instance = await CreateInstanceAsync(
            root,
            "bindings",
            "return { required = function(value) return value end, optional = nil, wrong = 9 }");

        var required = instance.GetRequiredEntrypoint("required");
        Assert.Same(required, instance.GetRequiredEntrypoint("required"));
        Assert.True(instance.TryGetEntrypoint("required", out var optionalRequired));
        Assert.Same(required, optionalRequired);
        Assert.Equal("required", required.Name);
        Assert.Equal("bindings:required", required.OperationLabel);
        Assert.Same(instance, required.Instance);
        Assert.Same(root, required.Root);

        Assert.False(instance.TryGetEntrypoint("optional", out var optional));
        Assert.Null(optional);
        Assert.False(instance.TryGetEntrypoint("missing", out var missing));
        Assert.Null(missing);

        var requiredException = Assert.Throws<LuauScriptContractException>(
            () => instance.GetRequiredEntrypoint("missing"));
        Assert.Equal("bindings", requiredException.InstanceName);
        Assert.Equal("missing", requiredException.EntrypointName);
        Assert.Equal("bindings:missing", requiredException.ChunkName);

        var typeException = Assert.Throws<LuauScriptContractException>(
            () => instance.TryGetEntrypoint("wrong", out _));
        Assert.Equal("wrong", typeException.EntrypointName);
        Assert.Contains("Number", typeException.Message);
    }

    [Fact]
    public async Task EntrypointsExposeZeroSingleAndSequenceInvocationForms()
    {
        using var root = CreateSandboxedRoot();
        using var instance = await CreateInstanceAsync(
            root,
            "invocations",
            "return { echo = function(...) return ... end }");
        var echo = instance.GetRequiredEntrypoint("echo");

        using (var zero = echo.Invoke())
        {
            Assert.Empty(zero);
        }
        using (var single = echo.Invoke(17))
        {
            Assert.Equal(17, Assert.Single(single).Read<int>());
        }
        using (var sequence = echo.Invoke(new LuauValue[] { 4, 5 }))
        {
            Assert.Equal([4, 5], sequence.Select(value => value.Read<int>()));
        }
        using (var asyncSingle = await echo.InvokeAsync((LuauValue)23))
        {
            Assert.Equal(23, Assert.Single(asyncSingle).Read<int>());
        }
        using (var asyncSequence = await echo.InvokeAsync(
            new LuauValue[] { 8, 9 }.AsMemory()))
        {
            Assert.Equal([8, 9], asyncSequence.Select(value => value.Read<int>()));
        }

        var destination = new LuauValue[2];
        Assert.Equal(2, echo.InvokeInto(new LuauValue[] { 11, 12 }, destination));
        Assert.Equal([11, 12], destination.Select(value => value.Read<int>()));

        Array.Clear(destination);
        Assert.Equal(1, await echo.InvokeIntoAsync((LuauValue)31, destination));
        Assert.Equal(31, destination[0].Read<int>());
    }

    [Fact]
    public async Task RuntimeAndBudgetFailuresRetainStableEntrypointLabelAndRecover()
    {
        using var root = CreateSandboxedRoot();
        using var instance = await CreateInstanceAsync(
            root,
            "labeled",
            """
            return {
                explode = function() error("entrypoint failure") end,
                spin = function() while true do end end,
                recover = function() return 42 end,
            }
            """);

        var runtimeException = Assert.Throws<LuauException>(
            () => instance.GetRequiredEntrypoint("explode").InvokeVoid());
        Assert.Equal("labeled:explode", runtimeException.ChunkName);
        Assert.Contains("labeled:explode", runtimeException.Message);

        var budgetException = Assert.Throws<LuauExecutionBudgetException>(() =>
            instance.GetRequiredEntrypoint("spin").InvokeVoid(
                LuauExecutionOptions.Default with
                {
                    WallClockLimit = TimeSpan.FromMilliseconds(100),
                    InterruptCountLimit = 1,
                }));
        Assert.Equal("labeled:spin", budgetException.ChunkName);
        Assert.Contains("labeled:spin", budgetException.Message);

        using var recovery = instance.GetRequiredEntrypoint("recover").Invoke();
        Assert.Equal(42, Assert.Single(recovery).Read<int>());
    }

    [Fact]
    public async Task InstanceDisposalInvalidatesCachedEntrypointsBeforeKeepingRootReusable()
    {
        using var root = CreateSandboxedRoot();
        var instance = await CreateInstanceAsync(
            root,
            "disposed",
            "return { run = function() return 1 end }");
        var entrypoint = instance.GetRequiredEntrypoint("run");
        var function = entrypoint.Function;
        var exportTable = instance.ExportTable;
        var thread = instance.Thread;

        instance.Dispose();

        Assert.True(instance.IsDisposed);
        Assert.True(entrypoint.IsDisposed);
        Assert.True(function.IsDisposed);
        Assert.True(exportTable.IsDisposed);
        Assert.True(thread.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => instance.GetRequiredEntrypoint("run"));
        Assert.Throws<ObjectDisposedException>(() => entrypoint.Invoke());
        Assert.False(root.IsDisposed);

        using var recovery = root.DoString("return 40 + 2");
        Assert.Equal(42, Assert.Single(recovery).Read<int>());
    }

    [Fact]
    public async Task CreationValidatesRootNameAndSandboxContract()
    {
        using var root = LuauState.Create();
        LuauScriptLoader loader = (thread, _) =>
            new ValueTask<LuauResultScope>(thread.DoString("return {}"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            LuauScriptInstance.CreateAsync(root, " ", loader).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LuauScriptInstance.CreateAsync(root, "unsandboxed", loader).AsTask());

        root.OpenBaseLibrary();
        root.SandboxRoot();
        using var child = root.CreateSandboxedThread();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            LuauScriptInstance.CreateAsync(child, "child", loader).AsTask());
    }

    static ValueTask<LuauScriptInstance> CreateInstanceAsync(
        LuauState root,
        string name,
        string source)
    {
        return LuauScriptInstance.CreateAsync(
            root,
            name,
            (thread, cancellationToken) => thread.DoStringAsync(
                source,
                $"@instances/{name}.luau",
                cancellationToken: cancellationToken));
    }

    static LuauState CreateSandboxedRoot(ILuauContinuationScheduler? scheduler = null)
    {
        var root = LuauState.Create(new LuauStateOptions
        {
            DefaultExecutionOptions = LuauExecutionOptions.Default with
            {
                ContinuationScheduler = scheduler,
            },
        });
        try
        {
            root.OpenBaseLibrary();
            root.SandboxRoot();
            return root;
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    sealed class InlineOwnerScheduler : ILuauContinuationScheduler
    {
        int hasAccess;
        int postCount;

        public int PostCount => Volatile.Read(ref postCount);

        public bool CheckAccess() => Volatile.Read(ref hasAccess) != 0;

        public void Post(Action continuation)
        {
            if (continuation == null)
            {
                throw new ArgumentNullException(nameof(continuation));
            }

            Interlocked.Increment(ref postCount);
            Run(continuation);
        }

        public T Run<T>(Func<T> action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            Interlocked.Increment(ref hasAccess);
            try
            {
                return action();
            }
            finally
            {
                Interlocked.Decrement(ref hasAccess);
            }
        }

        void Run(Action action)
        {
            Interlocked.Increment(ref hasAccess);
            try
            {
                action();
            }
            finally
            {
                Interlocked.Decrement(ref hasAccess);
            }
        }
    }
}
