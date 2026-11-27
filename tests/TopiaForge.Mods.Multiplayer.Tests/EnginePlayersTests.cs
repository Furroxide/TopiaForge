using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static class EnginePlayersTests
{
    internal static void CancelBeforeJoinFlush()
    {
        using var rig = new VoiceSessionHarness();
        var client = rig.Track(SessionEngine.CreateClient(rig.Options, new MultiplayerSessionId("joining"), rig.NowMicros));
        rig.Connect(client, "guest");
        client.SwitchToSolo(new SoloSessionStart(new MultiplayerSessionId("solo"), new SessionSeed(1), new ParticipantId("local"), "Sam"));
        rig.Flush();
        if (rig.Diagnostics.Count(EngineDiagnosticKind.ProtocolViolation) != 0 || rig.Host.Snapshot.Participants.Count != 1 || client.CreateFacade("test").Players.Players.Count != 1)
            throw new InvalidOperationException("Cancelling before the join flush must leave cleanly without a protocol violation.");
    }
}
