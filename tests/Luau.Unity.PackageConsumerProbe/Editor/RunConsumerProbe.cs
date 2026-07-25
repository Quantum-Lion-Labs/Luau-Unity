using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Luau;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Luau.Unity.PackageConsumerProbe
{
    internal static class RunConsumerProbe
    {
        public const string PassedMarker = "LUAU_PACKAGE_CONSUMER_PASS";
        public const string FailedMarker = "LUAU_PACKAGE_CONSUMER_FAIL";

        public static void Execute()
        {
            try
            {
                Run();
                Debug.Log(PassedMarker);
            }
            catch (Exception exception)
            {
                Debug.LogError(FailedMarker + "\n" + exception);
                throw;
            }
        }

        static void Run()
        {
            ValidateXmlIntelliSense();

            var options = ConsumerApiProbe.CreateOptions(state =>
                state.OpenLibrary(ConsumerGeneratedLibrary.CreateGeneratedLibrary()));
            using var root = LuauUnity.CreateState(options);
            using var thread = root.CreateSandboxedThread();

            AssertSingleInteger(
                thread.DoString("return 40 + 2", "@consumer/native-vm.luau"),
                42,
                "Native VM execution");
            AssertSingleInteger(
                thread.DoString("return consumerProbe.addOne(41)", "@consumer/generated-library.luau"),
                42,
                "Generated host-library dispatch");

            ValidateImportedGettingStartedLibrary();
            ValidateImportedFullDemo();
        }

        static void ValidateImportedGettingStartedLibrary()
        {
            const string libraryTypeName =
                "Luau.Unity.Samples.GettingStarted.GettingStartedLibrary, Assembly-CSharp";
            var libraryType = Type.GetType(libraryTypeName, throwOnError: true);
            var library = (ILuauLibrary)Activator.CreateInstance(libraryType);
            using var root = LuauUnity.CreateState(ConsumerApiProbe.CreateOptions(
                state => state.OpenLibrary(library)));
            using var thread = root.CreateSandboxedThread();
            using var values = thread.DoString(
                "return sample.double(21), sample.Double == nil",
                "@consumer/getting-started-library-name.luau");

            if (values.Length != 2 ||
                values[0].Read<int>() != 42 ||
                !values[1].Read<bool>())
            {
                throw new InvalidOperationException(
                    "The imported Getting Started library did not expose the explicit " +
                    "sample.double name override.");
            }
        }

        static void ValidateImportedFullDemo()
        {
            const string coreAssembly =
                "Luau.Unity.Samples.FullLuauScriptingDemo.Core";
            const string namespaceName =
                "Luau.Unity.Samples.FullLuauScriptingDemo.";
            var runtimeType = Type.GetType(
                namespaceName + "LuauBehaviourRuntime, " + coreAssembly,
                throwOnError: true);
            var behaviourType = Type.GetType(
                namespaceName + "LuauBehaviour, " + coreAssembly,
                throwOnError: true);
            if (!runtimeType.IsPublic || !behaviourType.IsPublic)
            {
                throw new InvalidOperationException(
                    "The imported Full Demo Core did not expose its reusable " +
                    "runtime and behaviour components.");
            }
            ValidateUnsupportedBehaviourReference(behaviourType);
            var quaternionLibraryType = Type.GetType(
                namespaceName + "LuauQuaternionLibrary, " + coreAssembly,
                throwOnError: true);
            var inputLibraryType = Type.GetType(
                namespaceName + "LuauInputLibrary, " + coreAssembly,
                throwOnError: true);
            var quaternionLibrary =
                (ILuauLibrary)Activator.CreateInstance(quaternionLibraryType);
            var inputLibrary =
                (ILuauLibrary)Activator.CreateInstance(inputLibraryType);
            using var root = LuauUnity.CreateState(ConsumerApiProbe.CreateOptions(state =>
            {
                state.OpenLibrary(quaternionLibrary);
                state.OpenLibrary(inputLibrary);
            }));
            using var thread = root.CreateSandboxedThread();

            ValidateGeneratedFullDemoLibraries(thread);
            ValidateSharedTable(root);

            const string capabilityTypeName =
                "Luau.Unity.Samples.FullLuauScriptingDemo.LuauUnityCapabilities, " +
                coreAssembly;
            var capabilityType = Type.GetType(capabilityTypeName, throwOnError: true);
            var gameObjectDescriptor = GetDescriptor<GameObject>(
                capabilityType,
                "GameObjectDescriptor");
            var transformDescriptor = GetDescriptor<Transform>(
                capabilityType,
                "TransformDescriptor");
            var rigidbodyDescriptor = GetDescriptor<Rigidbody2D>(
                capabilityType,
                "Rigidbody2DDescriptor");
            var colliderDescriptor = GetDescriptor<Collider2D>(
                capabilityType,
                "Collider2DDescriptor");
            var spriteRendererDescriptor = GetDescriptor<SpriteRenderer>(
                capabilityType,
                "SpriteRendererDescriptor");
            var audioSourceDescriptor = GetDescriptor<AudioSource>(
                capabilityType,
                "AudioSourceDescriptor");
            var textMeshDescriptor = GetDescriptor<TextMesh>(
                capabilityType,
                "TextMeshDescriptor");

            var gameObject = new GameObject("Sample capability target");
            var physicsGameObject = new GameObject("Physics capability target");
            var textGameObject = new GameObject("Text capability target");
            var emptyGameObject = new GameObject("Missing component target");
            try
            {
                var gameObjectRigidbody = gameObject.AddComponent<Rigidbody2D>();
                gameObjectRigidbody.bodyType = RigidbodyType2D.Kinematic;
                gameObjectRigidbody.simulated = false;
                var rigidbody = physicsGameObject.AddComponent<Rigidbody2D>();
                rigidbody.bodyType = RigidbodyType2D.Kinematic;
                rigidbody.simulated = false;
                var collider = gameObject.AddComponent<BoxCollider2D>();
                var spriteRenderer = gameObject.AddComponent<SpriteRenderer>();
                var audioSource = gameObject.AddComponent<AudioSource>();
                var textMesh = textGameObject.AddComponent<TextMesh>();

                using (var gameObjectHandle = root.CreateHandle(
                    gameObject,
                    gameObjectDescriptor))
                using (var textGameObjectHandle = root.CreateHandle(
                    textGameObject,
                    gameObjectDescriptor))
                {
                    thread["gameObject"] = gameObjectHandle;
                    thread["textGameObject"] = textGameObjectHandle;
                    using var values = thread.DoString(
                        "local object = gameObject\n" +
                        "local textObject = textGameObject\n" +
                        "object.name = 'Renamed by Full Demo policy'\n" +
                        "object.tag = 'Untagged'\n" +
                        "object.layer = 3\n" +
                        "local transform = object.transform\n" +
                        "local hasComponents = " +
                        "object:GetComponent('Transform') ~= nil and " +
                        "object:GetComponent('Rigidbody2D') ~= nil and " +
                        "object:GetComponent('Collider2D') ~= nil and " +
                        "object:GetComponent('SpriteRenderer') ~= nil and " +
                        "object:GetComponent('AudioSource') ~= nil\n" +
                        "local hasText = " +
                        "textObject:GetComponent('TextMesh') ~= nil and " +
                        "textObject:GetComponent('SpriteRenderer') == nil\n" +
                        "object:SetActive(false)\n" +
                        "return object.name, object.tag, object.layer, " +
                        "object.activeSelf, object.activeInHierarchy, " +
                        "object:CompareTag('Untagged'), hasComponents, " +
                        "hasText, object.unknown == nil, transform.unknown == nil",
                        "@consumer/full-demo-game-object-policy.luau");

                    if (values.Length != 10 ||
                        values[0].Read<string>() != "Renamed by Full Demo policy" ||
                        values[1].Read<string>() != "Untagged" ||
                        values[2].Read<int>() != 3 ||
                        values[3].Read<bool>() ||
                        values[4].Read<bool>() ||
                        !values[5].Read<bool>() ||
                        !values[6].Read<bool>() ||
                        !values[7].Read<bool>() ||
                        !values[8].Read<bool>() ||
                        !values[9].Read<bool>())
                    {
                        throw new InvalidOperationException(
                            "The imported Full Demo GameObject policy did not preserve " +
                            "its expected member surface or behavior.");
                    }
                }

                gameObject.SetActive(true);
                using (var emptyHandle = root.CreateHandle(
                    emptyGameObject,
                    gameObjectDescriptor))
                {
                    thread["emptyGameObject"] = emptyHandle;
                    using var values = thread.DoString(
                        "local object = emptyGameObject\n" +
                        "return object:GetComponent('AudioSource') == nil",
                        "@consumer/full-demo-missing-component.luau");
                    if (values.Length != 1 || !values[0].Read<bool>())
                    {
                        throw new InvalidOperationException(
                            "The Full Demo GetComponent allowlist did not return nil " +
                            "for a missing supported component.");
                    }
                }

                try
                {
                    using var ignored = thread.DoString(
                        "local object = gameObject\n" +
                        "return object:GetComponent('Camera')",
                        "@consumer/full-demo-unknown-component.luau");
                    throw new InvalidOperationException(
                        "The Full Demo GetComponent allowlist accepted an unknown type.");
                }
                catch (LuauManagedCallbackException exception)
                    when (exception.InnerException is LuauException)
                {
                }

                using (var transformHandle = root.CreateHandle(
                    gameObject.transform,
                    transformDescriptor))
                {
                    thread["transform"] = transformHandle;
                    using var values = thread.DoString(
                        "local target = transform\n" +
                        "target.name = 'Renamed by Transform policy'\n" +
                        "target.position = vector.create(0, 0, 0)\n" +
                        "local worldPosition = target.position\n" +
                        "target.localPosition = vector.create(1, 2, 3)\n" +
                        "target.localScale = vector.create(2, 3, 4)\n" +
                        "target:Translate(vector.create(4, 5, 6))\n" +
                        "local worldPoint = target:TransformPoint(" +
                        "vector.create(1, 0, 0))\n" +
                        "local localPoint = target:InverseTransformPoint(worldPoint)\n" +
                        "target.eulerAngles = vector.create(0, 0, 5)\n" +
                        "target.localEulerAngles = vector.create(0, 0, 10)\n" +
                        "target.rotation = " +
                        "Quaternion.Euler(vector.create(0, 0, 15))\n" +
                        "target.localRotation = " +
                        "Quaternion.Euler(vector.create(0, 0, 30))\n" +
                        "local localRotation = target.localRotation\n" +
                        "local worldRotation = target.rotation\n" +
                        "local eulerAngles = target.eulerAngles\n" +
                        "local localEulerAngles = target.localEulerAngles\n" +
                        "target:Rotate(vector.create(0, 0, 15))\n" +
                        "local linked = target.gameObject\n" +
                        "return linked.name, linked.transform.name, " +
                        "localPoint.x, localPoint.y, localPoint.z, " +
                        "type(localRotation) == 'table', " +
                        "type(worldRotation) == 'table', eulerAngles.z, " +
                        "localEulerAngles.z, target.forward.z, " +
                        "target.right.x, target.up.y",
                        "@consumer/full-demo-transform-policy.luau");

                    if (values.Length != 12 ||
                        values[0].Read<string>() != "Renamed by Transform policy" ||
                        values[1].Read<string>() != "Renamed by Transform policy" ||
                        !values[5].Read<bool>() ||
                        !values[6].Read<bool>() ||
                        gameObject.transform.localPosition != new Vector3(5, 7, 9) ||
                        gameObject.transform.localScale != new Vector3(2, 3, 4) ||
                        Quaternion.Angle(
                            gameObject.transform.localRotation,
                            Quaternion.Euler(0, 0, 45)) > 0.01f)
                    {
                        throw new InvalidOperationException(
                            "The imported Full Demo Transform policy did not preserve " +
                            "vectors, quaternions, methods, or cross-object links.");
                    }
                    AssertApproximately(values[2].Read<double>(), 1, 0.001, "TransformPoint x");
                    AssertApproximately(values[3].Read<double>(), 0, 0.001, "TransformPoint y");
                    AssertApproximately(values[4].Read<double>(), 0, 0.001, "TransformPoint z");
                    AssertApproximately(values[7].Read<double>(), 30, 0.01, "rotation Euler z");
                    AssertApproximately(values[8].Read<double>(), 30, 0.01, "local Euler z");
                    AssertApproximately(values[9].Read<double>(), 1, 0.001, "forward z");
                    AssertApproximately(values[10].Read<double>(), Math.Sqrt(0.5), 0.001, "right x");
                    AssertApproximately(values[11].Read<double>(), Math.Sqrt(0.5), 0.001, "up y");
                }

                using (var rigidbodyHandle = root.CreateHandle(
                    rigidbody,
                    rigidbodyDescriptor))
                {
                    thread["rigidbody"] = rigidbodyHandle;
                    using (thread.DoString(
                        "local body = rigidbody\n" +
                        "body.position = vector.create(2, 3, 99)\n" +
                        "body.rotation = 10\n" +
                        "body.linearVelocity = vector.create(4, 5, 99)\n" +
                        "body.angularVelocity = 6\n" +
                        "body.gravityScale = 0.75",
                        "@consumer/full-demo-rigidbody2d-writes.luau"))
                    {
                    }
                    if ((rigidbody.position - new Vector2(2, 3)).sqrMagnitude > 0.0001f ||
                        Math.Abs(rigidbody.rotation - 10) > 0.001f ||
                        (rigidbody.linearVelocity - new Vector2(4, 5)).sqrMagnitude > 0.0001f ||
                        Math.Abs(rigidbody.angularVelocity - 6) > 0.001f ||
                        Math.Abs(rigidbody.gravityScale - 0.75f) > 0.001f)
                    {
                        throw new InvalidOperationException(
                            "The imported Full Demo Rigidbody2D setters did not update " +
                            "the isolated Unity body.");
                    }

                    using var values = thread.DoString(
                        "local body = rigidbody\n" +
                        "local position = body.position\n" +
                        "local rotation = body.rotation\n" +
                        "local velocity = body.linearVelocity\n" +
                        "local angularVelocity = body.angularVelocity\n" +
                        "local gravityScale = body.gravityScale\n" +
                        "local simulated = body.simulated\n" +
                        "local linked = body.gameObject\n" +
                        "return linked.name, position.x, position.y, position.z, " +
                        "rotation, velocity.x, velocity.y, velocity.z, " +
                        "angularVelocity, gravityScale, simulated",
                        "@consumer/full-demo-rigidbody2d-reads.luau");
                    if (values.Length != 11 ||
                        values[0].Read<string>() != physicsGameObject.name ||
                        values[3].Read<double>() != 0 ||
                        values[7].Read<double>() != 0 ||
                        values[10].Read<bool>())
                    {
                        throw new InvalidOperationException(
                            "The imported Full Demo Rigidbody2D getters did not preserve " +
                            "its Unity-analogous surface or Vector2 mapping.");
                    }
                    AssertApproximately(values[1].Read<double>(), 2, 0.001, "Rigidbody2D position x");
                    AssertApproximately(values[2].Read<double>(), 3, 0.001, "Rigidbody2D position y");
                    AssertApproximately(values[4].Read<double>(), 10, 0.001, "Rigidbody2D rotation");
                    AssertApproximately(values[5].Read<double>(), 4, 0.001, "Rigidbody2D velocity x");
                    AssertApproximately(values[6].Read<double>(), 5, 0.001, "Rigidbody2D velocity y");
                    AssertApproximately(values[8].Read<double>(), 6, 0.001, "Rigidbody2D angular velocity");
                    AssertApproximately(values[9].Read<double>(), 0.75, 0.001, "Rigidbody2D gravity");

                    using (thread.DoString(
                        "local body = rigidbody\n" +
                        "body.simulated = true\n" +
                        "body:AddForce(vector.create(1, 2, 99))\n" +
                        "body:MovePosition(vector.create(7, 8, 99))\n" +
                        "body:MoveRotation(20)\n" +
                        "body:Sleep()\n" +
                        "body:WakeUp()",
                        "@consumer/full-demo-rigidbody2d-methods.luau"))
                    {
                    }
                    if (!rigidbody.simulated)
                    {
                        throw new InvalidOperationException(
                            "The imported Full Demo Rigidbody2D simulated setter did not " +
                            "enable the isolated Unity body.");
                    }
                }

                using (var colliderHandle = root.CreateHandle(
                    (Collider2D)collider,
                    colliderDescriptor))
                {
                    thread["collider"] = colliderHandle;
                    using var values = thread.DoString(
                        "local target = collider\n" +
                        "target.enabled = false\n" +
                        "target.isTrigger = true\n" +
                        "return target.gameObject.name, target.enabled, target.isTrigger",
                        "@consumer/full-demo-collider2d-policy.luau");
                    if (values.Length != 3 ||
                        values[0].Read<string>() != gameObject.name ||
                        values[1].Read<bool>() ||
                        !values[2].Read<bool>())
                    {
                        throw new InvalidOperationException(
                            "The imported Full Demo Collider2D policy did not preserve " +
                            "its expected member surface.");
                    }
                }

                using (var spriteHandle = root.CreateHandle(
                    spriteRenderer,
                    spriteRendererDescriptor))
                {
                    thread["spriteRenderer"] = spriteHandle;
                    using var values = thread.DoString(
                        "local renderer = spriteRenderer\n" +
                        "renderer.enabled = false\n" +
                        "renderer.color = { r = 0.1, g = 0.2, b = 0.3, a = 0.4 }\n" +
                        "renderer.flipX = true\n" +
                        "renderer.flipY = true\n" +
                        "renderer.sortingOrder = 7\n" +
                        "local color = renderer.color\n" +
                        "return renderer.gameObject.name, renderer.enabled, " +
                        "color.r, color.g, color.b, color.a, renderer.flipX, " +
                        "renderer.flipY, renderer.sortingOrder",
                        "@consumer/full-demo-sprite-renderer-policy.luau");
                    if (values.Length != 9 ||
                        values[0].Read<string>() != gameObject.name ||
                        values[1].Read<bool>() ||
                        !values[6].Read<bool>() ||
                        !values[7].Read<bool>() ||
                        values[8].Read<int>() != 7)
                    {
                        throw new InvalidOperationException(
                            "The imported Full Demo SpriteRenderer policy did not " +
                            "preserve its expected member surface.");
                    }
                    AssertApproximately(values[2].Read<double>(), 0.1, 0.001, "Sprite color r");
                    AssertApproximately(values[3].Read<double>(), 0.2, 0.001, "Sprite color g");
                    AssertApproximately(values[4].Read<double>(), 0.3, 0.001, "Sprite color b");
                    AssertApproximately(values[5].Read<double>(), 0.4, 0.001, "Sprite color a");
                }

                using (var audioHandle = root.CreateHandle(
                    audioSource,
                    audioSourceDescriptor))
                {
                    thread["audioSource"] = audioHandle;
                    using var values = thread.DoString(
                        "local audio = audioSource\n" +
                        "audio.volume = 0.25\n" +
                        "audio.pitch = 0.75\n" +
                        "audio.loop = true\n" +
                        "audio:Play()\n" +
                        "audio:Pause()\n" +
                        "audio:Stop()\n" +
                        "return audio.gameObject.name, audio.hasClip, " +
                        "audio.volume, audio.pitch, audio.loop, audio.isPlaying",
                        "@consumer/full-demo-audio-source-policy.luau");
                    if (values.Length != 6 ||
                        values[0].Read<string>() != gameObject.name ||
                        values[1].Read<bool>() ||
                        !values[4].Read<bool>() ||
                        values[5].Read<bool>())
                    {
                        throw new InvalidOperationException(
                            "The imported Full Demo AudioSource policy did not " +
                            "preserve its expected member surface.");
                    }
                    AssertApproximately(values[2].Read<double>(), 0.25, 0.001, "Audio volume");
                    AssertApproximately(values[3].Read<double>(), 0.75, 0.001, "Audio pitch");
                }

                using (var textHandle = root.CreateHandle(
                    textMesh,
                    textMeshDescriptor))
                {
                    thread["textMesh"] = textHandle;
                    using var values = thread.DoString(
                        "local text = textMesh\n" +
                        "text.text = 'Score: 42'\n" +
                        "text.fontSize = 48\n" +
                        "text.color = { r = 0.8, g = 0.7, b = 0.6, a = 0.5 }\n" +
                        "local color = text.color\n" +
                        "return text.gameObject.name, text.text, " +
                        "text.fontSize, color.r, color.g, color.b, color.a",
                        "@consumer/full-demo-text-mesh-policy.luau");
                    if (values.Length != 7 ||
                        values[0].Read<string>() != textGameObject.name ||
                        values[1].Read<string>() != "Score: 42" ||
                        values[2].Read<int>() != 48)
                    {
                        throw new InvalidOperationException(
                            "The imported Full Demo TextMesh policy did not preserve " +
                            "its expected member surface.");
                    }
                    const double textColorTolerance = 0.005;
                    AssertApproximately(
                        values[3].Read<double>(),
                        0.8,
                        textColorTolerance,
                        "Text color r");
                    AssertApproximately(
                        values[4].Read<double>(),
                        0.7,
                        textColorTolerance,
                        "Text color g");
                    AssertApproximately(
                        values[5].Read<double>(),
                        0.6,
                        textColorTolerance,
                        "Text color b");
                    AssertApproximately(
                        values[6].Read<double>(),
                        0.5,
                        textColorTolerance,
                        "Text color a");
                }

                var destroyedTarget = new GameObject("Destroyed sample capability target");
                using var destroyedHandle = root.CreateHandle(
                    destroyedTarget,
                    gameObjectDescriptor);
                thread["destroyedTarget"] = destroyedHandle;
                UnityEngine.Object.DestroyImmediate(destroyedTarget);

                try
                {
                    using var ignored = thread.DoString(
                        "local target = destroyedTarget\n" +
                        "return target.name",
                        "@consumer/full-demo-destroyed-policy.luau");
                    throw new InvalidOperationException(
                        "The imported Full Demo policy accepted a destroyed target.");
                }
                catch (LuauManagedCallbackException exception)
                    when (exception.InnerException is MissingReferenceException)
                {
                }
            }
            finally
            {
                thread["gameObject"] = LuauValue.Nil;
                thread["textGameObject"] = LuauValue.Nil;
                thread["emptyGameObject"] = LuauValue.Nil;
                thread["transform"] = LuauValue.Nil;
                thread["rigidbody"] = LuauValue.Nil;
                thread["collider"] = LuauValue.Nil;
                thread["spriteRenderer"] = LuauValue.Nil;
                thread["audioSource"] = LuauValue.Nil;
                thread["textMesh"] = LuauValue.Nil;
                thread["destroyedTarget"] = LuauValue.Nil;
                UnityEngine.Object.DestroyImmediate(gameObject);
                UnityEngine.Object.DestroyImmediate(physicsGameObject);
                UnityEngine.Object.DestroyImmediate(textGameObject);
                UnityEngine.Object.DestroyImmediate(emptyGameObject);
            }
        }

        static void ValidateUnsupportedBehaviourReference(Type behaviourType)
        {
            var target = new GameObject("Unsupported reference probe");
            try
            {
                var behaviour = target.AddComponent(behaviourType);
                var camera = target.AddComponent<Camera>();
                var referenceType = behaviourType.GetNestedType(
                    "ObjectReference",
                    BindingFlags.Public)
                    ?? throw new InvalidOperationException(
                        "The imported Full Demo behaviour is missing ObjectReference.");
                var reference = Activator.CreateInstance(referenceType);
                referenceType.GetField(
                    "referenceName",
                    BindingFlags.Instance | BindingFlags.NonPublic)?
                    .SetValue(reference, "unsupportedCamera");
                referenceType.GetField(
                    "target",
                    BindingFlags.Instance | BindingFlags.NonPublic)?
                    .SetValue(reference, camera);

                var references = Array.CreateInstance(referenceType, 1);
                references.SetValue(reference, 0);
                behaviourType.GetField(
                    "objectReferences",
                    BindingFlags.Instance | BindingFlags.NonPublic)?
                    .SetValue(behaviour, references);
                var validate = behaviourType.GetMethod(
                    "ValidateBindingsAndCreatePrefabCatalog",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException(
                        "The imported Full Demo behaviour is missing binding validation.");

                try
                {
                    validate.Invoke(behaviour, null);
                    throw new InvalidOperationException(
                        "LuauBehaviour accepted an unsupported Camera named reference.");
                }
                catch (TargetInvocationException exception)
                    when (exception.InnerException is InvalidOperationException invalid &&
                        invalid.Message.Contains(
                            "unsupported type 'Camera'",
                            StringComparison.Ordinal))
                {
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        static LuauObjectDescriptor<T> GetDescriptor<T>(
            Type capabilityType,
            string fieldName)
            where T : class
        {
            var field = capabilityType.GetField(fieldName)
                ?? throw new InvalidOperationException(
                    "The imported Full Demo capability type is missing " + fieldName + ".");
            return (LuauObjectDescriptor<T>)field.GetValue(null);
        }

        static void ValidateGeneratedFullDemoLibraries(LuauState thread)
        {
            using var values = thread.DoString(
                "local euler = Quaternion.Euler(vector.create(0, 0, 90))\n" +
                "local axis = Quaternion.AngleAxis(90, vector.create(0, 0, 1))\n" +
                "local inverse = Quaternion.Inverse(euler)\n" +
                "local lerp = Quaternion.Lerp(euler, axis, 0.5)\n" +
                "local slerp = Quaternion.Slerp(euler, axis, 0.5)\n" +
                "local product = Quaternion.Multiply(euler, inverse)\n" +
                "local angles = Quaternion.ToEulerAngles(euler)\n" +
                "return type(euler) == 'table', euler.z, euler.w, " +
                "type(lerp) == 'table', type(slerp) == 'table', product.w, " +
                "angles.z, type(Input.GetKeyDown) == 'function', " +
                "type(Input.GetKey) == 'function', " +
                "type(Input.GetMouseButtonDown) == 'function', " +
                "type(Input.GetMouseButton) == 'function', " +
                "type(Input.GetTouchPhase) == 'function', Input.touchCount >= 0",
                "@consumer/full-demo-generated-libraries.luau");

            if (values.Length != 13 ||
                !values[0].Read<bool>() ||
                !values[3].Read<bool>() ||
                !values[4].Read<bool>() ||
                !values[7].Read<bool>() ||
                !values[8].Read<bool>() ||
                !values[9].Read<bool>() ||
                !values[10].Read<bool>() ||
                !values[11].Read<bool>() ||
                !values[12].Read<bool>())
            {
                throw new InvalidOperationException(
                    "The imported Full Luau Scripting Demo libraries did not expose " +
                    "their generated member surfaces.");
            }

            AssertApproximately(values[1].Read<double>(), Math.Sqrt(0.5), 0.001, "Euler z");
            AssertApproximately(values[2].Read<double>(), Math.Sqrt(0.5), 0.001, "Euler w");
            AssertApproximately(values[5].Read<double>(), 1.0, 0.001, "Multiply identity w");
            AssertApproximately(values[6].Read<double>(), 90.0, 0.01, "ToEulerAngles z");
        }

        static void ValidateSharedTable(LuauState root)
        {
            using var shared = root.CreateTable();
            using var first = root.CreateSandboxedThread();
            using var second = root.CreateSandboxedThread();
            first["shared"] = shared;
            second["shared"] = shared;

            using (first.DoString(
                "local sharedState = shared\n" +
                "privateValue = 17\n" +
                "sharedState.score = 41",
                "@consumer/full-demo-shared-writer.luau"))
            {
            }

            using var values = second.DoString(
                "local sharedState = shared\n" +
                "sharedState.score += 1\n" +
                "return sharedState.score, privateValue == nil",
                "@consumer/full-demo-shared-reader.luau");
            if (values.Length != 2 ||
                values[0].Read<int>() != 42 ||
                !values[1].Read<bool>())
            {
                throw new InvalidOperationException(
                    "The Full Luau Scripting Demo shared table did not cross sandboxed " +
                    "threads while their ordinary globals remained isolated.");
            }
        }

        static void AssertApproximately(
            double actual,
            double expected,
            double tolerance,
            string operation)
        {
            if (Math.Abs(actual - expected) > tolerance)
            {
                throw new InvalidOperationException(
                    operation + " returned " + actual + " instead of " + expected + ".");
            }
        }

        static void ValidateXmlIntelliSense()
        {
            var package = PackageInfo.FindForAssembly(typeof(LuauState).Assembly)
                ?? throw new InvalidOperationException(
                    "Unity did not resolve the Luau assembly from a package.");
            var xmlPath = Path.Combine(package.resolvedPath, "Runtime", "Luau.xml");
            if (!File.Exists(xmlPath))
            {
                throw new InvalidOperationException(
                    "The resolved package is missing Runtime/Luau.xml IntelliSense documentation.");
            }

            var document = XDocument.Load(xmlPath, LoadOptions.None);
            var assemblyName = document.Root?
                .Element("assembly")?
                .Element("name")?
                .Value;
            if (!string.Equals(assemblyName, "Luau", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Runtime/Luau.xml does not describe the shipped Luau assembly.");
            }

            var documentedMembers = document.Root?
                .Element("members")?
                .Elements("member")
                .Select(element => (string)element.Attribute("name"))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var requiredMember in new[]
            {
                "T:Luau.LuauState",
                "T:Luau.LuauResultScope",
                "M:Luau.LuauCallContext.Read``1(System.Int32)",
            })
            {
                if (documentedMembers == null || !documentedMembers.Contains(requiredMember))
                {
                    throw new InvalidOperationException(
                        "Runtime/Luau.xml is missing IntelliSense for " + requiredMember + ".");
                }
            }
        }

        static void AssertSingleInteger(LuauResultScope values, int expected, string operation)
        {
            using (values)
            {
                if (values.Length != 1 || values[0].Read<int>() != expected)
                {
                    throw new InvalidOperationException(operation + " returned an unexpected result.");
                }
            }
        }
    }
}
