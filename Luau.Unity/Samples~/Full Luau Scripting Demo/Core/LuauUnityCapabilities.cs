using Luau;
using UnityEngine;

namespace Luau.Unity.Samples.FullLuauScriptingDemo
{
    /// <summary>
    /// Editable, reflection-free capability policy for the Unity types used by
    /// the sample. A handle grants only the members listed in these immutable
    /// descriptors.
    /// </summary>
    public static class LuauUnityCapabilities
    {
        public static readonly LuauObjectDescriptor<GameObject> GameObjectDescriptor =
            new LuauObjectDescriptor<GameObject>(
                "GameObject",
                LuauUnityObjectGuard.ThrowIfDestroyed,
                new[]
                {
                    LuauObjectMember<GameObject>.Property(
                        "name",
                        (target, context) => context.Return(target.name),
                        (target, context) => target.name = context.Read<string>(2)),
                    LuauObjectMember<GameObject>.Property(
                        "tag",
                        (target, context) => context.Return(target.tag),
                        (target, context) => target.tag = context.Read<string>(2)),
                    LuauObjectMember<GameObject>.Property(
                        "layer",
                        (target, context) => context.Return(target.layer),
                        (target, context) => target.layer = context.Read<int>(2)),
                    LuauObjectMember<GameObject>.Property(
                        "activeSelf",
                        (target, context) => context.Return(target.activeSelf),
                        null),
                    LuauObjectMember<GameObject>.Property(
                        "activeInHierarchy",
                        (target, context) => context.Return(target.activeInHierarchy),
                        null),
                    LuauObjectMember<GameObject>.Property(
                        "transform",
                        (target, context) => ReturnHandle(
                            context,
                            target.transform,
                            TransformDescriptor),
                        null),
                    LuauObjectMember<GameObject>.Method(
                        "SetActive",
                        (target, context) =>
                            target.SetActive(context.Read<bool>(1))),
                    LuauObjectMember<GameObject>.Method(
                        "CompareTag",
                        (target, context) =>
                            context.Return(target.CompareTag(context.Read<string>(1)))),
                    LuauObjectMember<GameObject>.Method(
                        "GetComponent",
                        GetComponent),
                });

        public static readonly LuauObjectDescriptor<Transform> TransformDescriptor =
            new LuauObjectDescriptor<Transform>(
                "Transform",
                LuauUnityObjectGuard.ThrowIfDestroyed,
                new[]
                {
                    LuauObjectMember<Transform>.Property(
                        "name",
                        (target, context) => context.Return(target.name),
                        (target, context) => target.name = context.Read<string>(2)),
                    LuauObjectMember<Transform>.Property(
                        "gameObject",
                        (target, context) => ReturnHandle(
                            context,
                            target.gameObject,
                            GameObjectDescriptor),
                        null),
                    Vector3Property<Transform>(
                        "position",
                        target => target.position,
                        (target, value) => target.position = value),
                    Vector3Property<Transform>(
                        "localPosition",
                        target => target.localPosition,
                        (target, value) => target.localPosition = value),
                    LuauObjectMember<Transform>.Property(
                        "rotation",
                        (target, context) =>
                            LuauUnityTableValues.ReturnQuaternion(context, target.rotation),
                        (target, context) =>
                            target.rotation =
                                LuauUnityTableValues.ReadQuaternion(context, 2)),
                    LuauObjectMember<Transform>.Property(
                        "localRotation",
                        (target, context) =>
                            LuauUnityTableValues.ReturnQuaternion(
                                context,
                                target.localRotation),
                        (target, context) =>
                            target.localRotation =
                                LuauUnityTableValues.ReadQuaternion(context, 2)),
                    Vector3Property<Transform>(
                        "eulerAngles",
                        target => target.eulerAngles,
                        (target, value) => target.eulerAngles = value),
                    Vector3Property<Transform>(
                        "localEulerAngles",
                        target => target.localEulerAngles,
                        (target, value) => target.localEulerAngles = value),
                    Vector3Property<Transform>(
                        "localScale",
                        target => target.localScale,
                        (target, value) => target.localScale = value),
                    Vector3Property<Transform>(
                        "forward",
                        target => target.forward,
                        null),
                    Vector3Property<Transform>(
                        "right",
                        target => target.right,
                        null),
                    Vector3Property<Transform>(
                        "up",
                        target => target.up,
                        null),
                    LuauObjectMember<Transform>.Method(
                        "Translate",
                        (target, context) =>
                            target.Translate(ReadFiniteVector3(
                                context,
                                1,
                                "Transform.Translate"))),
                    LuauObjectMember<Transform>.Method(
                        "Rotate",
                        (target, context) =>
                            target.Rotate(ReadFiniteVector3(
                                context,
                                1,
                                "Transform.Rotate"))),
                    LuauObjectMember<Transform>.Method(
                        "TransformPoint",
                        (target, context) =>
                            LuauUnityValue.ReturnVector3(
                                context,
                                target.TransformPoint(
                                    ReadFiniteVector3(
                                        context,
                                        1,
                                        "Transform.TransformPoint")))),
                    LuauObjectMember<Transform>.Method(
                        "InverseTransformPoint",
                        (target, context) =>
                            LuauUnityValue.ReturnVector3(
                                context,
                                target.InverseTransformPoint(
                                    ReadFiniteVector3(
                                        context,
                                        1,
                                        "Transform.InverseTransformPoint")))),
                });

        public static readonly LuauObjectDescriptor<Rigidbody2D> Rigidbody2DDescriptor =
            new LuauObjectDescriptor<Rigidbody2D>(
                "Rigidbody2D",
                LuauUnityObjectGuard.ThrowIfDestroyed,
                new[]
                {
                    LuauObjectMember<Rigidbody2D>.Property(
                        "gameObject",
                        (target, context) => ReturnHandle(
                            context,
                            target.gameObject,
                            GameObjectDescriptor),
                        null),
                    Vector2Property<Rigidbody2D>(
                        "position",
                        target => target.position,
                        (target, value) => target.position = value),
                    LuauObjectMember<Rigidbody2D>.Property(
                        "rotation",
                        (target, context) => context.Return((double)target.rotation),
                        (target, context) => target.rotation = ReadFloat(context, 2)),
                    Vector2Property<Rigidbody2D>(
                        "linearVelocity",
                        target => target.linearVelocity,
                        (target, value) => target.linearVelocity = value),
                    LuauObjectMember<Rigidbody2D>.Property(
                        "angularVelocity",
                        (target, context) =>
                            context.Return((double)target.angularVelocity),
                        (target, context) =>
                            target.angularVelocity = ReadFloat(context, 2)),
                    LuauObjectMember<Rigidbody2D>.Property(
                        "gravityScale",
                        (target, context) =>
                            context.Return((double)target.gravityScale),
                        (target, context) =>
                            target.gravityScale = ReadFloat(context, 2)),
                    LuauObjectMember<Rigidbody2D>.Property(
                        "simulated",
                        (target, context) => context.Return(target.simulated),
                        (target, context) =>
                            target.simulated = context.Read<bool>(2)),
                    LuauObjectMember<Rigidbody2D>.Method(
                        "AddForce",
                        (target, context) =>
                            target.AddForce(ReadVector2(context, 1))),
                    LuauObjectMember<Rigidbody2D>.Method(
                        "MovePosition",
                        (target, context) =>
                            target.MovePosition(ReadVector2(context, 1))),
                    LuauObjectMember<Rigidbody2D>.Method(
                        "MoveRotation",
                        (target, context) =>
                            target.MoveRotation(ReadFloat(context, 1))),
                    LuauObjectMember<Rigidbody2D>.Method(
                        "WakeUp",
                        (target, context) => target.WakeUp()),
                    LuauObjectMember<Rigidbody2D>.Method(
                        "Sleep",
                        (target, context) => target.Sleep()),
                });

        public static readonly LuauObjectDescriptor<Collider2D> Collider2DDescriptor =
            new LuauObjectDescriptor<Collider2D>(
                "Collider2D",
                LuauUnityObjectGuard.ThrowIfDestroyed,
                new[]
                {
                    LuauObjectMember<Collider2D>.Property(
                        "gameObject",
                        (target, context) => ReturnHandle(
                            context,
                            target.gameObject,
                            GameObjectDescriptor),
                        null),
                    LuauObjectMember<Collider2D>.Property(
                        "enabled",
                        (target, context) => context.Return(target.enabled),
                        (target, context) =>
                            target.enabled = context.Read<bool>(2)),
                    LuauObjectMember<Collider2D>.Property(
                        "isTrigger",
                        (target, context) => context.Return(target.isTrigger),
                        (target, context) =>
                            target.isTrigger = context.Read<bool>(2)),
                });

        public static readonly LuauObjectDescriptor<SpriteRenderer> SpriteRendererDescriptor =
            new LuauObjectDescriptor<SpriteRenderer>(
                "SpriteRenderer",
                LuauUnityObjectGuard.ThrowIfDestroyed,
                new[]
                {
                    LuauObjectMember<SpriteRenderer>.Property(
                        "gameObject",
                        (target, context) => ReturnHandle(
                            context,
                            target.gameObject,
                            GameObjectDescriptor),
                        null),
                    LuauObjectMember<SpriteRenderer>.Property(
                        "enabled",
                        (target, context) => context.Return(target.enabled),
                        (target, context) =>
                            target.enabled = context.Read<bool>(2)),
                    LuauObjectMember<SpriteRenderer>.Property(
                        "color",
                        (target, context) =>
                            LuauUnityTableValues.ReturnColor(context, target.color),
                        (target, context) =>
                            target.color = LuauUnityTableValues.ReadColor(context, 2)),
                    LuauObjectMember<SpriteRenderer>.Property(
                        "flipX",
                        (target, context) => context.Return(target.flipX),
                        (target, context) =>
                            target.flipX = context.Read<bool>(2)),
                    LuauObjectMember<SpriteRenderer>.Property(
                        "flipY",
                        (target, context) => context.Return(target.flipY),
                        (target, context) =>
                            target.flipY = context.Read<bool>(2)),
                    LuauObjectMember<SpriteRenderer>.Property(
                        "sortingOrder",
                        (target, context) => context.Return(target.sortingOrder),
                        (target, context) =>
                            target.sortingOrder = context.Read<int>(2)),
                });

        public static readonly LuauObjectDescriptor<AudioSource> AudioSourceDescriptor =
            new LuauObjectDescriptor<AudioSource>(
                "AudioSource",
                LuauUnityObjectGuard.ThrowIfDestroyed,
                new[]
                {
                    LuauObjectMember<AudioSource>.Property(
                        "gameObject",
                        (target, context) => ReturnHandle(
                            context,
                            target.gameObject,
                            GameObjectDescriptor),
                        null),
                    LuauObjectMember<AudioSource>.Property(
                        "hasClip",
                        (target, context) => context.Return(target.clip != null),
                        null),
                    LuauObjectMember<AudioSource>.Property(
                        "volume",
                        (target, context) => context.Return((double)target.volume),
                        (target, context) =>
                            target.volume = ReadFloat(context, 2)),
                    LuauObjectMember<AudioSource>.Property(
                        "pitch",
                        (target, context) => context.Return((double)target.pitch),
                        (target, context) =>
                            target.pitch = ReadFloat(context, 2)),
                    LuauObjectMember<AudioSource>.Property(
                        "loop",
                        (target, context) => context.Return(target.loop),
                        (target, context) =>
                            target.loop = context.Read<bool>(2)),
                    LuauObjectMember<AudioSource>.Property(
                        "isPlaying",
                        (target, context) => context.Return(target.isPlaying),
                        null),
                    LuauObjectMember<AudioSource>.Method(
                        "Play",
                        (target, context) => target.Play()),
                    LuauObjectMember<AudioSource>.Method(
                        "Pause",
                        (target, context) => target.Pause()),
                    LuauObjectMember<AudioSource>.Method(
                        "Stop",
                        (target, context) => target.Stop()),
                });

        public static readonly LuauObjectDescriptor<TextMesh> TextMeshDescriptor =
            new LuauObjectDescriptor<TextMesh>(
                "TextMesh",
                LuauUnityObjectGuard.ThrowIfDestroyed,
                new[]
                {
                    LuauObjectMember<TextMesh>.Property(
                        "gameObject",
                        (target, context) => ReturnHandle(
                            context,
                            target.gameObject,
                            GameObjectDescriptor),
                        null),
                    LuauObjectMember<TextMesh>.Property(
                        "text",
                        (target, context) => context.Return(target.text),
                        (target, context) =>
                            target.text = context.Read<string>(2)),
                    LuauObjectMember<TextMesh>.Property(
                        "fontSize",
                        (target, context) => context.Return(target.fontSize),
                        (target, context) =>
                            target.fontSize = context.Read<int>(2)),
                    LuauObjectMember<TextMesh>.Property(
                        "color",
                        (target, context) =>
                            LuauUnityTableValues.ReturnColor(context, target.color),
                        (target, context) =>
                            target.color = LuauUnityTableValues.ReadColor(context, 2)),
                });

        static void GetComponent(GameObject target, LuauCallContext context)
        {
            var typeName = context.Read<string>(1);
            switch (typeName)
            {
                case "Transform":
                    ReturnHandle(context, target.transform, TransformDescriptor);
                    break;
                case "Rigidbody2D":
                    ReturnOptionalHandle(
                        context,
                        target.GetComponent<Rigidbody2D>(),
                        Rigidbody2DDescriptor);
                    break;
                case "Collider2D":
                    ReturnOptionalHandle(
                        context,
                        target.GetComponent<Collider2D>(),
                        Collider2DDescriptor);
                    break;
                case "SpriteRenderer":
                    ReturnOptionalHandle(
                        context,
                        target.GetComponent<SpriteRenderer>(),
                        SpriteRendererDescriptor);
                    break;
                case "AudioSource":
                    ReturnOptionalHandle(
                        context,
                        target.GetComponent<AudioSource>(),
                        AudioSourceDescriptor);
                    break;
                case "TextMesh":
                    ReturnOptionalHandle(
                        context,
                        target.GetComponent<TextMesh>(),
                        TextMeshDescriptor);
                    break;
                default:
                    throw new LuauException(
                        "GameObject:GetComponent does not allow component type '" +
                        typeName + "'.");
            }
        }

        internal static void ReturnSupportedObject(
            LuauCallContext context,
            Object target)
        {
            switch (target)
            {
                case GameObject gameObject:
                    ReturnHandle(context, gameObject, GameObjectDescriptor);
                    break;
                case Transform transform:
                    ReturnHandle(context, transform, TransformDescriptor);
                    break;
                case Rigidbody2D rigidbody:
                    ReturnHandle(context, rigidbody, Rigidbody2DDescriptor);
                    break;
                case Collider2D collider:
                    ReturnHandle(context, collider, Collider2DDescriptor);
                    break;
                case SpriteRenderer spriteRenderer:
                    ReturnHandle(context, spriteRenderer, SpriteRendererDescriptor);
                    break;
                case AudioSource audioSource:
                    ReturnHandle(context, audioSource, AudioSourceDescriptor);
                    break;
                case TextMesh textMesh:
                    ReturnHandle(context, textMesh, TextMeshDescriptor);
                    break;
                default:
                    throw new LuauException(
                        "The assigned reference type is not part of this sample's " +
                        "Unity capability allowlist.");
            }
        }

        internal static bool IsSupportedObject(Object target)
        {
            return target is GameObject ||
                target is Transform ||
                target is Rigidbody2D ||
                target is Collider2D ||
                target is SpriteRenderer ||
                target is AudioSource ||
                target is TextMesh;
        }

        internal static LuauObjectHandle CreateSupportedHandle(
            LuauState state,
            Object target)
        {
            switch (target)
            {
                case GameObject gameObject:
                    return state.CreateHandle(gameObject, GameObjectDescriptor);
                case Transform transform:
                    return state.CreateHandle(transform, TransformDescriptor);
                case Rigidbody2D rigidbody:
                    return state.CreateHandle(rigidbody, Rigidbody2DDescriptor);
                case Collider2D collider:
                    return state.CreateHandle(collider, Collider2DDescriptor);
                case SpriteRenderer spriteRenderer:
                    return state.CreateHandle(
                        spriteRenderer,
                        SpriteRendererDescriptor);
                case AudioSource audioSource:
                    return state.CreateHandle(audioSource, AudioSourceDescriptor);
                case TextMesh textMesh:
                    return state.CreateHandle(textMesh, TextMeshDescriptor);
                default:
                    throw new LuauException(
                        "The assigned reference type is not part of this sample's " +
                        "Unity capability allowlist.");
            }
        }

        static LuauObjectMember<T> Vector3Property<T>(
            string name,
            System.Func<T, Vector3> getter,
            System.Action<T, Vector3> setter)
            where T : class
        {
            return LuauObjectMember<T>.Property(
                name,
                getter == null
                    ? (System.Action<T, LuauCallContext>)null
                    : (target, context) =>
                        LuauUnityValue.ReturnVector3(context, getter(target)),
                setter == null
                    ? (System.Action<T, LuauCallContext>)null
                    : (target, context) =>
                        setter(
                            target,
                            ReadFiniteVector3(
                                context,
                                2,
                                name)));
        }

        static LuauObjectMember<T> Vector2Property<T>(
            string name,
            System.Func<T, Vector2> getter,
            System.Action<T, Vector2> setter)
            where T : class
        {
            return LuauObjectMember<T>.Property(
                name,
                getter == null
                    ? (System.Action<T, LuauCallContext>)null
                    : (target, context) =>
                        ReturnVector2(context, getter(target)),
                setter == null
                    ? (System.Action<T, LuauCallContext>)null
                    : (target, context) =>
                        setter(target, ReadVector2(context, 2)));
        }

        static void ReturnOptionalHandle<T>(
            LuauCallContext context,
            T target,
            LuauObjectDescriptor<T> descriptor)
            where T : Object
        {
            if (target == null)
            {
                context.Return(LuauValue.Nil);
                return;
            }

            ReturnHandle(context, target, descriptor);
        }

        static void ReturnHandle<T>(
            LuauCallContext context,
            T target,
            LuauObjectDescriptor<T> descriptor)
            where T : class
        {
            using (var handle = context.State.CreateHandle(target, descriptor))
            {
                context.Return(handle);
            }
        }

        static Vector2 ReadVector2(LuauCallContext context, int index)
        {
            var value = ReadFiniteVector3(context, index, "Vector2");
            return new Vector2(value.x, value.y);
        }

        static void ReturnVector2(LuauCallContext context, Vector2 value)
        {
            LuauUnityValue.ReturnVector3(
                context,
                new Vector3(value.x, value.y, 0f));
        }

        static float ReadFloat(LuauCallContext context, int index)
        {
            var value = context.Read<double>(index);
            if (double.IsNaN(value) ||
                double.IsInfinity(value) ||
                value < -float.MaxValue ||
                value > float.MaxValue)
            {
                throw new LuauException("Expected a finite float value.");
            }

            return (float)value;
        }

        static Vector3 ReadFiniteVector3(
            LuauCallContext context,
            int index,
            string memberName)
        {
            var value = LuauUnityValue.ReadVector3(context, index);
            if (!IsFinite(value.x) ||
                !IsFinite(value.y) ||
                !IsFinite(value.z))
            {
                throw new LuauException(
                    memberName + " requires finite vector components.");
            }

            return value;
        }

        static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
