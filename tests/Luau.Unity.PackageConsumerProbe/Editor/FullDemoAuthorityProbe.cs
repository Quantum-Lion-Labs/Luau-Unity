using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Luau;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Luau.Unity.PackageConsumerProbe
{
    /// <summary>
    /// Drives the imported Full Demo Core through real behaviours in Play Mode
    /// and checks what its scripts can reach: the other object in a physics
    /// callback, objects spawned through nested behaviours, and keys read
    /// through the Input library.
    /// </summary>
    internal static class FullDemoAuthorityProbe
    {
        const string CoreAssembly = "Luau.Unity.Samples.FullLuauScriptingDemo.Core";
        const string NamespaceName = "Luau.Unity.Samples.FullLuauScriptingDemo.";
        const string ScriptFolder = "Assets/ConsumerProbe/FullDemoScripts/";
        const string GeneratedFolder = "Assets/ConsumerProbe/Generated";
        const string ResultPrefix = "FULL_DEMO_AUTHORITY ";
        const BindingFlags InstanceMembers =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);
        static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(5);
        static readonly List<string> errorLog = new List<string>();

        static CoreTypes types;
        static Scripts scripts;
        static GameObject nestedRuntimePrefab;
        static Action<Exception> completion;
        static IEnumerator steps;
        static Stopwatch clock;
        static bool exitRequested;
        static bool finished;

        sealed class CoreTypes
        {
            public Type Runtime;
            public Type Behaviour;
            public Type ObjectReference;
            public Type PrefabReference;
            public Type InputLibrary;

            public static CoreTypes Load()
            {
                var behaviour = Type.GetType(
                    NamespaceName + "LuauBehaviour, " + CoreAssembly,
                    throwOnError: true);
                return new CoreTypes
                {
                    Runtime = Type.GetType(
                        NamespaceName + "LuauBehaviourRuntime, " + CoreAssembly,
                        throwOnError: true),
                    Behaviour = behaviour,
                    ObjectReference = behaviour.GetNestedType("ObjectReference")
                        ?? throw new InvalidOperationException(
                            "LuauBehaviour is missing ObjectReference."),
                    PrefabReference = behaviour.GetNestedType("PrefabReference")
                        ?? throw new InvalidOperationException(
                            "LuauBehaviour is missing PrefabReference."),
                    InputLibrary = Type.GetType(
                        NamespaceName + "LuauInputLibrary, " + CoreAssembly,
                        throwOnError: true),
                };
            }
        }

        sealed class Scripts
        {
            public LuauAsset PhysicsAuthority;
            public LuauAsset NestedSpawner;
            public LuauAsset ExtraSpawner;
            public LuauAsset SpawnOnce;
            public LuauAsset InputProbe;

            public static Scripts Load()
            {
                return new Scripts
                {
                    PhysicsAuthority = LoadScript("PhysicsAuthority"),
                    NestedSpawner = LoadScript("NestedSpawner"),
                    ExtraSpawner = LoadScript("ExtraSpawner"),
                    SpawnOnce = LoadScript("SpawnOnce"),
                    InputProbe = LoadScript("InputProbe"),
                };
            }

            static LuauAsset LoadScript(string name)
            {
                var path = ScriptFolder + name + ".luau";
                return AssetDatabase.LoadAssetAtPath<LuauAsset>(path)
                    ?? throw new InvalidOperationException(
                        "The consumer probe script did not import as a LuauAsset: " + path);
            }
        }

        // ------------------------------------------------------------------
        // Edit Mode: the Input library over virtual devices.
        // ------------------------------------------------------------------

        /// <summary>
        /// Holds keys, a mouse button, and a touch on virtual devices and reads
        /// them through the generated Input library.
        /// </summary>
        public static void ValidateInputLibrary()
        {
            var inputLibraryType = CoreTypes.Load().InputLibrary;
            var keyboard = InputSystem.AddDevice<Keyboard>();
            var mouse = InputSystem.AddDevice<Mouse>();
            var touchscreen = InputSystem.AddDevice<Touchscreen>();
            try
            {
                InputState.Change(keyboard, new KeyboardState(Key.A, Key.Space));
                InputState.Change(
                    mouse,
                    new MouseState()
                        .WithButton(MouseButton.Left)
                        .WithButton(MouseButton.Right));
                InputState.Change(
                    touchscreen.touches[0],
                    new TouchState
                    {
                        touchId = 1,
                        phase = UnityEngine.InputSystem.TouchPhase.Began,
                        position = new Vector2(1, 1),
                    });
                keyboard.MakeCurrent();
                mouse.MakeCurrent();
                touchscreen.MakeCurrent();
                if (!keyboard.aKey.isPressed ||
                    !mouse.rightButton.isPressed ||
                    touchscreen.touches[0].phase.ReadValue() !=
                        UnityEngine.InputSystem.TouchPhase.Began)
                {
                    throw new InvalidOperationException(
                        "The virtual input devices did not take the probe's state.");
                }

                var library = (ILuauLibrary)Activator.CreateInstance(inputLibraryType);
                using var root = LuauUnity.CreateState(ConsumerApiProbe.CreateOptions(
                    state => state.OpenLibrary(library)));
                using var thread = root.CreateSandboxedThread();
                using var values = thread.DoString(
                    "return Input.GetKey('A'), Input.GetKey('Enter'), " +
                    "Input.GetMouseButton(1), Input.touchCount, Input.GetTouchPhase(0)",
                    "@consumer/full-demo-input-any-key.luau");
                if (values.Length != 5 ||
                    !values[0].Read<bool>() ||
                    values[1].Read<bool>() ||
                    !values[2].Read<bool>() ||
                    values[3].Read<int>() != 1 ||
                    values[4].Read<string>() != "Began")
                {
                    throw new InvalidOperationException(
                        "Claim 3 did not reproduce: the Input library did not read " +
                        "the held A key, the right mouse button, and the touch.");
                }

                Debug.Log(
                    ResultPrefix + "claim 3 (Input) reproduced: Input.GetKey('A') " +
                    "returned true for a held key the demo never reads, " +
                    "Input.GetMouseButton(1) returned true, and touchCount/GetTouchPhase(0) " +
                    "returned 1/'Began'; the library has no per-runtime policy.");
            }
            finally
            {
                InputSystem.RemoveDevice(touchscreen);
                InputSystem.RemoveDevice(mouse);
                InputSystem.RemoveDevice(keyboard);
            }
        }

        // ------------------------------------------------------------------
        // Play Mode driver.
        // ------------------------------------------------------------------

        /// <summary>
        /// Enters Play Mode and runs the behaviour checks. <paramref name="onCompleted"/>
        /// receives null on success or the failure, after Play Mode has ended
        /// when it can.
        /// </summary>
        public static void Begin(Action<Exception> onCompleted)
        {
            completion = onCompleted ?? throw new ArgumentNullException(nameof(onCompleted));
            clock = Stopwatch.StartNew();
            types = CoreTypes.Load();
            scripts = Scripts.Load();
            nestedRuntimePrefab = CreateNestedRuntimePrefab();

            // Keep this class's state, the loaded assets, and the already JIT-compiled
            // capability thunks across the transition.
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions =
                EnterPlayModeOptions.DisableDomainReload |
                EnterPlayModeOptions.DisableSceneReload;
            Application.logMessageReceived += RecordLog;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update += Tick;
            EditorApplication.EnterPlaymode();
        }

        static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredPlayMode)
            {
                steps = RunPlayModeChecks().GetEnumerator();
            }
            else if (change == PlayModeStateChange.EnteredEditMode && exitRequested)
            {
                Finish(null);
            }
        }

        static void Tick()
        {
            if (finished)
            {
                return;
            }

            try
            {
                if (clock.Elapsed > ProbeTimeout)
                {
                    throw new TimeoutException(
                        "The Full Demo Play Mode probe did not finish within " +
                        ProbeTimeout + "." + DescribeErrors());
                }
                if (steps == null || exitRequested || !EditorApplication.isPlaying)
                {
                    return;
                }
                if (!steps.MoveNext())
                {
                    exitRequested = true;
                    EditorApplication.ExitPlaymode();
                }
            }
            catch (Exception exception)
            {
                Finish(exception);
            }
        }

        static void Finish(Exception exception)
        {
            if (finished)
            {
                return;
            }

            finished = true;
            EditorApplication.update -= Tick;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            Application.logMessageReceived -= RecordLog;
            completion(exception);
        }

        static void RecordLog(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception)
            {
                errorLog.Add(condition);
            }
        }

        static IEnumerable<object> RunPlayModeChecks()
        {
            foreach (var step in PhysicsCallbacks())
            {
                yield return step;
            }
            foreach (var step in NestedSpawning())
            {
                yield return step;
            }
            foreach (var step in NestedRuntimeSpawning())
            {
                yield return step;
            }
            foreach (var step in RuntimeInput())
            {
                yield return step;
            }
        }

        // ------------------------------------------------------------------
        // Physics callbacks.
        // ------------------------------------------------------------------

        static IEnumerable<object> PhysicsCallbacks()
        {
            var created = new List<GameObject>();
            var runtime = CreateRuntime("Physics probe runtime", created);
            var block = CreateStaticCollider("Spawn block", new Vector3(1000, 0, 0), true, created);
            var granted = CreateStaticCollider("Granted target", Vector3.zero, false, created);
            var unlisted = CreateStaticCollider("Unlisted target", Vector3.zero, false, created);

            var owner = CreateInactive("Physics owner", created);
            var body = owner.AddComponent<Rigidbody2D>();
            body.bodyType = RigidbodyType2D.Dynamic;
            body.gravityScale = 0;
            body.sleepMode = RigidbodySleepMode2D.NeverSleep;
            owner.AddComponent<BoxCollider2D>().isTrigger = true;
            var behaviour = AddBehaviour(
                owner,
                runtime,
                scripts.PhysicsAuthority,
                new[] { ("granted", (Object)granted.transform) },
                new[] { ("block", block) },
                maxSpawnedObjects: 1);
            owner.SetActive(true);

            try
            {
                foreach (var step in WaitUntil(
                    () => PhysicsReport(runtime).Contains("wrote|Spawn block(Clone)\n"),
                    "the physics owner to touch the block it spawned",
                    behaviour))
                {
                    yield return step;
                }

                granted.GetComponent<Collider2D>().enabled = true;
                foreach (var step in WaitUntil(
                    () => PhysicsReport(runtime).Contains("wrote|Granted target\n"),
                    "the physics owner to touch its Object Reference",
                    behaviour))
                {
                    yield return step;
                }

                unlisted.GetComponent<Collider2D>().enabled = true;
                foreach (var step in WaitUntil(
                    () => PhysicsReport(runtime).Contains("seen|Unlisted target|") &&
                        (PhysicsReport(runtime).Contains("wrote|Unlisted target\n") ||
                            !behaviour.enabled),
                    "the physics owner to touch an object it was not given"))
                {
                    yield return step;
                }

                var spawned = FindSceneObject("Spawn block(Clone) [written]");
                if (spawned == null ||
                    spawned.activeSelf ||
                    granted.name != "Granted target [written]" ||
                    granted.activeSelf)
                {
                    throw new InvalidOperationException(
                        "A physics callback could not change an object its behaviour " +
                        "owns.\n" + PhysicsReport(runtime) + DescribeErrors());
                }

                if (unlisted.name != "Unlisted target [written]" ||
                    unlisted.transform.position != new Vector3(50, 50, 0) ||
                    !unlisted.GetComponent<Collider2D>().isTrigger ||
                    unlisted.activeSelf)
                {
                    throw new InvalidOperationException(
                        "Claim 1 did not reproduce: triggerEnter2D did not rename, move, " +
                        "reconfigure, and deactivate an object the behaviour was not " +
                        "given.\n" + PhysicsReport(runtime) + DescribeErrors());
                }

                Debug.Log(
                    ResultPrefix + "claim 1 (physics callbacks) reproduced: triggerEnter2D " +
                    "received a full GameObject capability for 'Unlisted target', which is " +
                    "not in the behaviour's Object References; the script renamed it to '" +
                    unlisted.name + "', moved it to " + unlisted.transform.position +
                    ", set its Collider2D.isTrigger through GetComponent, and deactivated it.\n" +
                    PhysicsReport(runtime));
            }
            finally
            {
                DestroyAll(created);
            }

            yield return null;
        }

        static string PhysicsReport(Component runtime)
        {
            return ReadSharedString(runtime, "physicsReport") ?? string.Empty;
        }

        // ------------------------------------------------------------------
        // Spawning through nested behaviours on one runtime.
        // ------------------------------------------------------------------

        static IEnumerable<object> NestedSpawning()
        {
            const string templateName = "Nested spawner";
            var created = new List<GameObject>();
            var runtime = CreateRuntime("Spawning probe runtime", created);
            var plainBlock = new GameObject("Plain block");
            plainBlock.transform.position = new Vector3(2000, 0, 0);
            created.Add(plainBlock);

            // A scene object listed as its own prefab. Unity remaps the
            // reference to each clone, so every clone lists itself in turn.
            var template = CreateInactive(templateName, created);
            AddBehaviour(
                template,
                runtime,
                scripts.NestedSpawner,
                Array.Empty<(string, Object)>(),
                new[] { ("child", template) },
                maxSpawnedObjects: 2);
            var extra = CreateInactive("Extra spawner", created);
            var extraBehaviour = AddBehaviour(
                extra,
                runtime,
                scripts.ExtraSpawner,
                Array.Empty<(string, Object)>(),
                new[] { ("block", plainBlock) },
                maxSpawnedObjects: 4);
            template.SetActive(true);
            extra.SetActive(true);

            try
            {
                foreach (var step in WaitUntilStable(
                    () => CountSceneObjects(templateName + "(Clone)"),
                    () => IsReady(runtime),
                    "the nested spawners to settle"))
                {
                    yield return step;
                }

                var clones = CountSceneObjects(templateName + "(Clone)");
                if (clones != 14)
                {
                    throw new InvalidOperationException(
                        "Claim 2 did not reproduce as expected: " + clones +
                        " nested clones are live instead of 14." + DescribeErrors());
                }

                WriteShared(runtime, "extraAttempt", "protected");
                foreach (var step in WaitUntil(
                    () => ReadSharedString(runtime, "extraResult") != null,
                    "the extra spawner's attempt",
                    extraBehaviour))
                {
                    yield return step;
                }

                var extraResult = ReadSharedString(runtime, "extraResult");
                if (extraResult != "spawned")
                {
                    throw new InvalidOperationException(
                        "Claim 2 did not reproduce as expected: the extra spawn was " +
                        extraResult + "." + DescribeErrors());
                }

                Debug.Log(
                    ResultPrefix + "claim 2 (spawning) reproduced: every behaviour had " +
                    "Max Spawned Objects = 2, yet one scene object listed as its own " +
                    "prefab produced " + clones + " live clones on one runtime, and " +
                    "another behaviour on that runtime spawned one more (" +
                    (clones + 1) + " live spawned objects). The runtime has no limit of its own.");
            }
            finally
            {
                DestroyAll(created);
            }

            yield return null;
        }

        // ------------------------------------------------------------------
        // Spawning a prefab that brings its own runtime.
        // ------------------------------------------------------------------

        static IEnumerable<object> NestedRuntimeSpawning()
        {
            var created = new List<GameObject>();
            var runtime = CreateRuntime("Nested runtime host runtime", created);
            var host = CreateInactive("Nested runtime host", created);
            AddBehaviour(
                host,
                runtime,
                scripts.SpawnOnce,
                Array.Empty<(string, Object)>(),
                new[] { ("child", nestedRuntimePrefab) },
                maxSpawnedObjects: 1);
            host.SetActive(true);

            try
            {
                var cloneName = nestedRuntimePrefab.name + "(Clone)";
                foreach (var step in WaitUntilStable(
                    () => CountSceneObjects(cloneName),
                    () => IsReady(runtime) && CountSceneObjects(cloneName) > 0,
                    "the spawned runtimes to settle"))
                {
                    yield return step;
                }

                var clones = CountSceneObjects(cloneName);
                var runtimes = CountRuntimesUnder(cloneName);
                if (clones != 7 || runtimes != 7)
                {
                    throw new InvalidOperationException(
                        "The nested-runtime spawn produced " + clones + " objects and " +
                        runtimes + " runtimes instead of 7 of each." + DescribeErrors());
                }

                Debug.Log(
                    ResultPrefix + "claim 2 (spawning) also reproduced through a prefab " +
                    "that carries its own LuauBehaviourRuntime: a host behaviour allowed " +
                    "one spawn, and that one prefab produced " + clones + " live objects " +
                    "with " + runtimes + " separate runtimes, each with fresh limits.");
            }
            finally
            {
                DestroyAll(created);
            }

            // Spawned runtimes and their clones are destroyed with their spawner.
            foreach (var step in WaitFrames(3))
            {
                yield return step;
            }
        }

        static GameObject CreateNestedRuntimePrefab()
        {
            if (!AssetDatabase.IsValidFolder(GeneratedFolder))
            {
                AssetDatabase.CreateFolder("Assets/ConsumerProbe", "Generated");
            }

            // Edit Mode does not run Awake for these components, so nothing
            // starts while the asset is authored.
            var root = new GameObject("Nested runtime spawner");
            try
            {
                var runtime = root.AddComponent(types.Runtime);
                var behaviour = root.AddComponent(types.Behaviour);
                SetField(behaviour, "runtimeHost", runtime);
                SetField(behaviour, "script", scripts.NestedSpawner);
                SetField(
                    behaviour,
                    "objectReferences",
                    CreateReferences(types.ObjectReference, "target", Array.Empty<(string, Object)>()));
                SetField(
                    behaviour,
                    "prefabReferences",
                    CreateReferences(
                        types.PrefabReference,
                        "prefab",
                        new[] { ("child", (Object)root) }));
                SetField(behaviour, "maxSpawnedObjects", 2);
                var prefab = PrefabUtility.SaveAsPrefabAsset(
                    root,
                    GeneratedFolder + "/Nested runtime spawner.prefab",
                    out var saved);
                if (!saved || prefab == null)
                {
                    throw new InvalidOperationException(
                        "The consumer probe could not save its nested-runtime prefab.");
                }

                return prefab;
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // ------------------------------------------------------------------
        // The Input library through a runtime.
        // ------------------------------------------------------------------

        static IEnumerable<object> RuntimeInput()
        {
            var created = new List<GameObject>();
            var runtime = CreateRuntime("Input probe runtime", created);
            WriteShared(runtime, "allowedKey", "Space");
            WriteShared(runtime, "unlistedKey", "A");
            WriteShared(runtime, "checkPointer", true);
            var owner = CreateInactive("Input probe", created);
            var behaviour = AddBehaviour(
                owner,
                runtime,
                scripts.InputProbe,
                Array.Empty<(string, Object)>(),
                Array.Empty<(string, GameObject)>(),
                maxSpawnedObjects: 0);
            owner.SetActive(true);

            try
            {
                foreach (var step in WaitUntil(
                    () => ReadSharedString(runtime, "unlistedRead") != null || !behaviour.enabled,
                    "the input probe's start"))
                {
                    yield return step;
                }

                var unlisted = ReadSharedString(runtime, "unlistedRead");
                if (unlisted == null || !behaviour.enabled)
                {
                    throw new InvalidOperationException(
                        "Claim 3 did not reproduce: a runtime script could not read key 'A'." +
                        DescribeErrors());
                }

                Debug.Log(
                    ResultPrefix + "claim 3 (Input) reproduced through a runtime: a " +
                    "behaviour script read key 'A' (" + unlisted + "), mouse button 0 and " +
                    "touch (" + ReadSharedString(runtime, "pointerRead") + ") with no " +
                    "runtime setting to refuse them.");
            }
            finally
            {
                DestroyAll(created);
            }

            yield return null;
        }

        // ------------------------------------------------------------------
        // Helpers.
        // ------------------------------------------------------------------

        static Component CreateRuntime(string name, List<GameObject> created)
        {
            var gameObject = CreateInactive(name, created);
            var runtime = gameObject.AddComponent(types.Runtime);
            gameObject.SetActive(true);
            return runtime;
        }

        static GameObject CreateInactive(string name, List<GameObject> created)
        {
            var gameObject = new GameObject(name);
            gameObject.SetActive(false);
            created.Add(gameObject);
            return gameObject;
        }

        static GameObject CreateStaticCollider(
            string name,
            Vector3 position,
            bool enabled,
            List<GameObject> created)
        {
            var gameObject = new GameObject(name);
            gameObject.transform.position = position;
            gameObject.AddComponent<BoxCollider2D>().enabled = enabled;
            created.Add(gameObject);
            return gameObject;
        }

        static Behaviour AddBehaviour(
            GameObject owner,
            Component runtime,
            LuauAsset script,
            (string Name, Object Target)[] references,
            (string Name, GameObject Prefab)[] prefabs,
            int maxSpawnedObjects)
        {
            if (owner.activeSelf)
            {
                throw new InvalidOperationException(
                    "Configure a LuauBehaviour before its GameObject wakes.");
            }

            var behaviour = (Behaviour)owner.AddComponent(types.Behaviour);
            SetField(behaviour, "runtimeHost", runtime);
            SetField(behaviour, "script", script);
            SetField(
                behaviour,
                "objectReferences",
                CreateReferences(types.ObjectReference, "target", references));
            var prefabTargets = new (string, Object)[prefabs.Length];
            for (var index = 0; index < prefabs.Length; index++)
            {
                prefabTargets[index] = (prefabs[index].Name, prefabs[index].Prefab);
            }
            SetField(
                behaviour,
                "prefabReferences",
                CreateReferences(types.PrefabReference, "prefab", prefabTargets));
            SetField(behaviour, "maxSpawnedObjects", maxSpawnedObjects);
            return behaviour;
        }

        static Array CreateReferences(
            Type referenceType,
            string targetField,
            (string Name, Object Target)[] entries)
        {
            var array = Array.CreateInstance(referenceType, entries.Length);
            for (var index = 0; index < entries.Length; index++)
            {
                var reference = Activator.CreateInstance(referenceType);
                SetField(reference, "referenceName", entries[index].Name);
                SetField(reference, targetField, entries[index].Target);
                array.SetValue(reference, index);
            }

            return array;
        }

        static void SetField(object target, string name, object value)
        {
            var field = target.GetType().GetField(name, InstanceMembers)
                ?? throw new InvalidOperationException(
                    target.GetType().Name + " is missing field '" + name + "'.");
            field.SetValue(target, value);
        }

        static bool IsReady(Component runtime)
        {
            return (bool)types.Runtime.GetProperty("IsReady").GetValue(runtime);
        }

        static LuauTable Shared(Component runtime)
        {
            var property = types.Runtime.GetProperty("Shared", InstanceMembers)
                ?? throw new InvalidOperationException(
                    "LuauBehaviourRuntime is missing its shared table.");
            return (LuauTable)property.GetValue(runtime);
        }

        static string ReadSharedString(Component runtime, string key)
        {
            var value = Shared(runtime).RawGet(key);
            return value.TryRead<string>(out var text) ? text : null;
        }

        static double ReadSharedNumber(Component runtime, string key)
        {
            var value = Shared(runtime).RawGet(key);
            return value.TryRead<double>(out var number) ? number : 0;
        }

        static void WriteShared(Component runtime, string key, LuauValue value)
        {
            Shared(runtime).RawSet(key, value);
        }

        static GameObject FindSceneObject(string name)
        {
            foreach (var candidate in Object.FindObjectsByType<GameObject>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None))
            {
                if (candidate.name == name)
                {
                    return candidate;
                }
            }

            return null;
        }

        static int CountSceneObjects(string namePrefix)
        {
            var count = 0;
            foreach (var candidate in Object.FindObjectsByType<GameObject>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None))
            {
                if (candidate.name.StartsWith(namePrefix, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        static int CountRuntimesUnder(string namePrefix)
        {
            var count = 0;
            foreach (var candidate in Object.FindObjectsByType(
                types.Runtime,
                FindObjectsInactive.Include,
                FindObjectsSortMode.None))
            {
                if (((Component)candidate).gameObject.name.StartsWith(
                    namePrefix,
                    StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        static void DestroyAll(List<GameObject> created)
        {
            for (var index = created.Count - 1; index >= 0; index--)
            {
                if (created[index] != null)
                {
                    Object.Destroy(created[index]);
                }
            }

            created.Clear();
        }

        static IEnumerable<object> WaitUntil(
            Func<bool> condition,
            string description,
            Behaviour mustStayEnabled = null)
        {
            var started = clock.Elapsed;
            while (!condition())
            {
                if (mustStayEnabled != null && !mustStayEnabled.enabled)
                {
                    throw new InvalidOperationException(
                        mustStayEnabled.name + " was disabled while waiting for " +
                        description + "." + DescribeErrors());
                }
                if (clock.Elapsed - started > StepTimeout)
                {
                    throw new TimeoutException(
                        "Timed out waiting for " + description + "." + DescribeErrors());
                }

                yield return null;
            }
        }

        /// <summary>
        /// Waits until <paramref name="ready"/> holds and the counted value has
        /// not changed for a while, so the spawn tree has finished growing.
        /// </summary>
        static IEnumerable<object> WaitUntilStable(
            Func<int> count,
            Func<bool> ready,
            string description)
        {
            var started = clock.Elapsed;
            var last = -1;
            var stableSince = clock.Elapsed;
            while (true)
            {
                var current = count();
                if (current != last || !ready())
                {
                    last = current;
                    stableSince = clock.Elapsed;
                }
                else if (clock.Elapsed - stableSince > TimeSpan.FromSeconds(1))
                {
                    yield break;
                }

                if (clock.Elapsed - started > StepTimeout)
                {
                    throw new TimeoutException(
                        "Timed out waiting for " + description + " (last count " +
                        current + ")." + DescribeErrors());
                }

                yield return null;
            }
        }

        static IEnumerable<object> WaitFrames(int frames)
        {
            for (var index = 0; index < frames; index++)
            {
                yield return null;
            }
        }

        static string DescribeErrors()
        {
            if (errorLog.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder("\nLogged errors:");
            foreach (var entry in errorLog)
            {
                builder.Append("\n- ").Append(entry);
            }

            return builder.ToString();
        }
    }
}
