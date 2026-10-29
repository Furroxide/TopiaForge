using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using TopiaForge.Mods;
using TopiaForge.RobotKit;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class AvatarAppearanceTests
    {
        private static void NativeAccessorySlots()
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Program.FindRepoRoot(),
                "bindings/io.github.furroxide.topiaforge.robotkit.gamebindings.json")));
            var expected = new[] { "Hat", "Glasses", "Hair", "Beard", "Necklace" };
            var slots = Enum.GetValues<AvatarAccessorySlot>();
            Check(slots.Length == expected.Length, "every SDK accessory slot has a native mapping");
            for (var i = 0; i < slots.Length; i++)
            {
                var name = RobotAvatarAccessorySlots.NativeName(slots[i]);
                Check(name == expected[i], "native accessory name for " + slots[i]);
                var binding = manifest.RootElement.GetProperty("bindings").EnumerateArray().Single(item =>
                    item.GetProperty("id").GetString() == "robotkit.avatar.slot." + expected[i].ToLowerInvariant());
                Check(binding.GetProperty("member").GetString() == name,
                    "consumed native accessory name matches its declared binding");
                Check(binding.GetProperty("contractValue").GetString() == name
                    && binding.GetProperty("declaringType").GetString() == "ComposerPartType"
                    && binding.GetProperty("matchMode").GetString() == "ValueContract",
                    "native accessory value contract remains explicit");
                Check((NativeSlot)Enum.Parse(typeof(NativeSlot), name, false) == NativeSlots[i],
                    "native slot resolution uses names rather than SDK ordinals");
            }
            Throws(() => RobotAvatarAccessorySlots.NativeName((AvatarAccessorySlot)(-1)));
            Throws(() => RobotAvatarAccessorySlots.NativeName((AvatarAccessorySlot)99));
        }

        // Deliberately different ordinals from the SDK, as the runtime parses the installed native enum by name.
        private enum NativeSlot { Hat = 9, Glasses = 3, Hair = 15, Beard = 2, Necklace = 20 }
        private static readonly NativeSlot[] NativeSlots =
            { NativeSlot.Hat, NativeSlot.Glasses, NativeSlot.Hair, NativeSlot.Beard, NativeSlot.Necklace };
    }
}
