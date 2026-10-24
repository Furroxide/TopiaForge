using System;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Which presses TopiaForge has taken from the game, Unity-free. The Input System filter shows it each control's value
    /// in each keyboard and controller event, before any game action sees the event. A press that begins while the claims
    /// cover its control is taken: that event holds the control at rest, and so does every later one until the control is
    /// released, however the claims change meanwhile, so a held key never reaches the game as a fresh press. A press that
    /// began before a claim, or that no claim covers, is the game's until it is released. The kit reads the presses it took
    /// here: Back, submit, held directions and keys, each press counted in the frame it was taken.
    /// </summary>
    internal sealed class TopiaForgeInputGate
    {
        /// <summary>The most keyboards and controllers holding taken presses at once; a press on one more reaches the game.</summary>
        public const int DeviceSlots = 8;

        /// <summary>Controls per device: a keyboard's keys by their Input System key number, a controller's by index.</summary>
        public const int ControlsPerDevice = 128;

        /// <summary>A control counts as pressed from this magnitude, as a stick pushed halfway does.</summary>
        public const float PressThreshold = 0.5f;

        /// <summary>A taken control counts as released below this magnitude, so a stick settling back is not a fresh press.</summary>
        public const float ReleaseThreshold = 0.15f;

        private const int MaxDirections = 16;
        private const int NoFrame = int.MinValue;

        private readonly int[] deviceIds = new int[DeviceSlots];
        private readonly int[] heldPerSlot = new int[DeviceSlots];
        private readonly bool[] taken = new bool[DeviceSlots * ControlsPerDevice];
        private readonly TopiaForgeInputRole[] roles = new TopiaForgeInputRole[DeviceSlots * ControlsPerDevice];
        private readonly float[] directionX = new float[DeviceSlots * ControlsPerDevice];
        private readonly float[] directionY = new float[DeviceSlots * ControlsPerDevice];
        private readonly int[] directions = new int[MaxDirections];
        private readonly int[] keyFrames = new int[ControlsPerDevice];
        private int directionCount;
        private int held;
        private int backHeld;
        private int backFrame = NoFrame;
        private int submitFrame = NoFrame;

        /// <summary>Creates a gate that holds nothing.</summary>
        public TopiaForgeInputGate()
        {
            for (var index = 0; index < keyFrames.Length; index++)
            {
                keyFrames[index] = NoFrame;
            }
        }

        /// <summary>Gets how many taken presses are still held.</summary>
        public int Held => held;

        /// <summary>Gets whether any taken press is still held, which keeps the filter looking after the claims end.</summary>
        public bool HoldsAny => held > 0;

        /// <summary>Gets whether a taken Back press (Escape, or a controller's B) is still held.</summary>
        public bool BackHeld => backHeld > 0;

        /// <summary>
        /// Gets whether an event from <paramref name="device"/> needs looking at under <paramref name="claims"/>: only a
        /// keyboard's or a controller's, and only while something is claimed or a taken press is still held. Everything else,
        /// a mouse moving above all, passes at once.
        /// </summary>
        public bool Examines(TopiaForgeInputDevice device, TopiaForgeInputClaim claims)
        {
            return device != TopiaForgeInputDevice.Other && (claims != TopiaForgeInputClaim.None || held > 0);
        }

        /// <summary>Gets whether <paramref name="deviceId"/> holds any taken press.</summary>
        public bool HoldsOn(int deviceId)
        {
            var slot = SlotOf(deviceId);
            return slot >= 0 && heldPerSlot[slot] > 0;
        }

        /// <summary>
        /// Shows the gate one control's value in an event. <paramref name="magnitude"/> is the control's in the event and
        /// <paramref name="before"/> the device's before it (a taken press never reaches the device, so it reads at rest);
        /// <paramref name="x"/> and <paramref name="y"/> are a direction's vector. Returns whether the event must hold the
        /// control at rest.
        /// </summary>
        public bool Take(
            int deviceId,
            TopiaForgeInputDevice device,
            int control,
            TopiaForgeInputRole role,
            float magnitude,
            float before,
            float x,
            float y,
            TopiaForgeInputClaim claims,
            int frame)
        {
            if (device == TopiaForgeInputDevice.Other || control < 0 || control >= ControlsPerDevice)
            {
                return false;
            }

            var slot = SlotOf(deviceId);
            if (slot >= 0 && taken[(slot * ControlsPerDevice) + control])
            {
                var index = (slot * ControlsPerDevice) + control;
                if (magnitude < ReleaseThreshold)
                {
                    // The event shows the control released, and the device already reads it at rest: it passes as it is.
                    Release(slot, index);
                    return false;
                }

                directionX[index] = x;
                directionY[index] = y;
                return true;
            }

            // Only a fresh press a claim covers is taken: one already held passed to the game and stays the game's.
            if (magnitude < PressThreshold || before >= PressThreshold || !TopiaForgeInputClaims.Cover(claims, role, device))
            {
                return false;
            }

            if (slot < 0 && (slot = ClaimSlot(deviceId)) < 0)
            {
                return false;
            }

            Hold(slot, (slot * ControlsPerDevice) + control, role, x, y);
            if (role == TopiaForgeInputRole.Back)
            {
                backFrame = frame;
            }
            else if (role == TopiaForgeInputRole.Submit)
            {
                submitFrame = frame;
            }

            if (device == TopiaForgeInputDevice.Keyboard)
            {
                keyFrames[control] = frame;
            }

            return true;
        }

        /// <summary>Gets whether a Back press was taken in <paramref name="frame"/>.</summary>
        public bool BackPressedIn(int frame) => backFrame == frame;

        /// <summary>Gets whether a submit press (Enter, Space, or a controller's A) was taken in <paramref name="frame"/>.</summary>
        public bool SubmitPressedIn(int frame) => submitFrame == frame;

        /// <summary>Gets whether a press of keyboard key <paramref name="key"/> was taken in <paramref name="frame"/>.</summary>
        public bool KeyPressedIn(int key, int frame) => key >= 0 && key < ControlsPerDevice && keyFrames[key] == frame;

        /// <summary>Gets whether a press of <paramref name="control"/> on <paramref name="deviceId"/> is taken and held.</summary>
        public bool IsTaken(int deviceId, int control)
        {
            var slot = SlotOf(deviceId);
            return slot >= 0 && control >= 0 && control < ControlsPerDevice && taken[(slot * ControlsPerDevice) + control];
        }

        /// <summary>Gets the direction the taken, held direction controls point, each axis between -1 and 1.</summary>
        public void Direction(out float x, out float y)
        {
            x = 0f;
            y = 0f;
            for (var index = 0; index < directionCount; index++)
            {
                x += directionX[directions[index]];
                y += directionY[directions[index]];
            }

            x = Math.Max(-1f, Math.Min(1f, x));
            y = Math.Max(-1f, Math.Min(1f, y));
        }

        /// <summary>Lets go of every press a device holds, as when it is removed or reset to rest.</summary>
        public void Forget(int deviceId)
        {
            var slot = SlotOf(deviceId);
            if (slot < 0)
            {
                return;
            }

            for (var control = 0; control < ControlsPerDevice && heldPerSlot[slot] > 0; control++)
            {
                if (taken[(slot * ControlsPerDevice) + control])
                {
                    Release(slot, (slot * ControlsPerDevice) + control);
                }
            }
        }

        /// <summary>Lets go of every taken press, as when the game loses focus or the kit shuts down.</summary>
        public void ForgetAll()
        {
            for (var slot = 0; slot < DeviceSlots; slot++)
            {
                if (heldPerSlot[slot] > 0)
                {
                    Forget(deviceIds[slot]);
                }
            }
        }

        private int SlotOf(int deviceId)
        {
            for (var slot = 0; slot < DeviceSlots; slot++)
            {
                if (heldPerSlot[slot] > 0 && deviceIds[slot] == deviceId)
                {
                    return slot;
                }
            }

            return -1;
        }

        private int ClaimSlot(int deviceId)
        {
            for (var slot = 0; slot < DeviceSlots; slot++)
            {
                if (heldPerSlot[slot] == 0)
                {
                    deviceIds[slot] = deviceId;
                    return slot;
                }
            }

            return -1;
        }

        private void Hold(int slot, int index, TopiaForgeInputRole role, float x, float y)
        {
            taken[index] = true;
            roles[index] = role;
            directionX[index] = x;
            directionY[index] = y;
            heldPerSlot[slot]++;
            held++;
            if (role == TopiaForgeInputRole.Back)
            {
                backHeld++;
            }
            else if (role == TopiaForgeInputRole.Direction && directionCount < MaxDirections)
            {
                directions[directionCount++] = index;
            }
        }

        private void Release(int slot, int index)
        {
            taken[index] = false;
            heldPerSlot[slot]--;
            held--;
            if (roles[index] == TopiaForgeInputRole.Back)
            {
                backHeld--;
            }
            else if (roles[index] == TopiaForgeInputRole.Direction)
            {
                for (var at = 0; at < directionCount; at++)
                {
                    if (directions[at] == index)
                    {
                        directions[at] = directions[--directionCount];
                        break;
                    }
                }
            }
        }
    }
}
