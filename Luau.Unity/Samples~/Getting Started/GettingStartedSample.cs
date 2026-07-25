using Luau;
using Luau.Unity;
using UnityEngine;

namespace Luau.Unity.Samples.GettingStarted
{
    /// <summary>A source-generated global library used by the first lessons.</summary>
    [LuauLibrary("sample")]
    public sealed partial class GettingStartedLibrary
    {
        // This explicit override makes the Luau call sample.double rather than
        // using the managed member name sample.Double.
        [LuauMember("double")]
        public static int Double(int value)
        {
            return checked(value * 2);
        }
    }

    /// <summary>
    /// A deliberately narrow manual policy for an external type that cannot be
    /// annotated. The descriptor grants name access and nothing else.
    /// </summary>
    public static class GettingStartedUnityCapabilities
    {
        public static readonly LuauObjectDescriptor<GameObject> GameObjectNameDescriptor =
            new LuauObjectDescriptor<GameObject>(
                "NamedGameObject",
                LuauUnityObjectGuard.ThrowIfDestroyed,
                new[]
                {
                    LuauObjectMember<GameObject>.Property(
                        "name",
                        (target, context) => context.Return(target.name),
                        (target, context) => target.name = context.Read<string>(2)),
                });
    }

    public sealed class GettingStartedSample : MonoBehaviour
    {
        [SerializeField]
        LuauAsset script;

        [SerializeField]
        [Tooltip("Application-owned component exposed through a generated capability.")]
        GettingStartedTarget generatedTarget;

        [SerializeField]
        [Tooltip("Unity object exposed through the sample's name-only manual descriptor.")]
        GameObject namedTarget;

        async void Start()
        {
            if (script == null || generatedTarget == null || namedTarget == null)
            {
                Debug.LogError(
                    "Assign GettingStarted.luau, a generated target, and a named target " +
                    "to the sample component.",
                    this);
                return;
            }

            using var root = LuauUnity.CreateState(new LuauUnityOptions
            {
                ConfigureHostApis = state =>
                    state.OpenLibrary(new GettingStartedLibrary()),
            });
            using var sandbox = root.CreateSandboxedThread();
            using var generatedHandle = root.CreateHandle(generatedTarget);
            using var namedHandle = root.CreateHandle(
                namedTarget,
                GettingStartedUnityCapabilities.GameObjectNameDescriptor);
            sandbox["generatedTarget"] = generatedHandle;
            sandbox["namedTarget"] = namedHandle;

            using var results = await sandbox.ExecuteAsync(
                script,
                destroyCancellationToken);

            Debug.Log(
                "Luau returned " + results[0].Read<int>() +
                " and renamed the explicit target to " + results[1].Read<string>(),
                this);
        }
    }
}
