using System.Text.Json;
using TopiaForge.Mods;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static class VoiceProtocolBaselineTests
{
    internal static void Run()
    {
        var baseline = File.ReadAllText("baselines/topiaforge.mods.multiplayer.api.txt", System.Text.Encoding.UTF8);
        That(baseline.Contains("  field const System.String ProtocolVersion = " + JsonSerializer.Serialize(MultiplayerModule.ProtocolVersion)),
            "SDK API baseline records the implemented protocol constant");
        That(baseline.Contains("  field const System.String PeerVersionRange = " + JsonSerializer.Serialize(MultiplayerModule.PeerVersionRange)),
            "SDK API baseline records the implemented peer range constant");
        Console.WriteLine("Voice protocol API baseline constants passed.");
    }
}
