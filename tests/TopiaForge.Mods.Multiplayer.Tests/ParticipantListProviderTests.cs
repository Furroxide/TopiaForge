extern alias provider;

using provider::TopiaForge.Multiplayer.Engine;
using TopiaForge.Mods;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The participant list through the provider: its own copy of the engine, the session a mod sees with its players
/// directory, and the voice service, with the real codec over the rig. A join to another host lists nobody until that
/// host lists its players, so the first host's speakers leave and the new host's are heard on streams of their own. A
/// lost link keeps the list, and with it every speaker's stream, through the outage and the rejoin.
/// </summary>
internal static class ParticipantListProviderTests
{
    private static readonly MultiplayerSessionId SecondSession = new("voice-list-2");

    private static VirtualLinkConditions SlowLink => new() { LatencyTicks = 1, TicksPerSecond = 100, BandwidthBytesPerSecond = 64_000 };

    internal static void AJoinToAnotherHostListsNobodyUntilThatHostDoes()
    {
        // b hears the first host's a, then moves to a second host whose world is large, so over a 64 kB/s link its answer
        // spans two frames, its list in the second. In between, the session a mod sees names the second host's session and
        // lists nobody, its players directory holds nobody, and the first host's a, who is nobody there, has left: its
        // stream has ended. Once the second host's list arrives, b's session lists that host's players, b among them as
        // the local player, and b hears the second host's a on a stream of its own.
        using var rig = new VoiceServiceRig(SlowLink);
        rig.AddHost();
        var nearA = rig.AddClient("a", untilReady: false);
        StepUntilReady(rig, nearA);
        var b = rig.AddClient("b", untilReady: false);
        StepUntilReady(rig, b);
        var second = rig.AddHost("host-2", session: SecondSession.Value);
        VoiceMuteTests.FillWorld(second.Engine.CreateFacade("world"));
        var farA = rig.AddClient("a", host: second, untilReady: false);
        StepUntilReady(rig, farA);
        var session = b.Engine.CreateFacade("observer");
        That(ParticipantListTests.Rows(session.Snapshot) == "host=Host^,a=a,b=b*", "b is in the first host's session (" + ParticipantListTests.Rows(session.Snapshot) + ")");
        var nearStream = b.Listen("a");
        nearA.Source = VoiceSource.Speech(1, seed: 61);
        rig.StepFor(800_000);
        var near = b.Heard("a").ToArray();
        That(VoiceAudio.LevelDecibels(near, near.Length / 2, near.Length / 2) > -35, "b hears the first host's a");

        var seen = new List<MultiplayerSessionSnapshot>();
        using var watch = session.SubscribeChanged(seen.Add);
        rig.Connect(b, second);
        for (var step = 0; step < 400 && !session.Snapshot.Id.Equals(SecondSession); step++) rig.Step();
        That(session.Snapshot.State == MultiplayerSessionState.Synchronizing && session.Snapshot.Participants.Count == 0 &&
             !session.Snapshot.LocalParticipantId.HasValue && !session.Snapshot.HostParticipantId.HasValue,
            "the second host accepted b, and the session lists nobody (" + ParticipantListTests.Rows(session.Snapshot) + ")");
        That(session.Players.Players.Count == 0 && session.Players.Local == null, "nor does its players directory");
        That(nearStream.IsEnded, "the first host's a is nobody in this session: its stream has ended");

        StepUntilReady(rig, b);
        That(ParticipantListTests.Rows(session.Snapshot) == "host-2=Host^,a=a,b=b*", "b's session lists the second host's players (" + ParticipantListTests.Rows(session.Snapshot) + ")");
        That(session.Players.Local is { } local && local.Id.Equals(b.Id), "and the players directory's local player is b");
        var farStream = b.Listen("a");
        farA.Source = VoiceSource.Speech(2, seed: 62);
        rig.StepFor(1_500_000);
        var far = b.Heard("a").ToArray();
        That(!farStream.IsEnded && !ReferenceEquals(farStream, nearStream) && VoiceAudio.LevelDecibels(far, far.Length / 2, far.Length / 2) > -35,
            "b hears the second host's a on a stream of its own");

        foreach (var snapshot in seen)
        {
            var rows = ParticipantListTests.Rows(snapshot);
            That(snapshot.Id.Equals(SecondSession) ? rows.Length == 0 || rows == "host-2=Host^,a=a,b=b*" : rows == "host=Host^,a=a,b=b",
                "every snapshot the session reported lists one host's players with that host's ids (" + snapshot.Id + ": " + rows + ")");
        }

        That(seen.Any(snapshot => snapshot.Id.Equals(SecondSession) && snapshot.Participants.Count == 0), "and some of them fell in the window");
    }

    internal static void ALostLinkKeepsTheListAndTheVoiceStreams()
    {
        // b hears a. b's link drops, stays down for a second, and comes back to the same host, which kept b's record. The
        // host's world is large, so over a 64 kB/s link its answer takes a while. Through the outage and the answer, b's
        // session lists the same three players, and b's stream for a is the one it had, never ended: b hears a on it again.
        using var rig = new VoiceServiceRig(SlowLink);
        var host = rig.AddHost();
        VoiceMuteTests.FillWorld(host.Engine.CreateFacade("world"));
        var a = rig.AddClient("a", untilReady: false);
        StepUntilReady(rig, a);
        var b = rig.AddClient("b", untilReady: false);
        StepUntilReady(rig, b);
        var session = b.Engine.CreateFacade("observer");
        var voice = b.Voice!;
        var stream = b.Listen("a");
        a.Source = VoiceSource.Speech(1, seed: 63);
        rig.StepFor(1_000_000);
        var before = b.Heard("a").ToArray();
        That(VoiceAudio.LevelDecibels(before, before.Length / 2, before.Length / 2) > -35, "b hears a");
        const string Listed = "host=Host^,a=a,b=b";
        That(ParticipantListTests.Rows(session.Snapshot) == Listed + "*", "b lists the host's players (" + ParticipantListTests.Rows(session.Snapshot) + ")");

        var seen = new List<MultiplayerSessionSnapshot>();
        using var watch = session.SubscribeChanged(seen.Add);
        rig.Network.Sever(b.Link!);
        for (var step = 0; step < 50 && session.Snapshot.State != MultiplayerSessionState.Connecting; step++) rig.Step();
        rig.StepFor(1_000_000);
        That(session.Snapshot.State == MultiplayerSessionState.Connecting && ParticipantListTests.Rows(session.Snapshot) == Listed,
            "a second into the outage, b's session lists the same players, none of them as b (" + ParticipantListTests.Rows(session.Snapshot) + ")");
        That(!stream.IsEnded && ReferenceEquals(voice.GetPlaybackStream(a.Id), stream), "and a's stream is the one b had, still open");

        rig.Connect(b);
        for (var step = 0; step < 400 && session.Snapshot.State != MultiplayerSessionState.Synchronizing; step++) rig.Step();
        var window = 0;
        for (; window < 400 && session.Snapshot.State == MultiplayerSessionState.Synchronizing; window++)
        {
            That(ParticipantListTests.Rows(session.Snapshot) == Listed + "*" && !stream.IsEnded,
                "while the host's answer arrives, the list and a's stream stay (" + ParticipantListTests.Rows(session.Snapshot) + ")");
            rig.Step();
        }

        That(window > 5 && session.Snapshot.State == MultiplayerSessionState.Ready && ParticipantListTests.Rows(session.Snapshot) == Listed + "*",
            "b is Ready again, " + window + " steps after the host accepted it, and listed as before");
        var mark = b.Heard("a").Count;
        a.Source = VoiceSource.Speech(1.5, seed: 64);
        rig.StepFor(1_500_000);
        var again = b.Heard("a").Skip(mark).ToArray();
        That(!stream.IsEnded && ReferenceEquals(voice.GetPlaybackStream(a.Id), stream) && VoiceAudio.LevelDecibels(again, again.Length / 2, again.Length / 2) > -35,
            "and b hears a again on the same stream");
        That(seen.Count > 0 && seen.All(snapshot => snapshot.Participants.Count == 3), "no snapshot through the outage and the rejoin lists fewer than three players");
    }

    private static void StepUntilReady(VoiceServiceRig rig, VoiceServiceRig.Member member)
    {
        for (var step = 0; step < 400 && member.Engine.Snapshot.State != MultiplayerSessionState.Ready; step++) rig.Step();
        That(member.Engine.Snapshot.State == MultiplayerSessionState.Ready, member.Name + " is Ready");
    }
}
