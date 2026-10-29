using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>Runs the presence suites (WS3.10): the model's rules and wire, then presence over the loopback network.</summary>
    internal static class PresenceSuite
    {
        internal static void Run()
        {
            PresencePlausibilityTests.Run();
            PresenceFirstPoseTests.Run();
            PresenceFirstPoseHostTests.Run();
            PresenceWireTests.Run();
            PresenceTests.Run();
            PresenceRulesTests.Run();
            PresenceHostileTests.Run();
            PresenceSpawnRingTests.Run();
            PresenceAvatarTests.Run();
            PresenceBudgetTests.Run();
            PresenceSessionTests.Run();
            PresenceNametagTests.Run();
            Console.WriteLine("Presence tests passed.");
        }
    }

    /// <summary>One peer as ModRuntime composes presence: a composition root over a loopback control, and presence over a fake engine.</summary>
    internal sealed class PresenceNode
    {
        internal PresenceNode(LoopbackControl control, ManualClock clock, string name)
        {
            Control = control;
            Name = name;
            Log = new RecordingRuntimeLog();
            Runtime = new MultiplayerRuntime(() => Control, new MultiplayerRuntimeHost("2545", "0.2.0", Log), Log) { SessionPhaseSource = () => Phase };
            Replication = new MultiplayerReplication(Runtime);
            Backend = new FakePresenceBackend();
            Presence = MultiplayerPresence.Install(Runtime, Replication, Backend, clock.Read);
            Presence.RunningWorld = () => World;
            Presence.RunningSpawn = () => Spawn;
            Presence.SceneVersion = () => SceneVersion;
        }

        internal string Name { get; }

        internal LoopbackControl Control { get; }

        internal ParticipantId Id => Control.Id;

        internal RecordingRuntimeLog Log { get; }

        internal MultiplayerRuntime Runtime { get; }

        internal MultiplayerReplication Replication { get; }

        internal FakePresenceBackend Backend { get; }

        internal MultiplayerPresence Presence { get; }

        internal PresenceCounters Counters => Presence.Counters;

        internal SessionPhase Phase { get; set; } = SessionPhase.Running;

        /// <summary>Gets or sets the world session this game runs; the host's id on every peer by default, as session follow has it.</summary>
        internal string? World { get; set; } = "world-one";

        /// <summary>Gets or sets where the running world put its player.</summary>
        internal TransformState? Spawn { get; set; } = new TransformState(new Vec3(0f, 0f, 0f), Quat.Identity, new Vec3(1f, 1f, 1f));

        internal int SceneVersion { get; set; }

        /// <summary>Gets or sets whether this peer runs frames; a stalled game runs none.</summary>
        internal bool Paused { get; set; }

        internal NetVector3 Feet
        {
            get => Backend.Feet;
            set => Backend.Feet = value;
        }

        /// <summary>One frame, as ModRuntime runs it: the pump and step 2, the mods, step 4 and the flush, then LateUpdate.</summary>
        internal void Frame(float deltaTime = 0.016f)
        {
            if (Paused) return;
            Runtime.BeforeModsUpdate(deltaTime);
            Runtime.AfterModsUpdate(deltaTime);
            Runtime.BeforeModsLateUpdate(new GameTimeSample(GameLoopPhase.Late, deltaTime, deltaTime, 0d, 0));
        }

        /// <summary>Moves the player's feet by the given step.</summary>
        internal void Step(float x, float y, float z) => Backend.Feet = new NetVector3(Backend.Feet.X + x, Backend.Feet.Y + y, Backend.Feet.Z + z);

        /// <summary>Gets the peer presence keeps for <paramref name="other"/>, or null.</summary>
        internal PresencePeer? PeerOf(PresenceNode other) => Presence.TryGetPeer(other.Id, out var peer) ? peer : null;

        /// <summary>Gets the fake engine's avatar presence shows for <paramref name="other"/>, alive, or null.</summary>
        internal FakeAvatar? AvatarOf(PresenceNode other)
        {
            var avatar = PeerOf(other)?.Avatar;
            if (avatar == null || !avatar.IsAlive) return null;
            foreach (var made in Backend.Avatars)
            {
                if (made.IsAlive && ReferenceEquals(made.Entity, avatar.Entity)) return made;
            }

            return null;
        }

        internal LoopbackChannel Channel => Control.Channel(InternalSystemChannelKind.Avatars) ?? throw new InvalidOperationException("The avatars channel is not open.");
    }

    /// <summary>
    /// A player with no runtime behind it, which writes presence's payloads itself: a hostile or broken game. The rig pumps
    /// and flushes its control with the others.
    /// </summary>
    internal sealed class RawPresencePlayer
    {
        private readonly NetBitWriter bits = new NetBitWriter(InternalSystemLimits.MaximumDatagramBytes);
        private readonly WorldWireWriter writer = new WorldWireWriter(PresenceMessages.MaximumMessageBytes);
        private readonly ManualClock clock;

        internal RawPresencePlayer(LoopbackControl control, ManualClock clock)
        {
            Control = control;
            this.clock = clock;
            Channel = (LoopbackChannel)control.OpenSystemChannel(InternalSystemChannelKind.Avatars).Value!;
        }

        internal LoopbackControl Control { get; }

        internal ParticipantId Id => Control.Id;

        internal LoopbackChannel Channel { get; }

        /// <summary>Gets every message the host sent this player, in order.</summary>
        internal List<byte[]> Messages { get; } = new List<byte[]>();

        /// <summary>Gets every datagram the host sent this player, in order.</summary>
        internal List<byte[]> Datagrams { get; } = new List<byte[]>();

        internal void Pump()
        {
            Control.Pump();
            while (Channel.TryReceive(out var message)) Messages.Add(message.Payload);
            while (Channel.TryReceiveDatagram(out var datagram)) Datagrams.Add(datagram.Payload.ToArray());
        }

        internal void Flush() => Control.Flush();

        /// <summary>Sends a pose of its own, stamped now, numbered with <paramref name="correction"/>.</summary>
        internal OperationResult<int> SendPose(NetVector3 feet, PresenceFlags flags = PresenceFlags.None, byte correction = 0, NetQuaternion? view = null, long? micros = null)
        {
            var pose = new PresencePoseRecord
            {
                Millis = unchecked((uint)((micros ?? clock.Now) / 1000)),
                Correction = correction,
                Flags = flags,
                EyeCentimeters = 160,
                Position = NetQuantization.QuantizePosition(feet),
                Rotation = NetQuantization.QuantizeRotation(view ?? NetQuaternion.Identity)
            };
            PresenceWire.WritePose(bits, pose);
            return Channel.SendDatagram(bits.AsSegment(), InternalSystemTarget.Host);
        }

        /// <summary>Sends a datagram in the host's format, speaking for the players it names.</summary>
        internal OperationResult<int> SendRelay(params PresencePoseRecord[] records)
        {
            PresenceWire.BeginPoses(bits, unchecked((uint)(clock.Now / 1000)), records.Length);
            foreach (var record in records) PresenceWire.WriteRelay(bits, record);
            return Channel.SendDatagram(bits.AsSegment(), InternalSystemTarget.Host);
        }

        internal OperationResult<int> SendDatagram(byte[] payload) => Channel.SendDatagram(new ArraySegment<byte>(payload), InternalSystemTarget.Host);

        /// <summary>Sends what <paramref name="write"/> puts in a message.</summary>
        internal OperationResult<int> SendMessage(Action<WorldWireWriter> write)
        {
            write(writer);
            return Channel.Send(writer.Segment, InternalSystemTarget.Host);
        }

        internal OperationResult<int> SendMessage(byte[] payload) => Channel.Send(new ArraySegment<byte>(payload), InternalSystemTarget.Host);

        /// <summary>Gets the corrections the host sent, as numbers and targets.</summary>
        internal List<(byte Number, NetVector3 Target)> Corrections()
        {
            var found = new List<(byte, NetVector3)>();
            var reader = new WorldWireReader();
            foreach (var message in Messages)
            {
                if (PresenceWire.TryReadMessageType(reader, message, out var type) && type == PresenceMessages.Correct &&
                    PresenceWire.TryReadCorrect(reader, out var number, out var target))
                {
                    found.Add((number, target));
                }
            }

            return found;
        }
    }

    /// <summary>A listen host and its players over one loopback network, each with presence over its own fake engine, on one clock.</summary>
    internal sealed class PresenceRig
    {
        internal PresenceRig()
        {
            Network = new LoopbackNetPair();
            Host = new PresenceNode(Network.Host, Clock, "host");
            Network.Host.DisplayName = "Bolt";
            Host.Runtime.Connect();
        }

        internal ManualClock Clock { get; } = new ManualClock();

        internal LoopbackNetPair Network { get; }

        internal PresenceNode Host { get; }

        internal List<PresenceNode> Players { get; } = new List<PresenceNode>();

        internal List<RawPresencePlayer> RawPlayers { get; } = new List<RawPresencePlayer>();

        /// <summary>Gets or sets what each frame does first, before any peer runs: a test's script for the players' movement.</summary>
        internal Action? BeforeFrame { get; set; }

        internal IEnumerable<PresenceNode> All
        {
            get
            {
                yield return Host;
                foreach (var player in Players) yield return player;
            }
        }

        internal PresenceNode AddPlayer(string id, string? displayName = null)
        {
            var control = Network.AddPlayer(id);
            if (displayName != null) control.DisplayName = displayName;
            var player = new PresenceNode(control, Clock, id);
            player.Runtime.Connect();
            Players.Add(player);
            return player;
        }

        /// <summary>Adds a player that runs no runtime and joins it at once.</summary>
        internal RawPresencePlayer AddRawPlayer(string id)
        {
            var control = Network.AddPlayer(id);
            var raw = new RawPresencePlayer(control, Clock);
            Network.Join(control);
            RawPlayers.Add(raw);
            return raw;
        }

        /// <summary>Opens the host's running session to players and runs until it hosts.</summary>
        internal void OpenToMultiplayer()
        {
            var opened = Host.Runtime.OpenToMultiplayer(new InternalHostSettings("Presence", InternalHostSettings.MaximumPlayers));
            if (!opened.Succeeded) throw new InvalidOperationException("Presence test failed: hosting starts (" + opened + ")");
            Frames(2);
            if (Host.Presence.Realm != NetRealm.ListenHost) throw new InvalidOperationException("Presence test failed: the host hosts.");
        }

        /// <summary>Joins <paramref name="player"/> and runs until it has arrived and the host has placed it, if it will.</summary>
        internal void Join(PresenceNode player, int frames = 12)
        {
            Network.Join(player.Control);
            Frames(frames);
        }

        /// <summary>Hosts and joins <paramref name="count"/> players named p1 onwards.</summary>
        internal void HostWith(int count)
        {
            OpenToMultiplayer();
            for (var index = 1; index <= count; index++) Join(AddPlayer("p" + index, DisplayNameOf(index)));
        }

        internal static string DisplayNameOf(int index) => index switch
        {
            1 => "Sprocket",
            2 => "Widget",
            3 => "Gizmo",
            _ => "Unit " + index
        };

        /// <summary>Runs one frame on every peer, host first, 16 ms apart.</summary>
        internal void Frame()
        {
            BeforeFrame?.Invoke();
            foreach (var raw in RawPlayers) raw.Pump();
            Host.Frame();
            foreach (var player in Players) player.Frame();
            foreach (var raw in RawPlayers) raw.Flush();
            Clock.Advance(16_000);
        }

        internal void Frames(int count)
        {
            for (var index = 0; index < count; index++) Frame();
        }

        internal bool RunUntil(Func<bool> condition, int maximum)
        {
            for (var frame = 0; frame < maximum; frame++)
            {
                if (condition()) return true;
                Frame();
            }

            return condition();
        }
    }

    /// <summary>Assertions for the presence suites.</summary>
    internal static class PresenceAssert
    {
        internal static void That(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Presence test failed: " + message);
        }

        internal static bool Near(NetVector3 left, NetVector3 right, float tolerance) =>
            Math.Abs(left.X - right.X) <= tolerance && Math.Abs(left.Y - right.Y) <= tolerance && Math.Abs(left.Z - right.Z) <= tolerance;

        internal static string Show(NetVector3 value) =>
            "(" + value.X.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + ", " +
            value.Y.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + ", " +
            value.Z.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + ")";

        /// <summary>Gets the position a pose at <paramref name="feet"/> carries once quantized.</summary>
        internal static NetVector3 OnGrid(NetVector3 feet) => NetQuantization.DequantizePosition(NetQuantization.QuantizePosition(feet));
    }
}
