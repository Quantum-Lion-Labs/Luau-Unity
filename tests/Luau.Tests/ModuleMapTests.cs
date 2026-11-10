using System.Text;

namespace Luau.Tests;

public sealed class ModuleMapTests
{
    [Fact]
    public void IdenticalPathsInDifferentMapsHaveIndependentCacheNamespaces()
    {
        using var root = TestBudgets.CreateRoot();
        root.OpenBaseLibrary();
        var first = Map(("shared", "return 11"));
        var second = Map(("shared", "return 22"));

        root.OpenRequireLibrary(first);
        using (var result = root.DoString("return require('shared')"))
        {
            Assert.Equal(11, result.Read<int>(0));
        }
        root.OpenRequireLibrary(second);
        using (var result = root.DoString("return require('shared')"))
        {
            Assert.Equal(22, result.Read<int>(0));
        }
        root.OpenRequireLibrary(first);
        using (var result = root.DoString("return require('shared')"))
        {
            Assert.Equal(11, result.Read<int>(0));
        }
    }

    [Fact]
    public void SandboxedSiblingsDeliberatelyShareOneRootModuleInstance()
    {
        using var root = TestBudgets.CreateRoot();
        root.OpenBaseLibrary();
        root.OpenRequireLibrary(Map(("shared", "return { value = 1 }")));
        root.SandboxRoot();
        using var first = root.CreateSandboxedThread();
        using var second = root.CreateSandboxedThread();

        using (var result = first.DoString(
            "local module = require('shared'); module.value = 99; return module.value"))
        {
            Assert.Equal(99, result.Read<int>(0));
        }
        using (var result = second.DoString("return require('shared').value"))
        {
            Assert.Equal(99, result.Read<int>(0));
        }
    }

    [Fact]
    public void MapConstructionDefensivelyCopiesSourcesAndEnforcesAllAdmissionLimits()
    {
        var source = Encoding.UTF8.GetBytes("return 7");
        var map = new LuauModuleMap(new Dictionary<string, byte[]> { ["module"] = source });
        source.AsSpan().Fill((byte)'!');
        using var root = TestBudgets.CreateRoot();
        root.OpenBaseLibrary();
        root.OpenRequireLibrary(map);
        using (var result = root.DoString("return require('module')"))
        {
            Assert.Equal(7, result.Read<int>(0));
        }

        AssertLimit(
            LuauModuleLimitKind.ModuleCount,
            () => new LuauModuleMap(
                new Dictionary<string, byte[]> { ["a"] = [1], ["b"] = [2] },
                limits: new LuauModuleLimits { MaxModuleCount = 1 }));
        AssertLimit(
            LuauModuleLimitKind.SourceBytes,
            () => new LuauModuleMap(
                new Dictionary<string, byte[]> { ["a"] = [1, 2] },
                limits: new LuauModuleLimits { MaxTotalSourceBytes = 1 }));
        AssertLimit(
            LuauModuleLimitKind.ModuleIdBytes,
            () => new LuauModuleMap(
                new Dictionary<string, byte[]> { ["é"] = [1] },
                limits: new LuauModuleLimits { MaxModuleIdUtf8Bytes = 1 }));

        var exact = new LuauModuleMap(
            new Dictionary<string, byte[]> { ["é"] = [1, 2] },
            limits: new LuauModuleLimits
            {
                MaxModuleCount = 1,
                MaxTotalSourceBytes = 2,
                MaxModuleIdUtf8Bytes = 2,
            });
        Assert.Equal(1, exact.Count);
        Assert.Equal(2, exact.TotalSourceBytes);

        var exactAlias = new LuauModuleMap(
            new Dictionary<string, byte[]> { ["m"] = [1] },
            new Dictionary<string, string> { ["é"] = "m" },
            new LuauModuleLimits
            {
                MaxModuleCount = 1,
                MaxModuleIdUtf8Bytes = 2,
            });
        Assert.Equal(1, exactAlias.Count);
        AssertLimit(
            LuauModuleLimitKind.ModuleCount,
            () => new LuauModuleMap(
                new Dictionary<string, byte[]> { ["m"] = [1] },
                new Dictionary<string, string> { ["a"] = "m", ["b"] = "m" },
                new LuauModuleLimits { MaxModuleCount = 1 }));
        AssertLimit(
            LuauModuleLimitKind.ModuleIdBytes,
            () => new LuauModuleMap(
                new Dictionary<string, byte[]> { ["m"] = [1] },
                new Dictionary<string, string> { ["é"] = "m" },
                new LuauModuleLimits { MaxModuleIdUtf8Bytes = 1 }));

        // Reserve a representable overflow sentinel so even an explicitly
        // unbounded policy reports typed quota failures instead of wrapping.
        Assert.Throws<ArgumentOutOfRangeException>(() => new LuauModuleLimits
        {
            MaxTotalSourceBytes = long.MaxValue,
        });
        Assert.Throws<ArgumentOutOfRangeException>(() => new LuauModuleLimits
        {
            MaxTotalBytecodeBytes = long.MaxValue,
        });
    }

    [Fact]
    public async Task BundleCompilationEnforcesPerModuleAndAggregateBytecodeBeforePublication()
    {
        byte[] firstSource = "return 1"u8.ToArray();
        byte[] secondSource = "return 2"u8.ToArray();
        var firstOutput = LuauCompiler.Compile(firstSource);
        var secondOutput = LuauCompiler.Compile(secondSource);
        await using var service = new LuauThreadedCompilationService();

        var perModuleMap = new LuauModuleMap(
            new Dictionary<string, byte[]> { ["first"] = firstSource },
            limits: new LuauModuleLimits
            {
                MaxBytecodeBytesPerModule = firstOutput.BytecodeLength - 1,
            });
        var perModuleFailure = await Assert.ThrowsAsync<LuauModuleLimitException>(async () =>
            await perModuleMap.CompileModuleBundleAsync(service));
        Assert.Equal(LuauModuleLimitKind.BytecodeBytesPerModule, perModuleFailure.LimitKind);

        var totalMap = new LuauModuleMap(
            new Dictionary<string, byte[]> { ["first"] = firstSource, ["second"] = secondSource },
            limits: new LuauModuleLimits
            {
                MaxTotalBytecodeBytes =
                    firstOutput.BytecodeLength + secondOutput.BytecodeLength - 1L,
            });
        var totalFailure = await Assert.ThrowsAsync<LuauModuleLimitException>(async () =>
            await totalMap.CompileModuleBundleAsync(service));
        Assert.Equal(LuauModuleLimitKind.BundleBytecodeBytes, totalFailure.LimitKind);
    }

    [Fact]
    public async Task SuccessfulBundleInstallsOnlyAfterEveryOutputPassesAndExecutes()
    {
        var map = Map(("first", "return 20"), ("second", "return require('first') + 22"));
        await using var service = new LuauThreadedCompilationService();

        var bundle = await map.CompileModuleBundleAsync(service);

        Assert.Equal(2, bundle.Count);
        Assert.True(bundle.TotalBytecodeBytes > 0);
        using var root = TestBudgets.CreateRoot();
        root.OpenBaseLibrary();
        root.OpenRequireLibrary(bundle);
        using var result = root.DoString("return require('second')");
        Assert.Equal(42, result.Read<int>(0));
    }

    [Fact]
    public async Task FailedBundleCompilationIsAtomicAndReportsFirstCanonicalModule()
    {
        var map = Map(("z-last", "return 2"), ("a-first", "return 1"));
        await using var service = new OrderedFailureService();

        var failure = await Assert.ThrowsAsync<LuauModuleBundleCompilationException>(async () =>
            await map.CompileModuleBundleAsync(service));

        Assert.Equal("a-first", failure.ModuleId);
        Assert.Equal(1, service.CallCount);

        // A failed bundle has not installed or poisoned the source resolver.
        using var root = TestBudgets.CreateRoot();
        root.OpenBaseLibrary();
        root.OpenRequireLibrary(map);
        using var result = root.DoString("return require('a-first')");
        Assert.Equal(1, result.Read<int>(0));
    }

    [Fact]
    public async Task BundleCompilationCancellationPublishesNoResolver()
    {
        var map = Map(("first", "return 1"), ("second", "return 2"));
        await using var service = new CancelingService();
        using var cancellation = new CancellationTokenSource();

        // Cancel once a compilation is in progress. A timer can fire before a
        // loaded runner reaches the first call, and the bundle is then
        // canceled before it compiles anything.
        var compilation = map.CompileModuleBundleAsync(
            service,
            cancellationToken: cancellation.Token).AsTask();
        await service.FirstCallStarted.WaitAsync(TestBudgets.WaitTimeout);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => compilation.WaitAsync(TestBudgets.WaitTimeout));

        Assert.InRange(service.CallCount, 1, 2);
    }

    [Fact]
    public async Task AggregateBundleQuotaStopsAdmissionBeforeLaterModulesCompile()
    {
        var output = LuauCompiler.Compile("return 1"u8);
        var map = new LuauModuleMap(
            new Dictionary<string, byte[]>
            {
                ["a"] = "return 1"u8.ToArray(),
                ["b"] = "return 2"u8.ToArray(),
                ["c"] = "return 3"u8.ToArray(),
            },
            limits: new LuauModuleLimits
            {
                MaxTotalBytecodeBytes = output.BytecodeLength,
            });
        await using var service = new FixedOutputService(output);

        var failure = await Assert.ThrowsAsync<LuauModuleLimitException>(async () =>
            await map.CompileModuleBundleAsync(service));

        Assert.Equal(LuauModuleLimitKind.BundleBytecodeBytes, failure.LimitKind);
        Assert.Equal(2, service.CallCount);
    }

    [Fact]
    public async Task LargeBundleUsesSequentialSharedLaneAdmissionWithoutQueueSelfExhaustion()
    {
        var modules = Enumerable.Range(0, 40).ToDictionary(
            index => $"module-{index:D2}",
            index => Encoding.UTF8.GetBytes($"return {index}"),
            StringComparer.Ordinal);
        var map = new LuauModuleMap(modules);
        await using var service = new LuauThreadedCompilationService(new LuauThreadedCompilationOptions
        {
            WorkerCount = 1,
            MaxQueuedRequestCount = 2,
            MaxQueuedSourceBytes = 1024,
            MaxSourceBytesPerRequest = 128,
            MaxBytecodeBytesPerResult = 16 * 1024,
        });

        var bundle = await map.CompileModuleBundleAsync(service);

        Assert.Equal(40, bundle.Count);
    }

    [Fact]
    public void CircularDependenciesAndDepthQuotaAreTypedAndRecoverable()
    {
        using var cycleRoot = TestBudgets.CreateRoot();
        cycleRoot.OpenBaseLibrary();
        cycleRoot.OpenRequireLibrary(Map(("a", "return require('b')"), ("b", "return require('a')")));
        var cycle = Assert.Throws<LuauManagedCallbackException>(
            () => cycleRoot.DoString("return require('a')"));
        Assert.Contains("Circular module dependency", cycle.ToString(), StringComparison.Ordinal);
        Assert.Equal(5, Assert.Single(cycleRoot.DoString("return 5")).Read<int>());

        using var depthRoot = LuauState.Create(TestBudgets.StateOptions with
        {
            MaxModuleDependencyDepth = 2,
        });
        depthRoot.OpenBaseLibrary();
        depthRoot.OpenRequireLibrary(Map(
            ("a", "return require('b')"),
            ("b", "return require('c')"),
            ("c", "return 3")));
        var depth = Assert.Throws<LuauManagedCallbackException>(
            () => depthRoot.DoString("return require('a')"));
        var depthLimit = FindInner<LuauModuleLimitException>(depth);
        Assert.Equal(LuauModuleLimitKind.DependencyDepth, depthLimit.LimitKind);
        Assert.Equal(3, depthLimit.Actual);
        Assert.Equal(2, depthLimit.Limit);
        Assert.Equal(6, Assert.Single(depthRoot.DoString("return 6")).Read<int>());
    }

    [Fact]
    public void RootModuleCacheQuotaFailsTypedAndRootCloseInvalidatesCachedReferences()
    {
        using var root = LuauState.Create(TestBudgets.StateOptions with { MaxCachedModuleCount = 1 });
        root.OpenBaseLibrary();
        root.OpenRequireLibrary(Map(("first", "return {}"), ("second", "return {}")));
        using var firstResult = root.DoString("return require('first')");
        var cachedTable = firstResult.Read<LuauTable>(0).Retain();

        var failure = Assert.Throws<LuauManagedCallbackException>(
            () => root.DoString("return require('second')"));
        var cacheLimit = FindInner<LuauModuleLimitException>(failure);
        Assert.Equal(LuauModuleLimitKind.CachedResultCount, cacheLimit.LimitKind);
        Assert.Equal(2, cacheLimit.Actual);
        Assert.Equal(1, cacheLimit.Limit);

        root.Dispose();
        Assert.True(cachedTable.IsDisposed);
        cachedTable.Dispose();
    }

    [Fact]
    public void ThreadModuleResultsAreRejectedBeforeEnteringTheSharedCache()
    {
        using var root = TestBudgets.CreateRoot();
        root.OpenBaseLibrary();
        root.OpenCoroutineLibrary();
        root.OpenRequireLibrary(Map((
            "thread-result",
            "return coroutine.create(function() return 42 end)")));
        var releasedBefore = root.Context.ReleasedReferenceCount;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var failure = Assert.Throws<LuauManagedCallbackException>(
                () => root.DoString("return require('thread-result')"));
            Assert.Contains(
                "cannot return a Luau thread",
                failure.ToString(),
                StringComparison.Ordinal);
            Assert.Equal(1, root.Context.CachedStateCount);
        }

        Assert.True(root.Context.ReleasedReferenceCount >= releasedBefore + 4);
        using var recovery = root.DoString("return 9");
        Assert.Equal(9, Assert.Single(recovery).Read<int>());
    }

    static LuauModuleMap Map(params (string Id, string Source)[] modules) =>
        new(modules.ToDictionary(
            static module => module.Id,
            static module => Encoding.UTF8.GetBytes(module.Source),
            StringComparer.Ordinal));

    static void AssertLimit(LuauModuleLimitKind expected, Action action)
    {
        var exception = Assert.Throws<LuauModuleLimitException>(action);
        Assert.Equal(expected, exception.LimitKind);
    }

    static T FindInner<T>(Exception exception)
        where T : Exception
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is T typed) return typed;
        }
        throw new Xunit.Sdk.XunitException($"No inner exception of type {typeof(T).FullName} was found.");
    }

    sealed class OrderedFailureService : ILuauCompilationService
    {
        int callCount;
        public int CallCount => Volatile.Read(ref callCount);

        public async ValueTask<LuauCompileResult> CompileAsync(
            ReadOnlyMemory<byte> utf8Source,
            LuauCompileOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref callCount);
            await Task.Delay(call == 1 ? 30 : 1, cancellationToken);
            return LuauCompileResult.Diagnostic(
                new LuauCompilationException($"ordered failure {call}"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class CancelingService : ILuauCompilationService
    {
        int callCount;
        readonly TaskCompletionSource firstCallStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref callCount);

        public Task FirstCallStarted => firstCallStarted.Task;

        public async ValueTask<LuauCompileResult> CompileAsync(
            ReadOnlyMemory<byte> utf8Source,
            LuauCompileOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref callCount);
            firstCallStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return LuauCompileResult.InfrastructureFailure(
                    new InvalidOperationException("The cancellation delay completed unexpectedly."));
            }
            catch (OperationCanceledException)
            {
                return LuauCompileResult.Canceled();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class FixedOutputService(LuauCompilerOutput output) : ILuauCompilationService
    {
        int callCount;
        public int CallCount => Volatile.Read(ref callCount);

        public ValueTask<LuauCompileResult> CompileAsync(
            ReadOnlyMemory<byte> utf8Source,
            LuauCompileOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref callCount);
            return ValueTask.FromResult(LuauCompileResult.Success(output));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
