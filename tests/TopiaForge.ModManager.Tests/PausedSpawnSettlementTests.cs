using System;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static class PausedSpawnSettlementTests
    {
        private const float Step = 0.02f;
        private static readonly TransformState Start = Pose(0f);
        internal static void Run()
        {
            var paused = new NativeSpawnSettlement(Start, 2f, 0f);
            for (var frame = 0; frame < 20; frame++)
                Check(paused.Probe(Start, 0f) == null, "rendered frames without a fixed step cannot settle the requested pose");
            Check(paused.Probe(Start, Step) == null, "one fixed step is not proof of a steady spawn");
            for (var frame = 0; frame < 20; frame++)
                Check(paused.Probe(Start, Step) == null, "paused frames after a fixed step cannot add samples");

            var steady = new NativeSpawnSettlement(Start, 2f, 0f);
            for (var step = 1; step < 8; step++)
                Check(steady.Probe(Start, step * Step) == null, "steady samples must span 0.15 s of fixed time");
            Check(steady.Probe(Start, 8 * Step)!.Succeeded, "eight 20 ms steps span the steady time");

            var lifted = new NativeSpawnSettlement(Start, 2f, 0f);
            var actual = Pose(0.13f);
            for (var step = 1; step < 9; step++)
                Check(lifted.Probe(actual, step * Step) == null, "a lift starts the steady time at its first fixed observation");
            var accepted = lifted.Probe(actual, 9 * Step);
            Check(accepted != null && accepted.Succeeded && accepted.Value == actual, "a lifted pose settles after 0.15 s of steady fixed time");

            // A 0.42 m/s pop stays within 1 cm for 0.1 s while it already falls; the window must see the fall.
            var popping = new NativeSpawnSettlement(Start, 2f, 0f);
            OperationResult<TransformState>? pop = null;
            var popSteps = 0;
            while (pop == null && popSteps < 30)
            {
                popSteps++;
                var time = popSteps * Step;
                pop = popping.Probe(Pose(0.42f * time - 4.905f * time * time), time);
            }
            Check(pop != null && pop.ErrorCode == ModErrorCode.External && popSteps == 6, "a small upward pop that turns into a fall is refused");

            // A body that falls from rest moves less than 1 cm in each of its first steps;
            // the steady time keeps observing until the fall passes the downward guard.
            var falling = new NativeSpawnSettlement(Start, 2f, 0f);
            OperationResult<TransformState>? fall = null;
            var steps = 0;
            while (fall == null && steps < 30)
            {
                steps++;
                var time = steps * Step;
                fall = falling.Probe(Pose(-4.905f * time * time), time);
            }
            Check(fall != null && fall.ErrorCode == ModErrorCode.External && steps == 3, "a gradual fall from rest is refused, not accepted");

            var drifting = new NativeSpawnSettlement(Start, 2f, 0f);
            for (var step = 1; step <= 5; step++)
                Check(drifting.Probe(Pose(0.003f * step), step * Step) == null, "a slow climb is not a steady run");

            var budget = new NativeSpawnSettlement(Start, 2f, 0f);
            for (var step = 1; step < 25; step++)
                Check(budget.Probe(Pose(step * 0.05f), step * Step) == null, "unsteady samples wait until the fixed settle time ends");
            Check(budget.Probe(Pose(1.5f), 30 * Step)!.ErrorCode == ModErrorCode.External, "unsteady player is refused after 0.5 s of fixed time");

            Check(new NativeSpawnSettlement(Start, 2f, 0f).Probe(Start, float.NaN)!.ErrorCode == ModErrorCode.Unavailable,
                "a paused probe still validates the fixed clock");
            Check(new NativeSpawnSettlement(Start, 2f, 1f).Probe(Start, 0f)!.ErrorCode == ModErrorCode.External,
                "a paused probe still refuses a fixed-clock regression");
            foreach (var invalid in new[] { new TransformState(new Vec3(0.011f, 0, 0), Start.Rotation, Start.Scale),
                Pose(-0.011f), Pose(2.001f), new TransformState(Start.Position, new Quat(0, 0.1f, 0, 0.9949874f), Start.Scale) })
                Check(new NativeSpawnSettlement(Start, 2f, 0f).Probe(invalid, 0f)!.ErrorCode == ModErrorCode.External,
                    "a paused probe still enforces every placement guard");
            Console.WriteLine("Paused settlement policy passed.");
        }
        private static TransformState Pose(float height) => new TransformState(new Vec3(0, height, 0), Quat.Identity, new Vec3(1, 1, 1));
        private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Paused settlement: " + message); }
    }
}
