using TopiaForge.ModManager.Core.Multiplayer;
using static TopiaForge.ModManager.Tests.WorldAssert;

namespace TopiaForge.ModManager.Tests
{
    internal static class MotionNativeAdoptionTests
    {
        internal static void Run()
        {
            var rig = AuthorityRig.Create(players: 1);
            new AuthorityMod(rig.Host).Load();
            var player = rig.Players[0];
            var mod = new AuthorityMod(player);
            var made = mod.SpawnAsPlayer(mod.Load(), 1).Value!;
            var body = AuthorityRig.Object(made);
            player.Scene.Held = made;

            // The host has not answered the spawn yet, but the game's pickup has already reached the next frame.
            rig.Host.Paused = true;
            rig.Frames(4);
            That(body.Mark == 0 && player.Scene.Held != null && player.Scene.Drops == 0 && player.World.Motion.LocalCount == 0,
                "a native pickup waiting for spawn adoption stays in hand without claiming an unknown body");
            rig.Host.Paused = false;
            rig.Frames(30);
            That(body.Mark != 0 && player.Client.PendingPredictions == 0, "the held prediction is adopted in place");
            That(player.World.Motion.LocalCount == 1 && !body.Kinematic && player.Scene.Held != null && player.Scene.Drops == 0,
                "adopting a held native body starts its motion ownership without another pickup");
            That(rig.Host.World.Motion.TryGetHosted(new NetworkEntityId(body.Mark), out var state) && state.Rules.State == OwnershipState.Owned,
                "the host records the adopted native hold's ownership");

            body.Position = new NetVector3(2, 1, 0);
            rig.Frames(12);
            That(Near(state.Pose.Position, body.Position), "motion from the adopted native hold reaches the host");
            player.Scene.Held = null;
            rig.Frames(72);
            That(player.World.Motion.LocalCount == 0 && state.Rules.State == OwnershipState.Server,
                "dropping the adopted native hold returns ownership to the host");
        }
    }
}
