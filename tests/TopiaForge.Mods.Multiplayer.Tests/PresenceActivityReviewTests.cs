using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static partial class PresenceActivityTests
{
    private static void ReviewRegressions()
    {
        NativeIdlePoses();
        BufferedPosesBeforeCommit();
        ReadinessPoseBeforeArrival();
        HeldMuteAdmission();
        HeldWorldRetirement();
        DelayedRuntimeWorldRetirement();
        WorldResetDelivery();
        RosterBeforePosesRecovery();
        StaleLifetimeDelivery();
        FullRosterQuarantine();
        UplinkActivityWaitsForReady();
        Console.WriteLine("Presence activity review: 11 groups passed (held mute/world, delivered reset, reordered roster/poses, stale lifetime, full roster quarantine, readiness).");
    }

    private static void NoActivity(Node viewer, Node person, string message)
    {
        if (viewer.Presence.TryGetPose(person.Id, out var pose))
            Check((pose.Flags & Activity) == 0, message);
    }

    private static void NativeIdlePoses()
    {
        using var r = new Rig();
        var listener = r.Add("listener");
        r.Frames(40);
        foreach (var node in r.Nodes) { node.World = null; node.Phase = SessionPhase.Idle; }
        r.Frames(12);
        listener.Presence.DriveAvatars(r.Engine.NowMicros, 1f / 60);
        Check(listener.Presence.TryGetPose(r.Host.Id, out _) && Peer(listener, r.Host).Avatar != null,
            "settled native Idle admits trusted public pose and avatar without authored world token");
        foreach (var phase in new[] { SessionPhase.Preparing, SessionPhase.LoadingWorld, SessionPhase.StartingMode, SessionPhase.Stopping, SessionPhase.Running })
        {
            listener.Phase = phase;
            r.Frames(2);
            listener.Presence.DriveAvatars(r.Engine.NowMicros, 1f / 60);
            Check(!listener.Presence.TryGetPose(r.Host.Id, out _) && Peer(listener, r.Host).Avatar == null,
                "transitional null-world scope withholds public pose and avatar despite valid internal buffering");
        }
        listener.Phase = SessionPhase.Idle;
        listener.Presence.DriveAvatars(r.Engine.NowMicros, 1f / 60);
        Check(listener.Presence.TryGetPose(r.Host.Id, out _) && Peer(listener, r.Host).Avatar != null,
            "native Idle recovers from a genuine transition without reconnect");
    }

    private static void BufferedPosesBeforeCommit()
    {
        using var r = new Rig();
        var listener = r.Add("listener");
        listener.World = null;
        var phase = SessionPhase.LoadingWorld;
        listener.Runtime.SessionPhaseSource = () => phase;
        r.Frames(12);
        var held = Peer(listener, r.Host);
        Check(held.HasPose && held.Buffer != null && held.Buffer.Count > 0,
            "authenticated current-index pose buffers internally before world commit");
        Check(!listener.Presence.TryGetPose(r.Host.Id, out _), "uncommitted buffered pose stays unavailable through public lookup");
        listener.Presence.DriveAvatars(r.Engine.NowMicros, 1f / 60);
        Check(listener.Backend.Avatars.Count == 0 && listener.Presence.NametagCount == 0
            && !listener.Presence.TryGetNametag(0, out _), "uncommitted buffer creates neither avatars nor nametags");
        listener.World = r.Host.World;
        phase = SessionPhase.Running;
        listener.Presence.DriveAvatars(r.Engine.NowMicros, 1f / 60);
        Check(listener.Presence.TryGetPose(r.Host.Id, out _) && held.Avatar != null,
            "real committed world admits the buffered pose and presentation without reconnect");
        listener.World = null;
        phase = SessionPhase.Stopping;
        listener.Presence.DriveAvatars(r.Engine.NowMicros, 1f / 60);
        r.Host.Backend.Feet = new NetVector3(1, 0, 0);
        r.Frames(6);
        listener.Presence.DriveAvatars(r.Engine.NowMicros, 1f / 60);
        Check(held.HasPose && held.Buffer != null && held.Buffer.Count > 0,
            "valid traffic continues buffering during the next uncommitted load");
        Check(!listener.Presence.TryGetPose(r.Host.Id, out _) && held.Avatar == null
            && !listener.Presence.TryGetNametag(0, out _), "replacement loading withholds public pose and presentation while retaining buffers");
    }

    private static void ReadinessPoseBeforeArrival()
    {
        foreach (var sameLevel in new[] { false, true })
        {
            using var r = new Rig();
            var speaker = r.Add("speaker");
            var listener = r.Add("listener");
            r.Frames(40);
            speaker.Presence.LocalTyping = true;
            r.Speak(speaker);
            r.Frames(6);
            speaker.SendEnabled = false;
            var spawn = new TransformState(new Vec3(900, 0, 0), Quat.Identity, new Vec3(1, 1, 1));
            r.Host.Presence.RunningSpawn = () => spawn;
            if (sameLevel) r.Host.SceneVersion++;
            else r.Host.World = "world-two";
            listener.World = r.Host.World;
            r.Frames(6);
            Check(Peer(r.Host, speaker).NeedsWorldArrival, "fixture withholds the real Arrived message after world invalidation");
            speaker.Backend.Feet = new NetVector3(900, 0, 0);
            r.RawPose(speaker, PresenceFlags.Talking | PresenceFlags.Typing);
            r.Speak(speaker);
            r.Engine.Advance();
            r.Host.Presence.Receive.Run(1f / 60);
            Check((Peer(r.Host, speaker).Flags & PresenceFlags.Typing) == 0,
                "pre-Arrived typing claim is discarded rather than held for later revival");
            r.Frames(6);
            Check(Peer(r.Host, speaker).HasPose && r.Host.Presence.ValidatedPosition(speaker.Id).HasValue,
                "readiness-validated transform is accepted before Arrived in the current world");
            Check(Peer(r.Host, speaker).NeedsWorldArrival, "accepted readiness transform does not fabricate arrival");
            Flags(r.Host, speaker, 0, "host masks activity on the accepted pre-Arrived transform");
            Flags(listener, speaker, 0, "guest receives the accepted pre-Arrived transform without activity");
            Check((Peer(r.Host, speaker).Flags & PresenceFlags.Typing) == 0,
                "pre-Arrived typing claim is discarded rather than held for later revival");
            r.Frames(18);
            var writer = new WorldWireWriter(PresenceMessages.MaximumMessageBytes);
            PresenceWire.WriteArrived(writer, r.Host.World!);
            Check(speaker.Control.Inbox!.Send(writer.Segment, InternalSystemTarget.Host).Succeeded, "real current Arrived is sent");
            r.Frames(3);
            Check(!Peer(r.Host, speaker).NeedsWorldArrival, "real current Arrived enables activity normally");
            Flags(listener, speaker, 0, "arrival never revives a discarded preceding typing claim");
            r.Speak(speaker);
            r.Frames(6);
            Flags(listener, speaker, MultiplayerPoseFlags.Talking, "fresh admitted voice recovers after real arrival");
            r.RawPose(speaker, PresenceFlags.Typing);
            r.Frames(6);
            Flags(listener, speaker, Activity, "fresh current typing and voice recover after real arrival");
        }
    }

    private static void HeldMuteAdmission()
    {
        using var r = new Rig();
        var listener = r.Add("listener");
        r.Frames(40);
        Check(r.Host.Engine.SubmitVoiceFrame(++r.Host.Timestamp, VoiceFrameFlags.None, r.Codec.Packet(12)) == VoiceSubmitResult.Held,
            "non-final voiced frame really waits in the host hold");
        r.Engine.Host.SetServerMute(r.Host.Id, true);
        r.Frames(4);
        Check(!listener.Engine.TryReceiveVoiceFrame(out _), "a held host frame never relays audio after a server mute");
        r.Engine.Host.SetServerMute(r.Host.Id, false);
        r.Frames(2);
        Check(!r.Engine.Host.IsVoiceActive(r.Host.Id), "unmute cannot reveal activity from a frame flushed while muted");
        r.Speak(r.Host);
        r.Frames(6);
        Flags(listener, r.Host, MultiplayerPoseFlags.Talking, "fresh admitted speech still works after held-frame refusal");
    }

    private static void HeldWorldRetirement()
    {
        foreach (var replacement in new[] { false, true })
        {
            using var r = new Rig();
            var listener = r.Add("listener");
            r.Frames(40);
            Check(r.Host.Engine.SubmitVoiceFrame(++r.Host.Timestamp, VoiceFrameFlags.None, r.Codec.Packet(12)) == VoiceSubmitResult.Held,
                "world transition fixture really holds a non-final host frame");
            r.Host.Phase = replacement ? SessionPhase.Running : SessionPhase.Stopping;
            r.Host.World = replacement ? "world-two" : null;
            r.Frames(1);
            r.Frames(4);
            Check(!r.Engine.Host.IsVoiceActive(r.Host.Id), "world invalidation discards held voice before a later flush can revive activity");
            Check(!listener.Engine.TryReceiveVoiceFrame(out _), "world invalidation discards held voice before remote audio delivery");
        }
    }

    private static void DelayedRuntimeWorldRetirement()
    {
        foreach (var replacement in new[] { false, true })
        {
            using var r = new Rig(runtimeDriven: true);
            var listener = r.Add("listener");
            r.Frames(40);
            Check(r.Host.Engine.SubmitVoiceFrame(++r.Host.Timestamp, VoiceFrameFlags.None, r.Codec.Packet(12)) == VoiceSubmitResult.Held,
                "delayed production-frame fixture holds real non-final voice");
            r.Host.Phase = replacement ? SessionPhase.Running : SessionPhase.Stopping;
            r.Host.World = replacement ? "world-delayed" : null;
            r.Engine.AdvanceClockOnly(8);
            // This is the real composition root: pre-pump retirement must precede control.Pump/FlushVoiceHold.
            r.Host.Runtime.BeforeModsUpdate(8f / 60);
            r.Engine.AdvanceClockOnly();
            listener.Runtime.BeforeModsUpdate(8f / 60);
            Check(!listener.Engine.TryReceiveVoiceFrame(out _), "delayed first runtime pump cannot relay prior-world held audio");
            Check(!r.Host.Engine.IsVoiceActive(r.Host.Id), "delayed first runtime pump cannot revive prior-world activity");
        }
    }

    private static void WorldResetDelivery()
    {
        using var r = new Rig();
        var speaker = r.Add("speaker");
        var listener = r.Add("listener");
        r.Frames(40);
        r.Host.Presence.LocalTyping = true;
        speaker.Presence.LocalTyping = true;
        r.Speak(r.Host);
        r.Speak(speaker);
        r.Frames(6);
        Flags(listener, speaker, Activity, "remote speaker activity exists before world stop");
        Flags(listener, r.Host, Activity, "remote host activity exists before world stop");
        var received = listener.Control.Inbox!.ReceivedRosters;
        listener.Control.Inbox.HoldReliable = true;
        r.Host.Control.Inbox!.ZeroReliableTo = listener.Id;
        Peer(r.Host, listener).NeedsCatchUp = true;
        r.Host.Phase = SessionPhase.Stopping;
        r.Host.World = null;
        speaker.SendEnabled = false;
        r.Frames(18);
        Check(!r.Host.Presence.TryGetPose(speaker.Id, out _), "stopped host world never exposes an invalidated transform");
        Check(Peer(r.Host, listener).NeedsCatchUp, "a zero-result reliable roster send leaves reset catch-up pending");
        r.Host.Control.Inbox.ZeroReliableTo = null;
        r.Frames(4);
        Check(!Peer(r.Host, listener).NeedsCatchUp, "a positive reliable reset send completes catch-up");
        listener.Control.Inbox.HoldReliable = false;
        r.Frames(2);
        Check(listener.Control.Inbox.ReceivedRosters > received, "actual reliable roster reset is delivered to the guest");
        Check(!Peer(listener, speaker).HasPose && !Peer(listener, r.Host).HasPose,
            "delivered world reset removes remote old transforms instead of fabricating a reset pose");
        Check((Peer(listener, speaker).Flags & (PresenceFlags.Talking | PresenceFlags.Typing)) == 0,
            "delivered world reset clears remote speaker activity");
        Check((Peer(listener, r.Host).Flags & (PresenceFlags.Talking | PresenceFlags.Typing)) == 0,
            "delivered world reset clears remote host activity");
        foreach (var node in r.Nodes) { node.World = "world-two"; node.Phase = SessionPhase.Running; }
        speaker.SendEnabled = true;
        r.Frames(30);
        Flags(listener, speaker, 0, "stationary participant gets a fresh pose in the replacement world");
        r.Speak(speaker);
        r.Frames(6);
        Flags(listener, speaker, MultiplayerPoseFlags.Talking, "replacement world accepts fresh activity without inherited typing");
    }

    private static void RosterBeforePosesRecovery()
    {
        using var r = new Rig();
        var speaker = r.Add("speaker");
        var listener = r.Add("listener");
        r.Frames(40);
        var oldIndex = Peer(listener, speaker).Index;
        var received = listener.Control.Inbox!.ReceivedRosters;
        listener.Control.Inbox.HoldReliable = true;
        foreach (var node in r.Nodes) { node.World = "world-two"; node.Phase = SessionPhase.Running; }
        r.Frames(40);
        Check(Peer(r.Host, speaker).Index != oldIndex, "world boundary uses a quarantined fresh presence index");
        Check(listener.Control.Inbox.ReceivedRosters == received, "fixture delays actual reliable roster ownership while datagrams run");
        listener.Control.Inbox.HoldReliable = false;
        r.Frames(18);
        Check(Peer(listener, speaker).HasPose, "stationary fresh-index pose retries after its reliable roster arrives late");
        Check(Peer(listener, speaker).Index == Peer(r.Host, speaker).Index,
            "fresh roster and stationary pose converge on the same current index");
        r.Speak(speaker);
        r.Frames(6);
        Flags(listener, speaker, MultiplayerPoseFlags.Talking, "fresh activity works after roster/datagram reordering");
    }

    private static void StaleLifetimeDelivery()
    {
        using var r = new Rig();
        var speaker = r.Add("speaker");
        var listener = r.Add("listener");
        r.Frames(40);
        speaker.Presence.LocalTyping = true;
        r.Speak(speaker);
        r.Frames(6);
        listener.Control.Inbox!.HoldDatagrams = true;
        speaker.Backend.Feet = new NetVector3(speaker.Backend.Feet.X + 0.1f, 0, speaker.Backend.Feet.Z);
        r.Frames(5);
        var delayed = r.Host.Control.Inbox!.LastDatagramTo(listener.Id);
        var oldIndex = Peer(r.Host, speaker).Index;
        r.Engine.Network.Sever(speaker.Link!);
        r.Frames(2);
        listener.Control.Inbox.HoldDatagrams = false;
        r.Frames(1);
        Check(!Peer(listener, speaker).HasPose, "delayed host datagrams cannot refill a disconnected participant pose");
        NoActivity(listener, speaker, "disconnected public lookup never reveals delayed activity");
        speaker.Presence.Detaching(speaker.Control);
        r.Nodes.Remove(speaker);
        listener.Control.Inbox.HoldReliable = true;
        var rejoined = r.Add("speaker");
        rejoined.SendEnabled = false;
        r.Frames(3);
        listener.Control.Inbox.InjectedDatagrams.Enqueue(new InternalSystemDatagram(r.Host.Id, new ArraySegment<byte>(delayed)));
        r.Frames(1);
        Check(!Peer(listener, rejoined).HasPose, "old lifetime datagram is denied while the fresh reliable roster is pending");
        // A second real reconnect publishes another session lifetime while ordered roster messages remain unread.
        // The consumer later drains the actual ordered queue; no impossible reliable-message reordering is injected.
        r.Engine.Network.Sever(rejoined.Link!);
        r.Frames(2);
        rejoined.Presence.Detaching(rejoined.Control);
        r.Nodes.Remove(rejoined);
        rejoined = r.Add("speaker");
        rejoined.SendEnabled = false;
        r.Frames(3);
        listener.Control.Inbox.HoldReliable = false;
        rejoined.SendEnabled = true;
        r.Frames(18);
        Flags(listener, rejoined, 0, "rejoined stationary participant acquires a fresh current pose");
        Check(Peer(r.Host, rejoined).Index != oldIndex, "rejoin retains grace identity without reusing its quarantined presence index");
        var micros = Peer(listener, rejoined).PoseMicros;
        listener.Control.Inbox.InjectedDatagrams.Enqueue(new InternalSystemDatagram(r.Host.Id, new ArraySegment<byte>(delayed)));
        r.Frames(1);
        Flags(listener, rejoined, 0, "delayed old-lifetime activity cannot reappear after fresh roster and pose");
        Check(Peer(listener, rejoined).PoseMicros == micros, "old-lifetime packet cannot advance the current transform clock");
    }

    private static void FullRosterQuarantine()
    {
        using var r = new Rig(PresenceMessages.MaximumEntries);
        var speaker = r.Add("speaker");
        var listener = r.Add("listener");
        // Only three runtime drivers are needed: the remaining peers are real ready session participants.
        for (var i = 3; i < PresenceMessages.MaximumEntries; i++)
        {
            r.Engine.AddClient("p" + i);
            r.Engine.Flush();
        }
        r.Frames(40);
        Check(r.Host.Presence.Roster.Count == PresenceMessages.MaximumEntries, "actual host roster fills all existing presence indices");
        r.Host.Control.Inbox!.RecordPoses = false;
        for (var cycle = 0; cycle < 3; cycle++)
        {
            foreach (var node in r.Nodes) node.World = "world-q" + cycle;
            r.Frames(1);
            Check(r.Host.Presence.Peers.All(peer => peer.Index == 0), "exhausted rotation never reuses a quarantined index");
            Check(r.Host.Presence.Peers.All(peer => !r.Host.Presence.TryGetPose(peer.Id, out _)),
                "quarantine exhaustion leaves no old transform publicly current");
        }
        r.Frames(400);
        Check(r.Host.Presence.Peers.All(peer => peer.Index != 0)
            && r.Host.Presence.Peers.Select(peer => peer.Index).Distinct().Count() == PresenceMessages.MaximumEntries,
            "full roster automatically recovers distinct indices when the unchanged quarantine expires");
        r.Frames(20);
        Flags(listener, speaker, 0, "ordinary stationary presence resumes after full-roster quarantine recovery");
    }

    private static void UplinkActivityWaitsForReady()
    {
        using var r = new VoiceSessionHarness();
        var pair = r.Network.Connect("early");
        r.Host.Accept(pair.HostEnd);
        var peer = new RawPeer(pair.ClientEnd);
        peer.Join("Sam");
        r.Flush();
        var id = new ParticipantId("early");
        Check(!r.Host.IsParticipantReady(id), "raw peer deliberately withholds snapshot acknowledgement");
        pair.ClientEnd.Send(EngineChannel.Voice, new ArraySegment<byte>(VoiceSessionTests.Datagram(r.Codec, 0, 1, 1, 12)));
        r.Flush();
        Check(!r.Host.IsVoiceActive(id), "admitted pre-ready uplink cannot create current presence activity");
        peer.Send((frame, body) => SessionMessages.WriteEpochMessage(frame, body, MessageType.ReadyAck, 0));
        r.Flush();
        pair.ClientEnd.Send(EngineChannel.Voice, new ArraySegment<byte>(VoiceSessionTests.Datagram(r.Codec, 0, 2, 2, 12)));
        r.Flush();
        Check(r.Host.IsVoiceActive(id), "fresh ready uplink creates admitted activity normally");
    }
}
