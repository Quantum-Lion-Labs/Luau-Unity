using Luau;
using UnityEngine;

namespace Luau.Unity.Samples.GettingStarted
{
    /// <summary>
    /// An application-owned component whose Luau capability is generated from
    /// the annotated members. Unannotated members remain unreachable.
    /// </summary>
    [LuauLibrary("GettingStartedTarget", Exposure = LuauLibraryExposure.Capability)]
    public sealed partial class GettingStartedTarget : MonoBehaviour
    {
        [LuauMember("score")]
        public int Score { get; set; }

        [LuauMember("increment")]
        public void Increment(int amount)
        {
            Score = checked(Score + amount);
        }
    }
}
