using System;
using System.Collections.Generic;
using System.Threading;
using Luau;
using Luau.Unity;
using UnityEngine;

namespace Luau.Unity.Samples.LuauBehaviour
{
    /// <summary>Sample component backed by one sandboxed Luau script instance.</summary>
    public sealed class LuauBehaviourSample : MonoBehaviour
    {
        [Serializable]
        sealed class SceneObjectReference
        {
            [SerializeField]
            string referenceName;

            [SerializeField]
            GameObject target;

            internal string Name => referenceName;
            internal GameObject Target => target;
        }

        [Serializable]
        sealed class PrefabReference
        {
            [SerializeField]
            string referenceName;

            [SerializeField]
            GameObject prefab;

            internal string Name => referenceName;
            internal GameObject Prefab => prefab;
        }

        [SerializeField]
        LuauBehaviourRuntimeSample runtimeHost;

        [SerializeField]
        LuauAsset script;

        [SerializeField]
        int updateOrder;

        [Header("Script Bindings")]
        [SerializeField]
        [Tooltip("Named scene objects exposed to Luau through the refs table.")]
        SceneObjectReference[] sceneObjectReferences = Array.Empty<SceneObjectReference>();

        [SerializeField]
        [Tooltip("Named prefab assets available to the sample-local spawnPrefab function.")]
        PrefabReference[] prefabReferences = Array.Empty<PrefabReference>();

        readonly List<GameObject> spawnedObjects = new List<GameObject>();
        LuauScriptInstance instance;
        LuauScriptRegistration updateRegistration;
        Dictionary<string, GameObject> prefabCatalog;
        int destroyed;
        bool failureLogged;

        async void Start()
        {
            if (runtimeHost == null || script == null)
            {
                DisableAfterFailure(new InvalidOperationException(
                    "Assign an explicit Luau Behaviour runtime host and Luau asset."));
                return;
            }

            LuauScriptInstance createdInstance = null;
            try
            {
                prefabCatalog = ValidateBindingsAndCreatePrefabCatalog();
                createdInstance = await runtimeHost.Root.CreateScriptInstanceAsync(
                    script,
                    ConfigureThread,
                    destroyCancellationToken);

                if (Volatile.Read(ref destroyed) != 0)
                {
                    createdInstance.Dispose();
                    return;
                }

                if (createdInstance.TryGetEntrypoint("start", out var start))
                {
                    await start.InvokeVoidAsync(destroyCancellationToken);
                }

                var update = createdInstance.GetRequiredEntrypoint("update");
                var registration = runtimeHost.Register(this, update, updateOrder);
                registration.IsEnabled = isActiveAndEnabled;

                instance = createdInstance;
                updateRegistration = registration;
                createdInstance = null;
            }
            catch (OperationCanceledException) when (destroyCancellationToken.IsCancellationRequested)
            {
                createdInstance?.Dispose();
            }
            catch (Exception exception)
            {
                createdInstance?.Dispose();
                DisableAfterFailure(exception);
            }
        }

        void ConfigureThread(LuauState thread)
        {
            using var self = runtimeHost.Root.CreateHandle(gameObject);
            thread["self"] = self;

            var references = sceneObjectReferences ?? Array.Empty<SceneObjectReference>();
            using var refs = thread.CreateTable(0, references.Length);
            for (var index = 0; index < references.Length; index++)
            {
                var reference = references[index];
                using var handle = runtimeHost.Root.CreateHandle(reference.Target);
                refs.RawSet(reference.Name, handle);
            }
            thread["refs"] = refs;

            using var spawnPrefab = thread.CreateFunction("spawnPrefab", SpawnPrefab);
            thread["spawnPrefab"] = spawnPrefab;
        }

        Dictionary<string, GameObject> ValidateBindingsAndCreatePrefabCatalog()
        {
            var sceneNames = new HashSet<string>(StringComparer.Ordinal);
            var references = sceneObjectReferences ?? Array.Empty<SceneObjectReference>();
            for (var index = 0; index < references.Length; index++)
            {
                var reference = references[index];
                if (reference == null)
                {
                    throw new InvalidOperationException(
                        "Scene reference slot " + index + " is missing.");
                }

                ValidateReferenceName(reference.Name, "scene reference", index);
                if (!sceneNames.Add(reference.Name))
                {
                    throw new InvalidOperationException(
                        "Scene reference name '" + reference.Name + "' is assigned more than once.");
                }
                if (reference.Target == null)
                {
                    throw new InvalidOperationException(
                        "Assign a GameObject to scene reference '" + reference.Name + "'.");
                }
            }

            var catalog = new Dictionary<string, GameObject>(StringComparer.Ordinal);
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
                        "Assign a prefab to prefab reference '" + reference.Name + "'.");
                }
                if (!catalog.TryAdd(reference.Name, reference.Prefab))
                {
                    throw new InvalidOperationException(
                        "Prefab reference name '" + reference.Name + "' is assigned more than once.");
                }
            }

            return catalog;
        }

        static void ValidateReferenceName(string referenceName, string kind, int index)
        {
            if (string.IsNullOrWhiteSpace(referenceName))
            {
                throw new InvalidOperationException(
                    "The " + kind + " at slot " + index + " needs a non-empty name.");
            }

            if (!IsLuauIdentifier(referenceName))
            {
                throw new InvalidOperationException(
                    "The " + kind + " name '" + referenceName +
                    "' must be a Luau identifier: letters, digits, and underscores, " +
                    "without a leading digit.");
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

        static bool IsAsciiLetter(char value) =>
            (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');

        void SpawnPrefab(LuauCallContext context)
        {
            if (Volatile.Read(ref destroyed) != 0)
            {
                throw new InvalidOperationException(
                    "Cannot spawn a prefab after its Luau Behaviour has been destroyed.");
            }

            var referenceName = context.Read<string>(0);
            if (prefabCatalog == null ||
                !prefabCatalog.TryGetValue(referenceName, out var prefab))
            {
                throw new InvalidOperationException(
                    "No prefab named '" + referenceName + "' is assigned to this Luau Behaviour.");
            }
            if (prefab == null)
            {
                throw new MissingReferenceException(
                    "Prefab reference '" + referenceName + "' has been destroyed.");
            }

            var spawned = Instantiate(prefab, transform.position, prefab.transform.rotation);
            try
            {
                spawnedObjects.Add(spawned);
                using var handle = context.State.CreateHandle(spawned);
                context.Return(handle);
            }
            catch
            {
                spawnedObjects.Remove(spawned);
                Destroy(spawned);
                throw;
            }
        }

        void OnEnable()
        {
            if (updateRegistration != null && !updateRegistration.IsDisposed)
            {
                updateRegistration.IsEnabled = true;
            }
        }

        void OnDisable()
        {
            if (updateRegistration != null && !updateRegistration.IsDisposed)
            {
                updateRegistration.IsEnabled = false;
            }
        }

        internal void DisableAfterFailure(Exception exception)
        {
            if (!failureLogged)
            {
                failureLogged = true;
                Debug.LogError(
                    "Luau Behaviour '" + name + "' was disabled.\n" + exception,
                    this);
            }

            enabled = false;
        }

        void OnDestroy()
        {
            Interlocked.Exchange(ref destroyed, 1);
            try
            {
                if (instance != null &&
                    !instance.IsDisposed &&
                    instance.TryGetEntrypoint("destroy", out var destroy))
                {
                    destroy.InvokeVoid();
                }
            }
            catch (Exception exception)
            {
                if (!failureLogged)
                {
                    failureLogged = true;
                    Debug.LogWarning(
                        "Luau Behaviour '" + name + "' destroy hook failed.\n" + exception,
                        this);
                }
            }
            finally
            {
                if (runtimeHost != null)
                {
                    runtimeHost.Unregister(updateRegistration);
                }
                else
                {
                    updateRegistration?.Dispose();
                }
                updateRegistration = null;
                instance?.Dispose();
                instance = null;
                prefabCatalog = null;
                DestroySpawnedObjects();
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
