using System;
using NumericsVector3 = System.Numerics.Vector3;
using UnityEngine;

namespace Luau.Unity
{
    /// <summary>AOT-safe conversions between Unity and Luau's vector value.</summary>
    public static class LuauUnityValue
    {
        /// <summary>Reads a Luau vector argument as a Unity vector.</summary>
        public static Vector3 ReadVector3(LuauCallContext context, int index)
        {
            var value = context.Read<NumericsVector3>(index);
            return new Vector3(value.X, value.Y, value.Z);
        }

        /// <summary>Returns a Unity vector through Luau's vector value type.</summary>
        public static void ReturnVector3(LuauCallContext context, Vector3 value)
        {
            context.Return(new NumericsVector3(value.x, value.y, value.z));
        }
    }

    /// <summary>
    /// AOT-safe liveness validation for Unity objects exposed through a Luau
    /// capability.
    /// </summary>
    public static class LuauUnityObjectGuard
    {
        /// <summary>
        /// Rejects both a managed null reference and Unity's destroyed-object
        /// fake-null state before capability member dispatch.
        /// </summary>
        public static void ThrowIfDestroyed<T>(T target)
            where T : UnityEngine.Object
        {
            if (ReferenceEquals(target, null))
            {
                throw new ArgumentNullException(nameof(target));
            }
            if (target == null)
            {
                throw new MissingReferenceException(
                    "The Unity object exposed through this Luau capability has been destroyed.");
            }
        }
    }
}
