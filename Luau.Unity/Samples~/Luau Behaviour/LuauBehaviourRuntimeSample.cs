using System;
using System.Collections.Generic;
using Luau;
using Luau.Unity;
using UnityEngine;

namespace Luau.Unity.Samples.LuauBehaviour
{
    /// <summary>
    /// Sample scene-level host that shares one trust-domain root and one bounded
    /// Update phase among explicitly connected scripted behaviours.
    /// </summary>
    public sealed class LuauBehaviourRuntimeSample : MonoBehaviour
    {
        readonly Dictionary<LuauScriptRegistration, LuauBehaviourSample> owners =
            new Dictionary<LuauScriptRegistration, LuauBehaviourSample>();

        LuauState root;
        LuauScriptScheduler scheduler;
        LuauScriptPhase updatePhase;

        internal LuauState Root
        {
            get
            {
                if (root == null || root.IsDisposed)
                {
                    throw new InvalidOperationException(
                        "The Luau Behaviour runtime host is not available.");
                }

                return root;
            }
        }

        void Awake()
        {
            root = LuauUnity.CreateState();
            scheduler = new LuauScriptScheduler(root);
            updatePhase = scheduler.CreatePhase(
                "Update",
                new LuauScriptPhaseOptions
                {
                    InvocationOptions = LuauExecutionOptions.Default with
                    {
                        WallClockLimit = TimeSpan.FromMilliseconds(2),
                        InterruptCountLimit = 10_000,
                        MaxResultCount = 0,
                    },
                    AggregateWallClockBudget = TimeSpan.FromMilliseconds(4),
                    FailureMode = LuauScriptPhaseFailureMode.DisableAndContinue,
                    FailureCallback = HandleInvocationFailure,
                });
        }

        internal LuauScriptRegistration Register(
            LuauBehaviourSample owner,
            LuauScriptEntrypoint entrypoint,
            int order)
        {
            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }

            var registration = updatePhase.Register(entrypoint, order);
            owners.Add(registration, owner);
            return registration;
        }

        internal void Unregister(LuauScriptRegistration registration)
        {
            if (registration == null)
            {
                return;
            }

            owners.Remove(registration);
            registration.Dispose();
        }

        void Update()
        {
            if (updatePhase == null || root == null || root.IsDisposed)
            {
                return;
            }

            updatePhase.Dispatch((LuauValue)(double)Time.deltaTime);
        }

        void HandleInvocationFailure(
            LuauScriptRegistration registration,
            Exception exception)
        {
            if (owners.TryGetValue(registration, out var owner))
            {
                owner.DisableAfterFailure(exception);
            }
        }

        void OnDestroy()
        {
            owners.Clear();
            scheduler?.Dispose();
            scheduler = null;
            updatePhase = null;
            root?.Dispose();
            root = null;
        }
    }
}
