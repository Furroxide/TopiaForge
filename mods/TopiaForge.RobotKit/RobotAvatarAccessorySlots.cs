using System;
using TopiaForge.Mods;

namespace TopiaForge.RobotKit
{
    // Bind SDK slots to native names explicitly; neither enum ordinals nor SDK renames define the native contract.
    internal static class RobotAvatarAccessorySlots
    {
        internal static string NativeName(AvatarAccessorySlot slot) => slot switch
        {
            AvatarAccessorySlot.Hat => "Hat",
            AvatarAccessorySlot.Glasses => "Glasses",
            AvatarAccessorySlot.Hair => "Hair",
            AvatarAccessorySlot.Beard => "Beard",
            AvatarAccessorySlot.Necklace => "Necklace",
            _ => throw new ArgumentOutOfRangeException(nameof(slot)),
        };
    }
}
