using System;
using Luau;
using NumericsVector3 = System.Numerics.Vector3;
using UnityEngine;

namespace Luau.Unity.Samples.FullLuauScriptingDemo
{
    /// <summary>
    /// Source-generated global helpers analogous to UnityEngine.Quaternion.
    /// Quaternion values cross the script boundary as copied tables.
    /// </summary>
    [LuauLibrary("Quaternion")]
    public sealed partial class LuauQuaternionLibrary
    {
        [LuauMember("Euler")]
        public static void Euler(
            NumericsVector3 euler,
            LuauCallContext context)
        {
            ValidateFiniteVector(euler, "euler");
            LuauUnityTableValues.ReturnQuaternion(
                context,
                Quaternion.Euler(euler.X, euler.Y, euler.Z));
        }

        [LuauMember("AngleAxis")]
        public static void AngleAxis(
            double angle,
            NumericsVector3 axis,
            LuauCallContext context)
        {
            ValidateFiniteVector(axis, "axis");
            LuauUnityTableValues.ReturnQuaternion(
                context,
                Quaternion.AngleAxis(
                    ToFiniteFloat(angle, "angle"),
                    new Vector3(axis.X, axis.Y, axis.Z)));
        }

        [LuauMember("Inverse")]
        public static void Inverse(
            LuauTable value,
            LuauCallContext context)
        {
            LuauUnityTableValues.ReturnQuaternion(
                context,
                Quaternion.Inverse(LuauUnityTableValues.ReadQuaternion(value)));
        }

        [LuauMember("Lerp")]
        public static void Lerp(
            LuauTable from,
            LuauTable to,
            double amount,
            LuauCallContext context)
        {
            LuauUnityTableValues.ReturnQuaternion(
                context,
                Quaternion.Lerp(
                    LuauUnityTableValues.ReadQuaternion(from),
                    LuauUnityTableValues.ReadQuaternion(to),
                    ToFiniteFloat(amount, "amount")));
        }

        [LuauMember("Slerp")]
        public static void Slerp(
            LuauTable from,
            LuauTable to,
            double amount,
            LuauCallContext context)
        {
            LuauUnityTableValues.ReturnQuaternion(
                context,
                Quaternion.Slerp(
                    LuauUnityTableValues.ReadQuaternion(from),
                    LuauUnityTableValues.ReadQuaternion(to),
                    ToFiniteFloat(amount, "amount")));
        }

        [LuauMember("Multiply")]
        public static void Multiply(
            LuauTable left,
            LuauTable right,
            LuauCallContext context)
        {
            LuauUnityTableValues.ReturnQuaternion(
                context,
                LuauUnityTableValues.ReadQuaternion(left) *
                LuauUnityTableValues.ReadQuaternion(right));
        }

        [LuauMember("ToEulerAngles")]
        public static void ToEulerAngles(
            LuauTable value,
            LuauCallContext context)
        {
            LuauUnityValue.ReturnVector3(
                context,
                LuauUnityTableValues.ReadQuaternion(value).eulerAngles);
        }

        static float ToFiniteFloat(double value, string parameterName)
        {
            if (double.IsNaN(value) ||
                double.IsInfinity(value) ||
                value < -float.MaxValue ||
                value > float.MaxValue)
            {
                throw new LuauException(
                    "Quaternion." + parameterName + " must be a finite float.");
            }

            return (float)value;
        }

        static void ValidateFiniteVector(
            NumericsVector3 value,
            string parameterName)
        {
            if (float.IsNaN(value.X) ||
                float.IsInfinity(value.X) ||
                float.IsNaN(value.Y) ||
                float.IsInfinity(value.Y) ||
                float.IsNaN(value.Z) ||
                float.IsInfinity(value.Z))
            {
                throw new LuauException(
                    "Quaternion." + parameterName +
                    " requires finite vector components.");
            }
        }
    }
}
