using System;
using System.IO;
using System.Text.Json;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class ManifestActivationSerializationTests
    {
        internal static void Run()
        {
            var file = Path.Combine(Program.FindRepoRoot(), "mods", "TopiaForge.Worlds", "topiaforge.mod.json");
            var manifest = ModManifestJson.Deserialize(File.ReadAllText(file));
            var json = JsonUtil.Serialize(manifest);
            using var document = JsonDocument.Parse(json);
            Assert(!document.RootElement.TryGetProperty("worldGamemodes", out _),
                "Serializing active V6 must not synthesize an empty rejecting retired field.");
            var restored = ModManifestJson.Deserialize(json);
            Assert(restored.Contributions!.Worlds.Count == manifest.Contributions!.Worlds.Count
                && restored.Contributions.Gamemodes.Count == manifest.Contributions.Gamemodes.Count,
                "Serialized V6 declarations must remain readable and complete.");
            Console.WriteLine("Manifest activation serialization tests passed.");
        }
        private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
