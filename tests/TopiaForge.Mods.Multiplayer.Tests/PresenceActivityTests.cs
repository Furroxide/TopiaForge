using TopiaForge.ModManager;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static class PresenceActivityTests
{
    private const MultiplayerPoseFlags Activity = MultiplayerPoseFlags.Talking | MultiplayerPoseFlags.Typing;
    private static CommunicationMember Member(string id, byte group) => new(new ParticipantId(id), group);
    internal static void Run()
    {
        UnityMainThreadGuard.CaptureCurrentThread();
        PrivateAudiences(); FarHost(); ExpiryMutesAndRest(); SchedulerSkip(); Backpressure(); HostileAndLifetimes();
        Console.WriteLine("Presence activity: 6 groups passed (private audiences/host projection, far host, expiry/mutes/rest, scheduler skip/backpressure, forged voice/lifetimes).");
    }
    private static void PrivateAudiences()
    {
        using var r = new Rig();
        var ghost = r.Add("ghost"); var living = r.Add("living"); var observer = r.Add("observer");
        r.Frames(40);
        using var life = new FakeModLifetime();
        using var route = r.Engine.Host.RouteCommunications(life, new CommunicationPolicy(
            new[] { Member("host", 2), Member("ghost", 2), Member("living", 1), Member("observer", 2) },
            new[] { new CommunicationRoute(1, 1), new CommunicationRoute(1, 2), new CommunicationRoute(2, 2) })).Value!;
        ghost.Presence.LocalTyping = true; r.Speak(ghost); r.Frames(6);
        Flags(r.Host, ghost, Activity, "host in ghost group sees voice and typing without playback");
        Flags(observer, ghost, Activity, "ghost observer sees relayed activity");
        Flags(living, ghost, 0, "living recipient receives neither private flag");
        Check(r.Host.Control.Voice == null, "fixture never provides host playback");
        r.Host.Presence.LocalTyping = true; r.Speak(r.Host); r.Frames(6);
        Flags(observer, r.Host, Activity, "host speaker reaches allowed recipient");
        Flags(living, r.Host, 0, "host microphone/typing is masked for living recipient");
        route.SetPolicy(new CommunicationPolicy(
            new[] { Member("host", 1), Member("ghost", 2), Member("living", 1), Member("observer", 2) },
            new[] { new CommunicationRoute(1, 1), new CommunicationRoute(2, 2) }));
        Flags(r.Host, ghost, 0, "host public projection checks current policy immediately");
        r.Speak(ghost); r.Frames(6);
        Flags(observer, ghost, Activity, "allowed remote retains activity while host is denied");
        Flags(r.Host, ghost, 0, "host remains denied through public projection");
        Check(r.Host.Control.Inbox!.LastWireFlags(living.Id, Peer(r.Host, ghost).Index) == 0, "outgoing wire strips both private flags");
    }
    private static void FarHost()
    {
        using var r = new Rig(); var speaker = r.Add("speaker"); var listener = r.Add("listener"); r.Frames(40);
        r.Host.Backend.Feet = new NetVector3(60, 0, 0); speaker.Backend.Feet = new NetVector3(0, 0, 0);
        listener.Backend.Feet = new NetVector3(2, 0, 0); r.Frames(20);
        using var life = new FakeModLifetime();
        using var route = r.Engine.Host.RouteCommunications(life, new CommunicationPolicy(
            new[] { Member("host", 1), Member("speaker", 1), Member("listener", 1) }, new[] { new CommunicationRoute(1, 1, 10) })).Value!;
        Check(!r.Engine.Host.CanCommunicate(speaker.Id, r.Host.Id) && r.Engine.Host.CanCommunicate(speaker.Id, listener.Id),
            "real routing uses fresh host-validated positions with host outside earshot");
        speaker.Presence.LocalTyping = true; r.Speak(speaker); r.Frames(6);
        Flags(listener, speaker, Activity, "near remote listener sees activity when host cannot hear");
        Flags(r.Host, speaker, 0, "far host public projection contains no activity");
    }
    private static void ExpiryMutesAndRest()
    {
        using var r = new Rig(); var speaker = r.Add("speaker"); var listener = r.Add("listener"); r.Frames(40);
        speaker.Presence.LocalTyping = true; r.Speak(speaker); r.Frames(6);
        Flags(listener, speaker, Activity, "ordinary unrouted voice and typing remain available");
        var poseMicros = Peer(r.Host, speaker).PoseMicros; r.Frames(24);
        Flags(listener, speaker, MultiplayerPoseFlags.Typing, "expiry clears talking on resting remote pose");
        Flags(r.Host, speaker, MultiplayerPoseFlags.Typing, "expiry clears host public pose");
        Check(Peer(r.Host, speaker).PoseMicros == poseMicros, "expiry does not fabricate transform timestamp");
        r.Speak(speaker); listener.Engine.SetListenerMutes(new[] { speaker.Id }); r.Frames(6);
        Flags(listener, speaker, MultiplayerPoseFlags.Typing, "listener mute removes talking but preserves typing");
        r.Engine.Host.SetListenerMutes(new[] { speaker.Id });
        Flags(r.Host, speaker, MultiplayerPoseFlags.Typing, "host listener mute applies immediately");
        listener.Engine.SetListenerMutes(Array.Empty<ParticipantId>()); r.Engine.Host.SetListenerMutes(Array.Empty<ParticipantId>());
        r.Engine.Host.SetServerMute(speaker.Id, true);
        Flags(r.Host, speaker, MultiplayerPoseFlags.Typing, "server mute clears canonical activity immediately");
        r.Engine.Host.SetServerMute(speaker.Id, false); r.Frames(6);
        Flags(listener, speaker, MultiplayerPoseFlags.Typing, "unmuting never revives old activity");
        r.Speak(speaker); r.Frames(6); Flags(listener, speaker, Activity, "fresh speech resumes after unmute"); r.Frames(24);
        using var life = new FakeModLifetime();
        using var route = r.Engine.Host.RouteCommunications(life, new CommunicationPolicy(
            new[] { Member("host", 1), Member("speaker", 2), Member("listener", 1) }, Array.Empty<CommunicationRoute>())).Value!;
        r.Frames(6); Flags(listener, speaker, 0, "policy denial wakes suppressed stationary typing pose"); r.Frames(30);
        r.Host.Control.Inbox!.RecordPoses = false;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        { r.Host.Presence.TryGetPose(speaker.Id, out _); r.Host.Presence.Host.Send(r.Engine.NowMicros + i * PresenceHost.RelayIntervalMicros); }
        Check(GC.GetAllocatedBytesForCurrentThread() - before == 0, "projection and idle routed relay allocate exactly zero bytes");
    }
    private static void SchedulerSkip()
    {
        using var r = new Rig(); var speaker = r.Add("speaker"); var listener = r.Add("listener"); r.Frames(40);
        speaker.Presence.LocalTyping = true; r.Frames(16);
        Flags(listener, speaker, MultiplayerPoseFlags.Typing, "typing reaches recipient before scheduler pressure");
        var recipient = Peer(r.Host, listener); var previous = recipient.Scheduler!;
        Check(previous.IsSuppressed(new NetworkEntityId(Peer(r.Host, speaker).Index)), "ordinary pose is already suppressed at rest");
        var constrained = new ReplicationScheduler(1);
        constrained.Schedule(new[] { new ReplicationCandidate(new NetworkEntityId(254), 1, PresenceMessages.RelayRecordBytes, false) }, new List<NetworkEntityId>());
        recipient.Scheduler = constrained;
        using var life = new FakeModLifetime();
        using var route = r.Engine.Host.RouteCommunications(life, new CommunicationPolicy(
            new[] { Member("host", 1), Member("speaker", 2), Member("listener", 1) }, Array.Empty<CommunicationRoute>())).Value!;
        r.Frames(6);
        Flags(listener, speaker, MultiplayerPoseFlags.Typing, "budget debt selects no correction while the recipient is constrained");
        recipient.Scheduler = previous; r.Frames(6);
        Flags(listener, speaker, 0, "scheduler-skipped private activity clear remains pending when budget returns");
    }
    private static void Backpressure()
    {
        using var r = new Rig(); var speaker = r.Add("speaker"); var listener = r.Add("listener"); r.Frames(40);
        speaker.Presence.LocalTyping = true; r.Frames(12);
        Flags(listener, speaker, MultiplayerPoseFlags.Typing, "typing reaches recipient before backpressure");
        var oldFlags = r.Host.Control.Inbox!.LastDatagramTo(listener.Id);
        r.Host.Control.Inbox.DropDatagramsTo = listener.Id;
        using var life = new FakeModLifetime();
        using var route = r.Engine.Host.RouteCommunications(life, new CommunicationPolicy(
            new[] { Member("host", 1), Member("speaker", 2), Member("listener", 1) }, Array.Empty<CommunicationRoute>())).Value!;
        r.Frames(24);
        Check(r.Host.Control.Inbox.DroppedDatagrams >= 4, "recipient correction survives repeated zero-delivery sends");
        Flags(listener, speaker, MultiplayerPoseFlags.Typing, "fixture retains last delivered flag while transport refuses");
        r.Host.Control.Inbox.DropDatagramsTo = null; r.Frames(6);
        Flags(listener, speaker, 0, "pending private typing clear reaches recipient after backpressure ends");
        listener.Control.Inbox!.InjectedDatagrams.Enqueue(new InternalSystemDatagram(r.Host.Id, new ArraySegment<byte>(oldFlags)));
        r.Frames(1);
        Flags(listener, speaker, 0, "reordered older host relay cannot revive private activity");
    }
    private static void HostileAndLifetimes()
    {
        using var r = new Rig(); var speaker = r.Add("speaker"); var listener = r.Add("listener"); r.Frames(40);
        speaker.SendEnabled = false; r.RawPose(speaker, PresenceFlags.Talking | PresenceFlags.Typing); r.Frames(6);
        Flags(listener, speaker, MultiplayerPoseFlags.Typing, "forged Talking claim cannot create activity");
        var before = r.Engine.Host.GetVoiceStatistics().TotalDropped;
        speaker.Link!.Send(EngineChannel.Voice, new ArraySegment<byte>(new byte[] { VoicePacket.UplinkKind, 255 })); r.Frames(3);
        Check(r.Engine.Host.GetVoiceStatistics().TotalDropped > before, "malformed voice is rejected");
        r.Speak(speaker, silence: true); r.Frames(6);
        Flags(listener, speaker, MultiplayerPoseFlags.Typing, "codec silence with forged voiced flag cannot create Talking");
        r.Speak(speaker); r.Frames(6); Flags(listener, speaker, Activity, "admitted voice works without a new claimed pose");
        r.Engine.Network.Sever(speaker.Link!); r.Frames(2);
        Flags(r.Host, speaker, 0, "disconnect clears both activity claims");
        speaker.Presence.Detaching(speaker.Control); r.Nodes.Remove(speaker);
        var rejoined = r.Add("speaker"); rejoined.SendEnabled = false; r.Frames(3);
        Check(!r.Engine.Host.IsVoiceActive(rejoined.Id), "new stream within grace forgets admitted activity");
        Flags(r.Host, rejoined, 0, "rejoin never inherits old typing or talking");
        rejoined.SendEnabled = true; r.Frames(8); r.Speak(rejoined); r.Frames(6);
        Flags(listener, rejoined, MultiplayerPoseFlags.Talking, "fresh rejoined stream speech works");
        r.Host.World = null; Flags(r.Host, rejoined, 0, "world stop hides held activity immediately"); r.Frames(2);
        r.Host.World = "world-one"; r.Frames(3);
        Check(!r.Engine.Host.IsVoiceActive(rejoined.Id), "world restart cannot revive preceding activity");
        r.Speak(r.Host); Check(r.Engine.Host.IsVoiceActive(r.Host.Id), "host activity admitted before replacement");
        r.Engine.Host.ReplaceSession(new MultiplayerSessionId("replacement"), new SessionSeed(8));
        Check(!r.Engine.Host.IsVoiceActive(r.Host.Id), "session epoch invalidates host activity immediately");
    }
    private static PresencePeer Peer(Node viewer, Node person) => viewer.Presence.TryGetPeer(person.Id, out var p) && p != null
        ? p : throw new InvalidOperationException("Presence activity fixture lost a peer.");
    private static void Flags(Node viewer, Node person, MultiplayerPoseFlags expected, string message)
    {
        Check(viewer.Presence.TryGetPose(person.Id, out var pose), message + " (pose exists)");
        Check((pose.Flags & Activity) == expected, message + " (actual " + (pose.Flags & Activity) + ")");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Presence activity: " + message); }
    private sealed class Rig : IDisposable
    {
        internal readonly FakeVoiceCodec Codec = new(); internal readonly EngineHarness Engine;
        internal readonly List<Node> Nodes = new(); internal readonly Node Host;
        internal Rig()
        {
            Engine = new EngineHarness(MultiplayerProcessKind.Interactive, o => o.Voice = new VoiceOptions(Codec));
            Host = new Node(Engine.Host, this); Nodes.Add(Host);
        }
        internal Node Add(string identity)
        {
            var c = Engine.AddClient(identity); Engine.Flush();
            var n = new Node(c.Engine, this) { Link = c.Link }; Nodes.Add(n); return n;
        }
        internal void Frames(int count)
        {
            for (var i = 0; i < count; i++)
            {
                Engine.Advance(); foreach (var n in Nodes) n.Presence.Receive.Run(1f / 60);
                foreach (var n in Nodes) if (n.SendEnabled) n.Presence.Send.Run(1f / 60);
            }
        }
        internal void Speak(Node n, bool silence = false) => Check(n.Engine.SubmitVoiceFrame(++n.Timestamp,
            VoiceFrameFlags.EndOfTalk, Codec.Packet(silence ? (short)0 : (short)12)) == VoiceSubmitResult.Sent, "fixture submits valid frame");
        internal void RawPose(Node n, PresenceFlags flags)
        {
            var w = new NetBitWriter(PresenceMessages.PoseBytes);
            PresenceWire.WritePose(w, new PresencePoseRecord { Millis = (uint)(Engine.NowMicros / 1000),
                Correction = n.Presence.Client.Correction, Position = NetQuantization.QuantizePosition(n.Backend.Feet),
                Rotation = NetQuantization.IdentityRotationBits, EyeCentimeters = 160, Flags = flags });
            Check(n.Control.Inbox!.SendDatagram(w.AsSegment(), InternalSystemTarget.Host).Succeeded, "raw pose enters actual channel");
        }
        public void Dispose() { foreach (var n in Nodes) n.Presence.Detaching(n.Control); Engine.Dispose(); }
    }
    private sealed class Node
    {
        internal readonly SessionEngine Engine; internal readonly PresenceEngineControl Control;
        internal readonly FakePresenceBackend Backend = new(); internal readonly MultiplayerPresence Presence;
        internal readonly ParticipantId Id;
        internal VirtualLinkEnd? Link; internal ulong Timestamp; internal bool SendEnabled = true; internal string? World = "world-one";
        internal Node(SessionEngine engine, Rig r)
        {
            Engine = engine; Id = engine.Snapshot.LocalParticipantId!.Value; Control = new PresenceEngineControl(engine); var log = new QuietLog();
            var runtime = new MultiplayerRuntime(() => Control, new MultiplayerRuntimeHost("2545", "0.2.0", log), log);
            Presence = MultiplayerPresence.Install(runtime, new MultiplayerReplication(runtime), Backend, () => r.Engine.NowMicros);
            Presence.RunningWorld = () => World; Presence.RunningSpawn = () => TransformState.Identity; Presence.Attached(Control);
            Presence.RealmChanged(NetRealm.Offline, engine.Role == EngineRole.Host ? NetRealm.ListenHost : NetRealm.Client);
            if (engine.Role == EngineRole.Host) Control.Inbox!.RecordPoses = true;
        }
    }
    private sealed class QuietLog : IMultiplayerLog
    {
        public void Info(string message) { } public void Warn(string message) { }
        public void Error(string message) => throw new InvalidOperationException(message);
    }
}
