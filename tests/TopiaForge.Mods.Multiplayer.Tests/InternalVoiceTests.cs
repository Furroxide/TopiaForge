extern alias provider;

using System.Reflection;
using TopiaForge.Mods.Internal;
using static TopiaForge.Mods.Multiplayer.Tests.ControlVoiceSteps;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;
using EngineChannel = provider::TopiaForge.Multiplayer.Engine.EngineChannel;
using IEngineLink = provider::TopiaForge.Multiplayer.Engine.IEngineLink;
using LiveJoin = provider::TopiaForge.Multiplayer.Network.LiveJoin;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The voice SPI over real providers on the deterministic network, with real cryptography, transport, engine and Opus
/// codec, each provider composed with the runtime host and composition root: voice off until it is turned on,
/// push-to-talk, listener mutes across a reconnect, an in-place replacement and a move to another host, and server mutes
/// that reach the voice relay, by hand and automatically.
/// </summary>
internal static class InternalVoiceTests
{
    private const double Heard = -35;
    private const double Silent = -80;
    private static readonly PackageSpec Sandbox = PackageSpec.Implicit("io.example.sandbox", "0.2.0", 'a');

    internal static void VoiceIsOffUntilThePlayerTurnsItOn()
    {
        // Without voice the control has none to hand out, and a host cannot mute anyone for everyone.
        using (var plain = new ControlRig(330, Sandbox))
        {
            plain.StartHosting(new InternalHostSettings("Plain", 4));
            var guest = plain.AddPeer("guest", Sandbox);
            guest.Runtime.PreferredDisplayName = "Guest";
            plain.JoinAndWait(guest);
            That(plain.Host.Control.Voice == null && guest.Control.Voice == null && plain.Host.Control.GetStatistics().Voice == null,
                "a provider without voice hands out none");
            That(plain.Run(() => plain.Host.Session.Snapshot.Participants.Count == 2), "the guest is listed");
            var refused = plain.Host.Control.SetServerMute(plain.Host.IdOf("Guest"), true);
            That(refused.ErrorCode == ModErrorCode.Unavailable && !plain.Host.Control.IsServerMuted(plain.Host.IdOf("Guest")),
                "and mutes for everyone are unavailable without it (" + refused.ErrorCode + ")");
        }

        using var rig = ControlRig.WithVoice(331, Sandbox);
        var hostEars = Host(rig);
        var sam = rig.AddPeer("sam", Sandbox);
        sam.Runtime.PreferredDisplayName = "Sam";
        rig.JoinAndWait(sam);
        var voice = sam.Control.Voice;
        That(voice != null && ReferenceEquals(voice, sam.Control.Voice), "with voice, the control hands out the provider's voice service");
        That(!voice!.Enabled && voice.Mode == InternalVoiceTransmitMode.PushToTalk && voice.ActivationThresholdDb == -40f &&
             voice.InputLevelDb == -96f && voice.TransmitState == InternalVoiceTransmitState.Idle,
            "it starts off, in push-to-talk at -40 dBFS, with a silent meter");
        Throws<ArgumentOutOfRangeException>(() => voice.Mode = (InternalVoiceTransmitMode)7, "an undefined mode is refused");
        Throws<ArgumentOutOfRangeException>(() => voice.ActivationThresholdDb = 3f, "a threshold above 0 dBFS is refused");
        Throws<ArgumentOutOfRangeException>(() => voice.ActivationThresholdDb = float.NaN, "and so is one that is no number");
        RunUntilListed(rig, rig.Host, "sam");
        hostEars.Listen("sam", IdOf(rig.Host, "sam"));

        // Off: the microphone plays under a held binding, and nothing is captured, coded or sent.
        var mic = new ControlVoice(sam, rig.Network);
        mic.Talk(Speech(1, 41));
        RunFor(rig, 1_500_000);
        var off = voice.GetStatistics();
        That(hostEars.LevelSince("sam", 0) < Silent, "with voice off the host hears nothing (" + hostEars.LevelSince("sam", 0) + " dBFS)");
        That(off.SubmittedSamples == 0 && off.FramesEncoded == 0 && off.FramesSent == 0 && voice.TransmitState == InternalVoiceTransmitState.Idle &&
             voice.InputLevelDb == -96f, "nothing was captured, coded or sent, and nothing claims otherwise");

        // On: the host hears Sam, and Sam's indicator shows it.
        voice.Enabled = true;
        That(voice.Enabled, "the player turns voice on");
        var mark = hostEars.Heard("sam").Count;
        var transmitting = false;
        mic.Talk(Speech(1.2, 42));
        rig.Run(() =>
        {
            transmitting |= voice.TransmitState == InternalVoiceTransmitState.Transmitting;
            return false;
        }, 1_600_000);
        That(hostEars.LevelSince("sam", mark) > Heard && transmitting, "on, the host hears Sam, and Sam's indicator showed transmitting");
        That(voice.GetStatistics().FramesEncoded > 40 && rig.Host.Control.GetStatistics().Voice != null, "frames were coded, and the control's statistics carry voice");

        // Off again: coding stops at once, though the microphone goes on.
        mic.Talk(Speech(2, 43));
        RunFor(rig, 500_000);
        voice.Enabled = false;
        RunFor(rig, 100_000);
        var stopped = voice.GetStatistics().FramesEncoded;
        RunFor(rig, 900_000);
        That(voice.GetStatistics().FramesEncoded == stopped && voice.InputLevelDb == -96f, "turned off, nothing more is coded and the meter reads silence");
        sam.Control.Dispose();
        That(sam.Control.Voice == null, "a control that has stopped hands out no voice");
    }

    internal static void PushToTalkAndVoiceActivationFollowTheSpi()
    {
        using var rig = ControlRig.WithVoice(332, Sandbox);
        var hostEars = Host(rig);
        var (sam, mic) = Join(rig, "sam", Sandbox);
        var voice = mic.Service;
        RunUntilListed(rig, rig.Host, "sam");
        hostEars.Listen("sam", IdOf(rig.Host, "sam"));

        // With the binding up, capture is only metered.
        mic.HoldsBinding = false;
        mic.Talk(Speech(1, 44));
        var metered = -96f;
        rig.Run(() =>
        {
            metered = Math.Max(metered, voice.InputLevelDb);
            return false;
        }, 1_300_000);
        That(metered > -60 && voice.GetStatistics().FramesEncoded == 0 && voice.GetStatistics().SubmittedSamples > 0,
            "with the binding up the meter moves and nothing is coded (" + metered + " dBFS)");
        That(hostEars.LevelSince("sam", 0) < Silent, "so the host hears nothing");

        // Held, Sam is heard; released mid-sentence, coding stops though capture goes on.
        mic.HoldsBinding = true;
        var mark = hostEars.Heard("sam").Count;
        mic.Talk(Speech(3, 45));
        RunFor(rig, 1_000_000);
        That(hostEars.LevelSince("sam", mark) > Heard, "held, the host hears Sam");
        mic.HoldsBinding = false;
        RunFor(rig, 100_000);
        var released = voice.GetStatistics().FramesEncoded;
        var submitted = voice.GetStatistics().SubmittedSamples;
        RunFor(rig, 300_000);
        mark = hostEars.Heard("sam").Count;
        RunFor(rig, 600_000);
        That(voice.GetStatistics().FramesEncoded == released && voice.GetStatistics().SubmittedSamples > submitted,
            "released, the spurt ends: capture goes on, and nothing more is coded");
        That(hostEars.LevelSince("sam", mark) < Silent, "and the host hears silence");

        // Voice activation codes only what clears the threshold the SPI set.
        mic.StopTalking();
        voice.Mode = InternalVoiceTransmitMode.VoiceActivation;
        voice.ActivationThresholdDb = -1f;
        That(voice.Mode == InternalVoiceTransmitMode.VoiceActivation && voice.ActivationThresholdDb == -1f, "the SPI sets voice activation and its threshold");
        var before = voice.GetStatistics().FramesEncoded;
        mic.HoldsBinding = true;
        mic.Talk(Speech(1, 46));
        RunFor(rig, 1_300_000);
        That(voice.GetStatistics().FramesEncoded == before, "speech under a -1 dBFS threshold codes nothing");
        voice.ActivationThresholdDb = -70f;
        mark = hostEars.Heard("sam").Count;
        mic.Talk(Speech(1.5, 47));
        RunFor(rig, 1_800_000);
        That(voice.GetStatistics().FramesEncoded > before + 40 && hostEars.LevelSince("sam", mark) > Heard, "and the same speech over -70 dBFS is heard");
    }

    internal static void ListenerMutesStayWithTheHostThatListedThem()
    {
        // Sam is p1 with the first host and Ana is p1 with the second: ids name players only on the host that gave them.
        using var rig = ControlRig.WithVoice(333, Sandbox);
        Host(rig);
        var (_, samMic) = Join(rig, "sam", Sandbox);
        var (_, kaiMic) = Join(rig, "kai", Sandbox);
        var (lee, ears) = Join(rig, "lee", Sandbox);
        var voice = ears.Service;
        RunUntilListed(rig, lee, "sam", "kai");
        var samId = IdOf(lee, "sam");
        var kaiId = IdOf(lee, "kai");
        ears.Listen("sam", samId);
        ears.Listen("kai", kaiId);
        voice.SetListenerMutes(new[] { samId });
        var mutes = new List<ParticipantId>();
        That(!voice.GetListenerMutes(mutes) && mutes.SequenceEqual(new[] { samId }), "Lee mutes Sam and reads the set back");
        void HearsKaiNotSam(string when)
        {
            var samMark = ears.Heard("sam").Count;
            var kaiMark = ears.Heard("kai").Count;
            samMic.Talk(Speech(1, 51));
            kaiMic.Talk(Speech(1, 52));
            RunFor(rig, 1_500_000);
            That(ears.LevelSince("sam", samMark) < Silent && ears.LevelSince("kai", kaiMark) > Heard,
                when + ", Lee hears Kai and not Sam (" + ears.LevelSince("sam", samMark) + ", " + ears.LevelSince("kai", kaiMark) + " dBFS)");
            That(!voice.GetListenerMutes(mutes) && mutes.SequenceEqual(new[] { samId }), when + ", the set still names Sam");
        }

        HearsKaiNotSam("at first");

        // An in-place replacement keeps every record, so the names still name the same people.
        var session = rig.Host.Session.Snapshot.Id;
        That(rig.Host.Control.ReplaceSession().Succeeded, "the host replaces its session in place");
        That(rig.Run(() => !lee.Session.Snapshot.Id.Equals(session) && lee.Session.Snapshot.State == MultiplayerSessionState.Ready), "Lee follows it");
        HearsKaiNotSam("after the replacement");

        // So does a reconnect to the host that kept Lee's record.
        var hostNode = rig.Host.Factory.Networks[^1].Node;
        var leeNode = lee.Factory.Networks[^1].Node;
        rig.Network.Partition(leeNode, hostNode, cut: true);
        That(rig.Run(() => lee.Control.GetStatus().JoinPhase == InternalJoinPhase.Reconnecting, 20_000_000, 20_000), "Lee's link goes");
        rig.Network.Partition(leeNode, hostNode, cut: false);
        That(rig.Run(() => lee.Control.GetStatus().JoinPhase == InternalJoinPhase.InSession && lee.Session.Snapshot.State == MultiplayerSessionState.Ready, 20_000_000),
            "and comes back to the same host");
        HearsKaiNotSam("after the reconnect");

        // A move to another host forgets the names: Lee hears the second host's p1, who is not Sam.
        var second = SecondHost(rig, "second", Sandbox);
        var (ana, anaMic) = Join(rig, "ana", Sandbox);
        RunUntilListed(rig, second, "ana");
        lee.Control.Leave();
        That(rig.Run(() => lee.Control.Role == InternalSessionRole.Solo), "Lee leaves the first host");
        rig.JoinAndWait(lee);
        RunUntilListed(rig, lee, "ana");
        var anaId = IdOf(lee, "ana");
        That(anaId.Equals(samId) && !voice.GetListenerMutes(mutes) && mutes.Count == 0,
            "at the second host Ana has Sam's id, " + anaId + ", and the set names nobody: the names stayed with their host");
        ears.Listen("ana", anaId);
        anaMic.Talk(Speech(1, 53));
        RunFor(rig, 1_500_000);
        That(ears.LevelSince("ana", 0) > Heard, "so Lee hears Ana");

        // The wish to hear nobody names no one, so it holds across the move back.
        voice.SetListenerMutes(Array.Empty<ParticipantId>(), muteAll: true);
        That(voice.GetListenerMutes(mutes) && mutes.Count == 0, "Lee wants to hear nobody");
        lee.Control.Leave();
        That(rig.Run(() => lee.Control.Role == InternalSessionRole.Solo), "Lee leaves the second host");
        That(voice.GetListenerMutes(mutes), "the wish holds in solo play");
        rig.Resolver.Host = () => rig.Host.Provider.Live!.Host?.Descriptor is { } descriptor ? (rig.Host.Factory.Networks[^1].Node, descriptor) : null;
        rig.JoinAndWait(lee);
        RunUntilListed(rig, lee, "sam", "kai");
        That(voice.GetListenerMutes(mutes) && mutes.Count == 0, "and back at the first host");
        ears.Listen("sam-again", IdOf(lee, "sam"));
        ears.Listen("kai-again", IdOf(lee, "kai"));
        var relayed = lee.Provider.Voice!.GetStatistics().FramesReceived;
        samMic.Talk(Speech(1, 54));
        kaiMic.Talk(Speech(1, 55));
        RunFor(rig, 1_500_000);
        That(ears.LevelSince("sam-again", 0) < Silent && ears.LevelSince("kai-again", 0) < Silent &&
             lee.Provider.Voice!.GetStatistics().FramesReceived == relayed, "where the host relays Lee nobody at all");
        That(ana.Control.Role == InternalSessionRole.Client, "while Ana stays with the second host");
    }

    internal static void AHostsServerMuteReachesTheVoiceRelay()
    {
        using var rig = ControlRig.WithVoice(334, Sandbox);
        Host(rig);
        var (sam, samMic) = Join(rig, "sam", Sandbox);
        var (lee, ears) = Join(rig, "lee", Sandbox);
        RunUntilListed(rig, lee, "sam");
        RunUntilListed(rig, rig.Host, "sam", "lee");
        var samOnHost = IdOf(rig.Host, "sam");
        var samToLee = IdOf(lee, "sam");
        var samToSam = sam.Session.Snapshot.LocalParticipantId!.Value;
        ears.Listen("sam", samToLee);
        samMic.Talk(Speech(6, 61));
        RunFor(rig, 1_000_000);
        That(ears.LevelSince("sam", 0) > Heard, "Lee hears Sam");

        That(rig.Host.Control.SetServerMute(samOnHost, true).Succeeded && rig.Host.Control.IsServerMuted(samOnHost), "the host mutes Sam");
        That(rig.Host.Control.GetStatistics().ServerMutes == 1, "and counts one mute");
        That(rig.Run(() => lee.Control.IsServerMuted(samToLee) && sam.Control.IsServerMuted(samToSam)),
            "joined players read the mute from the host's roster");
        RunFor(rig, 400_000);
        var mark = ears.Heard("sam").Count;
        RunFor(rig, 1_200_000);
        That(ears.LevelSince("sam", mark) < Silent, "the relay drops Sam's voice, so Lee hears none of it");
        That(samMic.Service.TransmitState == InternalVoiceTransmitState.MutedByHost, "and Sam's indicator says the host muted them");

        That(rig.Host.Control.SetServerMute(samOnHost, false).Succeeded && !rig.Host.Control.IsServerMuted(samOnHost), "the host unmutes Sam");
        That(rig.Run(() => !lee.Control.IsServerMuted(samToLee) && !sam.Control.IsServerMuted(samToSam)), "and everyone learns it");
        mark = ears.Heard("sam").Count;
        RunFor(rig, 1_000_000);
        That(ears.LevelSince("sam", mark) > Heard && rig.Host.Control.GetStatistics().ServerMutes == 0, "Lee hears Sam again");

        // The mute follows Sam's identity until hosting stops; hosting again starts with nobody muted.
        samMic.StopTalking();
        That(rig.Host.Control.SetServerMute(samOnHost, true).Succeeded, "the host mutes Sam again");
        rig.Host.Control.StopHosting();
        That(rig.Host.Control.GetStatistics().ServerMutes == 0, "stopping hosting forgets the mute");
        That(rig.Run(() => sam.Control.Role == InternalSessionRole.Solo && lee.Control.Role == InternalSessionRole.Solo), "and ends the players' sessions");
        rig.StartHosting(new InternalHostSettings("Again", 8));
        rig.JoinAndWait(sam);
        RunUntilListed(rig, rig.Host, "sam");
        var samAgain = IdOf(rig.Host, "sam");
        That(rig.Run(() => sam.Session.Snapshot.State == MultiplayerSessionState.Ready, 2_000_000) && !rig.Host.Control.IsServerMuted(samAgain) &&
             !sam.Control.IsServerMuted(sam.Session.Snapshot.LocalParticipantId!.Value), "Sam comes back unmuted");
    }

    internal static void AutomaticMutesAreServerMutes()
    {
        // A modified client sends malformed voice straight onto its link, past its own engine. Twenty strikes within ten
        // seconds mute it automatically for 30 s, and every control says so, as the chat ingress does.
        using var rig = ControlRig.WithVoice(335, Sandbox);
        Host(rig);
        var (max, _) = Join(rig, "max", Sandbox);
        var (lee, _) = Join(rig, "lee", Sandbox);
        RunUntilListed(rig, lee, "max");
        RunUntilListed(rig, rig.Host, "max", "lee");
        var maxOnHost = IdOf(rig.Host, "max");
        var maxToLee = IdOf(lee, "max");
        var maxToMax = max.Session.Snapshot.LocalParticipantId!.Value;
        That(!rig.Host.Control.IsServerMuted(maxOnHost), "Max starts unmuted");
        var link = (IEngineLink)typeof(LiveJoin).GetField("link", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(max.Provider.Live!.Join)!;
        for (var index = 0; index < 25; index++) link.Send(EngineChannel.Voice, new ArraySegment<byte>(new byte[] { 0x01, (byte)index }));
        That(rig.Run(() => rig.Host.Control.IsServerMuted(maxOnHost), 2_000_000), "the relay mutes Max automatically, and the host's control says so");
        That(rig.Host.Control.GetStatistics().ServerMutes == 1, "it counts as a mute");
        That(rig.Run(() => lee.Control.IsServerMuted(maxToLee) && max.Control.IsServerMuted(maxToMax)), "so do the joined players' controls, from the roster");
        That(rig.Run(() => max.Control.Voice!.TransmitState == InternalVoiceTransmitState.MutedByHost), "and Max's indicator");

        var hostChat = InternalControlTests.Open(rig.Host.Control, InternalSystemChannelKind.Chat);
        var maxChat = InternalControlTests.Open(max.Control, InternalSystemChannelKind.Chat);
        That(maxChat.Send(InternalControlTests.Bytes("flood"), InternalSystemTarget.Host).Succeeded && rig.Run(() => hostChat.GetStatistics().Dropped == 1),
            "the host drops Max's chat at ingress while the automatic mute lasts");

        That(rig.Run(() => !rig.Host.Control.IsServerMuted(maxOnHost) && !lee.Control.IsServerMuted(maxToLee) && !max.Control.IsServerMuted(maxToMax), 35_000_000, 20_000),
            "the automatic mute ends on its own, for everyone");
        That(rig.Host.Control.GetStatistics().ServerMutes == 0, "and stops counting");
    }
}
