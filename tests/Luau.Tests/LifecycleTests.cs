using System.Runtime.CompilerServices;

namespace Luau.Tests;

public sealed class LifecycleTests
{
    [Fact]
    public void DisposingChildReleasesOnlyTheThreadReference()
    {
        using var root = TestBudgets.CreateRoot();
        var context = root.Context;
        var child = root.CreateThread();

        Assert.Equal(2, context.CachedStateCount);

        child.Dispose();
        child.Dispose();

        Assert.True(child.IsDisposed);
        Assert.Equal(1, context.CachedStateCount);
        Assert.Equal(1, context.ReleasedReferenceCount);
        Assert.Equal(3, root.DoString("return 1 + 2")[0].Read<int>());
    }

    [Fact]
    public void DisposingRootInvalidatesEveryCachedChild()
    {
        var root = TestBudgets.CreateRoot();
        var context = root.Context;
        var child = root.CreateThread();
        var nestedChild = child.CreateThread();

        Assert.Equal(3, context.CachedStateCount);

        root.Dispose();
        root.Dispose();

        Assert.True(root.IsDisposed);
        Assert.True(child.IsDisposed);
        Assert.True(nestedChild.IsDisposed);
        Assert.True(context.IsDisposed);
        Assert.Equal(0, context.CachedStateCount);
        Assert.Equal(1, context.CloseCount);

        child.Dispose();
        nestedChild.Dispose();

        Assert.Throws<ObjectDisposedException>(() => child.CreateTable());
        Assert.Throws<ObjectDisposedException>(() => nestedChild.GetMainThread());
    }

    [Fact]
    public void LuaCreatedThreadIsRootedAndOwnedByTheMainState()
    {
        using var root = TestBudgets.CreateRoot();
        var context = root.Context;
        root.OpenCoroutineLibrary();

        var child = root
            .DoString("return coroutine.create(function() return 42 end)")
            .Single()
            .Read<LuauState>();

        Assert.False(child.IsMainThread);
        Assert.Same(root, child.GetMainThread());
        Assert.Equal(2, context.CachedStateCount);
        Assert.Equal(42, child.Resume().Single().Read<int>());

        child.Dispose();

        Assert.Equal(1, context.CachedStateCount);
        Assert.Equal(7, root.DoString("return 7").Single().Read<int>());
    }

    [Fact]
    public void ManagedCallbackCachesPreviouslyUnseenLuaCoroutineSafely()
    {
        using var root = TestBudgets.CreateRoot();
        var context = root.Context;
        root.OpenCoroutineLibrary();
        LuauState? callbackState = null;
        root["inspectThread"] = root.CreateFunction(context =>
        {
            callbackState = context.State;
            context.Return(!context.State.IsMainThread);
        });

        var results = root.DoString(
            "local thread = coroutine.create(function() return inspectThread() end); " +
            "local ok, value = coroutine.resume(thread); return ok, value");

        Assert.True(results[0].Read<bool>());
        Assert.True(results[1].Read<bool>());
        Assert.NotNull(callbackState);
        Assert.False(callbackState!.IsMainThread);
        Assert.Same(root, callbackState.GetMainThread());
        Assert.Equal(2, context.CachedStateCount);

        callbackState.Dispose();
        Assert.Equal(1, context.CachedStateCount);
    }

    [Fact]
    public void ReferenceWrappersAreIdempotentAndRejectUseAfterDispose()
    {
        using var root = TestBudgets.CreateRoot();
        var table = root.CreateTable();
        var buffer = root.CreateBuffer(8);
        var function = root.LoadCompilerOutput(LuauCompiler.Compile("return 1"u8));
        var callback = root.CreateFunction(_ => { });

        Parallel.For(0, 8, _ => table.Dispose());
        Parallel.For(0, 8, _ => buffer.Dispose());
        Parallel.For(0, 8, _ => function.Dispose());
        Parallel.For(0, 8, _ => callback.Dispose());

        Assert.True(table.IsDisposed);
        Assert.True(buffer.IsDisposed);
        Assert.True(function.IsDisposed);
        Assert.True(callback.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => _ = table.Length);
        Assert.Throws<ObjectDisposedException>(() => _ = buffer.Length);
        Assert.Throws<ObjectDisposedException>(() => _ = function.State);
        Assert.Throws<ObjectDisposedException>(() => _ = callback.State);
    }

    [Fact]
    public void RootDisposalInvalidatesReferenceWrappersBeforeTheyAreDisposed()
    {
        var root = TestBudgets.CreateRoot();
        var table = root.CreateTable();
        var buffer = root.CreateBuffer(8);
        var function = root.LoadCompilerOutput(LuauCompiler.Compile("return 1"u8));
        var callback = root.CreateFunction(_ => { });

        root.Dispose();

        Assert.True(table.IsDisposed);
        Assert.True(buffer.IsDisposed);
        Assert.True(function.IsDisposed);
        Assert.True(callback.IsDisposed);

        table.Dispose();
        table.Dispose();
        buffer.Dispose();
        buffer.Dispose();
        function.Dispose();
        function.Dispose();
        callback.Dispose();
        callback.Dispose();
    }

    [Fact]
    public void RootDisposalMakesPublicStateOperationsFailSafely()
    {
        var output = LuauCompiler.Compile("return 1"u8);
        var root = TestBudgets.CreateRoot();
        var child = root.CreateThread();

        root.Dispose();

        Assert.True(root.IsMainThread);
        Assert.Throws<ObjectDisposedException>(() => root.CreateTable());
        Assert.Throws<ObjectDisposedException>(() => root.GetMainThread());
        Assert.Throws<ObjectDisposedException>(() => root.CreateThread());
        Assert.Throws<ObjectDisposedException>(() => root.ExecuteCompilerOutput(output));
        Assert.Throws<ObjectDisposedException>(() => root.DoString("return 1"));
        Assert.Throws<ObjectDisposedException>(() => child.Resume());
    }

    [Fact]
    public void ConcurrentDisposalClosesTheVmExactlyOnce()
    {
        var root = TestBudgets.CreateRoot();
        var context = root.Context;
        var child = root.CreateThread();
        var table = child.CreateTable();

        Parallel.Invoke(
            root.Dispose,
            root.Dispose,
            child.Dispose,
            child.Dispose,
            table.Dispose,
            table.Dispose);

        Assert.True(context.IsDisposed);
        Assert.Equal(0, context.CachedStateCount);
        Assert.Equal(1, context.CloseCount);
    }

    [Fact]
    public void AbandonedRootAndChildrenAreFinalized()
    {
        var abandoned = CreateAbandonedState();

        // The context reports itself disposed as soon as the finalizer starts
        // closing it, before the cache is cleared and the VM is closed, so
        // wait for the counters this test asserts.
        ForceFinalizersUntil(() =>
            abandoned.Context.IsDisposed &&
            abandoned.Context.CachedStateCount == 0 &&
            abandoned.Context.CloseCount != 0);

        Assert.False(abandoned.State.TryGetTarget(out _));
        Assert.True(abandoned.Context.IsDisposed);
        Assert.Equal(0, abandoned.Context.CachedStateCount);
        Assert.Equal(1, abandoned.Context.CloseCount);
    }

    [Fact]
    public void AbandonedChildIsRemovedWhileTheRootRemainsAlive()
    {
        using var root = TestBudgets.CreateRoot();
        var context = root.Context;
        var releasedBefore = context.ReleasedReferenceCount;
        var child = CreateAbandonedChild(root);

        ForceFinalizersUntil(() =>
            !child.TryGetTarget(out _) &&
            context.CachedStateCount == 1 &&
            context.ReleasedReferenceCount == releasedBefore + 1);

        Assert.False(child.TryGetTarget(out _));
        Assert.Equal(1, context.CachedStateCount);
        Assert.Equal(releasedBefore + 1, context.ReleasedReferenceCount);
        Assert.Equal(5, root.DoString("return 5").Single().Read<int>());
    }

    [Fact]
    public void RetainedDisposedChildDoesNotKeepAnAbandonedRootAlive()
    {
        var abandoned = CreateRootWithRetainedDisposedChild();

        ForceFinalizersUntil(() =>
            abandoned.Context.IsDisposed &&
            abandoned.Context.CloseCount != 0);

        Assert.False(abandoned.Root.TryGetTarget(out _));
        Assert.True(abandoned.Child.IsDisposed);
        Assert.True(abandoned.Context.IsDisposed);
        Assert.Equal(1, abandoned.Context.CloseCount);
    }

    [Fact]
    public void AbandonedReferenceWrappersReleaseTheirRegistryReferences()
    {
        using var root = TestBudgets.CreateRoot();
        var context = root.Context;
        var releasedBefore = context.ReleasedReferenceCount;
        var references = CreateAbandonedReferences(root);

        // A weak reference is cleared when its target is collected, before
        // the finalizer that releases the registry reference has run.
        ForceFinalizersUntil(() =>
            references.All(reference => !reference.IsAlive) &&
            context.ReleasedReferenceCount >= releasedBefore + references.Length);

        Assert.All(references, reference => Assert.False(reference.IsAlive));
        Assert.True(context.ReleasedReferenceCount >= releasedBefore + references.Length);
        Assert.Equal(9, root.DoString("return 9").Single().Read<int>());
    }

    [Fact]
    public void ReferenceFinalizersRemainSafeAfterTheRootIsClosed()
    {
        var references = CreateAbandonedReferencesAfterRootClose();

        ForceFinalizersUntil(() => references.All(reference => !reference.IsAlive));

        Assert.All(references, reference => Assert.False(reference.IsAlive));
    }

    [Fact]
    public void TableClearKeepsStackBalanced()
    {
        using var root = TestBudgets.CreateRoot();
        using var table = root.CreateTable();
        for (var i = 0; i < 250; i++)
        {
            table.Clear();
        }

        Assert.Equal(9, root.DoString("return 9").Single().Read<int>());
    }

    [Fact]
    public async Task ConcurrentWrapperUseAndRootDisposeAreCloseSafe()
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var root = TestBudgets.CreateRoot();
            var table = root.CreateTable();
            table[1] = iteration;
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var reader = Task.Run(() =>
            {
                started.TrySetResult();
                while (true)
                {
                    try
                    {
                        _ = table.Length;
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                }
            });

            await started.Task.WaitAsync(TestBudgets.WaitTimeout);
            root.Dispose();
            await reader.WaitAsync(TestBudgets.WaitTimeout);
            table.Dispose();
        }
    }

    [Fact]
    public async Task ConcurrentReferenceWrapperUseAndOwnDisposeAreSafe()
    {
        for (var iteration = 0; iteration < 50; iteration++)
        {
            using var root = TestBudgets.CreateRoot();
            var table = root.CreateTable();
            table[1d] = 11;
            var buffer = root.CreateBuffer(16);
            var script = root.LoadCompilerOutput(LuauCompiler.Compile("return 1"u8));
            var callback = root.CreateFunction(_ => { });

            await RaceUseAndDisposeAsync(() => Assert.Equal(1, table.Length), table.Dispose);
            await RaceUseAndDisposeAsync(() => Assert.Equal(16, buffer.Length), buffer.Dispose);
            await RaceUseAndDisposeAsync(() => Assert.StartsWith("function:", script.ToString()), script.Dispose);
            await RaceUseAndDisposeAsync(
                () => Assert.Same(root, callback.State),
                callback.Dispose);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static AbandonedState CreateAbandonedState()
    {
        var state = TestBudgets.CreateRoot();
        var context = state.Context;
        _ = state.CreateThread();
        return new(new WeakReference<LuauState>(state), context);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference[] CreateAbandonedReferences(LuauState state)
    {
        var table = state.CreateTable();
        var buffer = state.CreateBuffer(8);
        var function = state.LoadCompilerOutput(LuauCompiler.Compile("return 1"u8));

        return
        [
            new WeakReference(table),
            new WeakReference(buffer),
            new WeakReference(function),
        ];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference<LuauState> CreateAbandonedChild(LuauState root)
    {
        // Count the child while it is still reachable: once this returns, a
        // collection forced by a parallel test can finalize it before the
        // caller looks.
        var child = root.CreateThread();
        Assert.Equal(2, root.Context.CachedStateCount);
        return new(child);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static AbandonedRootWithChild CreateRootWithRetainedDisposedChild()
    {
        var root = TestBudgets.CreateRoot();
        var context = root.Context;
        var child = root.CreateThread();
        child.Dispose();
        return new(new WeakReference<LuauState>(root), child, context);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference[] CreateAbandonedReferencesAfterRootClose()
    {
        var root = TestBudgets.CreateRoot();
        var table = root.CreateTable();
        var buffer = root.CreateBuffer(8);
        var function = root.LoadCompilerOutput(LuauCompiler.Compile("return 1"u8));
        var callback = root.CreateFunction(_ => { });

        root.Dispose();

        return
        [
            new WeakReference(table),
            new WeakReference(buffer),
            new WeakReference(function),
            new WeakReference(callback),
        ];
    }

    // Parallel tests force collections too, so the objects can already be
    // collected, and their finalizers queued or running on the finalizer
    // thread, before this is called. The condition must therefore name every
    // counter the test asserts, not only that the objects are unreachable, and
    // each attempt drains the finalizer queue before checking it.
    static void ForceFinalizersUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            if (condition())
            {
                return;
            }
        }

        Assert.True(condition(), "Objects were not finalized within the forced-GC retry budget");
    }

    static async Task RaceUseAndDisposeAsync(Action use, Action dispose)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = Task.Run(() =>
        {
            use();
            started.TrySetResult();

            while (true)
            {
                try
                {
                    use();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        });

        await started.Task.WaitAsync(TestBudgets.WaitTimeout);
        dispose();
        await reader.WaitAsync(TestBudgets.WaitTimeout);
    }

    readonly record struct AbandonedState(WeakReference<LuauState> State, LuauVmContext Context);
    readonly record struct AbandonedRootWithChild(WeakReference<LuauState> Root, LuauState Child, LuauVmContext Context);
}
