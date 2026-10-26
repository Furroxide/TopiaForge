using System;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using static TopiaForge.ModManager.Tests.PresenceAssert;

namespace TopiaForge.ModManager.Tests
{
    internal static class PresenceFirstPoseHostTests
    {
        internal static void Run()
        {
            MissingOrInvalidSpawnWaits();
            ArrivalOrderingKeepsTheTrustedSpawn();
            NoClearSlotStillUsesReadiness();
            WorldChangesInvalidateBeforeArrival();
            LinkReturnKeepsValidationHistory();
            Console.WriteLine("PresenceFirstPoseHostTests passed: 5 runtime groups.");
        }

        private static TransformState Spawn(float x) => new TransformState(new Vec3(x, 0f, 0f), Quat.Identity, new Vec3(1f, 1f, 1f));
        private static PresencePeer Peer(PresenceRig rig, RawPresencePlayer raw)
        {
            That(rig.Host.Presence.TryGetPeer(raw.Id, out var peer), "the host knows the raw participant");
            return peer!;
        }
        private static void Pose(PresenceRig rig, RawPresencePlayer raw, float x, PresenceFlags flags = PresenceFlags.None)
        {
            raw.SendPose(new NetVector3(x, 0f, 0f), flags, Peer(rig, raw).Plausibility!.Correction);
            rig.Frames(3);
        }
        private static void Arrive(PresenceRig rig, RawPresencePlayer raw)
        {
            raw.SendMessage(writer => PresenceWire.WriteArrived(writer, rig.Host.World!));
            rig.Frames(3);
        }

        private static void MissingOrInvalidSpawnWaits()
        {
            // TransformState rejects non-finite values at construction; default(struct) can still bypass its constructor.
            foreach (var spawn in new TransformState?[] { null, Spawn(4096), default(TransformState) })
            {
                var rig = new PresenceRig();
                rig.Host.Spawn = spawn;
                rig.OpenToMultiplayer();
                var raw = rig.AddRawPlayer("raw");
                rig.Frames(3);
                Arrive(rig, raw);
                Pose(rig, raw, 900, PresenceFlags.Teleported);
                Pose(rig, raw, 0);
                var peer = Peer(rig, raw);
                That(!peer.HasPose && peer.Avatar == null && rig.Host.Presence.ValidatedPosition(raw.Id) == null &&
                    raw.Corrections().Count == 0 && rig.Host.Counters.Placements == 0 && rig.Network.Kicks.Count == 0,
                    "missing or invalid readiness creates no pose, avatar, placement, correction target or kick");
                rig.Host.Spawn = Spawn(100);
                rig.Frames(1);
                Pose(rig, raw, 100);
                That(peer.HasPose && peer.Avatar != null, "late valid readiness restores presence without trusting the earlier packet");
            }

            var native = new PresenceRig();
            native.Host.World = null;
            native.Host.Spawn = Spawn(0);
            native.OpenToMultiplayer();
            var player = native.AddRawPlayer("raw");
            native.Frames(3);
            Pose(native, player, 0);
            That(!Peer(native, player).HasPose, "a coordinate without a running readiness world is not a trusted spawn");

            var outside = new PresenceRig();
            outside.Host.Spawn = Spawn(100);
            outside.Host.Backend.Bounds = PresenceBounds.FromMeters(new NetVector3(-10f, -10f, -10f), new NetVector3(10f, 10f, 10f), 0f, 0f);
            outside.OpenToMultiplayer();
            var bounded = outside.AddRawPlayer("raw");
            outside.Frames(3);
            Arrive(outside, bounded);
            Pose(outside, bounded, 0);
            That(!Peer(outside, bounded).HasPose && outside.Host.Counters.Placements == 0 && bounded.Corrections().Count == 0,
                "an out-of-bounds readiness target is not a trusted correction or placement");
        }

        private static void ArrivalOrderingKeepsTheTrustedSpawn()
        {
            foreach (var arrivalFirst in new[] { false, true })
            {
                var rig = new PresenceRig();
                rig.Host.Spawn = Spawn(100);
                rig.OpenToMultiplayer();
                var raw = rig.AddRawPlayer("raw");
                rig.Frames(3);
                if (arrivalFirst) Arrive(rig, raw);
                Pose(rig, raw, 900, PresenceFlags.Teleported);
                That(!Peer(rig, raw).HasPose && !rig.Host.Presence.TryGetAvatar(raw.Id, out _) &&
                    rig.Host.Presence.ValidatedPosition(raw.Id) == null, "forged first pose is invisible in either arrival order");
                Pose(rig, raw, 101);
                That(Peer(rig, raw).HasPose, "an honest player can move while the messages arrive");
                if (!arrivalFirst) Arrive(rig, raw);
                var peer = Peer(rig, raw);
                var corrections = rig.Host.Counters.Corrections;
                raw.SendPose(peer.PlacementFeet, PresenceFlags.Teleported, peer.Plausibility!.Correction);
                rig.Frames(3);
                That(rig.Host.Counters.Corrections == corrections && Near(rig.Host.Presence.ValidatedPosition(raw.Id)!.Value, peer.PlacementFeet, 0.001f),
                    "the ring landing is still pending after the first readiness pose");
            }
        }

        private static void NoClearSlotStillUsesReadiness()
        {
            var rig = new PresenceRig();
            rig.Host.Spawn = Spawn(100);
            rig.Host.Backend.Clear = _ => false;
            rig.OpenToMultiplayer();
            var raw = rig.AddRawPlayer("raw");
            rig.Frames(3);
            Arrive(rig, raw);
            Pose(rig, raw, 900);
            That(!Peer(rig, raw).HasPose && rig.Host.Counters.Placements == 0, "no ring slot does not permit an arbitrary first pose");
            Pose(rig, raw, 100);
            That(Peer(rig, raw).HasPose && Peer(rig, raw).Avatar != null, "the actual readiness spawn still works without a ring slot");

            var unrepresentable = new PresenceRig();
            unrepresentable.Host.Backend.Ground = _ => new NetVector3(5000f, 0f, 0f);
            unrepresentable.HostWith(1);
            That(unrepresentable.Host.Counters.Placements == 0 && unrepresentable.Host.Presence.ValidatedPosition(unrepresentable.Players[0].Id).HasValue,
                "an unrepresentable ring landing is refused without clamping it into a different validated position");
        }

        private static void WorldChangesInvalidateBeforeArrival()
        {
            foreach (var sameLevel in new[] { false, true })
            {
                var rig = new PresenceRig();
                rig.OpenToMultiplayer();
                var raw = rig.AddRawPlayer("raw");
                rig.Frames(3);
                Pose(rig, raw, 0);
                var avatar = Peer(rig, raw).Avatar!;
                That(avatar.IsAlive, "the old world had an avatar");
                rig.Host.Spawn = Spawn(900);
                if (sameLevel) rig.Host.SceneVersion++;
                else rig.Host.World = "world-two";
                That(rig.Host.Presence.ValidatedPosition(raw.Id) == null, "motion queries before presence's receive stage cannot reuse the old world");
                rig.Frames(1);
                That(!avatar.IsAlive && !Peer(rig, raw).HasPose && rig.Host.Presence.ValidatedPosition(raw.Id) == null,
                    "host world or scene instance change clears old validation and presentation before arrival");
                Pose(rig, raw, 0, PresenceFlags.Teleported);
                That(!Peer(rig, raw).HasPose, "the old world cannot supply the new first pose");
                Pose(rig, raw, 900);
                That(Peer(rig, raw).HasPose, "the new readiness spawn works before arrival");
                Arrive(rig, raw);
                var peer = Peer(rig, raw);
                That(peer.Slot >= 0, "arrival still places the player");
                var strikes = peer.Plausibility!.Strikes;
                Arrive(rig, raw);
                That(peer.Plausibility.Strikes == strikes && rig.Host.Counters.Placements == 1, "repeated arrival resets neither strikes nor placement");
            }
        }

        private static void LinkReturnKeepsValidationHistory()
        {
            var rig = new PresenceRig();
            rig.OpenToMultiplayer();
            var raw = rig.AddRawPlayer("raw");
            rig.Frames(3);
            Pose(rig, raw, 0);
            Pose(rig, raw, 200, PresenceFlags.Teleported);
            Pose(rig, raw, 400, PresenceFlags.Teleported);
            Pose(rig, raw, 900);
            var rules = Peer(rig, raw).Plausibility!;
            rig.Network.LoseLink(raw.Control);
            rig.Frames(3);
            That(rig.Host.Presence.ValidatedPosition(raw.Id) == null, "disconnected motion fails closed");
            rig.Network.Restore(raw.Control);
            rig.Frames(3);
            Pose(rig, raw, 403);
            That(rules.Anchor.Equals(NetQuantization.QuantizePosition(new NetVector3(403f, 0f, 0f))) && rules.Strikes == 1,
                "honest movement on return keeps the previous trusted position and strike history");
            Pose(rig, raw, 900, PresenceFlags.Teleported);
            That(rules.Strikes == 2 && rules.Anchor.X < NetQuantization.QuantizePositionAxis(500), "link and stream return do not replenish teleport credit");
        }
    }
}
