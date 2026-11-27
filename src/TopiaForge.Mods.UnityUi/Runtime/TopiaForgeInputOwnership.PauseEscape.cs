using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using InputSystemKey = UnityEngine.InputSystem.Key;

namespace TopiaForge.Mods.UnityUi
{
    internal static partial class TopiaForgeInputOwnership
    {
        private static readonly HashSet<int> PauseEscapeDevices = new HashSet<int>(TopiaForgeInputGate.DeviceSlots);
        private static int pauseEscapeFrame = int.MinValue;

        // The legacy copy can arrive a frame after the Input System event. A Back that the normal gate took
        // still reaches the stack independently of this legacy copy.
        internal static bool SuppressesLegacyEscape
        {
            get
            {
                var frame = Time.frameCount;
                return PauseEscapeDevices.Count != 0 || pauseEscapeFrame == frame || pauseEscapeFrame == frame - 1;
            }
        }

        private static TopiaForgeInputClaim RouteKeyboardEscape(int deviceId, float pressed, float before,
            TopiaForgeInputClaim claims, int frame)
        {
            if (pressed == 0f)
            {
                ForgetPauseEscape(deviceId);
                return claims;
            }

            if (PauseEscapeDevices.Contains(deviceId)) return TopiaForgeInputClaim.None;
            if (before != 0f || Gate.IsTaken(deviceId, (int)InputSystemKey.Escape)
                || (claims & TopiaForgeInputClaim.Keyboard) != 0 || !TopiaForgeOverlayStack.AllowsKeyboardPause) return claims;

            PauseEscapeDevices.Add(deviceId);
            pauseEscapeFrame = frame;
            return TopiaForgeInputClaim.None;
        }

        private static void FinishPauseEscape(InputDevice device, InputEventPtr eventPtr)
        {
            // Opening the game's pause can remove the last overlay claim before Escape is released.
            if (device is Keyboard keyboard && PauseEscapeDevices.Contains(device.deviceId))
                Examine(keyboard, keyboard.escapeKey, eventPtr, TopiaForgeInputClaim.None, Time.frameCount);
        }

        private static void ForgetPauseEscape(int deviceId)
        {
            if (PauseEscapeDevices.Remove(deviceId)) pauseEscapeFrame = Time.frameCount;
        }

        private static void ForgetInputDevice(int deviceId)
        {
            Gate.Forget(deviceId);
            ViewerInput.Forget(deviceId);
            TopiaForgeBindingCaptureInput.Forget(deviceId);
            ForgetPauseEscape(deviceId);
        }

        private static void ResetInputGates(bool failed = false)
        {
            Gate.ForgetAll();
            PauseEscapeDevices.Clear();
            pauseEscapeFrame = int.MinValue;
            TopiaForgeBindingCaptureInput.Reset(failed);
        }
    }
}
