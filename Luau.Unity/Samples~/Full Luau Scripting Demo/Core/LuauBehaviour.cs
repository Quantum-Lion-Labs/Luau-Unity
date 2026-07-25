using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Luau;
using Luau.Unity;
using UnityEngine;
using Object = UnityEngine.Object;
using NumericsVector3 = System.Numerics.Vector3;

namespace Luau.Unity.Samples.FullLuauScriptingDemo
{
    /// <summary>
    /// Hosts one sandboxed Luau script instance and exposes only explicitly
    /// configured Unity capabilities to it.
    /// </summary>
    public sealed class LuauBehaviour : MonoBehaviour
    {
        [Serializable]
        public sealed class ObjectReference
        {
            [SerializeField]
            string referenceName;

            [SerializeField]
            Object target;

            public string Name => referenceName;
            public Object Target => target;
        }

        [Serializable]
        public sealed class PrefabReference
        {
            [SerializeField]
            string referenceName;

            [SerializeField]
            GameObject prefab;

            public string Name => referenceName;
            public GameObject Prefab => prefab;
        }

        [SerializeField]
        LuauBehaviourRuntime runtimeHost;

        [SerializeField]
        LuauAsset script;

        [SerializeField]
        int executionOrder;

        [Header("Script Bindings")]
        [SerializeField]
        [Tooltip(
            "Named GameObject, Transform, Rigidbody2D, Collider2D, " +
            "SpriteRenderer, AudioSource, or TextMesh capabilities exposed in refs.")]
        ObjectReference[] objectReferences = Array.Empty<ObjectReference>();

        [SerializeField]
        [Tooltip(
            "Named prefab assets admitted by this behaviour's spawnPrefab function.")]
        PrefabReference[] prefabReferences = Array.Empty<PrefabReference>();

        [SerializeField]
        [Min(0)]
        [Tooltip(
            "Maximum live prefab instances this behaviour may own. Set to zero " +
            "to disable spawning even when prefab references are assigned.")]
        int maxSpawnedObjects = 32;

        readonly List<GameObject> spawnedObjects = new List<GameObject>();

        Dictionary<string, GameObject> prefabCatalog;
        LuauScriptInstance instance;
        LuauScriptEntrypoint collisionEnterEntrypoint;
        LuauScriptEntrypoint collisionExitEntrypoint;
        LuauScriptEntrypoint triggerEnterEntrypoint;
        LuauScriptEntrypoint triggerExitEntrypoint;
        LuauScriptEntrypoint destroyEntrypoint;
        LuauScriptRegistration updateRegistration;
        LuauScriptRegistration fixedUpdateRegistration;
        LuauScriptRegistration lateUpdateRegistration;
        bool initializationAttempted;
        bool initialized;
        bool failureLogged;
        int shutdown;
        int destroyed;

        /// <summary>Gets the order used for initialization and lifecycle phases.</summary>
        public int ExecutionOrder => executionOrder;

        /// <summary>Gets whether this component owns a live initialized instance.</summary>
        public bool IsInitialized =>
            initialized &&
            Volatile.Read(ref shutdown) == 0 &&
            instance != null &&
            !instance.IsDisposed;

        internal bool IsDestroyed => Volatile.Read(ref destroyed) != 0;

        internal string StableSortKey
        {
            get
            {
                var builder = new StringBuilder();
                builder.Append(gameObject.scene.path);
                builder.Append('|');

                var current = transform;
                while (current != null)
                {
                    builder.Insert(
                        builder.ToString().IndexOf('|') + 1,
                        current.GetSiblingIndex().ToString(
                            "D8",
                            CultureInfo.InvariantCulture) + "/");
                    current = current.parent;
                }

                var components = GetComponents<LuauBehaviour>();
                for (var index = 0; index < components.Length; index++)
                {
                    if (ReferenceEquals(components[index], this))
                    {
                        builder.Append('|');
                        builder.Append(index.ToString(
                            "D4",
                            CultureInfo.InvariantCulture));
                        break;
                    }
                }

                return builder.ToString();
            }
        }

        void Awake()
        {
            if (runtimeHost == null)
            {
                DisableAfterFailure(new InvalidOperationException(
                    "Assign an explicit LuauBehaviourRuntime."));
                return;
            }

            runtimeHost.Attach(this);
        }

        internal async ValueTask InitializeAsync(
            LuauBehaviourRuntime host,
            CancellationToken hostCancellationToken)
        {
            if (initializationAttempted ||
                IsDestroyed ||
                Volatile.Read(ref shutdown) != 0)
            {
                return;
            }
            if (!ReferenceEquals(host, runtimeHost))
            {
                throw new InvalidOperationException(
                    "The behaviour was attached to a different Luau runtime.");
            }

            initializationAttempted = true;
            LuauScriptInstance createdInstance = null;
            LuauScriptRegistration createdUpdate = null;
            LuauScriptRegistration createdFixedUpdate = null;
            LuauScriptRegistration createdLateUpdate = null;
            using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                hostCancellationToken,
                destroyCancellationToken))
            {
                try
                {
                    if (script == null)
                    {
                        throw new InvalidOperationException(
                            "Assign a LuauAsset to the LuauBehaviour.");
                    }

                    prefabCatalog = ValidateBindingsAndCreatePrefabCatalog();
                    createdInstance = await host.Root.CreateScriptInstanceAsync(
                        script,
                        ConfigureThread,
                        cancellation.Token);

                    cancellation.Token.ThrowIfCancellationRequested();
                    if (IsDestroyed || Volatile.Read(ref shutdown) != 0)
                    {
                        throw new OperationCanceledException(cancellation.Token);
                    }

                    if (createdInstance.TryGetEntrypoint(
                        "start",
                        out var startEntrypoint))
                    {
                        await startEntrypoint.InvokeVoidAsync(
                            cancellation.Token,
                            host.StartInvocationOptions);
                    }

                    if (createdInstance.TryGetEntrypoint(
                        "update",
                        out var updateEntrypoint))
                    {
                        createdUpdate = host.RegisterUpdate(
                            this,
                            updateEntrypoint,
                            executionOrder);
                    }
                    if (createdInstance.TryGetEntrypoint(
                        "fixedUpdate",
                        out var fixedUpdateEntrypoint))
                    {
                        createdFixedUpdate = host.RegisterFixedUpdate(
                            this,
                            fixedUpdateEntrypoint,
                            executionOrder);
                    }
                    if (createdInstance.TryGetEntrypoint(
                        "lateUpdate",
                        out var lateUpdateEntrypoint))
                    {
                        createdLateUpdate = host.RegisterLateUpdate(
                            this,
                            lateUpdateEntrypoint,
                            executionOrder);
                    }

                    createdInstance.TryGetEntrypoint(
                        "collisionEnter2D",
                        out collisionEnterEntrypoint);
                    createdInstance.TryGetEntrypoint(
                        "collisionExit2D",
                        out collisionExitEntrypoint);
                    createdInstance.TryGetEntrypoint(
                        "triggerEnter2D",
                        out triggerEnterEntrypoint);
                    createdInstance.TryGetEntrypoint(
                        "triggerExit2D",
                        out triggerExitEntrypoint);
                    createdInstance.TryGetEntrypoint(
                        "destroy",
                        out destroyEntrypoint);

                    instance = createdInstance;
                    updateRegistration = createdUpdate;
                    fixedUpdateRegistration = createdFixedUpdate;
                    lateUpdateRegistration = createdLateUpdate;
                    createdInstance = null;
                    createdUpdate = null;
                    createdFixedUpdate = null;
                    createdLateUpdate = null;
                    initialized = true;
                    SetRegistrationState(isActiveAndEnabled);
                }
                finally
                {
                    host.Unregister(createdLateUpdate);
                    host.Unregister(createdFixedUpdate);
                    host.Unregister(createdUpdate);
                    createdInstance?.Dispose();
                    if (!initialized)
                    {
                        prefabCatalog = null;
                        collisionEnterEntrypoint = null;
                        collisionExitEntrypoint = null;
                        triggerEnterEntrypoint = null;
                        triggerExitEntrypoint = null;
                        destroyEntrypoint = null;
                        DestroySpawnedObjects();
                    }
                }
            }
        }

        void ConfigureThread(LuauState thread)
        {
            var host = runtimeHost;
            if (host == null || IsDestroyed || Volatile.Read(ref shutdown) != 0)
            {
                throw new ObjectDisposedException(nameof(LuauBehaviour));
            }

            using (var self = host.Root.CreateHandle(
                gameObject,
                LuauUnityCapabilities.GameObjectDescriptor))
            {
                thread["self"] = self;
            }

            var references = objectReferences ?? Array.Empty<ObjectReference>();
            using (var refs = thread.CreateTable(0, references.Length))
            {
                for (var index = 0; index < references.Length; index++)
                {
                    var reference = references[index];
                    using (var handle =
                        LuauUnityCapabilities.CreateSupportedHandle(
                            host.Root,
                            reference.Target))
                    {
                        refs.RawSet(reference.Name, handle);
                    }
                }

                thread["refs"] = refs;
            }

            thread["shared"] = host.Shared;
            using (var spawnPrefab = thread.CreateFunction(
                "spawnPrefab",
                SpawnPrefab))
            {
                thread["spawnPrefab"] = spawnPrefab;
            }
        }

        Dictionary<string, GameObject> ValidateBindingsAndCreatePrefabCatalog()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var references = objectReferences ?? Array.Empty<ObjectReference>();
            for (var index = 0; index < references.Length; index++)
            {
                var reference = references[index];
                if (reference == null)
                {
                    throw new InvalidOperationException(
                        "Object reference slot " + index + " is missing.");
                }

                ValidateReferenceName(reference.Name, "object reference", index);
                if (!names.Add(reference.Name))
                {
                    throw new InvalidOperationException(
                        "Object reference name '" + reference.Name +
                        "' is assigned more than once.");
                }
                if (reference.Target == null)
                {
                    throw new InvalidOperationException(
                        "Assign a Unity object to reference '" +
                        reference.Name + "'.");
                }
                if (!LuauUnityCapabilities.IsSupportedObject(reference.Target))
                {
                    throw new InvalidOperationException(
                        "Reference '" + reference.Name + "' has unsupported type '" +
                        reference.Target.GetType().Name + "'. Assign a GameObject, " +
                        "Transform, Rigidbody2D, Collider2D, SpriteRenderer, " +
                        "AudioSource, or TextMesh.");
                }
            }

            var catalog = new Dictionary<string, GameObject>(
                StringComparer.Ordinal);
            var prefabs = prefabReferences ?? Array.Empty<PrefabReference>();
            for (var index = 0; index < prefabs.Length; index++)
            {
                var reference = prefabs[index];
                if (reference == null)
                {
                    throw new InvalidOperationException(
                        "Prefab reference slot " + index + " is missing.");
                }

                ValidateReferenceName(reference.Name, "prefab reference", index);
                if (reference.Prefab == null)
                {
                    throw new InvalidOperationException(
                        "Assign a prefab to prefab reference '" +
                        reference.Name + "'.");
                }
                if (!catalog.TryAdd(reference.Name, reference.Prefab))
                {
                    throw new InvalidOperationException(
                        "Prefab reference name '" + reference.Name +
                        "' is assigned more than once.");
                }
            }

            return catalog;
        }

        static void ValidateReferenceName(
            string referenceName,
            string kind,
            int index)
        {
            if (string.IsNullOrWhiteSpace(referenceName))
            {
                throw new InvalidOperationException(
                    "The " + kind + " at slot " + index +
                    " needs a non-empty name.");
            }
            if (!IsLuauIdentifier(referenceName))
            {
                throw new InvalidOperationException(
                    "The " + kind + " name '" + referenceName +
                    "' must be a Luau identifier: letters, digits, and " +
                    "underscores, without a leading digit.");
            }
        }

        static bool IsLuauIdentifier(string value)
        {
            if (!(value[0] == '_' || IsAsciiLetter(value[0])))
            {
                return false;
            }

            for (var index = 1; index < value.Length; index++)
            {
                var character = value[index];
                if (!(character == '_' ||
                    IsAsciiLetter(character) ||
                    (character >= '0' && character <= '9')))
                {
                    return false;
                }
            }

            return true;
        }

        static bool IsAsciiLetter(char value)
        {
            return (value >= 'A' && value <= 'Z') ||
                (value >= 'a' && value <= 'z');
        }

        void SpawnPrefab(LuauCallContext context)
        {
            if (IsDestroyed || Volatile.Read(ref shutdown) != 0)
            {
                throw new InvalidOperationException(
                    "Cannot spawn a prefab after its LuauBehaviour has shut down.");
            }

            var referenceName = context.Read<string>(0);
            if (prefabCatalog == null ||
                !prefabCatalog.TryGetValue(referenceName, out var prefab))
            {
                throw new LuauException(
                    "No prefab named '" + referenceName +
                    "' is assigned to this LuauBehaviour.");
            }
            if (prefab == null)
            {
                throw new MissingReferenceException(
                    "Prefab reference '" + referenceName +
                    "' has been destroyed.");
            }

            PruneDestroyedSpawnedObjects();
            if (spawnedObjects.Count >= maxSpawnedObjects)
            {
                throw new LuauException(
                    "LuauBehaviour '" + name + "' reached its prefab limit of " +
                    maxSpawnedObjects + " live object(s).");
            }

            var spawned = Instantiate(
                prefab,
                transform.position,
                prefab.transform.rotation);
            try
            {
                spawnedObjects.Add(spawned);
                using (var handle = context.State.CreateHandle(
                    spawned,
                    LuauUnityCapabilities.GameObjectDescriptor))
                {
                    context.Return(handle);
                }
            }
            catch
            {
                spawnedObjects.Remove(spawned);
                Destroy(spawned);
                throw;
            }
        }

        void PruneDestroyedSpawnedObjects()
        {
            for (var index = spawnedObjects.Count - 1; index >= 0; index--)
            {
                if (spawnedObjects[index] == null)
                {
                    spawnedObjects.RemoveAt(index);
                }
            }
        }

        void OnEnable()
        {
            SetRegistrationState(true);
        }

        void OnDisable()
        {
            SetRegistrationState(false);
        }

        void SetRegistrationState(bool isEnabled)
        {
            SetRegistrationState(updateRegistration, isEnabled);
            SetRegistrationState(fixedUpdateRegistration, isEnabled);
            SetRegistrationState(lateUpdateRegistration, isEnabled);
        }

        static void SetRegistrationState(
            LuauScriptRegistration registration,
            bool isEnabled)
        {
            if (registration != null && !registration.IsDisposed)
            {
                registration.IsEnabled = isEnabled;
            }
        }

        void OnCollisionEnter2D(Collision2D collision)
        {
            if (collisionEnterEntrypoint == null || collision == null)
            {
                return;
            }

            var point = Vector2.zero;
            var normal = Vector2.zero;
            if (collision.contactCount > 0)
            {
                var contact = collision.GetContact(0);
                point = contact.point;
                normal = contact.normal;
            }

            InvokePhysicsHook(
                collisionEnterEntrypoint,
                collision.gameObject,
                new NumericsVector3(point.x, point.y, 0f),
                new NumericsVector3(normal.x, normal.y, 0f));
        }

        void OnCollisionExit2D(Collision2D collision)
        {
            if (collisionExitEntrypoint != null && collision != null)
            {
                InvokePhysicsHook(
                    collisionExitEntrypoint,
                    collision.gameObject);
            }
        }

        void OnTriggerEnter2D(Collider2D other)
        {
            if (triggerEnterEntrypoint != null && other != null)
            {
                InvokePhysicsHook(triggerEnterEntrypoint, other.gameObject);
            }
        }

        void OnTriggerExit2D(Collider2D other)
        {
            if (triggerExitEntrypoint != null && other != null)
            {
                InvokePhysicsHook(triggerExitEntrypoint, other.gameObject);
            }
        }

        void InvokePhysicsHook(
            LuauScriptEntrypoint entrypoint,
            GameObject other,
            params NumericsVector3[] vectors)
        {
            if (!IsInitialized ||
                !isActiveAndEnabled ||
                runtimeHost == null ||
                !runtimeHost.IsReady ||
                other == null)
            {
                return;
            }

            try
            {
                using (var otherHandle = runtimeHost.Root.CreateHandle(
                    other,
                    LuauUnityCapabilities.GameObjectDescriptor))
                {
                    var arguments = new LuauValue[1 + vectors.Length];
                    arguments[0] = otherHandle;
                    for (var index = 0; index < vectors.Length; index++)
                    {
                        arguments[index + 1] = vectors[index];
                    }

                    entrypoint.InvokeVoid(
                        arguments,
                        runtimeHost.InvocationOptions);
                }
            }
            catch (Exception exception)
            {
                DisableAfterFailure(exception);
            }
        }

        internal void DisableAfterFailure(Exception exception)
        {
            if (!failureLogged)
            {
                failureLogged = true;
                Debug.LogError(
                    "LuauBehaviour '" + name + "' was disabled.\n" + exception,
                    this);
            }

            enabled = false;
        }

        void OnDestroy()
        {
            Interlocked.Exchange(ref destroyed, 1);
            runtimeHost?.Detach(this);
            Shutdown(invokeDestroyHook: true);
        }

        internal void ShutdownFromRuntime()
        {
            Shutdown(invokeDestroyHook: true);
        }

        void Shutdown(bool invokeDestroyHook)
        {
            if (Interlocked.Exchange(ref shutdown, 1) != 0)
            {
                return;
            }

            try
            {
                if (invokeDestroyHook &&
                    destroyEntrypoint != null)
                {
                    destroyEntrypoint.InvokeVoid(
                        runtimeHost == null
                            ? null
                            : runtimeHost.InvocationOptions);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "LuauBehaviour '" + name +
                    "' destroy hook failed.\n" + exception,
                    this);
            }
            finally
            {
                Unregister(ref lateUpdateRegistration);
                Unregister(ref fixedUpdateRegistration);
                Unregister(ref updateRegistration);

                instance?.Dispose();
                instance = null;
                collisionEnterEntrypoint = null;
                collisionExitEntrypoint = null;
                triggerEnterEntrypoint = null;
                triggerExitEntrypoint = null;
                destroyEntrypoint = null;
                prefabCatalog = null;
                initialized = false;
                DestroySpawnedObjects();
            }
        }

        void Unregister(ref LuauScriptRegistration registration)
        {
            var current = registration;
            registration = null;
            if (current == null)
            {
                return;
            }

            if (runtimeHost != null)
            {
                runtimeHost.Unregister(current);
            }
            else
            {
                current.Dispose();
            }
        }

        void DestroySpawnedObjects()
        {
            for (var index = spawnedObjects.Count - 1; index >= 0; index--)
            {
                var spawned = spawnedObjects[index];
                if (spawned != null)
                {
                    Destroy(spawned);
                }
            }

            spawnedObjects.Clear();
        }
    }
}
