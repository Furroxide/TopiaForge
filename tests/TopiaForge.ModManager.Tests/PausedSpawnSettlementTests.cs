using System;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static class PausedSpawnSettlementTests
    {
        private static readonly TransformState Start = Pose(0f);
        internal static void Run()
        {
            var paused = new NativeSpawnSettlement(Start, 2f, 0f, 10);
            var actual = Pose(0.13f);
            Check(paused.Probe(Start, 0f, 10, true) == null && paused.Probe(Start, 0f, 11, true) == null
                && paused.Probe(Start, 0f, 12, true) == null, "paused render frames alone cannot settle the requested pose");
            Check(paused.Probe(actual, 1f, 13, false) == null && paused.Probe(actual, 1f, 14, true) == null,
                "the first fixed observation of a lift still needs two steady samples");
            var accepted = paused.Probe(actual, 1f, 15, true);
            Check(accepted != null && accepted.Succeeded && accepted.Value == actual,
                "paused frames can confirm the actual pose after physics advances");

            var duplicate = new NativeSpawnSettlement(Start, 2f, 0f, 10);
            Check(duplicate.Probe(actual, 1f, 10, true) == null && duplicate.Probe(actual, 1f, 11, true) == null,
                "a fixed epoch first observed in pause still requires a fresh rendered frame and stable samples");
            Check(duplicate.Probe(actual, 1f, 12, true) == null && duplicate.Probe(actual, 1f, 12, true) == null
                && duplicate.Probe(actual, 1f, 13, true)!.Succeeded, "paused stability counts each rendered frame once");
            var running = new NativeSpawnSettlement(Start, 2f, 0f, 10);
            Check(running.Probe(Start, 0f, 11, false) == null && running.Probe(Start, 0f, 12, false) == null,
                "unpaused render frames cannot replace fixed progress");
            Check(running.Probe(Start, 1f, 12, false) == null && running.Probe(Start, 2f, 12, false)!.Succeeded,
                "unpaused fixed epochs govern even with the same render identity");

            var switching = new NativeSpawnSettlement(Start, 2f, 0f, 10);
            Check(switching.Probe(Start, 1f, 11, false) == null && switching.Probe(Start, 1f, 11, true) == null,
                "pausing after a fixed sample cannot count the same rendered frame again");
            Check(switching.Probe(Start, 1f, 12, false) == null && switching.Probe(Start, 1f, 12, true)!.Succeeded,
                "resuming needs fixed progress while a later paused frame remains admissible");
            var budget = new NativeSpawnSettlement(Start, 2f, 0f, 0);
            for (var index = 1; index < 8; index++)
                Check(budget.Probe(Pose(index * 0.1f), index, index, index % 2 == 0) == null,
                    "mode changes retain the shared unstable sample budget");
            Check(budget.Probe(Pose(0.8f), 8f, 8, true)!.ErrorCode == ModErrorCode.External,
                "unstable eighth observation refuses across pause modes");
            var wrap = new NativeSpawnSettlement(Start, 2f, 0f, int.MaxValue);
            Check(wrap.Probe(actual, 1f, int.MaxValue, false) == null && wrap.Probe(actual, 1f, int.MinValue, true) == null
                && wrap.Probe(actual, 1f, int.MinValue + 1, true)!.Succeeded,
                "render counter is equality identity rather than signed ordering");

            Check(new NativeSpawnSettlement(Start, 2f, 0f, 10).Probe(Start, float.NaN, 10, true)!.ErrorCode == ModErrorCode.Unavailable,
                "duplicate paused frame still validates the fixed clock");
            Check(new NativeSpawnSettlement(Start, 2f, 1f, 10).Probe(Start, 0f, 10, true)!.ErrorCode == ModErrorCode.External,
                "duplicate paused frame still refuses fixed-clock regression");
            foreach (var invalid in new[] { new TransformState(new Vec3(0.011f, 0, 0), Start.Rotation, Start.Scale),
                Pose(-0.011f), Pose(2.001f), new TransformState(Start.Position, new Quat(0, 0.1f, 0, 0.9949874f), Start.Scale) })
                Check(new NativeSpawnSettlement(Start, 2f, 0f, 10).Probe(invalid, 0f, 10, true)!.ErrorCode == ModErrorCode.External,
                    "duplicate paused frame still enforces every placement guard");
            Console.WriteLine("Paused settlement policy passed.");
        }
        private static TransformState Pose(float height) => new TransformState(new Vec3(0, height, 0), Quat.Identity, new Vec3(1, 1, 1));
        private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Paused settlement: " + message); }
    }
}
