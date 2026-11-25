using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace TopiaForge.Mods.UnityUi
{
    internal static partial class TopiaForgeInputOwnership
    {
        internal static bool ReadyForCapture => installed && !stopped;

        // A press taken before capture opened (for example, Submit on Rebind) must first be released.
        internal static bool HoldsControl(InputDevice device, InputControl control)
        {
            if (device is Keyboard && control is KeyControl key)
                return Gate.IsTaken(device.deviceId, (int)key.keyCode);
            if (device is not Gamepad pad) return false;

            var index = ReferenceEquals(control, pad.buttonEast) ? EastButton
                : ReferenceEquals(control, pad.buttonSouth) ? SouthButton
                : ReferenceEquals(control, pad.dpad) ? Dpad
                : ReferenceEquals(control, pad.leftStick) ? LeftStick
                : ReferenceEquals(control, pad.rightStick) ? RightStick : -1;
            return Gate.IsTaken(device.deviceId, index);
        }
    }

    /// <summary>Marks a kit text field: while it has the keyboard, TopiaForge takes every key from the game.</summary>
    internal sealed class TopiaForgeTextEntry : MonoBehaviour
    {
    }
}
