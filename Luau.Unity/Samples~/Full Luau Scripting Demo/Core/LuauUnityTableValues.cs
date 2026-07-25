using System;
using Luau;
using UnityEngine;

namespace Luau.Unity.Samples.FullLuauScriptingDemo
{
    /// <summary>
    /// Converts Unity value types that are not built into the Luau value model
    /// to and from copied Luau tables. The returned tables never grant access
    /// to a Unity object.
    /// </summary>
    public static class LuauUnityTableValues
    {
        /// <summary>Reads a quaternion table containing x, y, z, and w numbers.</summary>
        public static Quaternion ReadQuaternion(LuauCallContext context, int index)
        {
            return ReadQuaternion(context.Read<LuauTable>(index));
        }

        internal static Quaternion ReadQuaternion(LuauTable table)
        {
            var value = new Quaternion(
                ReadFiniteNumber(table, "x", "Quaternion"),
                ReadFiniteNumber(table, "y", "Quaternion"),
                ReadFiniteNumber(table, "z", "Quaternion"),
                ReadFiniteNumber(table, "w", "Quaternion"));
            return value;
        }

        /// <summary>Returns a copied { x, y, z, w } quaternion table.</summary>
        public static void ReturnQuaternion(
            LuauCallContext context,
            Quaternion value)
        {
            using (var table = context.State.CreateTable(0, 4))
            {
                table.RawSet("x", (double)value.x);
                table.RawSet("y", (double)value.y);
                table.RawSet("z", (double)value.z);
                table.RawSet("w", (double)value.w);
                context.Return(table);
            }
        }

        /// <summary>Reads a color table containing r, g, b, and a numbers.</summary>
        public static Color ReadColor(LuauCallContext context, int index)
        {
            return ReadColor(context.Read<LuauTable>(index));
        }

        internal static Color ReadColor(LuauTable table)
        {
            return new Color(
                ReadFiniteNumber(table, "r", "Color"),
                ReadFiniteNumber(table, "g", "Color"),
                ReadFiniteNumber(table, "b", "Color"),
                ReadFiniteNumber(table, "a", "Color"));
        }

        /// <summary>Returns a copied { r, g, b, a } color table.</summary>
        public static void ReturnColor(LuauCallContext context, Color value)
        {
            using (var table = context.State.CreateTable(0, 4))
            {
                table.RawSet("r", (double)value.r);
                table.RawSet("g", (double)value.g);
                table.RawSet("b", (double)value.b);
                table.RawSet("a", (double)value.a);
                context.Return(table);
            }
        }

        static float ReadFiniteNumber(
            LuauTable table,
            string fieldName,
            string valueType)
        {
            var field = table.RawGet(fieldName);
            try
            {
                if (!field.TryRead<double>(out var number))
                {
                    throw new LuauException(
                        valueType + "." + fieldName + " must be a number.");
                }
                if (double.IsNaN(number) ||
                    double.IsInfinity(number) ||
                    number < -float.MaxValue ||
                    number > float.MaxValue)
                {
                    throw new LuauException(
                        valueType + "." + fieldName + " must be a finite float.");
                }

                return (float)number;
            }
            finally
            {
                DisposeOwnedReference(field);
            }
        }

        // Raw table reads return owned wrappers. The expected path is numeric,
        // but malformed untrusted input must not leak an unexpected wrapper.
        static void DisposeOwnedReference(LuauValue value)
        {
            switch (value.Type)
            {
                case LuauType.Table:
                    value.Read<LuauTable>().Dispose();
                    break;
                case LuauType.Function:
                    value.Read<LuauFunction>().Dispose();
                    break;
                case LuauType.Buffer:
                    value.Read<LuauBuffer>().Dispose();
                    break;
                case LuauType.UserData:
                    if (value.TryRead<LuauObjectHandle>(out var handle))
                    {
                        handle.Dispose();
                    }
                    else if (value.TryRead<LuauUserData>(out var userData))
                    {
                        userData.Dispose();
                    }
                    break;
            }
        }
    }
}
