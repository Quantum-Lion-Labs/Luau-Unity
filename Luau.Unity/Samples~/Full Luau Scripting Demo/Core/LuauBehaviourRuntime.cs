using System;
using System.Collections.Generic;
using System.Threading;
using Luau;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Luau.Unity.Samples.FullLuauScriptingDemo
{
    /// <summary>
    /// Scene-level owner for one Luau trust domain, its shared table, its
    /// bounded Unity lifecycle phases, and the host's limits on what that
    /// domain's scripts may spawn and read as input.
    /// </summary>
    public sealed class LuauBehaviourRuntime : MonoBehaviour
    {
        static readonly LuauExecutionOptions BoundedHookOptions =
            LuauExecutionOptions.Default with
            {
                WallClockLimit = TimeSpan.FromMilliseconds(2),
                InterruptCountLimit = 10_000,
                MaxResultCount = 0,
            };

        static readonly LuauExecutionOptions BoundedInitializationOptions =
            LuauExecutionOptions.Default with
            {
                // Host-library registration and one-time script initialization
                // share the root default. Keep that lane bounded, but allow for
                // Unity Editor JIT/import overhead that is not part of a frame hook.
                WallClockLimit = TimeSpan.FromMilliseconds(50),
                InterruptCountLimit = 100_000,
                MaxResultCount = 1,
            };

        static readonly LuauExecutionOptions BoundedStartOptions =
            BoundedInitializationOptions with
            {
                MaxResultCount = 0,
            };

        [SerializeField]
        [Tooltip(
            "Admit package-generated first-party bytecode. Leave disabled for " +
            "ordinary source scripts and untrusted mod content.")]
        bool useFirstPartyBytecode;

        [Header("Host Limits")]
        [SerializeField]
        [Min(0)]
        [Tooltip(
            "Maximum live prefab instances all behaviours on this runtime may own " +
            "together, including behaviours on spawned objects. Each behaviour's " +
            "own Max Spawned Objects applies as well.")]
        int maxSpawnedObjects = 64;

        [SerializeField]
        [Tooltip(
            "The keys, mouse buttons, and touch input this runtime's scripts may " +
            "read through Input. Anything else is a script error.")]
        LuauInputLibrary.Policy inputPolicy = new LuauInputLibrary.Policy();

        readonly List<LuauBehaviour> behaviours = new List<LuauBehaviour>();
        readonly List<GameObject> spawnedObjects = new List<GameObject>();
        readonly List<LuauBehaviour> pendingBehaviours =
            new List<LuauBehaviour>();
        readonly Dictionary<LuauScriptRegistration, LuauBehaviour> owners =
            new Dictionary<LuauScriptRegistration, LuauBehaviour>();

        LuauState root;
        LuauTable shared;
        LuauScriptScheduler scheduler;
        LuauScriptPhase updatePhase;
        LuauScriptPhase fixedUpdatePhase;
        LuauScriptPhase lateUpdatePhase;
        Exception startupFailure;
        bool awakeCompleted;
        bool startCalled;
        bool initializationRequested;
        bool initializing;
        bool ready;
        int destroyed;

        /// <summary>Gets whether all currently attached behaviours are initialized.</summary>
        public bool IsReady => ready && !initializing && Volatile.Read(ref destroyed) == 0;

        internal LuauState Root
        {
            get
            {
                if (root == null || root.IsDisposed)
                {
                    throw new InvalidOperationException(
                        "The Full Luau Scripting Demo runtime is not available.",
                        startupFailure);
                }

                return root;
            }
        }

        internal LuauTable Shared
        {
            get
            {
                if (shared == null || shared.IsDisposed)
                {
                    throw new InvalidOperationException(
                        "The Full Luau Scripting Demo shared table is not available.");
                }

                return shared;
            }
        }

        internal LuauExecutionOptions InvocationOptions => BoundedHookOptions;

        internal LuauExecutionOptions StartInvocationOptions =>
            BoundedStartOptions;

        void Awake()
        {
            try
            {
                var inputLibrary = new LuauInputLibrary(inputPolicy);
                root = LuauUnity.CreateState(new LuauUnityOptions
                {
                    UseFirstPartyBytecode = useFirstPartyBytecode,
                    StateOptions = LuauStateOptions.Default with
                    {
                        DefaultExecutionOptions =
                            BoundedInitializationOptions,
                    },
                    ConfigureHostApis = state =>
                    {
                        state.OpenLibrary(new LuauQuaternionLibrary());
                        state.OpenLibrary(inputLibrary);
                    },
                });
                WarmHostApis(inputLibrary);
                shared = root.CreateTable();
                scheduler = new LuauScriptScheduler(root);
                updatePhase = CreatePhase("Update");
                fixedUpdatePhase = CreatePhase("FixedUpdate");
                lateUpdatePhase = CreatePhase("LateUpdate");
            }
            catch (Exception exception)
            {
                startupFailure = exception;
                Debug.LogError(
                    "The Full Luau Scripting Demo runtime could not start.\n" +
                    exception,
                    this);
                for (var index = 0; index < behaviours.Count; index++)
                {
                    behaviours[index]?.DisableAfterFailure(
                        new InvalidOperationException(
                            "The assigned Luau runtime failed during Awake.",
                            exception));
                }
                enabled = false;
            }
            finally
            {
                awakeCompleted = true;
            }
        }

        void Start()
        {
            startCalled = true;
            initializationRequested = true;
            BeginInitializationPump();
        }

        /// <summary>
        /// Warms the generated host-call thunks outside the 2 ms frame lane so
        /// the first real input frame is not charged for JIT. Warming is an
        /// optimization: a failure here costs one frame of jitter, so it is
        /// reported and swallowed rather than failing the whole runtime. Only
        /// the input the policy enables is touched.
        /// </summary>
        void WarmHostApis(LuauInputLibrary inputLibrary)
        {
            if (InputSystem.devices.Count == 0)
            {
                Debug.LogWarning(
                    "The Input System reports no devices, so this scene will not " +
                    "receive key, mouse, or touch input. Set Active Input Handling " +
                    "to \"Input System Package (New)\" or \"Both\" in Player Settings.",
                    this);
            }

            try
            {
                using (root.DoString(
                    inputLibrary.CreateWarmupSource() +
                    "Quaternion.Euler(vector.create(0, 0, 0))\n" +
                    "return true",
                    "@FullLuauScriptingDemo/HostApiWarmup.luau"))
                {
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "The Full Luau Scripting Demo host-API warmup failed. Scripts " +
                    "still run; the first frame that calls Input or Quaternion may " +
                    "hitch.\n" + exception,
                    this);
            }
        }

        LuauScriptPhase CreatePhase(string phaseName)
        {
            return scheduler.CreatePhase(
                phaseName,
                new LuauScriptPhaseOptions
                {
                    InvocationOptions = BoundedHookOptions,
                    AggregateWallClockBudget = TimeSpan.FromMilliseconds(4),
                    FailureMode = LuauScriptPhaseFailureMode.DisableAndContinue,
                    FailureCallback = HandleInvocationFailure,
                });
        }

        internal void Attach(LuauBehaviour behaviour)
        {
            if (behaviour == null)
            {
                throw new ArgumentNullException(nameof(behaviour));
            }
            if (Volatile.Read(ref destroyed) != 0)
            {
                behaviour.DisableAfterFailure(new ObjectDisposedException(
                    nameof(LuauBehaviourRuntime)));
                return;
            }
            if (behaviours.Contains(behaviour))
            {
                return;
            }

            behaviours.Add(behaviour);
            pendingBehaviours.Add(behaviour);
            ready = false;
            if (awakeCompleted && startupFailure != null)
            {
                behaviour.DisableAfterFailure(new InvalidOperationException(
                    "The assigned Luau runtime failed during Awake.",
                    startupFailure));
                return;
            }

            if (startCalled)
            {
                // A behaviour can attach while a Luau callback instantiates or
                // activates its GameObject. Pump from the next host Update so a
                // second VM execution cannot begin reentrantly inside that callback.
                initializationRequested = true;
            }
        }

        /// <summary>
        /// Refuses a spawn once the behaviours on this runtime own
        /// <see cref="maxSpawnedObjects"/> live instances between them.
        /// Destroyed instances stop counting.
        /// </summary>
        internal void ThrowIfSpawnLimitReached()
        {
            for (var index = spawnedObjects.Count - 1; index >= 0; index--)
            {
                if (spawnedObjects[index] == null)
                {
                    spawnedObjects.RemoveAt(index);
                }
            }

            if (spawnedObjects.Count >= maxSpawnedObjects)
            {
                throw new LuauException(
                    "LuauBehaviourRuntime '" + name + "' reached its limit of " +
                    maxSpawnedObjects + " live spawned object(s) across all of " +
                    "its behaviours.");
            }
        }

        internal void AddSpawnedObject(GameObject spawned)
        {
            spawnedObjects.Add(spawned);
        }

        internal void RemoveSpawnedObject(GameObject spawned)
        {
            spawnedObjects.Remove(spawned);
        }

        internal void Detach(LuauBehaviour behaviour)
        {
            if (behaviour == null)
            {
                return;
            }

            pendingBehaviours.Remove(behaviour);
            behaviours.Remove(behaviour);
        }

        async void BeginInitializationPump()
        {
            if (initializing ||
                !startCalled ||
                !initializationRequested ||
                startupFailure != null ||
                Volatile.Read(ref destroyed) != 0)
            {
                return;
            }

            initializationRequested = false;
            initializing = true;
            ready = false;
            try
            {
                while (pendingBehaviours.Count != 0 &&
                    Volatile.Read(ref destroyed) == 0)
                {
                    var batch = pendingBehaviours.ToArray();
                    pendingBehaviours.Clear();
                    Array.Sort(batch, CompareBehaviours);

                    for (var index = 0; index < batch.Length; index++)
                    {
                        var behaviour = batch[index];
                        if (behaviour == null ||
                            !behaviours.Contains(behaviour) ||
                            behaviour.IsDestroyed)
                        {
                            continue;
                        }

                        try
                        {
                            await behaviour.InitializeAsync(
                                this,
                                destroyCancellationToken);
                        }
                        catch (OperationCanceledException)
                            when (destroyCancellationToken.IsCancellationRequested ||
                                behaviour.IsDestroyed)
                        {
                            // Teardown owns cleanup when either Unity object dies.
                        }
                        catch (Exception exception)
                        {
                            behaviour.DisableAfterFailure(exception);
                        }
                    }
                }

                ready = Volatile.Read(ref destroyed) == 0;
            }
            catch (Exception exception)
            {
                startupFailure = exception;
                Debug.LogError(
                    "The Full Luau Scripting Demo initialization pump failed.\n" +
                    exception,
                    this);
                enabled = false;
            }
            finally
            {
                initializing = false;
                if (pendingBehaviours.Count != 0 &&
                    startupFailure == null &&
                    Volatile.Read(ref destroyed) == 0)
                {
                    initializationRequested = true;
                }
            }
        }

        static int CompareBehaviours(LuauBehaviour left, LuauBehaviour right)
        {
            var order = left.ExecutionOrder.CompareTo(right.ExecutionOrder);
            return order != 0
                ? order
                : string.CompareOrdinal(left.StableSortKey, right.StableSortKey);
        }

        internal LuauScriptRegistration RegisterUpdate(
            LuauBehaviour owner,
            LuauScriptEntrypoint entrypoint,
            int order)
        {
            return Register(owner, updatePhase, entrypoint, order);
        }

        internal LuauScriptRegistration RegisterFixedUpdate(
            LuauBehaviour owner,
            LuauScriptEntrypoint entrypoint,
            int order)
        {
            return Register(owner, fixedUpdatePhase, entrypoint, order);
        }

        internal LuauScriptRegistration RegisterLateUpdate(
            LuauBehaviour owner,
            LuauScriptEntrypoint entrypoint,
            int order)
        {
            return Register(owner, lateUpdatePhase, entrypoint, order);
        }

        LuauScriptRegistration Register(
            LuauBehaviour owner,
            LuauScriptPhase phase,
            LuauScriptEntrypoint entrypoint,
            int order)
        {
            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }
            if (phase == null || scheduler == null || scheduler.IsDisposed)
            {
                throw new ObjectDisposedException(nameof(LuauBehaviourRuntime));
            }

            var registration = phase.Register(entrypoint, order);
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
            if (initializationRequested && !initializing)
            {
                BeginInitializationPump();
            }

            if (!IsReady)
            {
                return;
            }

            updatePhase.Dispatch((LuauValue)(double)Time.deltaTime);
        }

        void FixedUpdate()
        {
            if (!IsReady)
            {
                return;
            }

            fixedUpdatePhase.Dispatch((LuauValue)(double)Time.fixedDeltaTime);
        }

        void LateUpdate()
        {
            if (!IsReady)
            {
                return;
            }

            lateUpdatePhase.Dispatch((LuauValue)(double)Time.deltaTime);
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
            if (Interlocked.Exchange(ref destroyed, 1) != 0)
            {
                return;
            }

            ready = false;
            pendingBehaviours.Clear();

            // Stop phase dispatch and invalidate registrations before releasing
            // their owning instances.
            scheduler?.Dispose();
            scheduler = null;
            updatePhase = null;
            fixedUpdatePhase = null;
            lateUpdatePhase = null;
            owners.Clear();

            var snapshot = behaviours.ToArray();
            behaviours.Clear();
            for (var index = snapshot.Length - 1; index >= 0; index--)
            {
                snapshot[index]?.ShutdownFromRuntime();
            }

            spawnedObjects.Clear();
            shared?.Dispose();
            shared = null;
            root?.Dispose();
            root = null;
        }
    }
}
