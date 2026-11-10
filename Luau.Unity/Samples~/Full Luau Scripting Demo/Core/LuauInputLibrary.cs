using System;
using System.Collections.Generic;
using System.Text;
using Luau;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using TouchPhase = UnityEngine.InputSystem.TouchPhase;

namespace Luau.Unity.Samples.FullLuauScriptingDemo
{
    /// <summary>
    /// Source-generated global facade over the small Input System surface the
    /// sample needs. Each runtime opens its own instance with that runtime's
    /// <see cref="Policy"/>, so scripts read only the keys, mouse buttons, and
    /// touches the host enabled. Replace this editable class with your own
    /// policy when your game binds input through actions rather than polled
    /// devices.
    /// </summary>
    [LuauLibrary("Input")]
    public sealed partial class LuauInputLibrary
    {
        /// <summary>Mouse buttons by <c>Input.GetMouseButton</c> index.</summary>
        [Flags]
        public enum MouseButtons
        {
            None = 0,
            Left = 1 << 0,
            Right = 1 << 1,
            Middle = 1 << 2,
            Back = 1 << 3,
            Forward = 1 << 4,
        }

        /// <summary>
        /// The input one runtime's scripts may read. The defaults are what the
        /// demo game reads and nothing more: Space, the left mouse button, and
        /// touch. Edit them on the runtime, or change them here for every new
        /// runtime.
        /// </summary>
        [Serializable]
        public sealed class Policy
        {
            [SerializeField]
            [Tooltip(
                "Input System Key names scripts may read with GetKey and GetKeyDown. " +
                "Reading any other key is a script error.")]
            string[] keys = { "Space" };

            [SerializeField]
            [Tooltip(
                "Mouse buttons scripts may read with GetMouseButton and " +
                "GetMouseButtonDown. Reading any other button is a script error.")]
            MouseButtons mouseButtons = MouseButtons.Left;

            [SerializeField]
            [Tooltip(
                "Whether scripts may read touchCount and GetTouchPhase. When off, " +
                "both are script errors.")]
            bool touch = true;

            /// <summary>Creates the default policy.</summary>
            public Policy()
            {
            }

            /// <summary>Creates a policy for code that builds its runtime by hand.</summary>
            public Policy(string[] keys, MouseButtons mouseButtons, bool touch)
            {
                this.keys = (string[])(keys ?? throw new ArgumentNullException(nameof(keys))).Clone();
                this.mouseButtons = mouseButtons;
                this.touch = touch;
            }

            /// <summary>Gets the Input System Key names scripts may read.</summary>
            public IReadOnlyList<string> Keys => keys ?? Array.Empty<string>();

            /// <summary>Gets the mouse buttons scripts may read.</summary>
            public MouseButtons AllowedMouseButtons => mouseButtons;

            /// <summary>Gets whether scripts may read touches.</summary>
            public bool Touch => touch;
        }

        const int MouseButtonCount = 5;

        readonly Key[] allowedKeys;
        readonly MouseButtons allowedMouseButtons;
        readonly bool touchAllowed;

        /// <summary>
        /// Creates the library one runtime opens. The policy is copied here, so
        /// later edits do not change what that runtime's scripts may read.
        /// </summary>
        public LuauInputLibrary(Policy policy)
        {
            if (policy == null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            var keyNames = policy.Keys;
            allowedKeys = new Key[keyNames.Count];
            for (var index = 0; index < keyNames.Count; index++)
            {
                if (!TryParseKey(keyNames[index], out allowedKeys[index]))
                {
                    throw new InvalidOperationException(
                        "The input policy lists unknown Input System Key name '" +
                        keyNames[index] + "'.");
                }
            }

            allowedMouseButtons = policy.AllowedMouseButtons;
            touchAllowed = policy.Touch;
        }

        /// <summary>Gets the number of touches the touchscreen reports this frame.</summary>
        [LuauMember("touchCount")]
        public double TouchCount
        {
            get
            {
                RequireTouch();
                return CountActiveTouches();
            }
        }

        [LuauMember("GetKeyDown")]
        public bool GetKeyDown(string keyName)
        {
            var control = FindKey(keyName);
            return control != null && control.wasPressedThisFrame;
        }

        [LuauMember("GetKey")]
        public bool GetKey(string keyName)
        {
            var control = FindKey(keyName);
            return control != null && control.isPressed;
        }

        [LuauMember("GetMouseButtonDown")]
        public bool GetMouseButtonDown(int button)
        {
            var control = FindMouseButton(button);
            return control != null && control.wasPressedThisFrame;
        }

        [LuauMember("GetMouseButton")]
        public bool GetMouseButton(int button)
        {
            var control = FindMouseButton(button);
            return control != null && control.isPressed;
        }

        /// <summary>
        /// Returns the Input System touch phase name at an active-touch index:
        /// Began, Moved, Stationary, Ended, or Canceled.
        /// </summary>
        [LuauMember("GetTouchPhase")]
        public string GetTouchPhase(int index)
        {
            RequireTouch();
            var touch = FindActiveTouch(index);
            if (touch == null)
            {
                throw new LuauException(
                    "Input touch index must be less than Input.touchCount.");
            }

            return touch.phase.ReadValue().ToString();
        }

        /// <summary>
        /// Luau statements that call each enabled kind of input once, so the
        /// runtime can warm the generated thunks without reading anything this
        /// policy refuses.
        /// </summary>
        internal string CreateWarmupSource()
        {
            var builder = new StringBuilder();
            if (allowedKeys.Length != 0)
            {
                var key = allowedKeys[0].ToString();
                builder.Append("Input.GetKeyDown(\"").Append(key).Append("\")\n");
                builder.Append("Input.GetKey(\"").Append(key).Append("\")\n");
            }

            for (var button = 0; button < MouseButtonCount; button++)
            {
                if (IsMouseButtonAllowed(button))
                {
                    builder.Append("Input.GetMouseButtonDown(").Append(button).Append(")\n");
                    builder.Append("Input.GetMouseButton(").Append(button).Append(")\n");
                    break;
                }
            }

            if (touchAllowed)
            {
                builder.Append("local _touchCount = Input.touchCount\n");
            }

            return builder.ToString();
        }

        // The policy is checked before the device, so a refused key fails the
        // same way whether or not the player has a keyboard. A device the
        // player does not have is absent rather than an error, so every lookup
        // then returns null and the polling members report false. The runtime
        // host warns once when the Input System reports no devices at all,
        // which is the misconfiguration worth surfacing.
        KeyControl FindKey(string keyName)
        {
            if (!TryParseKey(keyName, out var key))
            {
                throw new LuauException(
                    "Unknown Input System Key name '" + keyName + "'.");
            }
            if (!IsKeyAllowed(key))
            {
                throw new LuauException(
                    "Input key '" + keyName + "' is not enabled for this Luau " +
                    "runtime. Add it to the runtime's Input Policy keys to let " +
                    "scripts read it.");
            }

            var keyboard = Keyboard.current;
            return keyboard == null ? null : keyboard[key];
        }

        ButtonControl FindMouseButton(int button)
        {
            if (button < 0 || button >= MouseButtonCount)
            {
                throw new LuauException(
                    "Mouse button indexes must be between 0 and 4.");
            }
            if (!IsMouseButtonAllowed(button))
            {
                throw new LuauException(
                    "Mouse button " + button + " is not enabled for this Luau " +
                    "runtime. Add it to the runtime's Input Policy mouse buttons " +
                    "to let scripts read it.");
            }

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
                default:
                    return mouse.forwardButton;
            }
        }

        void RequireTouch()
        {
            if (!touchAllowed)
            {
                throw new LuauException(
                    "Touch input is not enabled for this Luau runtime. Enable " +
                    "touch in the runtime's Input Policy to let scripts read it.");
            }
        }

        bool IsKeyAllowed(Key key)
        {
            for (var index = 0; index < allowedKeys.Length; index++)
            {
                if (allowedKeys[index] == key)
                {
                    return true;
                }
            }

            return false;
        }

        bool IsMouseButtonAllowed(int button)
        {
            return (allowedMouseButtons & (MouseButtons)(1 << button)) != 0;
        }

        static bool TryParseKey(string keyName, out Key key)
        {
            key = Key.None;
            return !string.IsNullOrWhiteSpace(keyName) &&
                Enum.TryParse(keyName, true, out key) &&
                key != Key.None &&
                Enum.IsDefined(typeof(Key), key);
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
