using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static partial class PresenceActivityTests
{
    private const MultiplayerPoseFlags Activity = MultiplayerPoseFlags.Talking | MultiplayerPoseFlags.Typing;
    private static CommunicationMember Member(string id, byte group) => new(new ParticipantId(id), group);
    internal static void Run()
    {
        UnityMainThreadGuard.CaptureCurrentThread();
        PrivateAudiences(); FarHost(); ExpiryMutesAndRest(); SchedulerSkip(); Backpressure(); HostileAndLifetimes();
        ActivityDoesNotWakeBarredRecipients();
        ReviewRegressions();
        Console.WriteLine("Presence activity: 7 groups passed (audiences, timing privacy, expiry, scheduler pressure, and lifetimes).");
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

    private static void ActivityDoesNotWakeBarredRecipients()
    {
        using var r = new Rig();
        var source = r.Add("source"); var barred = r.Add("barred"); var allowed = r.Add("allowed");
        r.Frames(40);
        using var life = new FakeModLifetime();
        using var route = r.Engine.Host.RouteCommunications(life, Policy(allowBarred: false)).Value!;
        var wire = r.Host.Control.Inbox!;
        var sourceIndex = Peer(r.Host, source).Index;
        var scheduler = Peer(r.Host, barred).Scheduler!;
        var entity = new NetworkEntityId(sourceIndex);
        var barredBefore = SampledAt(barred);
        var allowedBefore = SampledAt(allowed);
        var hostBefore = SampledAt(r.Host);
        var count = wire.SentPoseCount(barred.Id, sourceIndex);
        r.Frames(8);
        Check(scheduler.IsSuppressed(entity) && wire.SentPoseCount(barred.Id, sourceIndex) == count,
            "fixture has a stationary source with its rest confirmations settled");

        source.Presence.LocalTyping = true;
        r.Frames(8);
        Flags(allowed, source, MultiplayerPoseFlags.Typing, "allowed recipient gets a stationary typing start");
        BarredStaysResting("typing start");
        r.Speak(source);
        r.Frames(6);
        Flags(allowed, source, Activity, "allowed recipient gets a stationary voice start");
        BarredStaysResting("voice start");
        r.Frames(24);
        Flags(allowed, source, MultiplayerPoseFlags.Typing, "allowed recipient gets stationary voice expiry");
        BarredStaysResting("voice expiry");
        source.Presence.LocalTyping = false;
        r.Frames(8);
        Flags(allowed, source, 0, "allowed recipient gets a stationary typing stop");
        BarredStaysResting("typing stop");

        Check(scheduler.Refresh(entity), "an independent resync re-arms barred rest confirmations");
        source.Presence.LocalTyping = true;
        r.Frames(12);
        Check(wire.SentPoseCount(barred.Id, sourceIndex) == count + ReplicationScheduler.RestConfirmationSends,
            "private typing adds no records to an independent confirmation burst");
        Check(SampledAt(barred) == barredBefore, "pending confirmations retain the public transform timestamp");
        Flags(barred, source, 0, "pending confirmations still project private flags");
        Check(route.SetPolicy(Policy(allowBarred: true)).Succeeded, "fixture grants a stationary source's typing audience");
        r.Frames(8);
        Flags(barred, source, MultiplayerPoseFlags.Typing, "grant wakes stationary authorized typing");
        Check(SampledAt(barred) == barredBefore, "grant delivers activity with the held transform timestamp");
        Check(route.SetPolicy(Policy(allowBarred: false)).Succeeded, "fixture revokes a stationary source's typing audience");
        r.Frames(8);
        Flags(barred, source, 0, "revoke wakes the stationary activity clear");
        Check(SampledAt(barred) == barredBefore, "revoke leaves the held transform timestamp unchanged");

        source.Backend.Feet = new NetVector3(source.Backend.Feet.X + 0.1f, source.Backend.Feet.Y, source.Backend.Feet.Z);
        r.Frames(12);
        Check(SampledAt(barred) > barredBefore && SampledAt(allowed) > allowedBefore, "movement advances both recipients' samples");
        Check(Peer(barred, source).Position.Equals(Peer(r.Host, source).Position) &&
              Peer(allowed, source).Position.Equals(Peer(r.Host, source).Position), "both recipients receive the accepted movement");
        Flags(barred, source, 0, "movement carries the barred activity projection");
        var movingSample = SampledAt(barred);
        source.Backend.Crouching = true;
        r.Frames(12);
        Check(SampledAt(barred) > movingSample &&
            (ReadPose(barred).Flags & MultiplayerPoseFlags.Crouching) != 0 &&
            (ReadPose(allowed).Flags & MultiplayerPoseFlags.Crouching) != 0,
            "a public flag change wakes both recipients while standing still");

        // Keep the real heartbeat running past the wire age limit, then send a fresh activity clear.
        var oldAllowedSample = SampledAt(allowed);
        var oldBarredSample = SampledAt(barred);
        r.Frames(4_020);
        count = wire.SentPoseCount(barred.Id, sourceIndex);
        source.Presence.LocalTyping = false;
        r.Frames(8);
        Flags(allowed, source, 0, "activity still clears after the held transform's wire age saturates");
        Check(wire.SentPoseCount(barred.Id, sourceIndex) == count && SampledAt(barred) == oldBarredSample &&
              SampledAt(allowed) == oldAllowedSample, "wire age saturation neither wakes a barred peer nor refreshes an unchanged pose");

        var epoch = new MultiplayerSessionId("presence-timing-next");
        r.Engine.Host.ReplaceSession(epoch, new SessionSeed(8));
        using var next = r.Engine.Host.RouteCommunications(life, Policy(allowBarred: false)).Value!;
        r.Frames(40);
        Check(barred.Engine.Snapshot.Id.Equals(epoch) && SampledAt(barred) > oldBarredSample,
            "a fresh session publishes a new sample for the same stationary player");
        Flags(allowed, source, 0, "fresh session starts without held activity");
        sourceIndex = Peer(r.Host, source).Index;
        count = wire.SentPoseCount(barred.Id, sourceIndex);
        barredBefore = SampledAt(barred);
        allowedBefore = SampledAt(allowed);
        hostBefore = SampledAt(r.Host);
        source.Presence.LocalTyping = true;
        r.Speak(source);
        r.Frames(8);
        Flags(allowed, source, Activity, "fresh session delivers newly authorized activity");
        BarredStaysResting("fresh session activity");

        CommunicationPolicy Policy(bool allowBarred) => new(
            new[] { Member("host", 1), Member("source", 2), Member("barred", allowBarred ? (byte)2 : (byte)1), Member("allowed", 2) },
            new[] { new CommunicationRoute(2, 2) });
        MultiplayerPose ReadPose(Node viewer) => viewer.Presence.TryGetPose(source.Id, out var pose)
            ? pose : throw new InvalidOperationException("Presence activity: stationary fixture pose is missing.");
        ulong SampledAt(Node viewer) => ReadPose(viewer).SampledAt.Value;
        void BarredStaysResting(string transition)
        {
            Flags(barred, source, 0, transition + " keeps activity private");
            Check(wire.SentPoseCount(barred.Id, sourceIndex) == count && SampledAt(barred) == barredBefore,
                transition + " leaves the barred recipient's relay count and sample unchanged");
            Check(SampledAt(allowed) == allowedBefore && SampledAt(r.Host) == hostBefore,
                transition + " leaves the allowed and listen-host transform timestamps unchanged");
        }
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
        var relay = 0;
        var allocated = TopiaForge.TestSupport.AllocationProbe.Measure("projection and idle routed relay", () =>
        { r.Host.Presence.TryGetPose(speaker.Id, out _); r.Host.Presence.Host.Send(r.Engine.NowMicros + relay++ * PresenceHost.RelayIntervalMicros); }, 1000);
        Check(allocated == 0, "projection and idle routed relay allocate exactly zero bytes");
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
        NoActivity(r.Host, speaker, "disconnect clears both activity claims");
        speaker.Presence.Detaching(speaker.Control); r.Nodes.Remove(speaker);
        var rejoined = r.Add("speaker"); rejoined.SendEnabled = false; r.Frames(3);
        Check(!r.Engine.Host.IsVoiceActive(rejoined.Id), "new stream within grace forgets admitted activity");
        NoActivity(r.Host, rejoined, "rejoin never inherits old typing or talking");
        rejoined.SendEnabled = true; r.Frames(8); r.Speak(rejoined); r.Frames(6);
        Flags(listener, rejoined, MultiplayerPoseFlags.Talking, "fresh rejoined stream speech works");
        r.Host.Phase = SessionPhase.Stopping; r.Host.World = null; NoActivity(r.Host, rejoined, "world stop hides held activity immediately"); r.Frames(2);
        r.Host.World = "world-one"; r.Host.Phase = SessionPhase.Running; r.Frames(3);
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
        internal readonly bool RuntimeDriven;
        internal Rig(int maximumParticipants = 16, bool runtimeDriven = false)
        {
            RuntimeDriven = runtimeDriven;
            Engine = new EngineHarness(MultiplayerProcessKind.Interactive, o => { o.Voice = new VoiceOptions(Codec); o.MaximumParticipants = maximumParticipants; });
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
                if (RuntimeDriven)
                {
                    Engine.AdvanceClockOnly();
                    foreach (var n in Nodes) n.Runtime.BeforeModsUpdate(1f / 60);
                    foreach (var n in Nodes) if (n.SendEnabled) n.Runtime.AfterModsUpdate(1f / 60);
                }
                else
                {
                    Engine.Advance(); foreach (var n in Nodes) n.Presence.Receive.Run(1f / 60);
                    foreach (var n in Nodes) if (n.SendEnabled) n.Presence.Send.Run(1f / 60);
                }
            }
        }
        internal void Speak(Node n, bool silence = false) => Check(n.Engine.SubmitVoiceFrame(++n.Timestamp,
            VoiceFrameFlags.EndOfTalk, Codec.Packet(silence ? (short)0 : (short)12)) == VoiceSubmitResult.Sent, "fixture submits valid frame");
        internal void RawPose(Node n, PresenceFlags flags)
        {
            var w = new NetBitWriter(PresenceMessages.PoseBytes);
            PresenceWire.WritePose(w, new PresencePoseRecord
            {
                Millis = (uint)(Engine.NowMicros / 1000),
                Correction = n.Presence.Client.Correction,
                Position = NetQuantization.QuantizePosition(n.Backend.Feet),
                Rotation = NetQuantization.IdentityRotationBits,
                EyeCentimeters = 160,
                Flags = flags
            });
            Check(n.Control.Inbox!.SendDatagram(w.AsSegment(), InternalSystemTarget.Host).Succeeded, "raw pose enters actual channel");
        }
        public void Dispose()
        {
            foreach (var n in Nodes)
                if (RuntimeDriven) n.Runtime.Disconnect(); else n.Presence.Detaching(n.Control);
            Engine.Dispose();
        }
    }
    private sealed class Node
    {
        internal readonly MultiplayerRuntime Runtime;
        internal readonly SessionEngine Engine; internal readonly PresenceEngineControl Control;
        internal readonly FakePresenceBackend Backend = new(); internal readonly MultiplayerPresence Presence;
        internal readonly ParticipantId Id;
        internal VirtualLinkEnd? Link; internal ulong Timestamp; internal bool SendEnabled = true; internal string? World = "world-one";
        internal int SceneVersion;
        internal SessionPhase Phase = SessionPhase.Running;
        internal Node(SessionEngine engine, Rig r)
        {
            Engine = engine; Id = engine.Snapshot.LocalParticipantId!.Value; Control = new PresenceEngineControl(engine); var log = new QuietLog();
            var runtime = new MultiplayerRuntime(() => Control, new MultiplayerRuntimeHost("2545", "0.2.0", log), log);
            Runtime = runtime;
            runtime.SessionPhaseSource = () => Phase;
            Presence = MultiplayerPresence.Install(runtime, new MultiplayerReplication(runtime), Backend, () => r.Engine.NowMicros);
            Presence.RunningWorld = () => World; Presence.RunningSpawn = () => TransformState.Identity;
            Presence.SceneVersion = () => SceneVersion;
            if (r.RuntimeDriven)
            {
                Control.PumpAction = () => engine.Pump(r.Engine.NowMicros);
                Check(runtime.Attach(Control), "actual runtime attaches the engine control");
                runtime.BeforeModsUpdate(0);
            }
            else
            {
                Presence.Attached(Control);
                Presence.RealmChanged(NetRealm.Offline, engine.Role == EngineRole.Host ? NetRealm.ListenHost : NetRealm.Client);
            }
            if (engine.Role == EngineRole.Host) Control.Inbox!.RecordPoses = true;
        }
    }
    private sealed class QuietLog : IMultiplayerLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) => throw new InvalidOperationException(message);
    }
}
