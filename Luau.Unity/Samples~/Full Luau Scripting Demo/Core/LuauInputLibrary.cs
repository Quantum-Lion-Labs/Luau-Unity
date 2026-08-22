using System;
using Luau;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using TouchPhase = UnityEngine.InputSystem.TouchPhase;

namespace Luau.Unity.Samples.FullLuauScriptingDemo
{
    /// <summary>
    /// Source-generated global facade over the small Input System surface the
    /// sample needs. Replace this editable class with your own policy when your
    /// game binds input through actions rather than polled devices.
    /// </summary>
    [LuauLibrary("Input")]
    public sealed partial class LuauInputLibrary
    {
        /// <summary>Gets the number of touches the touchscreen reports this frame.</summary>
        [LuauMember("touchCount")]
        public static double TouchCount => CountActiveTouches();

        [LuauMember("GetKeyDown")]
        public static bool GetKeyDown(string keyName)
        {
            var control = FindKey(keyName);
            return control != null && control.wasPressedThisFrame;
        }

        [LuauMember("GetKey")]
        public static bool GetKey(string keyName)
        {
            var control = FindKey(keyName);
            return control != null && control.isPressed;
        }

        [LuauMember("GetMouseButtonDown")]
        public static bool GetMouseButtonDown(int button)
        {
            var control = FindMouseButton(button);
            return control != null && control.wasPressedThisFrame;
        }

        [LuauMember("GetMouseButton")]
        public static bool GetMouseButton(int button)
        {
            var control = FindMouseButton(button);
            return control != null && control.isPressed;
        }

        /// <summary>
        /// Returns the Input System touch phase name at an active-touch index:
        /// Began, Moved, Stationary, Ended, or Canceled.
        /// </summary>
        [LuauMember("GetTouchPhase")]
        public static string GetTouchPhase(int index)
        {
            var touch = FindActiveTouch(index);
            if (touch == null)
            {
                throw new LuauException(
                    "Input touch index must be less than Input.touchCount.");
            }

            return touch.phase.ReadValue().ToString();
        }

        // A device the player does not have is absent rather than an error, so
        // every lookup returns null and the polling members report false. The
        // runtime host warns once when the Input System reports no devices at
        // all, which is the misconfiguration worth surfacing.
        static KeyControl FindKey(string keyName)
        {
            var keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return null;
            }

            return keyboard[ParseKey(keyName)];
        }

        static ButtonControl FindMouseButton(int button)
        {
            var mouse = Mouse.current;
            if (mouse == null)
            {
                return null;
            }

            switch (button)
            {
                case 0:
                    return mouse.leftButton;
                case 1:
                    return mouse.rightButton;
                case 2:
                    return mouse.middleButton;
                case 3:
                    return mouse.backButton;
                case 4:
                    return mouse.forwardButton;
                default:
                    throw new LuauException(
                        "Mouse button indexes must be between 0 and 4.");
            }
        }

        static Key ParseKey(string keyName)
        {
            if (string.IsNullOrWhiteSpace(keyName) ||
                !Enum.TryParse(keyName, true, out Key key) ||
                key == Key.None ||
                !Enum.IsDefined(typeof(Key), key))
            {
                throw new LuauException(
                    "Unknown Input System Key name '" + keyName + "'.");
            }

            return key;
        }

        static int CountActiveTouches()
        {
            var touchscreen = Touchscreen.current;
            if (touchscreen == null)
            {
                return 0;
            }

            var touches = touchscreen.touches;
            var count = 0;
            for (var index = 0; index < touches.Count; index++)
            {
                if (touches[index].phase.ReadValue() != TouchPhase.None)
                {
                    count++;
                }
            }

            return count;
        }

        static TouchControl FindActiveTouch(int index)
        {
            var touchscreen = Touchscreen.current;
            if (touchscreen == null || index < 0)
            {
                return null;
            }

            var touches = touchscreen.touches;
            var active = 0;
            for (var position = 0; position < touches.Count; position++)
            {
                var touch = touches[position];
                if (touch.phase.ReadValue() == TouchPhase.None)
                {
                    continue;
                }
                if (active == index)
                {
                    return touch;
                }

                active++;
            }

            return null;
        }
    }
}
