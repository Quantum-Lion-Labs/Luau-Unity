using Luau;
using Luau.Unity;
using UnityEngine;

namespace Luau.Unity.Samples.GettingStarted
{
    [LuauLibrary("sample")]
    public sealed partial class GettingStartedLibrary
    {
        [LuauMember("double")]
        public static int Double(int value)
        {
            return checked(value * 2);
        }
    }

    public sealed class GettingStartedSample : MonoBehaviour
    {
        [SerializeField]
        LuauAsset script;

        async void Start()
        {
            if (script == null)
            {
                Debug.LogError("Assign GettingStarted.luau to the sample component.", this);
                return;
            }

            using var root = LuauUnity.CreateState(new LuauUnityOptions
            {
                ConfigureHostApis = state =>
                    state.OpenLibrary(new GettingStartedLibrary()),
            });
            using var results = await root.ExecuteAsync(
                script,
                destroyCancellationToken);

            Debug.Log("Luau returned " + results[0].Read<int>(), this);
        }
    }
}
