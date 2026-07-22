using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace Luau.Unity.Tests
{
    public sealed class LuauScriptSchedulerTests
    {
        [Test]
        public void DispatchUsesOrderThenRegistrationSequence()
        {
            var calls = new List<int>();
            using var root = CreateRoot(context => calls.Add(context.Read<int>(0)));
            using var first = CreateInstance(root, "first", ExportingUpdate(1));
            using var second = CreateInstance(root, "second", ExportingUpdate(2));
            using var third = CreateInstance(root, "third", ExportingUpdate(3));
            using var scheduler = new LuauScriptScheduler(root);
            var phase = scheduler.CreatePhase("ordered", CreatePhaseOptions());
            using var firstRegistration = phase.Register(
                first.GetRequiredEntrypoint("update"),
                order: 10);
            using var secondRegistration = phase.Register(
                second.GetRequiredEntrypoint("update"),
                order: -5);
            using var thirdRegistration = phase.Register(
                third.GetRequiredEntrypoint("update"),
                order: 10);

            var result = phase.Dispatch();

            Assert.That(calls, Is.EqualTo(new[] { 2, 1, 3 }));
            Assert.That(result.AttemptedCount, Is.EqualTo(3));
            Assert.That(result.SucceededCount, Is.EqualTo(3));
            Assert.That(result.FailedCount, Is.Zero);
            Assert.That(result.SkippedCount, Is.Zero);
        }

        [Test]
        public void DispatchPassesBorrowedArgumentSpan()
        {
            var calls = new List<string>();
            using var root = CreateRoot(context => calls.Add(context.Read<string>(0)));
            using var instance = CreateInstance(
                root,
                "span",
                "return { update = function(left, right) record(left); record(right) end }");
            using var scheduler = new LuauScriptScheduler(root);
            var phase = scheduler.CreatePhase("span", CreatePhaseOptions());
            using var registration = phase.Register(instance.GetRequiredEntrypoint("update"));
            var arguments = new LuauValue[] { "left", "right" };

            var result = phase.Dispatch(arguments.AsSpan());

            Assert.That(calls, Is.EqualTo(new[] { "left", "right" }));
            Assert.That(result.AttemptedCount, Is.EqualTo(1));
            Assert.That(result.SucceededCount, Is.EqualTo(1));
        }

        [Test]
        public void RegistrationAndEnabledMutationsTakeEffectNextDispatch()
        {
            var calls = new List<int>();
            LuauScriptPhase phase = null;
            LuauScriptRegistration secondRegistration = null;
            LuauScriptRegistration thirdRegistration = null;
            LuauScriptEntrypoint thirdEntrypoint = null;
            var mutated = false;
            using var root = CreateRoot(context =>
            {
                var id = context.Read<int>(0);
                calls.Add(id);
                if (id == 1 && !mutated)
                {
                    mutated = true;
                    secondRegistration.IsEnabled = false;
                    thirdRegistration = phase.Register(thirdEntrypoint);
                }
            });
            using var first = CreateInstance(root, "first", ExportingUpdate(1));
            using var second = CreateInstance(root, "second", ExportingUpdate(2));
            using var third = CreateInstance(root, "third", ExportingUpdate(3));
            using var scheduler = new LuauScriptScheduler(root);
            phase = scheduler.CreatePhase("mutating", CreatePhaseOptions());
            using var firstRegistration = phase.Register(first.GetRequiredEntrypoint("update"));
            secondRegistration = phase.Register(second.GetRequiredEntrypoint("update"));
            thirdEntrypoint = third.GetRequiredEntrypoint("update");

            phase.Dispatch();

            Assert.That(calls, Is.EqualTo(new[] { 1, 2 }));
            Assert.That(secondRegistration.IsEnabled, Is.False);

            calls.Clear();
            phase.Dispatch();

            Assert.That(calls, Is.EqualTo(new[] { 1, 3 }));
            thirdRegistration.Dispose();
            secondRegistration.Dispose();
        }

        [Test]
        public void RegistrationDisposalDuringDispatchIsDeferred()
        {
            var calls = new List<int>();
            LuauScriptRegistration later = null;
            using var root = CreateRoot(context =>
            {
                var id = context.Read<int>(0);
                calls.Add(id);
                if (id == 1)
                {
                    later.Dispose();
                }
            });
            using var first = CreateInstance(root, "first", ExportingUpdate(1));
            using var second = CreateInstance(root, "second", ExportingUpdate(2));
            using var scheduler = new LuauScriptScheduler(root);
            var phase = scheduler.CreatePhase("dispose-mutating", CreatePhaseOptions());
            using var firstRegistration = phase.Register(first.GetRequiredEntrypoint("update"));
            later = phase.Register(second.GetRequiredEntrypoint("update"));

            phase.Dispatch();

            Assert.That(calls, Is.EqualTo(new[] { 1, 2 }));
            Assert.That(later.IsDisposed, Is.True);
            calls.Clear();

            phase.Dispatch();

            Assert.That(calls, Is.EqualTo(new[] { 1 }));
        }

        [Test]
        public void RegisterRejectsEntrypointFromAnotherRoot()
        {
            using var firstRoot = CreateRoot(_ => { });
            using var secondRoot = CreateRoot(_ => { });
            using var instance = CreateInstance(secondRoot, "foreign", ExportingUpdate(1));
            using var scheduler = new LuauScriptScheduler(firstRoot);
            var phase = scheduler.CreatePhase("root-check", CreatePhaseOptions());

            var exception = Assert.Throws<ArgumentException>(() =>
                phase.Register(instance.GetRequiredEntrypoint("update")));

            Assert.That(exception.Message, Does.Contain("different Luau root"));
        }

        [Test]
        public void ReentrantDispatchIsRejectedClearly()
        {
            LuauScriptPhase phase = null;
            InvalidOperationException reentrantFailure = null;
            using var root = CreateRoot(_ =>
            {
                reentrantFailure = Assert.Throws<InvalidOperationException>(() =>
                    phase.Dispatch());
            });
            using var instance = CreateInstance(root, "reentrant", ExportingUpdate(1));
            using var scheduler = new LuauScriptScheduler(root);
            phase = scheduler.CreatePhase("reentrant", CreatePhaseOptions());
            using var registration = phase.Register(instance.GetRequiredEntrypoint("update"));

            phase.Dispatch();

            Assert.That(reentrantFailure, Is.Not.Null);
            Assert.That(reentrantFailure.Message, Does.Contain("already dispatching"));
        }

        [Test]
        public void CrossPhaseReentrantDispatchIsRejectedBeforeEnteringItsEntrypoint()
        {
            var calls = new List<int>();
            LuauScriptPhase otherPhase = null;
            InvalidOperationException reentrantFailure = null;
            using var root = CreateRoot(context =>
            {
                var id = context.Read<int>(0);
                calls.Add(id);
                if (id == 1)
                {
                    reentrantFailure = Assert.Throws<InvalidOperationException>(() =>
                        otherPhase.Dispatch());
                }
            });
            using var first = CreateInstance(root, "first-phase", ExportingUpdate(1));
            using var other = CreateInstance(root, "other-phase", ExportingUpdate(2));
            using var firstScheduler = new LuauScriptScheduler(root);
            using var otherScheduler = new LuauScriptScheduler(root);
            var firstPhase = firstScheduler.CreatePhase("first", CreatePhaseOptions());
            otherPhase = otherScheduler.CreatePhase("other", CreatePhaseOptions());
            using var firstRegistration = firstPhase.Register(
                first.GetRequiredEntrypoint("update"));
            using var otherRegistration = otherPhase.Register(
                other.GetRequiredEntrypoint("update"));

            firstPhase.Dispatch();

            Assert.That(reentrantFailure, Is.Not.Null);
            Assert.That(reentrantFailure.Message, Does.Contain("already dispatching"));
            Assert.That(calls, Is.EqualTo(new[] { 1 }));
            Assert.That(otherRegistration.IsEnabled, Is.True);

            calls.Clear();
            otherPhase.Dispatch();
            Assert.That(calls, Is.EqualTo(new[] { 2 }));
        }

        [Test]
        public void DispatchDuringDirectRootOperationIsRejectedBeforeRegistrationFailurePolicy()
        {
            var calls = new List<int>();
            LuauScriptPhase phase = null;
            InvalidOperationException reentrantFailure = null;
            using var root = CreateRoot(context =>
            {
                var id = context.Read<int>(0);
                if (id == 99)
                {
                    reentrantFailure = Assert.Throws<InvalidOperationException>(() =>
                        phase.Dispatch());
                    return;
                }

                calls.Add(id);
            });
            using var instance = CreateInstance(root, "direct-operation", ExportingUpdate(2));
            using var scheduler = new LuauScriptScheduler(root);
            phase = scheduler.CreatePhase("direct-operation", CreatePhaseOptions());
            using var registration = phase.Register(instance.GetRequiredEntrypoint("update"));

            using (root.DoString("record(99)"))
            {
            }

            Assert.That(reentrantFailure, Is.Not.Null);
            Assert.That(reentrantFailure.Message, Does.Contain("already executing"));
            Assert.That(registration.IsEnabled, Is.True);

            phase.Dispatch();
            Assert.That(calls, Is.EqualTo(new[] { 2 }));
        }

        [Test]
        public void PhaseCreationRejectsAReplacementContinuationScheduler()
        {
            var ownerScheduler = new ToggleContinuationScheduler { HasAccess = true };
            var replacementScheduler = new ToggleContinuationScheduler { HasAccess = true };
            using var root = CreateRoot(_ => { }, ownerScheduler);
            using var scheduler = new LuauScriptScheduler(root);
            var options = CreatePhaseOptions() with
            {
                InvocationOptions = CreatePhaseOptions().InvocationOptions with
                {
                    ContinuationScheduler = replacementScheduler,
                },
            };

            var exception = Assert.Throws<ArgumentException>(() =>
                scheduler.CreatePhase("invalid-policy", options));

            Assert.That(exception.Message, Does.Contain("cannot replace"));
        }

        [Test]
        public void WrongOwnerSchedulerAccessDoesNotDisableARegistration()
        {
            var calls = new List<int>();
            var ownerScheduler = new ToggleContinuationScheduler { HasAccess = true };
            using var root = CreateRoot(
                context => calls.Add(context.Read<int>(0)),
                ownerScheduler);
            using var instance = CreateInstance(root, "owner", ExportingUpdate(1));
            using var scheduler = new LuauScriptScheduler(root);
            var phase = scheduler.CreatePhase("owner", CreatePhaseOptions());
            using var registration = phase.Register(instance.GetRequiredEntrypoint("update"));
            ownerScheduler.HasAccess = false;

            var exception = Assert.Throws<InvalidOperationException>(() => phase.Dispatch());

            Assert.That(exception.Message, Does.Contain("owner scheduler"));
            Assert.That(registration.IsEnabled, Is.True);
            Assert.That(calls, Is.Empty);
        }

        [Test]
        public void DisableAndContinueDisablesBeforeCallbackAndRunsLaterRegistration()
        {
            var calls = new List<int>();
            var callbackSawDisabled = false;
            using var root = CreateRoot(context => calls.Add(context.Read<int>(0)));
            using var failing = CreateInstance(
                root,
                "failing",
                "return { update = function() error('expected failure') end }");
            using var later = CreateInstance(root, "later", ExportingUpdate(2));
            using var scheduler = new LuauScriptScheduler(root);
            var phase = scheduler.CreatePhase(
                "continue",
                CreatePhaseOptions(
                    LuauScriptPhaseFailureMode.DisableAndContinue,
                    (registration, _) =>
                    {
                        callbackSawDisabled = !registration.IsEnabled;
                        throw new InvalidOperationException(
                            "Failure observers cannot stop a continue phase.");
                    }));
            using var failingRegistration = phase.Register(
                failing.GetRequiredEntrypoint("update"));
            using var laterRegistration = phase.Register(
                later.GetRequiredEntrypoint("update"));

            var firstResult = phase.Dispatch();

            Assert.That(callbackSawDisabled, Is.True);
            Assert.That(failingRegistration.IsEnabled, Is.False);
            Assert.That(calls, Is.EqualTo(new[] { 2 }));
            Assert.That(firstResult.AttemptedCount, Is.EqualTo(2));
            Assert.That(firstResult.SucceededCount, Is.EqualTo(1));
            Assert.That(firstResult.FailedCount, Is.EqualTo(1));

            calls.Clear();
            var secondResult = phase.Dispatch();

            Assert.That(calls, Is.EqualTo(new[] { 2 }));
            Assert.That(secondResult.AttemptedCount, Is.EqualTo(1));
            Assert.That(secondResult.SkippedCount, Is.EqualTo(1));
        }

        [Test]
        public void StopAndThrowDoesNotRunLaterRegistration()
        {
            var calls = new List<int>();
            var callbackCount = 0;
            using var root = CreateRoot(context => calls.Add(context.Read<int>(0)));
            using var failing = CreateInstance(
                root,
                "fail-fast",
                "return { update = function() error('stop now') end }");
            using var later = CreateInstance(root, "later", ExportingUpdate(2));
            using var scheduler = new LuauScriptScheduler(root);
            var phase = scheduler.CreatePhase(
                "stop",
                CreatePhaseOptions(
                    LuauScriptPhaseFailureMode.StopAndThrow,
                    (_, __) =>
                    {
                        callbackCount++;
                        throw new InvalidOperationException(
                            "Failure observers cannot replace the script failure.");
                    }));
            using var failingRegistration = phase.Register(
                failing.GetRequiredEntrypoint("update"));
            using var laterRegistration = phase.Register(
                later.GetRequiredEntrypoint("update"));

            Assert.Catch<LuauException>(() => phase.Dispatch());

            Assert.That(callbackCount, Is.EqualTo(1));
            Assert.That(calls, Is.Empty);
            Assert.That(failingRegistration.IsEnabled, Is.True);
        }

        [Test]
        public void AggregateBudgetStopsAdmissionWithoutDisablingUncalledRegistrations()
        {
            var calls = new List<int>();
            var clock = new TestSchedulerClock();
            using var root = CreateRoot(context =>
            {
                calls.Add(context.Read<int>(0));
                clock.Advance(TimeSpan.FromMilliseconds(5));
            });
            using var first = CreateInstance(root, "first", ExportingUpdate(1));
            using var second = CreateInstance(root, "second", ExportingUpdate(2));
            using var third = CreateInstance(root, "third", ExportingUpdate(3));
            using var scheduler = new LuauScriptScheduler(root, clock);
            var phase = scheduler.CreatePhase(
                "budgeted",
                CreatePhaseOptions(
                    aggregateBudget: TimeSpan.FromMilliseconds(4)));
            using var firstRegistration = phase.Register(first.GetRequiredEntrypoint("update"));
            using var secondRegistration = phase.Register(second.GetRequiredEntrypoint("update"));
            using var thirdRegistration = phase.Register(third.GetRequiredEntrypoint("update"));

            var result = phase.Dispatch();

            Assert.That(calls, Is.EqualTo(new[] { 1 }));
            Assert.That(result.AttemptedCount, Is.EqualTo(1));
            Assert.That(result.SucceededCount, Is.EqualTo(1));
            Assert.That(result.SkippedCount, Is.EqualTo(2));
            Assert.That(result.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(5)));
            Assert.That(result.BudgetExhausted, Is.True);
            Assert.That(secondRegistration.IsEnabled, Is.True);
            Assert.That(thirdRegistration.IsEnabled, Is.True);
        }

        [Test]
        public void FailureObserverTimeDoesNotConsumeTheAggregateScriptBudget()
        {
            var calls = new List<int>();
            var clock = new TestSchedulerClock();
            using var root = CreateRoot(context => calls.Add(context.Read<int>(0)));
            using var failing = CreateInstance(
                root,
                "observer-failure",
                "return { update = function() error('expected') end }");
            using var later = CreateInstance(root, "observer-later", ExportingUpdate(2));
            using var scheduler = new LuauScriptScheduler(root, clock);
            var phase = scheduler.CreatePhase(
                "observer-budget",
                CreatePhaseOptions(
                    failureCallback: (_, __) =>
                        clock.Advance(TimeSpan.FromHours(1)),
                    aggregateBudget: TimeSpan.FromMilliseconds(4)));
            using var failingRegistration = phase.Register(
                failing.GetRequiredEntrypoint("update"));
            using var laterRegistration = phase.Register(
                later.GetRequiredEntrypoint("update"));

            var result = phase.Dispatch();

            Assert.That(calls, Is.EqualTo(new[] { 2 }));
            Assert.That(result.AttemptedCount, Is.EqualTo(2));
            Assert.That(result.FailedCount, Is.EqualTo(1));
            Assert.That(result.SucceededCount, Is.EqualTo(1));
            Assert.That(result.Elapsed, Is.EqualTo(TimeSpan.FromHours(1)));
            Assert.That(result.BudgetExhausted, Is.False);
        }

        [Test]
        public void BudgetExhaustionStillPrunesLaterDisposedInstances()
        {
            var calls = new List<int>();
            var clock = new TestSchedulerClock();
            using var root = CreateRoot(context =>
            {
                calls.Add(context.Read<int>(0));
                clock.Advance(TimeSpan.FromMilliseconds(5));
            });
            using var first = CreateInstance(root, "budget-first", ExportingUpdate(1));
            var disposed = CreateInstance(root, "budget-disposed", ExportingUpdate(2));
            using var later = CreateInstance(root, "budget-later", ExportingUpdate(3));
            using var scheduler = new LuauScriptScheduler(root, clock);
            var phase = scheduler.CreatePhase(
                "budget-prune",
                CreatePhaseOptions(aggregateBudget: TimeSpan.FromMilliseconds(4)));
            using var firstRegistration = phase.Register(first.GetRequiredEntrypoint("update"));
            var disposedRegistration = phase.Register(
                disposed.GetRequiredEntrypoint("update"));
            using var laterRegistration = phase.Register(later.GetRequiredEntrypoint("update"));
            disposed.Dispose();

            var result = phase.Dispatch();

            Assert.That(calls, Is.EqualTo(new[] { 1 }));
            Assert.That(result.BudgetExhausted, Is.True);
            Assert.That(result.AttemptedCount, Is.EqualTo(1));
            Assert.That(result.SkippedCount, Is.EqualTo(2));
            Assert.That(disposedRegistration.IsDisposed, Is.True);
            Assert.That(laterRegistration.IsEnabled, Is.True);
        }

        [Test]
        public void DisposedRegistrationIsRemovedFromAllPhaseStorageImmediately()
        {
            using var root = CreateRoot(_ => { });
            using var instance = CreateInstance(root, "released-token", ExportingUpdate(1));
            using var scheduler = new LuauScriptScheduler(root);
            var phase = scheduler.CreatePhase("release-token", CreatePhaseOptions());
            var registration = phase.Register(instance.GetRequiredEntrypoint("update"));
            phase.Dispatch();
            Assert.That(phase.RetainedRegistrationCount, Is.EqualTo(2));

            registration.Dispose();

            Assert.That(phase.RetainedRegistrationCount, Is.Zero);
        }

        [Test]
        public void PrunedDisposedInstanceIsRemovedFromAllPhaseStorage()
        {
            using var root = CreateRoot(_ => { });
            var instance = CreateInstance(root, "released-instance", ExportingUpdate(1));
            using var scheduler = new LuauScriptScheduler(root);
            var phase = scheduler.CreatePhase("release-instance", CreatePhaseOptions());
            phase.Register(instance.GetRequiredEntrypoint("update"));
            instance.Dispose();

            phase.Dispatch();

            Assert.That(phase.RetainedRegistrationCount, Is.Zero);
        }

        [Test]
        public void DisposedInstanceIsPrunedAndDoesNotPreventLaterRegistration()
        {
            var calls = new List<int>();
            using var root = CreateRoot(context => calls.Add(context.Read<int>(0)));
            var disposed = CreateInstance(root, "disposed", ExportingUpdate(1));
            using var later = CreateInstance(root, "later", ExportingUpdate(2));
            using var scheduler = new LuauScriptScheduler(root);
            var phase = scheduler.CreatePhase("prune", CreatePhaseOptions());
            var disposedRegistration = phase.Register(
                disposed.GetRequiredEntrypoint("update"));
            using var laterRegistration = phase.Register(
                later.GetRequiredEntrypoint("update"));
            disposed.Dispose();

            var result = phase.Dispatch();

            Assert.That(calls, Is.EqualTo(new[] { 2 }));
            Assert.That(result.AttemptedCount, Is.EqualTo(1));
            Assert.That(result.SkippedCount, Is.EqualTo(1));
            Assert.That(disposedRegistration.IsDisposed, Is.True);
        }

        [Test]
        public async Task AssetFactoryUsesBoundedLaneAndScheduledCapabilityChangesGameObject()
        {
            const string source =
                "return { update = function(amount) " +
                "self.transform:Translate(vector.create(amount, 0, 0)) end }";
            var asset = ScriptableObject.CreateInstance<LuauAsset>();
            asset.name = "@unity/script-instance-capability.luau";
            asset.SetSource(source, Encoding.UTF8.GetBytes(source));
            var gameObject = new GameObject("Scripted");
            using var root = CreateRoot(_ => { });
            var providerCalls = 0;
            using var providerOverride = LuauUnity.OverrideAssetCompilationProviderForTests(
                (utf8Source, options, cancellationToken) =>
                {
                    Interlocked.Increment(ref providerCalls);
                    return new ValueTask<LuauCompileResult>(
                        LuauCompileResult.Success(
                            LuauCompiler.Compile(utf8Source.Span, options)));
                });

            try
            {
                using var instance = await root.CreateScriptInstanceAsync(
                    asset,
                    thread =>
                    {
                        using var self = root.CreateHandle(gameObject);
                        thread["self"] = self;
                    });
                using var scheduler = new LuauScriptScheduler(root);
                var phase = scheduler.CreatePhase("update", CreatePhaseOptions());
                using var registration = phase.Register(
                    instance.GetRequiredEntrypoint("update"));

                var result = phase.Dispatch((LuauValue)2d);

                Assert.That(providerCalls, Is.EqualTo(1));
                Assert.That(instance.Name, Is.EqualTo(asset.name));
                Assert.That(result.SucceededCount, Is.EqualTo(1));
                Assert.That(gameObject.transform.position.x, Is.EqualTo(2f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void TrackedRootTeardownIsIdempotentAndIgnoresAlreadyDisposedRoots()
        {
            var alreadyDisposed = CreateRoot(_ => { });
            var live = CreateRoot(_ => { });
            var liveContext = live.Context;
            alreadyDisposed.Dispose();
            try
            {
                LuauUnity.DisposeTrackedRoots();

                Assert.That(alreadyDisposed.IsDisposed, Is.True);
                Assert.That(live.IsDisposed, Is.True);
                Assert.That(
                    liveContext.IsDisposed,
                    Is.True,
                    "An idle tracked root must complete native context teardown before the drain returns.");
                Assert.DoesNotThrow(LuauUnity.DisposeTrackedRoots);
            }
            finally
            {
                LuauUnity.ResetTrackedRootAdmissionAfterDrainForTests();
            }
        }

        [Test]
        public void RootAdmissionCannotRacePastLifecycleTeardown()
        {
            var configured = false;
            LuauUnity.DisposeTrackedRoots();
            try
            {
                Assert.Throws<ObjectDisposedException>(() =>
                    LuauUnity.CreateState(new LuauUnityOptions
                    {
                        CaptureUnitySynchronizationContext = false,
                        ConfigureHostApis = _ => configured = true,
                        Log = _ => { },
                    }));

                Assert.That(
                    configured,
                    Is.False,
                    "Root admission must be rejected before native state configuration is published.");
            }
            finally
            {
                LuauUnity.ResetTrackedRootAdmissionAfterDrainForTests();
            }
        }

        [Test]
        public void InFlightRootConfigurationIsTrackedBeforeLifecycleDrain()
        {
            using var configureEntered = new ManualResetEventSlim();
            using var releaseConfigure = new ManualResetEventSlim();
            LuauVmContext configuringContext = null;
            Task<LuauState> creation = null;
            Task drain = null;
            try
            {
                creation = Task.Run(() => LuauUnity.CreateState(new LuauUnityOptions
                {
                    CaptureUnitySynchronizationContext = false,
                    ConfigureHostApis = state =>
                    {
                        configuringContext = state.Context;
                        configureEntered.Set();
                        releaseConfigure.Wait();
                    },
                    Log = _ => { },
                }));

                Assert.That(
                    configureEntered.Wait(TimeSpan.FromSeconds(2)),
                    Is.True,
                    "CreateState did not reach its host-configuration callback.");

                drain = Task.Run(LuauUnity.DisposeTrackedRoots);
                Assert.That(
                    drain.Wait(TimeSpan.FromSeconds(2)),
                    Is.True,
                    "Lifecycle drain waited on host configuration instead of disposing the early-tracked root.");
                Assert.That(configuringContext, Is.Not.Null);
                Assert.That(
                    configuringContext.IsDisposed,
                    Is.True,
                    "Lifecycle drain returned before closing the in-flight root context.");

                releaseConfigure.Set();
                Assert.Throws<ObjectDisposedException>(() =>
                    creation.GetAwaiter().GetResult());
            }
            finally
            {
                releaseConfigure.Set();
                try
                {
                    if (creation != null)
                    {
                        creation.GetAwaiter().GetResult()?.Dispose();
                    }
                }
                catch
                {
                    // The expected path rejects publication after teardown.
                }
                drain?.GetAwaiter().GetResult();
                LuauUnity.DisposeTrackedRoots();
                LuauUnity.ResetTrackedRootAdmissionAfterDrainForTests();
            }
        }

        static LuauState CreateRoot(
            Action<LuauCallContext> record,
            ILuauContinuationScheduler continuationScheduler = null)
        {
            return LuauUnity.CreateState(new LuauUnityOptions
            {
                CaptureUnitySynchronizationContext = false,
                StateOptions = LuauStateOptions.Default with
                {
                    DefaultExecutionOptions = LuauExecutionOptions.Default with
                    {
                        ContinuationScheduler = continuationScheduler,
                    },
                },
                ConfigureHostApis = state =>
                    state["record"] = state.CreateFunction("record", record),
                Log = _ => { },
            });
        }

        static LuauScriptInstance CreateInstance(
            LuauState root,
            string name,
            string source)
        {
            return LuauScriptInstance.CreateAsync(
                    root,
                    name,
                    (thread, _) => new ValueTask<LuauResultScope>(
                        thread.DoString(
                            Encoding.UTF8.GetBytes(source),
                            Encoding.UTF8.GetBytes(name))))
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }

        static string ExportingUpdate(int id)
        {
            return "return { update = function() record(" + id + ") end }";
        }

        static LuauScriptPhaseOptions CreatePhaseOptions(
            LuauScriptPhaseFailureMode failureMode =
                LuauScriptPhaseFailureMode.DisableAndContinue,
            Action<LuauScriptRegistration, Exception> failureCallback = null,
            TimeSpan? aggregateBudget = null)
        {
            return new LuauScriptPhaseOptions
            {
                InvocationOptions = LuauExecutionOptions.Default with
                {
                    WallClockLimit = TimeSpan.FromSeconds(1),
                    InterruptCountLimit = 100_000,
                    MaxResultCount = 0,
                },
                AggregateWallClockBudget = aggregateBudget ?? TimeSpan.FromSeconds(5),
                FailureMode = failureMode,
                FailureCallback = failureCallback,
            };
        }

        sealed class TestSchedulerClock : ILuauScriptSchedulerClock
        {
            long ticks;

            public void Advance(TimeSpan elapsed)
            {
                ticks += elapsed.Ticks;
            }

            public long GetTimestamp()
            {
                return ticks;
            }

            public TimeSpan GetElapsedTime(long startTimestamp, long endTimestamp)
            {
                return TimeSpan.FromTicks(Math.Max(0L, endTimestamp - startTimestamp));
            }
        }

        sealed class ToggleContinuationScheduler : ILuauContinuationScheduler
        {
            public bool HasAccess { get; set; }

            public bool CheckAccess()
            {
                return HasAccess;
            }

            public void Post(Action continuation)
            {
                if (continuation == null)
                {
                    throw new ArgumentNullException(nameof(continuation));
                }

                continuation();
            }
        }
    }
}
