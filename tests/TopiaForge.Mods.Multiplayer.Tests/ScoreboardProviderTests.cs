using TopiaForge.ModManager;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods.Internal;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>Scoreboard actions traverse production authentication, moderation and the virtual transport.</summary>
internal static class ScoreboardProviderTests
{
    internal static void Run()
    {
        using var rig = ControlRig.WithVoice(621);
        rig.Host.Runtime.PreferredDisplayName = "Host";
        rig.StartHosting(new InternalHostSettings("Scoreboard", 16) { Voice = true });
        var a = rig.AddPeer("first"); a.Runtime.PreferredDisplayName = "Ada";
        var b = rig.AddPeer("second"); b.Runtime.PreferredDisplayName = "Bo";
        rig.JoinAndWait(a); rig.JoinAndWait(b);
        ScreenPrompt? question = null;
        var screen = new ScoreboardPresenter(() => rig.Host.Control, () => Array.Empty<ScoreboardData>(), () => null, prompt => question = prompt, (_, _) => { });
        screen.Tick(); var ada = screen.Rows.Single(row => row.Name == "Ada");
        var liveClient = new ScoreboardPresenter(() => b.Control, () => Array.Empty<ScoreboardData>(), () => null,
            _ => throw new Exception("Client offered moderation"), (_, _) => { });
        liveClient.Tick();
        var clientAda = liveClient.Rows.Single(row => row.Name == "Ada");
        That(liveClient.Rows.Count == 3 && liveClient.Rows.All(row => !row.CanModerate), "a ready client sees the full roster without host actions");
        liveClient.Kick(clientAda); liveClient.Ban(clientAda); liveClient.ServerMute(clientAda);
        That(a.Control.Role == InternalSessionRole.Client && !rig.Host.Control.IsServerMuted(rig.Host.IdOf("Ada")), "client callbacks cannot moderate another peer");
        screen.ServerMute(ada);
        That(rig.Host.Control.IsServerMuted(rig.Host.IdOf("Ada")) && !rig.Host.Control.IsServerMuted(rig.Host.IdOf("Bo")), "scoreboard server mute affects only the authenticated target");
        screen.Kick(ada); That(question != null, "kick asks before contacting provider");
        question!.Answer(true);
        That(rig.Run(() => a.Control.Role == InternalSessionRole.Solo) && b.Control.Role == InternalSessionRole.Client, "confirmed scoreboard kick removes only chosen peer");
        rig.JoinAndWait(a); screen.Tick();
        var currentAda = screen.Rows.Single(row => row.Name == "Ada");
        screen.Ban(currentAda); question!.Answer(true);
        That(rig.Run(() => a.Control.Role == InternalSessionRole.Solo), "confirmed scoreboard ban disconnects target");
        a.Control.Join(a.Target(), new InternalJoinSettings());
        That(rig.Run(() => a.Control.GetStatus().JoinPhase == InternalJoinPhase.Ended) && a.Control.GetStatus().JoinFailure == InternalLiveFailure.Banned, "real provider refuses banned identity");
        screen.Tick(); screen.Kick(ada);
        That(b.Control.Role == InternalSessionRole.Client, "removed row never substitutes the survivor");
        screen.Kick(screen.Rows.Single(row => row.Name == "Bo")); var old = question!;
        rig.Host.Control.StopHosting(); old.Answer(true);
        That(old.IsWithdrawn, "host stop invalidates pending destructive prompt");
        var client = new ScoreboardPresenter(() => b.Control, () => Array.Empty<ScoreboardData>(), () => null, _ => throw new Exception("Client offered moderation"), (_, _) => { });
        client.Tick(); That(client.Rows.All(row => !row.CanModerate), "client has no host actions");
        Console.WriteLine("ScoreboardProviderTests passed: real two-client mute, kick, ban, stale row and stopped-host confirmation.");
    }
}
