using TopiaForge.ModManager;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static class PresenceHostIdentityTests
{
    internal static void Run()
    {
        UnityMainThreadGuard.CaptureCurrentThread();
        Delivery(MultiplayerProcessKind.Headless);
        Delivery(MultiplayerProcessKind.Interactive);
        Console.WriteLine("Presence host identity: headless and listen-host delivery and forged-sender rejection passed.");
    }

    private static void Delivery(MultiplayerProcessKind kind)
    {
        using var rig = new EngineHarness(kind);
        var firstEngine = rig.AddClient("first").Engine;
        var secondEngine = rig.AddClient("second").Engine;
        rig.Flush();
        var host = new Node(rig.Host, rig);
        var first = new Node(firstEngine, rig);
        var second = new Node(secondEngine, rig);
        var nodes = new[] { host, first, second };
        void Frames(int count)
        {
            for (var i = 0; i < count; i++)
            {
                rig.Advance();
                foreach (var node in nodes) node.Presence.Receive.Run(1f / 60);
                foreach (var node in nodes) node.Presence.Send.Run(1f / 60);
                foreach (var node in nodes) node.Presence.Avatars.Run(1f / 60);
            }
        }

        Frames(40);
        var firstId = firstEngine.Snapshot.LocalParticipantId!.Value;
        var secondId = secondEngine.Snapshot.LocalParticipantId!.Value;
        var expectedHost = rig.Host.Snapshot.HostParticipantId ?? InternalSystemMessage.HeadlessHostSender;
        Check((kind == MultiplayerProcessKind.Headless) == !firstEngine.Snapshot.HostParticipantId.HasValue,
            "the engine exposes the expected host-player identity");
        Check(first.Control.Inbox!.ReceivedSenders.Count > 0 && first.Control.Inbox.ReceivedSenders.All(id => id.Equals(expectedHost)),
            "honest reliable envelopes carry the engine's host sender");
        Check(first.Presence.Roster.TryGetIndex(secondId, out var index) && index != 0,
            "the authenticated host roster reaches the first client");
        Check(second.Presence.Roster.TryGetIndex(firstId, out _), "the roster reaches both clients");
        Check(first.Presence.TryGetPeer(secondId, out var remote) && remote!.HasPose && remote.HasFed,
            "the roster resolves the second client's relayed poses into interpolation");
        Check(first.Backend.Teleports.Any(t => !t.KeepFacing) && second.Backend.Teleports.Any(t => !t.KeepFacing),
            "arrival placement reaches both clients");

        second.Backend.Health = 65;
        second.Backend.HeldItem = "tool";
        Frames(30);
        Check(remote!.Health == 65 && remote.HeldItem == "tool", "host-validated state reaches the other client");
        var look = new AvatarAppearance(new RgbaColor(1, 0, 0), RgbaColor.White, "Cap");
        using var lifetime = new FakeModLifetime();
        var presentation = host.Presence.PresentationFor(secondId)!;
        using var appearance = presentation.Add(lifetime, 0, look).Value!;
        using var name = presentation.Add(lifetime, 1, name: "Sam", flag: false).Value!;
        using var down = presentation.Add(lifetime, 2).Value!;
        Frames(20);
        foreach (var client in new[] { first, second })
        {
            var delivered = client.Presence.PresentationFor(secondId);
            Check(delivered != null && delivered.Current.Equals(look) && delivered.Name == "Sam"
                && !delivered.Visible && delivered.PoweredDown, "appearance, nameplate and powered-down state reach each client");
        }

        var correction = first.Presence.Client.Correction;
        // Send an unflagged impossible report: the local sampler correctly flags real controller teleports.
        rig.Advance();
        var bits = new NetBitWriter(PresenceMessages.PoseBytes);
        PresenceWire.WritePose(bits, new PresencePoseRecord
        {
            Millis = (uint)(rig.NowMicros / 1000),
            Correction = correction,
            Position = NetQuantization.QuantizePosition(new NetVector3(500, 0, 500)),
            Rotation = NetQuantization.IdentityRotationBits,
            EyeCentimeters = 160
        });
        Check(first.Control.Inbox!.SendDatagram(bits.AsSegment(), InternalSystemTarget.Host).Succeeded,
            "an impossible unflagged pose enters the production channel");
        Frames(12);
        Check(first.Presence.Client.Correction != correction && first.Backend.Teleports.Any(t => t.KeepFacing),
            "an invalid move receives the production host's correction");
        Check(first.Presence.Counters.Forged == 0 && second.Presence.Counters.Forged == 0,
            "legitimate host frames are never counted forged");

        var writer = new WorldWireWriter(PresenceMessages.MaximumMessageBytes);
        PresenceWire.WriteCorrect(writer, 199, new NetVector3(123, 0, 123));
        var before = first.Backend.Teleports.Count;
        var number = first.Presence.Client.Correction;
        var forged = first.Presence.Counters.Forged;
        var wrongSenders = new List<ParticipantId> { secondId, new ParticipantId("unknown") };
        if (kind == MultiplayerProcessKind.Interactive) wrongSenders.Add(InternalSystemMessage.HeadlessHostSender);
        foreach (var sender in wrongSenders)
            first.Control.Inbox.Injected.Enqueue(new InternalSystemMessage(sender, writer.Segment.ToArray()));
        first.Presence.Client.Receive(rig.NowMicros);
        Check(first.Presence.Counters.Forged == forged + wrongSenders.Count && first.Backend.Teleports.Count == before
            && first.Presence.Client.Correction == number, "non-host envelopes cannot apply a valid host payload");
        Console.WriteLine("Presence host identity: " + kind + " roster/state/placement/correction/presentation/pose and negatives passed.");
        foreach (var node in nodes) node.Presence.Detaching(node.Control);
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("Presence host identity: " + message);
    }

    private sealed class Node
    {
        internal readonly PresenceEngineControl Control;
        internal readonly FakePresenceBackend Backend = new();
        internal readonly MultiplayerPresence Presence;

        internal Node(SessionEngine engine, EngineHarness rig)
        {
            Control = new PresenceEngineControl(engine);
            Backend.HasPlayer = engine.Snapshot.LocalParticipantId.HasValue;
            var runtime = new MultiplayerRuntime(() => Control, new MultiplayerRuntimeHost("2545", "0.2.0", new QuietLog()), new QuietLog());
            var replication = new MultiplayerReplication(runtime);
            Presence = MultiplayerPresence.Install(runtime, replication, Backend, () => rig.NowMicros);
            Presence.RunningWorld = () => "world-one";
            Presence.RunningSpawn = () => TransformState.Identity;
            Presence.Attached(Control);
            Presence.RealmChanged(NetRealm.Offline, engine.Role == EngineRole.Host ? NetRealm.ListenHost : NetRealm.Client);
        }
    }

    private sealed class QuietLog : IMultiplayerLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) => throw new InvalidOperationException(message);
    }
}
