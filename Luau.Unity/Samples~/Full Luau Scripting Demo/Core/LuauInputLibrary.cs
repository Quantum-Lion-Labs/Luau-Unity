using System;
using Luau;
using UnityEngine;

namespace Luau.Unity.Samples.FullLuauScriptingDemo
{
    /// <summary>
    /// Source-generated global facade over the small legacy Unity input surface
    /// used by the sample. Projects can replace this editable class with their
    /// own input-system policy.
    /// </summary>
    [LuauLibrary("Input")]
    public sealed partial class LuauInputLibrary
    {
        [LuauMember("touchCount")]
        public static double TouchCount => Input.touchCount;

        [LuauMember("GetKeyDown")]
        public static bool GetKeyDown(string keyName)
        {
            return Input.GetKeyDown(ParseKey(keyName));
        }

        [LuauMember("GetKey")]
        public static bool GetKey(string keyName)
        {
            return Input.GetKey(ParseKey(keyName));
        }

        [LuauMember("GetMouseButtonDown")]
        public static bool GetMouseButtonDown(int button)
        {
            ValidateMouseButton(button);
            return Input.GetMouseButtonDown(button);
        }

        [LuauMember("GetMouseButton")]
        public static bool GetMouseButton(int button)
        {
            ValidateMouseButton(button);
            return Input.GetMouseButton(button);
        }

        [LuauMember("GetTouchPhase")]
        public static string GetTouchPhase(int index)
        {
            if ((uint)index >= (uint)Input.touchCount)
            {
                throw new LuauException(
                    "Input touch index must be less than Input.touchCount.");
            }

            return Input.GetTouch(index).phase.ToString();
        }

        static KeyCode ParseKey(string keyName)
        {
            if (string.IsNullOrWhiteSpace(keyName) ||
                !Enum.TryParse(keyName, true, out KeyCode key) ||
                !Enum.IsDefined(typeof(KeyCode), key))
            {
                throw new LuauException(
                    "Unknown Unity KeyCode name '" + keyName + "'.");
            }

            return key;
        }

        static void ValidateMouseButton(int button)
        {
            if (button < 0 || button > 6)
            {
                throw new LuauException(
                    "Unity mouse button indexes must be between 0 and 6.");
            }
        }
    }
}
