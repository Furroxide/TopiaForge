using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine.InputSystem;

namespace TopiaForge.ModManager
{
    /// <summary>Reads controller axes without the game's keyboard-backed legacy movement axes.</summary>
    internal static class UnityGamepadAxes
    {
        internal static float Read(InputGamepadAxis axis)
        {
            var gamepad = Gamepad.current;
            if (gamepad == null)
            {
                return 0f;
            }

            switch (axis)
            {
                case InputGamepadAxis.LeftX: return TopiaForgeInputOwnership.ReadStick(gamepad, false).x;
                case InputGamepadAxis.LeftY: return TopiaForgeInputOwnership.ReadStick(gamepad, false).y;
                case InputGamepadAxis.RightX: return TopiaForgeInputOwnership.ReadStick(gamepad, true).x;
                case InputGamepadAxis.RightY: return TopiaForgeInputOwnership.ReadStick(gamepad, true).y;
                case InputGamepadAxis.LeftTrigger: return gamepad.leftTrigger.ReadValue();
                case InputGamepadAxis.RightTrigger: return gamepad.rightTrigger.ReadValue();
                default: return 0f;
            }
        }
    }
}
