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

        const string NotGrantedSuffix =
            " was not granted to this script. A script can change only its own " +
            "GameObject, its Object References, and objects it spawned.";

        static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);
        static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(5);
        static readonly List<string> errorLog = new List<string>();
        static readonly List<string> warningLog = new List<string>();

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
            public Type InputPolicy;
            public Type MouseButtons;
            public Type Capabilities;

            public static CoreTypes Load()
            {
                var behaviour = Type.GetType(
                    NamespaceName + "LuauBehaviour, " + CoreAssembly,
                    throwOnError: true);
                var inputLibrary = Type.GetType(
                    NamespaceName + "LuauInputLibrary, " + CoreAssembly,
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
                    InputLibrary = inputLibrary,
                    InputPolicy = inputLibrary.GetNestedType("Policy")
                        ?? throw new InvalidOperationException(
                            "LuauInputLibrary is missing Policy."),
                    MouseButtons = inputLibrary.GetNestedType("MouseButtons")
                        ?? throw new InvalidOperationException(
                            "LuauInputLibrary is missing MouseButtons."),
                    Capabilities = Type.GetType(
                        NamespaceName + "LuauUnityCapabilities, " + CoreAssembly,
                        throwOnError: true),
                };
            }

            /// <summary>Creates a policy; <paramref name="keys"/> null means the default.</summary>
            public object CreateInputPolicy(string[] keys = null, int mouseButtons = 0, bool touch = false)
            {
                return keys == null
                    ? Activator.CreateInstance(InputPolicy)
                    : Activator.CreateInstance(
                        InputPolicy,
                        keys,
                        Enum.ToObject(MouseButtons, mouseButtons),
                        touch);
            }

            public ILuauLibrary CreateInputLibrary(object policy)
            {
                return (ILuauLibrary)Activator.CreateInstance(InputLibrary, policy);
            }

            public LuauObjectDescriptor<T> GetDescriptor<T>(string fieldName)
                where T : class
            {
                var field = Capabilities.GetField(fieldName)
                    ?? throw new InvalidOperationException(
                        "LuauUnityCapabilities is missing " + fieldName + ".");
                return (LuauObjectDescriptor<T>)field.GetValue(null);
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
        // Edit Mode: the read-only views a physics callback hands over.
        // ------------------------------------------------------------------

        /// <summary>
        /// Checks the view of an object a script was not granted: every read it
        /// offers works, no member changes the object, and each write names the
        /// object and says it was not granted. Running this in Edit Mode also
        /// compiles the view's members before a 2 ms physics hook first uses them.
        /// </summary>
        public static void ValidateReadOnlyViews()
        {
            var coreTypes = CoreTypes.Load();
            var viewDescriptor = coreTypes.GetDescriptor<GameObject>("GameObjectViewDescriptor");
            coreTypes.GetDescriptor<Transform>("TransformViewDescriptor");
            var quaternionLibrary = (ILuauLibrary)Activator.CreateInstance(Type.GetType(
                NamespaceName + "LuauQuaternionLibrary, " + CoreAssembly,
                throwOnError: true));
            using var root = LuauUnity.CreateState(ConsumerApiProbe.CreateOptions(
                state => state.OpenLibrary(quaternionLibrary)));
            using var thread = root.CreateSandboxedThread();
            var target = new GameObject("Ungranted view target");
            try
            {
                target.layer = 4;
                target.transform.position = new Vector3(1, 2, 3);
                target.transform.rotation = Quaternion.Euler(0, 0, 90);
                using (var handle = root.CreateHandle(target, viewDescriptor))
                {
                    thread["viewTarget"] = handle;
                }

                using (var values = thread.DoString(
                    "local object = viewTarget\n" +
                    "local transform = object.transform\n" +
                    "local position = transform.position\n" +
                    "return object.name, object.tag, object.layer, object.activeSelf, " +
                    "object.activeInHierarchy, object:CompareTag('Untagged'), " +
                    "position.x, position.y, position.z, transform.rotation.z, " +
                    "object.SetActive == nil, object.GetComponent == nil, " +
                    "transform.localPosition == nil, transform.Translate == nil, " +
                    "transform.gameObject == nil, tostring(object), tostring(transform)",
                    "@consumer/full-demo-view-reads.luau"))
                {
                    if (values.Length != 17 ||
                        values[0].Read<string>() != "Ungranted view target" ||
                        values[1].Read<string>() != "Untagged" ||
                        values[2].Read<int>() != 4 ||
                        !values[3].Read<bool>() ||
                        !values[4].Read<bool>() ||
                        !values[5].Read<bool>() ||
                        values[6].Read<double>() != 1 ||
                        values[7].Read<double>() != 2 ||
                        values[8].Read<double>() != 3 ||
                        Math.Abs(values[9].Read<double>() - Math.Sqrt(0.5)) > 0.001 ||
                        !values[10].Read<bool>() ||
                        !values[11].Read<bool>() ||
                        !values[12].Read<bool>() ||
                        !values[13].Read<bool>() ||
                        !values[14].Read<bool>() ||
                        values[15].Read<string>() != "GameObjectView" ||
                        values[16].Read<string>() != "TransformView")
                    {
                        throw new InvalidOperationException(
                            "The Full Demo read-only GameObject view did not offer " +
                            "exactly its documented reads.");
                    }
                }

                const string gameObjectError =
                    "GameObject 'Ungranted view target'" + NotGrantedSuffix;
                const string transformError =
                    "the Transform of 'Ungranted view target'" + NotGrantedSuffix;
                foreach (var (statement, expected) in new[]
                {
                    ("target.name = 'Renamed'", "Cannot set 'name': " + gameObjectError),
                    ("target.tag = 'Player'", "Cannot set 'tag': " + gameObjectError),
                    ("target.layer = 5", "Cannot set 'layer': " + gameObjectError),
                    ("target.activeSelf = false", "Cannot set 'activeSelf': " + gameObjectError),
                    ("target.activeInHierarchy = false",
                        "Cannot set 'activeInHierarchy': " + gameObjectError),
                    ("target.transform = target.transform",
                        "Cannot set 'transform': " + gameObjectError),
                    ("target.transform.position = vector.create(0, 0, 0)",
                        "Cannot set 'position': " + transformError),
                    ("target.transform.rotation = Quaternion.Euler(vector.create(0, 0, 0))",
                        "Cannot set 'rotation': " + transformError),
                })
                {
                    ExpectCallbackError(
                        thread,
                        "local target = viewTarget\n" + statement,
                        expected);
                }

                // The view has no SetActive or GetComponent at all, so calling
                // one is an ordinary Luau error rather than a host failure.
                foreach (var statement in new[]
                {
                    "target:SetActive(false)",
                    "target:GetComponent('Transform')",
                })
                {
                    try
                    {
                        using (thread.DoString(
                            "local target = viewTarget\n" + statement,
                            "@consumer/full-demo-view-method.luau"))
                        {
                        }

                        throw new InvalidOperationException(
                            "The read-only GameObject view accepted " + statement + ".");
                    }
                    catch (LuauException exception)
                        when (!(exception is LuauManagedCallbackException))
                    {
                        Debug.Log(ResultPrefix + "view " + statement + " -> " + exception.Message);
                    }
                }

                if (target.name != "Ungranted view target" ||
                    target.tag != "Untagged" ||
                    target.layer != 4 ||
                    !target.activeSelf ||
                    target.transform.position != new Vector3(1, 2, 3) ||
                    Quaternion.Angle(target.transform.rotation, Quaternion.Euler(0, 0, 90)) > 0.01f)
                {
                    throw new InvalidOperationException(
                        "A script changed an object through its read-only view.");
                }
            }
            finally
            {
                thread["viewTarget"] = LuauValue.Nil;
                Object.DestroyImmediate(target);
            }
        }

        // ------------------------------------------------------------------
        // Edit Mode: the Input library over virtual devices.
        // ------------------------------------------------------------------

        /// <summary>
        /// Holds keys, mouse buttons, and a touch on virtual devices and reads
        /// them through Input libraries built from different policies.
        /// </summary>
        public static void ValidateInputLibrary()
        {
            var coreTypes = CoreTypes.Load();
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

                // The default policy reads what the demo reads and nothing more.
                using (var root = LuauUnity.CreateState(ConsumerApiProbe.CreateOptions(
                    state => state.OpenLibrary(
                        coreTypes.CreateInputLibrary(coreTypes.CreateInputPolicy())))))
                using (var thread = root.CreateSandboxedThread())
                {
                    using (var values = thread.DoString(
                        "local pressed = Input.GetKeyDown('Space')\n" +
                        "return Input.GetKey('Space'), Input.GetKey('space'), " +
                        "type(pressed) == 'boolean', Input.GetMouseButton(0), " +
                        "Input.touchCount, Input.GetTouchPhase(0)",
                        "@consumer/full-demo-input-default-policy.luau"))
                    {
                        if (values.Length != 6 ||
                            !values[0].Read<bool>() ||
                            !values[1].Read<bool>() ||
                            !values[2].Read<bool>() ||
                            !values[3].Read<bool>() ||
                            values[4].Read<int>() != 1 ||
                            values[5].Read<string>() != "Began")
                        {
                            throw new InvalidOperationException(
                                "The default input policy did not read Space, mouse " +
                                "button 0, and the first touch.");
                        }
                    }

                    const string keyA =
                        "Input key 'A' is not enabled for this Luau runtime. Add it " +
                        "to the runtime's Input Policy keys to let scripts read it.";
                    const string mouse1 =
                        "Mouse button 1 is not enabled for this Luau runtime. Add it " +
                        "to the runtime's Input Policy mouse buttons to let scripts read it.";
                    ExpectCallbackError(thread, "return Input.GetKey('A')", keyA);
                    ExpectCallbackError(thread, "return Input.GetKeyDown('A')", keyA);
                    ExpectCallbackError(
                        thread,
                        "return Input.GetKey('Enter')",
                        "Input key 'Enter' is not enabled for this Luau runtime.");
                    ExpectCallbackError(thread, "return Input.GetMouseButton(1)", mouse1);
                    ExpectCallbackError(thread, "return Input.GetMouseButtonDown(1)", mouse1);
                    ExpectCallbackError(
                        thread,
                        "return Input.GetKey('NotAKey')",
                        "Unknown Input System Key name 'NotAKey'.");
                    ExpectCallbackError(
                        thread,
                        "return Input.GetMouseButton(5)",
                        "Mouse button indexes must be between 0 and 4.");
                }

                // A host policy replaces the defaults entirely.
                using (var root = LuauUnity.CreateState(ConsumerApiProbe.CreateOptions(
                    state => state.OpenLibrary(coreTypes.CreateInputLibrary(
                        coreTypes.CreateInputPolicy(
                            new[] { "A", "Enter" },
                            mouseButtons: 1 << 1,
                            touch: false))))))
                using (var thread = root.CreateSandboxedThread())
                {
                    using (var values = thread.DoString(
                        "return Input.GetKey('A'), Input.GetKey('Enter'), " +
                        "Input.GetMouseButton(1)",
                        "@consumer/full-demo-input-host-policy.luau"))
                    {
                        if (values.Length != 3 ||
                            !values[0].Read<bool>() ||
                            values[1].Read<bool>() ||
                            !values[2].Read<bool>())
                        {
                            throw new InvalidOperationException(
                                "A host input policy did not read the keys and mouse " +
                                "button it enabled.");
                        }
                    }

                    const string touchError =
                        "Touch input is not enabled for this Luau runtime. Enable " +
                        "touch in the runtime's Input Policy to let scripts read it.";
                    ExpectCallbackError(
                        thread,
                        "return Input.GetKey('Space')",
                        "Input key 'Space' is not enabled for this Luau runtime.");
                    ExpectCallbackError(
                        thread,
                        "return Input.GetMouseButton(0)",
                        "Mouse button 0 is not enabled for this Luau runtime.");
                    ExpectCallbackError(thread, "return Input.touchCount", touchError);
                    ExpectCallbackError(thread, "return Input.GetTouchPhase(0)", touchError);
                }

                try
                {
                    coreTypes.CreateInputLibrary(coreTypes.CreateInputPolicy(
                        new[] { "Spcae" },
                        mouseButtons: 0,
                        touch: false));
                    throw new InvalidOperationException(
                        "The Input library accepted a policy with an unknown key name.");
                }
                catch (TargetInvocationException exception)
                    when (exception.InnerException is InvalidOperationException invalid &&
                        invalid.Message ==
                            "The input policy lists unknown Input System Key name 'Spcae'.")
                {
                }
            }
            finally
            {
                InputSystem.RemoveDevice(touchscreen);
                InputSystem.RemoveDevice(mouse);
                InputSystem.RemoveDevice(keyboard);
            }
        }

        static void ExpectCallbackError(LuauState thread, string source, string expected)
        {
            try
            {
                using (thread.DoString(source, "@consumer/full-demo-expected-error.luau"))
                {
                }
            }
            catch (LuauManagedCallbackException exception)
                when (exception.InnerException is LuauException inner &&
                    inner.Message.Contains(expected, StringComparison.Ordinal))
            {
                return;
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "'" + source + "' failed, but not with: " + expected,
                    exception);
            }

            throw new InvalidOperationException(
                "'" + source + "' succeeded; expected it to fail with: " + expected);
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
            else if (type == LogType.Warning)
            {
                warningLog.Add(condition);
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

                // Objects the behaviour was granted stay fully writable.
                foreach (var written in new[]
                {
                    FindSceneObject("Spawn block(Clone) [written]"),
                    granted,
                })
                {
                    if (written == null ||
                        !written.name.EndsWith(" [written]", StringComparison.Ordinal) ||
                        written.transform.position != new Vector3(50, 50, 0) ||
                        !written.GetComponent<Collider2D>().isTrigger ||
                        written.activeSelf)
                    {
                        throw new InvalidOperationException(
                            "A physics callback could not change an object its behaviour " +
                            "spawned or references.\n" + PhysicsReport(runtime) +
                            DescribeErrors());
                    }
                }

                // Anything else arrives as a read-only view: readable, unchanged,
                // and the first write fails the hook with a clear error.
                const string expectedError =
                    "Cannot set 'name': GameObject 'Unlisted target'" + NotGrantedSuffix;
                var report = PhysicsReport(runtime);
                if (!report.Contains("seen|Unlisted target|Untagged|0|true|true|true|0|1\n") ||
                    report.Contains("wrote|Unlisted target") ||
                    unlisted.name != "Unlisted target" ||
                    unlisted.transform.position != Vector3.zero ||
                    unlisted.GetComponent<Collider2D>().isTrigger ||
                    !unlisted.activeSelf ||
                    behaviour.enabled ||
                    !ErrorLogged(expectedError))
                {
                    throw new InvalidOperationException(
                        "A physics callback changed, or failed to report, an object its " +
                        "behaviour was not granted.\n" + report + DescribeErrors());
                }

                Debug.Log(
                    ResultPrefix + "physics callbacks: the spawned block and the Object " +
                    "Reference were written; 'Unlisted target' was read through a view, " +
                    "left unchanged, and the write failed with: " +
                    FindLoggedLine(expectedError));
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
            const int runtimeLimit = 5;
            var created = new List<GameObject>();
            var runtime = CreateRuntime(
                "Spawning probe runtime",
                created,
                configure: component => SetField(component, "maxSpawnedObjects", runtimeLimit));
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

                // Every behaviour stayed within its own limit of 2, so each
                // refusal came from the runtime: 14 clones without it.
                var clones = CountSceneObjects(templateName + "(Clone)");
                var refused = ReadSharedNumber(runtime, "refused");
                if (clones != runtimeLimit || refused < 1)
                {
                    throw new InvalidOperationException(
                        "The runtime spawn limit of " + runtimeLimit + " did not hold " +
                        "across nested behaviours: " + clones + " clones are live and " +
                        refused + " spawns were refused." + DescribeErrors());
                }

                // Another behaviour under its own limit is refused too.
                foreach (var step in ExtraSpawn(runtime, "protected", extraBehaviour))
                {
                    yield return step;
                }
                if (ReadSharedString(runtime, "extraResult") != "refused")
                {
                    throw new InvalidOperationException(
                        "A behaviour under its own limit spawned past the runtime limit." +
                        DescribeErrors());
                }

                // A destroyed object stops counting.
                Object.Destroy(FindSceneObject(templateName + "(Clone)(Clone)"));
                foreach (var step in WaitFrames(2))
                {
                    yield return step;
                }
                foreach (var step in ExtraSpawn(runtime, "protected", extraBehaviour))
                {
                    yield return step;
                }
                if (ReadSharedString(runtime, "extraResult") != "spawned" ||
                    CountSceneObjects(templateName + "(Clone)") != runtimeLimit - 1 ||
                    FindSceneObject("Plain block(Clone)") == null)
                {
                    throw new InvalidOperationException(
                        "The runtime did not admit a spawn after one of its spawned " +
                        "objects was destroyed." + DescribeErrors());
                }

                // At the limit again, a refusal the script does not catch fails
                // its hook with the runtime's error.
                const string expectedError =
                    "LuauBehaviourRuntime 'Spawning probe runtime' reached its limit of 5 " +
                    "live spawned object(s) across all of its behaviours.";
                foreach (var step in ExtraSpawn(runtime, "strict", null))
                {
                    yield return step;
                }
                if (extraBehaviour.enabled || !ErrorLogged(expectedError))
                {
                    throw new InvalidOperationException(
                        "A spawn past the runtime limit did not fail with the runtime's " +
                        "error." + DescribeErrors());
                }

                Debug.Log(
                    ResultPrefix + "spawning: with each behaviour limited to 2 and the " +
                    "runtime to " + runtimeLimit + ", the self-listing spawner stopped at " +
                    clones + " clones (" + refused + " spawns refused), another behaviour " +
                    "was refused until one clone was destroyed, and an uncaught refusal " +
                    "failed with: " + FindLoggedLine(expectedError));
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
            var hostBehaviour = AddBehaviour(
                host,
                runtime,
                scripts.SpawnOnce,
                Array.Empty<(string, Object)>(),
                new[] { ("child", nestedRuntimePrefab) },
                maxSpawnedObjects: 1);
            host.SetActive(true);

            try
            {
                // Without the check, this one admitted spawn produced 7 objects
                // with 7 runtimes of their own.
                const string expectedError =
                    "Prefab reference 'child' contains a LuauBehaviourRuntime. A script " +
                    "cannot spawn a runtime of its own, because nothing that runtime's " +
                    "scripts do would count against this runtime's limits.";
                foreach (var step in WaitUntil(
                    () => !hostBehaviour.enabled,
                    "the host behaviour to refuse a prefab with its own runtime"))
                {
                    yield return step;
                }
                foreach (var step in WaitFrames(30))
                {
                    yield return step;
                }

                var clones = CountSceneObjects(nestedRuntimePrefab.name + "(Clone)");
                if (clones != 0 || !ErrorLogged(expectedError))
                {
                    throw new InvalidOperationException(
                        "A behaviour spawned a prefab that carries its own runtime (" +
                        clones + " live)." + DescribeErrors());
                }

                Debug.Log(
                    ResultPrefix + "nested runtime: the host behaviour refused to start " +
                    "with a prefab that carries its own runtime: " +
                    FindLoggedLine(expectedError));
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
            // The runtime's default policy: the demo's key, mouse button, and
            // touch read; any other key is refused.
            foreach (var step in RuntimeInputCase(
                "Input probe runtime",
                policy: null,
                allowedKey: "Space",
                unlistedKey: "A",
                checkPointer: true))
            {
                yield return step;
            }

            // A host policy on the runtime replaces the defaults.
            foreach (var step in RuntimeInputCase(
                "Host input policy runtime",
                policy: types.CreateInputPolicy(new[] { "A" }, mouseButtons: 0, touch: false),
                allowedKey: "A",
                unlistedKey: "Space",
                checkPointer: false))
            {
                yield return step;
            }
        }

        static IEnumerable<object> RuntimeInputCase(
            string runtimeName,
            object policy,
            string allowedKey,
            string unlistedKey,
            bool checkPointer)
        {
            var created = new List<GameObject>();
            var runtime = CreateRuntime(
                runtimeName,
                created,
                configure: policy == null
                    ? (Action<Component>)null
                    : component => SetField(component, "inputPolicy", policy));
            WriteShared(runtime, "allowedKey", allowedKey);
            WriteShared(runtime, "unlistedKey", unlistedKey);
            WriteShared(runtime, "checkPointer", checkPointer);
            var owner = CreateInactive(runtimeName + " probe", created);
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
                    () => !behaviour.enabled || ReadSharedString(runtime, "unlistedRead") != null,
                    "the input probe's start"))
                {
                    yield return step;
                }

                var expectedError =
                    "Input key '" + unlistedKey + "' is not enabled for this Luau runtime.";
                var allowedRead = ReadSharedString(runtime, "allowedRead");
                var pointerRead = ReadSharedString(runtime, "pointerRead");
                if (behaviour.enabled ||
                    ReadSharedString(runtime, "unlistedRead") != null ||
                    allowedRead != "released/released" ||
                    (checkPointer && pointerRead != "released/released/0") ||
                    !ErrorLogged(expectedError) ||
                    WarningLogged("host-API warmup failed"))
                {
                    throw new InvalidOperationException(
                        "Runtime '" + runtimeName + "' did not read '" + allowedKey +
                        "' and refuse '" + unlistedKey + "' (allowed " + allowedRead +
                        ", pointer " + pointerRead + ")." + DescribeErrors());
                }

                Debug.Log(
                    ResultPrefix + "input (" + runtimeName + "): '" + allowedKey +
                    "' read " + allowedRead +
                    (checkPointer ? ", mouse button 0 and touch read " + pointerRead : string.Empty) +
                    "; '" + unlistedKey + "' failed with: " + FindLoggedLine(expectedError));
            }
            finally
            {
                DestroyAll(created);
            }

            yield return null;
        }

        static IEnumerable<object> ExtraSpawn(Component runtime, string attempt, Behaviour extra)
        {
            WriteShared(runtime, "extraResult", LuauValue.Nil);
            WriteShared(runtime, "extraAttempt", attempt);
            foreach (var step in WaitUntil(
                () => ReadSharedString(runtime, "extraResult") != null ||
                    ReadSharedString(runtime, "extraAttempt") == null,
                "the extra spawner's " + attempt + " attempt",
                extra))
            {
                yield return step;
            }

            // Let a failed hook reach the phase's failure callback.
            yield return null;
        }

        // ------------------------------------------------------------------
        // Helpers.
        // ------------------------------------------------------------------

        static Component CreateRuntime(
            string name,
            List<GameObject> created,
            Action<Component> configure = null)
        {
            var gameObject = CreateInactive(name, created);
            var runtime = gameObject.AddComponent(types.Runtime);
            configure?.Invoke(runtime);
            gameObject.SetActive(true);
            return runtime;
        }

        static bool ErrorLogged(string text)
        {
            return FindLoggedLine(text) != null;
        }

        static bool WarningLogged(string text)
        {
            foreach (var entry in warningLog)
            {
                if (entry.Contains(text, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Returns the logged error line containing <paramref name="text"/>.</summary>
        static string FindLoggedLine(string text)
        {
            foreach (var entry in errorLog)
            {
                foreach (var line in entry.Split('\n'))
                {
                    if (line.Contains(text, StringComparison.Ordinal))
                    {
                        return line.Trim();
                    }
                }
            }

            return null;
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
