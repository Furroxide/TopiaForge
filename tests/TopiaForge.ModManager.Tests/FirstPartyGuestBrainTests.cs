using System;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.RobotKit;

namespace TopiaForge.ModManager.Tests
{
    internal static class FirstPartyGuestBrainTests
    {
        internal static void Run()
        {
            var failures = 0;
            foreach (var test in new Action[] { ForgedSpawn, DirectCommands, ForwardedBrain, ForwardedTalk, TrustedAndForwardedHost, Replica,
                InvalidSpawnArguments, GuestBrainUiAuthorityTests.Run, GuestBrainScopeTests.Run })
            {
                try { test(); Console.WriteLine("Guest brain authority: " + test.Method.Name + " passed."); }
                catch (Exception error) { failures++; Console.WriteLine("Guest brain authority: " + test.Method.Name + " FAILED: " + error.Message); }
            }
            Check(failures == 0, failures + " guest brain authority groups failed.");
        }

        private static (WorldRig Rig, FirstPartyRobots Host, FirstPartyRobots Guest, AuthorityMod Owner) Setup()
        {
            var rig = AuthorityRig.Create(players: 1);
            rig.Host.World.Proxies.IsSharedMod = mod => mod == WorldPeer.ModId || mod == "io.example.robot-provider";
            return (rig, new FirstPartyRobots(rig.Host), new FirstPartyRobots(rig.Players[0]),
                new AuthorityMod(rig.Players[0], ModExecutionContext.Participant));
        }

        private static RobotAgentSpawnRequest Autonomous() => new RobotAgentSpawnRequest(Vec3.Zero,
            brainMode: RobotBrainMode.Autonomous, interaction: RobotInteractionOptions.NativeTalkAtDistance(10));

        private static void Safe(RobotAgentSpawnRequest request, string where) => Check(
            request.BrainMode == RobotBrainMode.Dormant && request.Interaction.NativeTalkMode == RobotNativeTalkMode.Disabled,
            where + " must be Dormant with native talk disabled");

        private static void ForgedSpawn()
        {
            var (rig, host, guest, owner) = Setup();
            var initial = new InitialRobotDescription(rig.Host.Runtime.Spawns.Observer!);
            rig.Host.Runtime.Spawns.Observer = initial;
            // Bypass the author facade: these are the original unsafe wire values, with no caller intent.
            var descriptor = new InternalSpawnDescriptor(RobotReplicationWire.Kind, TransformState.Identity,
                RobotReplicationWire.Spawn(Autonomous()));
            Check(owner.Seam.CurrentIntent.Source == InternalIntent.None, "participant spawn has no intent");
            Check(owner.Seam.RequestSpawn(descriptor, () => guest.Provider.CreateReplica(descriptor)).Succeeded, "forged spawn admitted");
            Check(rig.Run(() => owner.Peer.Client.OwnedObjects == 1), "forged spawn adopted");
            var body = host.Agents.Values.Single();
            Safe(body.Request, "native construction input");
            Check(initial.Request != null, "initial canonical ledger notification captured");
            Safe(initial.Request!, "initial ledger before any refresh");
            Safe(RobotReplicationWire.Spawn(host.Provider.Describe(body).Payload).Request, "canonical description");
            var record = rig.Host.Runtime.Spawns.Find(body)!;
            Safe(RobotReplicationWire.Spawn(((InternalSpawnDescriptor)record.Description.Descriptor!).Payload).Request, "ledger replay");
            Safe(RobotReplicationWire.Spawn(AuthorityRig.OwnedBy(rig.Host, record.OwnerSlot).Single().Payload).Request, "host announcement");
            var late = rig.AddPlayer("late"); var copies = new FirstPartyRobots(late);
            rig.JoinAndSynchronize(late);
            Safe(copies.Agents.Values.Single().Request, "late replica construction");
        }

        private static void InvalidSpawnArguments()
        {
            var (rig, host, guest, owner) = Setup();
            var invalid = new[]
            {
                new RobotAgentSpawnRequest(Vec3.Zero, brainMode: (RobotBrainMode)2),
                new RobotAgentSpawnRequest(Vec3.Zero, brainMode: (RobotBrainMode)(-1)),
                new RobotAgentSpawnRequest(Vec3.Zero, interaction: RobotInteractionOptions.NativeTalkAtDistance(float.NaN)),
                new RobotAgentSpawnRequest(Vec3.Zero, interaction: RobotInteractionOptions.NativeTalkAtDistance(float.PositiveInfinity)),
                new RobotAgentSpawnRequest(Vec3.Zero, interaction: RobotInteractionOptions.NativeTalkAtDistance(101)),
            };
            var hostRecords = rig.Host.Runtime.Spawns.Live().Count;
            var guestRecords = owner.Peer.Runtime.Spawns.Live().Count;
            foreach (var request in invalid)
            {
                Check(guest.Provider.Spawn(owner.Seam, request).ErrorCode == ModErrorCode.InvalidArgument,
                    "original invalid brain/talk input must not be erased by guest normalization");
                Check(guest.Agents.Count == 0 && host.Agents.Count == 0, "invalid input invokes no prediction/native construction");
            }
            rig.Frames(4);
            Check(guest.Agents.Count == 0 && host.Agents.Count == 0 && owner.Peer.Client.OwnedObjects == 0 &&
                rig.Host.Runtime.Spawns.Live().Count == hostRecords && owner.Peer.Runtime.Spawns.Live().Count == guestRecords,
                "invalid input creates no canonical body, adoption or ledger record");
        }

        private static void DirectCommands()
        {
            var (rig, host, guest, owner) = Setup();
            var robot = guest.Provider.Spawn(owner.Seam, Autonomous()).Value!;
            Check(robot.SetBrainMode(RobotBrainMode.Autonomous).ErrorCode == ModErrorCode.NotAuthoritative,
                "direct guest brain is refused before adoption, never silently queued");
            Check(robot.SetInteraction(RobotInteractionOptions.NativeTalk()).ErrorCode == ModErrorCode.NotAuthoritative,
                "direct guest talk is refused before adoption, never silently queued");
            Check(rig.Run(() => owner.Peer.Client.OwnedObjects == 1), "direct spawn adopted");
            var body = host.Agents.Values.Single();
            Check(robot.SetBrainMode(RobotBrainMode.Dormant).Succeeded && robot.SetInteraction(RobotInteractionOptions.DisableNativeTalk()).Succeeded &&
                robot.SetName("Permitted").Succeeded, "guest non-AI controls accepted");
            Check(rig.Run(() => body.Name == "Permitted" && body.BrainCalls == 1 && body.InteractionCalls == 1), "permitted commands reach native adapter");
            Safe(RobotReplicationWire.Spawn(host.Provider.Describe(body).Payload).Request, "updated guest descriptor");
        }

        private static byte[] Brain() => RobotReplicationWire.Write(w => { w.Write((byte)RobotCommand.Brain); w.Write((byte)RobotBrainMode.Autonomous); });
        private static byte[] Talk() => RobotReplicationWire.Write(w => { w.Write((byte)RobotCommand.Interaction); w.Write(true); w.Write(10f); });
        private static void ForwardedBrain() => ForwardedCommand(Brain());
        private static void ForwardedTalk() => ForwardedCommand(Talk());

        private static void ForwardedCommand(byte[] payload)
        {
            var (rig, host, guest, owner) = Setup();
            var robot = guest.Provider.Spawn(owner.Seam, new RobotAgentSpawnRequest(Vec3.Zero)).Value!;
            Check(rig.Run(() => owner.Peer.Client.OwnedObjects == 1), "command target adopted");
            var body = host.Agents.Values.Single();
            var refused = rig.Host.Counters().ChangesRefused;
            var writer = new WorldWireWriter(4096);
            EntityCommandWire.Write(writer, AuthorityTarget.Entity(new NetworkEntityId(guest.Agents[robot.Id].Body.Mark)),
                WorldPeer.ModId, RobotReplicationWire.Kind, payload);
            Check(owner.Peer.World.Channel.TrySendToHost(writer), "forged command sent");
            Check(rig.Run(() => rig.Host.Counters().ChangesRefused > refused), "host refuses forged wake command");
            Check(body.BrainCalls == 0 && body.InteractionCalls == 0, "forged command must not invoke native effects");
            Safe(RobotReplicationWire.Spawn(host.Provider.Describe(body).Payload).Request, "refused command state");
            // Exercise the exact runtime scope and provider result as well as the on-wire refusal counter.
            using (rig.Host.Runtime.Intents.EnterForwarded(WorldPeer.ModId, IntentSource.OwnerCommand, owner.Peer.Control.Id.Value))
                Check(host.Provider.Execute(body, payload, _ => null).ErrorCode == ModErrorCode.NotAuthoritative,
                    "forwarded provider command reports NotAuthoritative across consuming/provider mod ids");
            Check(body.BrainCalls == 0 && body.InteractionCalls == 0, "direct executor also refuses before effects");
        }

        private static void TrustedAndForwardedHost()
        {
            var (rig, host, guest, owner) = Setup();
            var server = new AuthorityMod(rig.Host, ModExecutionContext.GamemodeServer);
            var robot = host.Provider.Spawn(server.Seam, Autonomous()).Value!;
            var body = host.Agents[robot.Id];
            Check(body.Request.BrainMode == RobotBrainMode.Autonomous && body.Request.Interaction.NativeTalkMode == RobotNativeTalkMode.Enabled,
                "trusted server construction retains native brain and talk");
            Check(robot.SetBrainMode(RobotBrainMode.Dormant).Succeeded && robot.SetBrainMode(RobotBrainMode.Autonomous).Succeeded &&
                robot.SetInteraction(RobotInteractionOptions.NativeTalk()).Succeeded, "host routed commands remain supported");
            var brains = body.BrainCalls; var talks = body.InteractionCalls;
            using (rig.Host.Runtime.Intents.EnterForwarded(WorldPeer.ModId, IntentSource.UiCallback, owner.Peer.Control.Id.Value))
            {
                Check(robot.SetBrainMode(RobotBrainMode.Autonomous).ErrorCode == ModErrorCode.NotAuthoritative &&
                    robot.SetInteraction(RobotInteractionOptions.NativeTalk()).ErrorCode == ModErrorCode.NotAuthoritative,
                    "a forwarded callback cannot spend host calls through a host handle");
                Check(server.Seam.SubmitEntityCommand(body, RobotReplicationWire.Kind, Brain()).ErrorCode == ModErrorCode.NotAuthoritative &&
                    server.Seam.SubmitEntityCommand(body, RobotReplicationWire.Kind, Talk()).ErrorCode == ModErrorCode.NotAuthoritative,
                    "raw host submission retains forwarded origin");
                var forwarded = host.Provider.Spawn(server.Seam, Autonomous()).Value!;
                Safe(host.Agents[forwarded.Id].Request, "forwarded host spawn");
                var record = rig.Host.Runtime.Spawns.Find(host.Agents[forwarded.Id])!;
                Safe(RobotReplicationWire.Spawn(((InternalSpawnDescriptor)record.Description.Descriptor!).Payload).Request, "forwarded host ledger");
                var malformed = Brain().Concat(new byte[] { 99 }).ToArray();
                Check(server.Seam.SubmitEntityCommand(body, RobotReplicationWire.Kind, malformed).ErrorCode == ModErrorCode.InvalidArgument,
                    "full framing validation precedes authority refusal");
            }
            Check(body.BrainCalls == brains && body.InteractionCalls == talks, "forwarded host controls invoke no native effect");
            Check(server.AsPlayer(() => robot.SetBrainMode(RobotBrainMode.Autonomous)).Succeeded &&
                robot.SetInteraction(RobotInteractionOptions.NativeTalk()).Succeeded, "host local player and trusted server recover after forwarded scope");
            Check(rig.Run(() => guest.Agents.Count == 2), "host bodies replicated");
            foreach (var copy in guest.Agents.Values) Safe(copy.Request, "host-owned replica");
        }

        private static void Replica()
        {
            var (_, _, guest, _) = Setup();
            var descriptor = new InternalSpawnDescriptor(RobotReplicationWire.Kind, TransformState.Identity, RobotReplicationWire.Spawn(Autonomous()));
            var entity = guest.Provider.CreateReplica(descriptor).Value!;
            var body = guest.Agents[entity.Id];
            Check(guest.Provider.Execute(body, Brain(), _ => null).ErrorCode == ModErrorCode.NotAuthoritative &&
                guest.Provider.Execute(body, Talk(), _ => null).ErrorCode == ModErrorCode.NotAuthoritative, "replica commands cannot wake native behavior");
            Check(body.BrainCalls == 1 && body.InteractionCalls == 1, "only dormant replica application touched adapter");
            guest.Provider.ApplyReplica(body, descriptor);
            Safe(RobotReplicationWire.Spawn(guest.Provider.Describe(body).Payload).Request, "replica replay after host update");
        }

        private sealed class InitialRobotDescription : IMultiplayerSpawnObserver
        {
            private readonly IMultiplayerSpawnObserver next;
            internal RobotAgentSpawnRequest? Request;
            internal InitialRobotDescription(IMultiplayerSpawnObserver next) { this.next = next; }
            public void Recorded(MultiplayerSpawnRecord record, bool redescribed)
            {
                if (!redescribed && Request == null && record.Description.Kind == RobotReplicationWire.Kind &&
                    record.Description.Descriptor is InternalSpawnDescriptor descriptor)
                    Request = RobotReplicationWire.Spawn(descriptor.Payload).Request;
                next.Recorded(record, redescribed);
            }
        }

        private static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }
}
