using UnityEngine;
using UnityEngine.InputSystem;

namespace TopiaForge.Mods.UnityUi
{
    internal static partial class TopiaForgeInputOwnership
    {
        /// <summary>Gets whether the current controller's stick is still held by the kit.</summary>
        internal static bool StickTaken(bool right)
        {
            var gamepad = Gamepad.current;
            return gamepad != null && Gate.IsTaken(gamepad.deviceId, right ? RightStick : LeftStick);
        }

        /// <summary>
        /// Reads this controller's processed stick, including a direction the kit took before the device saw it.
        /// Mod bindings apply their own per-press yield policy afterwards; opting out of that policy still reads the stick.
        /// </summary>
        internal static Vector2 ReadStick(Gamepad gamepad, bool right)
        {
            return Gate.TryGetDirection(gamepad.deviceId, right ? RightStick : LeftStick, out var x, out var y)
                ? new Vector2(x, y)
                : (right ? gamepad.rightStick : gamepad.leftStick).ReadValue();
        }
    }
}
