using System;
using System.Diagnostics;
using System.Globalization;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using static TopiaForge.ModManager.Tests.WorldAssert;

namespace TopiaForge.ModManager.Tests
{
    internal static class MotionCostTests
    {
        internal static void Run()
        {
            var rig = AuthorityRig.Create(players: 1, boxes: 1);
            var lease = MotionTests.Grab(rig.Players[0]);
            rig.Frames(120);
            var body = MotionTests.Box(rig.Players[0]);
            Action move = () =>
            {
                body.Position = new NetVector3((float)Math.Sin(rig.Clock.Now / 1_000_000.0) * 3, 0, 0);
                rig.Frame();
            };
            var allocated = Allocated(move, 600);
            var sent = rig.Players[0].World.Motion.BytesSent;
            var samples = rig.Host.World.Motion.SamplesAccepted;
            var watch = Stopwatch.StartNew();
            for (var frame = 0; frame < 600; frame++) move();
            watch.Stop();
            var bytes = rig.Players[0].World.Motion.BytesSent - sent;
            var accepted = rig.Host.World.Motion.SamplesAccepted - samples;
            That(lease.IsAlive && accepted >= 287 && accepted <= 289, "motion stays at 30 Hz through 600 honest 16 ms frames");
            That(bytes <= MotionRules.BytesPerSecond * 9.6, "motion stays within its independent byte budget");
            That(allocated / 600.0 < 256, "online motion has a bounded measured allocation cost");
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "Motion online, host and player together: {0:0.0} allocated bytes/frame, {1:0.0} microseconds/frame; {2} accepted samples, {3} outgoing payload bytes in 600 frames ({4:0.0} bytes/frame).",
                allocated / 600.0, watch.Elapsed.TotalMilliseconds * 1000 / 600, accepted, bytes, bytes / 600.0));

            // The same motion driven through the lease, as GravityGun drives it, with each step's intent scope and the
            // engine lease's own result included.
            var throughLease = Allocated(() =>
            {
                lease.MoveToward(new Vec3((float)Math.Sin(rig.Clock.Now / 1_000_000.0) * 3, 0, 0), 5, 1, 30, 0.016f);
                rig.Frame();
            }, 600);
            That(lease.IsAlive, "a lease driven every frame stays held");
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "Motion online through MoveToward: {0:0.0} allocated bytes/frame for host and player together.", throughLease / 600.0));
            lease.Dispose();
            rig.Frames(180);
            var resting = Allocated(() => rig.Frame(), 600);
            That(resting == 0, "motion history at rest allocates zero bytes (" + resting + ")");
            Console.WriteLine("Motion after handback: 0 allocated bytes/frame over 600 frames.");
            AStepOfAHeldBodyOpensItsIntentWithoutAllocating();
        }

        /// <summary>
        /// Every step of a held body opens its lease's intent. In the game's logical call context the scope itself allocates
        /// nothing, so the lease's record must be made once, not once a step.
        /// </summary>
        private static void AStepOfAHeldBodyOpensItsIntentWithoutAllocating()
        {
            object? slot = null;
            var store = new IntentLogicalStore(_ => slot, (_, value) => slot = value, _ => slot = null);
            var intents = new MultiplayerIntents(new IntentScope(IntentScopeMode.LogicalCallContext, store));
            intents.FollowRealm(NetRealm.Client);
            var allocated = Allocated(() =>
            {
                using (intents.EnterMotionLease(WorldPeer.ModId)) { }
            });
            That(allocated == 0, "a held body's steps open the motion lease's intent without allocating (" + allocated + " bytes)");
            using (intents.EnterMotionLease(WorldPeer.ModId))
            {
                var record = intents.VisibleTo(WorldPeer.ModId);
                That(record != null && record.Source == IntentSource.MotionLease, "the kept record is still the holder's motion-lease intent");
            }
        }
    }
}
