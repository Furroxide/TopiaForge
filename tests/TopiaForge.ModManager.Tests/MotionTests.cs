using System;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.WorldAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>Ownership through the actual decorators, world channel, clock and replica driver.</summary>
    internal static class MotionTests
    {
        internal static void Run()
        {
            FirstGrabWinsAndTheDriverYields();
            AChildColliderAcquiresTheKeyedBody();
            ThrowCanBeStolenAndHandsBackVelocity();
            HostThrowYieldsPhysicsToACatcher();
            HostRejectsSpeedAndRange();
            SilenceDisconnectAndScopeStopReclaim();
            NativePickupUsesTheSameProtocol();
            NativePickupLeavesWhatIsNoNetworkBody();
            TheHostHearsWhyItCannotHold();
            AnOvertakenGrabIsToldEvenWhenThrown();
            OfflineAndClientLocalPassThrough();
            Console.WriteLine("Motion ownership tests passed.");
        }

        internal static FakeWorldObject Box(WorldPeer peer, int index = 0) => AuthorityRig.Live(peer, "Box")[index];

        internal static NetEntityMotion Grab(WorldPeer peer, int index = 0, AuthorityMod? mod = null)
        {
            mod ??= new AuthorityMod(peer);
            var result = mod.AsPlayer(() => mod.Entities.AcquireMotion(Box(peer, index).Entity));
            That(result.Succeeded, "a grab starts optimistically: " + result.ErrorMessage);
            return (NetEntityMotion)result.Value!;
        }

        /// <summary>
        /// Whether the host simulates a lease's body itself again. The test boxes are kinematic while a player holds them
        /// and after, so their kinematic flag alone cannot show that the host took a body back.
        /// </summary>
        internal static bool HostHas(WorldRig rig, NetEntityMotion lease) =>
            rig.Host.World.Motion.TryGetHosted(lease.Target.Id, out var state) && state.Rules.State == OwnershipState.Server;

        private static void FirstGrabWinsAndTheDriverYields()
        {
            var rig = AuthorityRig.Create(boxes: 1);
            var first = Grab(rig.Players[0]);
            var secondMod = new AuthorityMod(rig.Players[1]);
            var second = Grab(rig.Players[1], mod: secondMod);
            That(!first.Granted && !Box(rig.Players[0]).Kinematic, "the first player simulates before the answer");
            rig.Frames(8);
            That(first.Granted && first.IsAlive && !second.IsAlive, "the first simultaneous grab wins; the second cannot take an owned body");
            That(Box(rig.Host).Mark != 0 && first.Target.Id.IsValid, "a stationary scene body binds immediately on grab");
            That(secondMod.Ui.Toasts.Count == 1, "a refusal gives the player one useful toast");
            var body = Box(rig.Players[0]);
            body.Position = new NetVector3(2, 0, 0);
            var writes = body.Writes;
            rig.Frames(16);
            That(body.Writes == writes && Near(Box(rig.Host).Position, body.Position), "the holder drives the host without interpolation overwriting local physics");
            That(Near(Box(rig.Players[1]).Position, body.Position, 0.02), "the other player sees the accepted motion");
            first.Dispose();
            rig.Frames(8);
            That(first.Ended && Box(rig.Host).Kinematic && body.Kinematic, "a normal release restores the server's original kinematic body and suppresses the replica");
        }

        private static void ThrowCanBeStolenAndHandsBackVelocity()
        {
            var rig = AuthorityRig.Create(boxes: 2);
            var thrower = new AuthorityMod(rig.Players[0]);
            var first = Grab(rig.Players[0], mod: thrower);
            rig.Frames(8);
            That(first.Throw(new Vec3(1, 0, 0), 12).Succeeded, "throw succeeds through the unchanged SDK lease");
            first.Dispose();
            rig.Frames(8);
            That(!first.Ended && !Box(rig.Players[0]).Kinematic, "disposing the thrown lease leaves its one-second tail running");
            var second = Grab(rig.Players[1]);
            rig.Frames(8);
            That(second.Granted && second.IsAlive && first.Ended, "another player may catch a released body and the old owner stops");
            That(thrower.Ui.Toasts.Count == 0, "a thrower whose throw another player catches is told nothing: it was no failure");
            var thrown = Grab(rig.Players[0], 1);
            rig.Frames(8);
            thrown.Throw(new Vec3(1, 0, 0), 12);
            thrown.Dispose();
            Box(rig.Players[0], 1).AngularVelocity = new NetVector3(0, 3, 0);
            rig.Frames(72);
            That(thrown.Ended && !Box(rig.Host, 1).Kinematic, "after a second the host resumes physics");
            That(Near(Box(rig.Host, 1).Velocity!.Value, new NetVector3(12, 0, 0)) &&
                Near(Box(rig.Host, 1).AngularVelocity, new NetVector3(0, 3, 0)), "handback preserves accepted linear and angular velocity");
        }

        private static void AChildColliderAcquiresTheKeyedBody()
        {
            var rig = AuthorityRig.Create(players: 1, boxes: 1);
            var player = rig.Players[0];
            var box = Box(player);
            var collider = player.Scene.Add(box.Scene, "Collider", box);
            var mod = new AuthorityMod(player);
            That(!mod.Entities.AcquireMotion(box.Entity).Succeeded, "a joined process script cannot acquire the host's body without player intent");
            var lease = mod.AsPlayer(() => mod.Entities.AcquireMotion(collider.Entity)).Value!;
            That(lease is NetEntityMotion && lease.Entity.Id == box.Entity.Id, "a collider child resolves to the scene's keyed body before it has a network mark");
            That(!mod.AsPlayer(() => mod.Entities.AcquireMotion(box.Entity)).Succeeded, "a second handle to the same root cannot acquire it twice");
            rig.Frames(8);
            That(lease.IsAlive && Box(rig.Host).Mark != 0, "the root's grant crosses the wire");
            lease.Dispose();
        }

        private static void HostRejectsSpeedAndRange()
        {
            var rig = AuthorityRig.Create(players: 1, boxes: 2);
            var player = rig.Players[0];
            var mod = new AuthorityMod(player);
            var lease = Grab(player, mod: mod);
            rig.Frames(8);
            Box(player).Position = new NetVector3(80, 0, 0);
            rig.Frames(10);
            That(lease.Ended && rig.Host.World.Motion.SamplesRefused > 0 && Box(rig.Host).Position.X < 1,
                "a teleport within hold range still fails the host's speed budget and restores the last accepted pose");
            That(mod.Ui.Toasts.Count == 1 && mod.Ui.Toasts[0].Message == MotionRules.Refusal(OwnershipResult.Rejected),
                "the player hears that the host took the body back for moving it too far or too fast");
            rig.Host.World.Motion.ParticipantPosition = _ => new NetVector3(1000, 0, 0);
            var far = Grab(player, 1);
            rig.Frames(8);
            That(far.Ended, "the host's validated avatar overrides a forged nearby player position");
            rig.Host.World.Motion.ParticipantPosition = _ => null;
            var missing = Grab(player, 1);
            rig.Frames(8);
            That(missing.Ended, "once presence is installed, a missing avatar fails closed");
        }

        private static void HostThrowYieldsPhysicsToACatcher()
        {
            var rig = AuthorityRig.Create(players: 1, boxes: 1);
            var thrower = new AuthorityMod(rig.Host);
            var thrown = Grab(rig.Host, mod: thrower);
            rig.Frames(8);
            thrown.Throw(new Vec3(1, 0, 0), 12);
            thrown.Dispose();
            rig.Frames(8);
            var caught = Grab(rig.Players[0]);
            rig.Frames(8);
            That(caught.Granted && thrown.Ended && Box(rig.Host).Kinematic,
                "a host's retired throw lease cannot re-enable host physics after a remote player catches it");
            That(thrower.Ui.Toasts.Count == 0, "the host's player is told nothing when another player catches its throw");
            caught.Throw(new Vec3(1, 0, 0), 12);
            caught.Dispose();
            rig.Frames(8);
            rig.Players[0].Paused = true;
            rig.Frames(80);
            That(!Box(rig.Host).Kinematic && Box(rig.Host).Velocity!.Value.X == 12,
                "silence during a throw resumes host physics from the last accepted velocity");
        }

        private static void SilenceDisconnectAndScopeStopReclaim()
        {
            var rig = AuthorityRig.Create(players: 1, boxes: 3);
            var player = rig.Players[0];
            var silent = Grab(player);
            rig.Frames(8);
            player.Paused = true;
            rig.Frames(80);
            That(Box(rig.Host).Kinematic && HostHas(rig, silent), "a silent holder is reclaimed by the host");
            player.Paused = false;
            rig.Frames(8);
            var mod = new AuthorityMod(player);
            var scoped = Grab(player, 1, mod);
            rig.Frames(8);
            mod.Lifetime.Dispose();
            rig.Frames(8);
            That(scoped.Ended && Box(rig.Host, 1).Kinematic && HostHas(rig, scoped), "stopping a context hands back its own body promptly");
            var linked = Grab(player, 2);
            rig.Frames(8);
            rig.Network.Leave(player.Control);
            rig.Frames(2);
            That(Box(rig.Host, 2).Kinematic && HostHas(rig, linked), "a closed link reclaims motion immediately, before the proxy grace expires");
            rig.Clock.Advance(31_000_000);
            rig.Frame();
            That(rig.Host.World.Proxies.ProxyCount == 0, "a departed participant's proxy and motion slot are cleaned up");
        }

        private static void NativePickupUsesTheSameProtocol()
        {
            var rig = AuthorityRig.Create(boxes: 1);
            var first = rig.Players[0];
            var second = rig.Players[1];
            first.Scene.Held = Box(first).Entity;
            rig.Frames(8);
            second.Scene.Held = Box(second).Entity;
            rig.Frames(8);
            That(first.World.Motion.LocalCount == 1 && second.Scene.Held == null && second.Scene.Drops == 1,
                "native held items contend through ownership and the denied pickup drops");
            first.Scene.Held = null;
            rig.Frames(72);
            That(first.World.Motion.LocalCount == 0 && !Box(rig.Host).Kinematic, "a native drop finishes its release window on the server");
        }

        private static void NativePickupLeavesWhatIsNoNetworkBody()
        {
            var rig = AuthorityRig.Create(players: 1, boxes: 1);
            foreach (var peer in new[] { rig.Host, rig.Players[0] })
            {
                // Made after its scene loaded, as the game's own items and an SDK item grant are: no scene key, no network id.
                var wrench = peer.Scene.Add(peer.Scene.ActiveScene!, "Wrench", null, rigidbody: true, position: new NetVector3(1, 0, 0));
                peer.Scene.Held = wrench.Entity;
                rig.Frames(8);
                That(peer.Scene.Held != null && peer.Scene.Drops == 0 && peer.World.Motion.LocalCount == 0,
                    "a held item that is no network body stays in the hand, as offline (" + peer.Name + ")");
                peer.Scene.Held = null;
                rig.Frames(2);
            }

            rig.Host.Scene.Held = Box(rig.Host).Entity;
            rig.Frames(8);
            That(rig.Host.World.Motion.LocalCount == 1, "the host owns the body it picked up natively");
            That(rig.Host.Runtime.Leave().Succeeded, "hosting stops");
            rig.Frames(4);
            That(rig.Host.Scene.Held != null && rig.Host.Scene.Drops == 0, "stopping hosting ends ownership but keeps what the player holds");
        }

        private static void TheHostHearsWhyItCannotHold()
        {
            var rig = AuthorityRig.Create(players: 1, boxes: 2);
            Grab(rig.Players[0]);
            rig.Frames(8);
            var refused = new AuthorityMod(rig.Host);
            var result = refused.AsPlayer(() => refused.Entities.AcquireMotion(Box(rig.Host).Entity));
            var denied = MotionRules.Refusal(OwnershipResult.Denied);
            That(!result.Succeeded && result.ErrorMessage == denied && refused.Ui.Toasts.Count == 1 && refused.Ui.Toasts[0].Message == denied,
                "the host's own refused grab shows one toast, as a joined player's does, saying another player holds the body");

            // Past the toast interval, the host's own lease that its speed check took back says so.
            rig.Frames(230);
            var mod = new AuthorityMod(rig.Host);
            var lease = Grab(rig.Host, 1, mod);
            rig.Frames(8);
            Box(rig.Host, 1).Position = new NetVector3(82, 0, 0);
            rig.Frames(10);
            That(lease.Ended && mod.Ui.Toasts.Count == 1 && mod.Ui.Toasts[0].Message == MotionRules.Refusal(OwnershipResult.Rejected),
                "the host's own lease its speed check took back says why, not that another player holds the body");
        }

        private static void AnOvertakenGrabIsToldEvenWhenThrown()
        {
            var rig = AuthorityRig.Create(players: 1, boxes: 1);
            var player = rig.Players[0];
            Box(rig.Host).Position = new NetVector3(0.5f, 0, 0);
            rig.Frames(30);
            var mod = new AuthorityMod(player);
            var lease = Grab(player, mod: mod);
            That(lease.Target.Id.IsValid && !lease.Granted, "the grab of a bound body waits for its answer");
            lease.Throw(new Vec3(1, 0, 0), 12);
            lease.Dispose();

            // The host granted the body to another player first, and that state overtakes this grab's answer, as on a slow link.
            var overtaking = new MotionMessage
            {
                Target = lease.Target,
                Epoch = lease.Epoch + 1,
                Slot = 2,
                State = OwnershipState.Owned,
                Result = OwnershipResult.Granted,
                Pose = new NetPose(new NetVector3(0.5f, 0, 0), NetQuaternion.Identity),
                TimeMicros = rig.Clock.Now
            };
            var writer = new WorldWireWriter();
            MotionWire.Write(writer, MotionMessages.State, overtaking);
            player.Control.Channel(InternalSystemChannelKind.WorldReplication)!.Inject(rig.Host.Control.Id, writer.Segment.ToArray());
            rig.Frame();
            That(lease.Ended && mod.Ui.Toasts.Count == 1 && mod.Ui.Toasts[0].Message == MotionRules.Refusal(OwnershipResult.Denied),
                "a grab another player's grant overtook before its answer is told so, though its throw had already let go");
        }

        private static void OfflineAndClientLocalPassThrough()
        {
            var rig = new WorldRig();
            rig.UseScene(scene => scene.Add(scene.AddScene("Yard"), "Box", rigidbody: true));
            var mod = new AuthorityMod(rig.Host);
            var lease = mod.Entities.AcquireMotion(Box(rig.Host).Entity).Value!;
            That(lease is FakeEntityMotion, "offline motion uses the original service");
            lease.Dispose();
            rig.OpenToMultiplayer();
            var local = new AuthorityMod(rig.Host, networkClass: ModNetworkClass.ClientLocal);
            var localLease = local.Entities.AcquireMotion(Box(rig.Host).Entity).Value!;
            That(localLease is FakeEntityMotion, "client-local mods keep their own motion path");
            localLease.Dispose();
        }
    }
}
