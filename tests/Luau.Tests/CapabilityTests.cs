using System.Runtime.CompilerServices;

namespace Luau.Tests;

public sealed class CapabilityTests
{
    static readonly LuauObjectDescriptor<CapabilityTarget> FullDescriptor =
        new(
            "CapabilityTarget",
            validateTarget: null,
            new[]
            {
                LuauObjectMember<CapabilityTarget>.Property(
                    "value",
                    (target, context) => context.Return(target.Value),
                    (target, context) => target.Value = context.Read<int>(2)),
                LuauObjectMember<CapabilityTarget>.Method(
                    "add",
                    (target, context) =>
                    {
                        target.Value += context.Read<int>(1);
                        context.Return(target.Value);
                    }),
                LuauObjectMember<CapabilityTarget>.AsyncMethod(
                    "addLater",
                    async (target, context) =>
                    {
                        await Task.Yield();
                        target.Value += context.Read<int>(1);
                        context.Return(target.Value);
                    }),
            });

    static readonly LuauObjectDescriptor<CapabilityTarget> NarrowDescriptor =
        new(
            "ReadonlyCapabilityTarget",
            validateTarget: null,
            new[]
            {
                LuauObjectMember<CapabilityTarget>.Property(
                    "value",
                    (target, context) => context.Return(target.Value),
                    setter: null),
            });

    static readonly LuauObjectDescriptor<CapabilityTarget> FailureDescriptor =
        new(
            "FailureCapabilityTarget",
            validateTarget: null,
            new[]
            {
                LuauObjectMember<CapabilityTarget>.Property(
                    "readonlyValue",
                    (target, context) => context.Return(target.Value),
                    setter: null),
                LuauObjectMember<CapabilityTarget>.Property(
                    "writeonlyValue",
                    getter: null,
                    (target, context) => target.Value = context.Read<int>(2)),
                LuauObjectMember<CapabilityTarget>.Method(
                    "fail",
                    (_, _) => throw new InvalidOperationException("expected capability failure")),
            });

    static readonly LuauObjectDescriptor<CapabilityTarget> BindingRaceDescriptor =
        new(
            "BindingRaceCapabilityTarget",
            validateTarget: null,
            Enumerable.Range(0, 32)
                .Select(index => LuauObjectMember<CapabilityTarget>.Method(
                    $"method{index}",
                    (_, _) => { }))
                .ToArray());

    [Fact]
    public async Task GeneratedCapabilityDispatchesPropertiesAndMethods()
    {
        using var state = TestBudgets.CreateRoot();
        var target = new GeneratedCapabilityTarget { Value = 4 };
        using var handle = state.CreateHandle(target);
        state["target"] = handle;

        var sync = state.DoString(
            "target.Value = 20; target:Increment(2); return target.Value, target.Hidden == nil");
        Assert.Equal(22, sync[0].Read<int>());
        Assert.True(sync[1].Read<bool>());

        var async = await state.DoStringAsync("return target:IncrementLater(20)");
        Assert.Equal(42, Assert.Single(async).Read<int>());
        Assert.Equal(42, target.Value);
    }

    [Fact]
    public async Task GeneratedCapabilityDispatchesFromSandboxedChild()
    {
        using var root = TestBudgets.CreateRoot();
        root.OpenBaseLibrary();
        root.SandboxRoot();
        using var child = root.CreateSandboxedThread();
        var target = new GeneratedCapabilityTarget { Value = 4 };
        using var handle = root.CreateHandle(target);
        child["target"] = handle;

        var sync = child.DoString(
            "target.Value = 20; target:Increment(2); return target.Value, target.Hidden == nil");
        Assert.Equal(22, sync[0].Read<int>());
        Assert.True(sync[1].Read<bool>());
        Assert.Equal(22, target.Value);

        var async = await child.DoStringAsync("return target:IncrementLater(20)");
        Assert.Equal(42, Assert.Single(async).Read<int>());
        Assert.Equal(42, target.Value);
    }

    [Fact]
    public void SameTargetAndDescriptorReuseNativeIdentity()
    {
        using var state = TestBudgets.CreateRoot();
        var target = new CapabilityTarget();
        using var first = state.CreateHandle(target, FullDescriptor);
        using var second = state.CreateHandle(target, FullDescriptor);
        using var narrow = state.CreateHandle(target, NarrowDescriptor);
        state["first"] = first;
        state["second"] = second;
        state["narrow"] = narrow;

        var results = state.DoString(
            "return first == second, first == narrow, narrow.add == nil");
        Assert.True(results[0].Read<bool>());
        Assert.False(results[1].Read<bool>());
        Assert.True(results[2].Read<bool>());
    }

    [Fact]
    public async Task ConcurrentSameTargetAndDescriptorReuseIdentityAtQuotaBoundary()
    {
        using var state = LuauState.Create(LuauStateOptions.UnboundedResources with
        {
            MaxManagedHandleCount = 1,
        });
        using var callersReady = new Barrier(2);
        var descriptor = new LuauObjectDescriptor<CapabilityTarget>(
            "ConcurrentIdentityCapabilityTarget",
            _ =>
            {
                if (!callersReady.SignalAndWait(TestBudgets.WaitTimeout))
                {
                    throw new TimeoutException("Concurrent capability creators did not rendezvous.");
                }
            },
            Enumerable.Range(0, 32)
                .Select(index => LuauObjectMember<CapabilityTarget>.Method(
                    $"method{index}",
                    (_, _) => { }))
                .ToArray());
        var target = new CapabilityTarget();

        Task<LuauObjectHandle> CreateHandle() => Task.Factory.StartNew(
            () => state.CreateHandle(target, descriptor),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        var handles = await Task.WhenAll(CreateHandle(), CreateHandle())
            .WaitAsync(TestBudgets.WaitTimeout);
        using var first = handles[0];
        using var second = handles[1];
        Assert.Equal(1, state.Context.ObjectRegistry.Count);

        state["first"] = first;
        state["second"] = second;
        Assert.True(Assert.Single(state.DoString("return first == second")).Read<bool>());
    }

    [Fact]
    public void HandleCreationOnAStateItsValidatorDisposedFailsAsDisposed()
    {
        // Target validation is host code and runs before the native gate is
        // entered. A validator that disposes the state must surface as a
        // disposed state, not as a null root.
        using var root = TestBudgets.CreateRoot();
        var child = root.CreateThread();
        var descriptor = new LuauObjectDescriptor<CapabilityTarget>(
            "DisposingValidatorCapabilityTarget",
            _ => child.Dispose(),
            [LuauObjectMember<CapabilityTarget>.Method("method", (_, _) => { })]);

        Assert.Throws<ObjectDisposedException>(() => child.CreateHandle(new CapabilityTarget(), descriptor));
        Assert.Equal(3, root.DoString("return 3").Single().Read<int>());
    }

    [Fact]
    public void HandleCannotCrossIndependentVmsOrDescriptors()
    {
        using var firstState = TestBudgets.CreateRoot();
        using var secondState = TestBudgets.CreateRoot();
        var target = new CapabilityTarget();
        using var full = firstState.CreateHandle(target, FullDescriptor);
        using var narrow = firstState.CreateHandle(target, NarrowDescriptor);

        Assert.Throws<InvalidOperationException>(() => secondState["foreign"] = full);

        firstState["full"] = full;
        firstState["narrow"] = narrow;
        var exception = Assert.Throws<LuauManagedCallbackException>(
            () => firstState.DoString("local method = full.add; return method(narrow, 1)"));
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("authority", exception.InnerException!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LuauReferenceOutlivesDisposedManagedWrapper()
    {
        using var state = TestBudgets.CreateRoot();
        var target = new CapabilityTarget { Value = 41 };
        var handle = state.CreateHandle(target, FullDescriptor);
        state["target"] = handle;
        handle.Dispose();

        Assert.Equal(42, Assert.Single(state.DoString("return target:add(1)")).Read<int>());
        using var retained = state["target"].Read<LuauObjectHandle>();
        Assert.Same(state, retained.State);
    }

    [Fact]
    public void CollectedTargetFailsWithoutCallingStaleObject()
    {
        using var state = TestBudgets.CreateRoot();
        var (handle, target) = CreateWeakTargetHandle(state);
        using (handle)
        {
            ForceCollection(target);
            Assert.False(target.TryGetTarget(out _));

            state["target"] = handle;
            var exception = Assert.Throws<LuauManagedCallbackException>(
                () => state.DoString("return target.value"));
            Assert.IsType<ObjectDisposedException>(exception.InnerException);
        }
    }

    [Fact]
    public void ManagedHandleQuotaRecoversAfterNativeCollection()
    {
        using var state = LuauState.Create(LuauStateOptions.UnboundedResources with
        {
            MaxManagedHandleCount = 1,
        });

        var first = state.CreateHandle(new CapabilityTarget(), FullDescriptor);
        Assert.Equal(1, state.Context.ObjectRegistry.Count);
        Assert.Throws<LuauManagedHandleLimitException>(
            () => state.CreateHandle(new CapabilityTarget(), FullDescriptor));

        first.Dispose();
        state.CollectGarbage();
        Assert.Equal(0, state.Context.ObjectRegistry.Count);

        using var replacement = state.CreateHandle(new CapabilityTarget(), FullDescriptor);
        Assert.Equal(1, state.Context.ObjectRegistry.Count);
    }

    [Fact]
    public void CapabilityMetatableIsProtected()
    {
        using var state = TestBudgets.CreateRoot();
        state.OpenBaseLibrary();
        using var handle = state.CreateHandle(new CapabilityTarget(), FullDescriptor);
        state["target"] = handle;

        var metatable = Assert.Single(state.DoString("return getmetatable(target)"));
        Assert.Equal("protected Luau object capability", metatable.Read<string>());
        Assert.Equal("CapabilityTarget", Assert.Single(state.DoString("return tostring(target)")).Read<string>());
    }

    [Fact]
    public void HandleCreationRequiresTheConfiguredOwnerScheduler()
    {
        var scheduler = new ToggleScheduler();
        using var state = LuauState.Create(LuauStateOptions.UnboundedResources with
        {
            DefaultExecutionOptions = LuauExecutionOptions.Unbounded with
            {
                ContinuationScheduler = scheduler,
            },
        });

        var exception = Assert.Throws<InvalidOperationException>(
            () => state.CreateHandle(new CapabilityTarget(), FullDescriptor));
        Assert.Contains("scheduler", exception.Message, StringComparison.OrdinalIgnoreCase);

        scheduler.HasAccess = true;
        using var handle = state.CreateHandle(new CapabilityTarget(), FullDescriptor);
        Assert.Same(state, handle.State);
    }

    [Fact]
    public void RootCloseInvalidatesWrappersAndDrainsRegistrations()
    {
        var state = TestBudgets.CreateRoot();
        var handle = state.CreateHandle(new CapabilityTarget(), FullDescriptor);
        state.Dispose();

        Assert.True(handle.IsDisposed);
        handle.Dispose();
        Assert.Equal(0, state.Context.ObjectRegistry.Count);
    }

    [Fact]
    public void RegistryRejectsForgedAndStaleGenerationTokens()
    {
        using var state = TestBudgets.CreateRoot();
        var registry = state.Context.ObjectRegistry;
        var first = registry.Reserve(new CapabilityTarget(), FullDescriptor);
        registry.CancelReservation(first);
        var current = registry.Reserve(new CapabilityTarget(), FullDescriptor);

        Assert.Equal(first.Slot, current.Slot);
        Assert.NotEqual(first.Generation, current.Generation);
        Assert.Throws<ObjectDisposedException>(
            () => registry.ResolveTarget(first, FullDescriptor));
        Assert.Throws<ObjectDisposedException>(() => registry.ResolveTarget(
            new LuauObjectToken(registry.ContextId, current.Slot, checked(current.Generation + 1)),
            FullDescriptor));
        Assert.Throws<ObjectDisposedException>(() => registry.ResolveTarget(
            new LuauObjectToken(registry.ContextId, int.MaxValue, current.Generation),
            FullDescriptor));

        registry.CancelReservation(current);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void NativeDestructorReleaseDoesNotWaitForRegistryGate()
    {
        using var state = TestBudgets.CreateRoot();
        var registry = state.Context.ObjectRegistry;
        var token = registry.Reserve(new CapabilityTarget(), FullDescriptor);
        var gate = typeof(LuauObjectRegistry)
            .GetField("gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(registry)!;
        using var completed = new ManualResetEventSlim();
        Exception? releaseFailure = null;
        var releaseThread = new Thread(() =>
        {
            try
            {
                LuauObjectRegistry.ReleaseFromNative(token);
            }
            catch (Exception exception)
            {
                releaseFailure = exception;
            }
            finally
            {
                completed.Set();
            }
        });

        // A release that waited for the gate could never complete while the
        // gate is held, so a long wait still fails that regression; a short
        // one also fails whenever a loaded runner starts the thread late.
        bool completedWhileGateHeld;
        lock (gate)
        {
            releaseThread.Start();
            completedWhileGateHeld = completed.Wait(TestBudgets.WaitTimeout);
        }

        Assert.True(completedWhileGateHeld);
        Assert.True(releaseThread.Join(TestBudgets.WaitTimeout));
        Assert.Null(releaseFailure);
        Assert.Equal(0, registry.Count);

        var replacement = registry.Reserve(new CapabilityTarget(), FullDescriptor);
        Assert.Equal(token.Slot, replacement.Slot);
        Assert.True(replacement.Generation > token.Generation);
        LuauObjectRegistry.ReleaseFromNative(token);
        Assert.Equal(1, registry.Count);
        registry.CancelReservation(replacement);
    }

    [Fact]
    public void DispatchFailuresAreTypedAndDoNotBroadenAuthority()
    {
        using var state = TestBudgets.CreateRoot();
        var target = new CapabilityTarget { Value = 7 };
        using var full = state.CreateHandle(target, FullDescriptor);
        using var failures = state.CreateHandle(target, FailureDescriptor);
        using var generated = state.CreateHandle(new GeneratedCapabilityTarget());
        state["full"] = full;
        state["failures"] = failures;
        state["generated"] = generated;

        AssertCallbackFailure<InvalidOperationException>(
            state,
            "local add = full.add; return add({}, 1)");
        AssertCallbackFailure<LuauException>(state, "return generated:Increment()");
        AssertCallbackFailure<InvalidOperationException>(state, "return full:add('not-a-number')");
        AssertCallbackFailure<LuauException>(state, "failures.readonlyValue = 1");
        AssertCallbackFailure<LuauException>(state, "return failures.writeonlyValue");
        AssertCallbackFailure<LuauException>(state, "failures.unknown = 1");
        AssertCallbackFailure<LuauException>(state, "full.add = function() end");
        var callbackFailure = AssertCallbackFailure<InvalidOperationException>(
            state,
            "return failures:fail()");
        Assert.Contains("expected capability failure", callbackFailure.Message, StringComparison.Ordinal);

        Assert.Equal(7, Assert.Single(state.DoString("return full.value")).Read<int>());
    }

    [Fact]
    public async Task ConcurrentRootCloseAndWrapperDisposalDrainNativeRegistrations()
    {
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var state = LuauState.Create(LuauStateOptions.UnboundedResources with
            {
                MaxManagedHandleCount = 64,
            });
            var handles = Enumerable.Range(0, 32)
                .Select(index => state.CreateHandle(
                    new CapabilityTarget { Value = index },
                    FullDescriptor))
                .ToArray();
            for (var index = 0; index < handles.Length; index++)
            {
                state[$"capability{index}"] = handles[index];
            }

            using var start = new ManualResetEventSlim();
            var wrapperDisposal = Task.Run(() =>
            {
                start.Wait();
                foreach (var handle in handles)
                {
                    handle.Dispose();
                }
            });
            var rootClose = Task.Run(() =>
            {
                start.Wait();
                state.Dispose();
            });

            start.Set();
            await Task.WhenAll(wrapperDisposal, rootClose).WaitAsync(TestBudgets.WaitTimeout);

            Assert.All(handles, handle => Assert.True(handle.IsDisposed));
            Assert.Equal(0, state.Context.ObjectRegistry.Count);
        }
    }

    [Fact]
    public async Task ConcurrentFirstBindingCreationAndRootCloseLeaveNoRegistrations()
    {
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var state = LuauState.Create(LuauStateOptions.UnboundedResources);
            using var start = new ManualResetEventSlim();
            var creation = Task.Run(() =>
            {
                start.Wait();
                try
                {
                    return state.CreateHandle(new CapabilityTarget(), BindingRaceDescriptor);
                }
                catch (ObjectDisposedException)
                {
                    return null;
                }
            });
            var close = Task.Run(() =>
            {
                start.Wait();
                state.Dispose();
            });

            start.Set();
            await Task.WhenAll(creation, close).WaitAsync(TestBudgets.WaitTimeout);
            var createdHandle = await creation;
            createdHandle?.Dispose();
            state.Dispose();

            Assert.Equal(0, state.Context.ObjectRegistry.Count);
            Assert.Equal(0, state.Context.ManagedCallbackCount);
        }
    }

    static TException AssertCallbackFailure<TException>(LuauState state, string source)
        where TException : Exception
    {
        var wrapper = Assert.Throws<LuauManagedCallbackException>(() => state.DoString(source));
        return Assert.IsType<TException>(wrapper.InnerException);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static (LuauObjectHandle Handle, WeakReference<CapabilityTarget> Target) CreateWeakTargetHandle(
        LuauState state)
    {
        var target = new CapabilityTarget { Value = 42 };
        return (state.CreateHandle(target, FullDescriptor), new WeakReference<CapabilityTarget>(target));
    }

    static void ForceCollection(WeakReference<CapabilityTarget> target)
    {
        for (var attempt = 0; attempt < 10 && IsAlive(target); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool IsAlive(WeakReference<CapabilityTarget> target)
    {
        return target.TryGetTarget(out _);
    }

    sealed class CapabilityTarget
    {
        public int Value { get; set; }
    }

    sealed class ToggleScheduler : ILuauContinuationScheduler
    {
        public bool HasAccess { get; set; }

        public bool CheckAccess() => HasAccess;

        public void Post(Action continuation) => continuation();
    }
}

[LuauLibrary("GeneratedTarget", Exposure = LuauLibraryExposure.Capability)]
public partial class GeneratedCapabilityTarget
{
    [LuauMember]
    public int Value { get; set; }

    public int Hidden { get; set; } = 99;

    [LuauMember]
    public void Increment(int amount)
    {
        Value += amount;
    }

    [LuauMember]
    public async ValueTask<int> IncrementLater(int amount, CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        Value += amount;
        return Value;
    }
}
