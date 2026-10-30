using System;
using System.Globalization;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static class NativeSpawnSettlementTests
    {
        private static readonly TransformState Start = Pose(0f, 0f, 0f);
        internal static void Run()
        {
            var settle = New();
            Check(settle.Probe(Start, 0f) == null && settle.Probe(Start, 0f) == null, "same fixed epoch cannot establish stability");
            Check(settle.Probe(Pose(0f, 0.4f, 0f), 1f) == null, "upward motion is observed without reteleport");
            Check(settle.Probe(Pose(0f, 0.4f, 0f), 2f) == null, "one stable epoch is insufficient");
            var ready = settle.Probe(Pose(0f, 0.4f, 0f), 3f);
            Check(ready != null && ready.Succeeded && ready.Value!.Position.Y == 0.4f, "exact settled actual pose is returned");
            var boundary = New(); var top = Pose(0f, 2f, 0f);
            Check(boundary.Probe(top, 1f) == null && boundary.Probe(top, 2f) == null && boundary.Probe(top, 3f)!.Succeeded, "actual height bound is inclusive");
            Refuses(Pose(0f, 2.001f, 0f), "above actual controller height");
            Refuses(Pose(0f, -0.011f, 0f), "downward beyond existing tolerance");
            var down = New();var smallDown=Pose(0f,-0.005f,0f);
            Check(down.Probe(smallDown,1f)==null && down.Probe(smallDown,2f)!.Succeeded, "existing small downward tolerance remains");
            Refuses(Pose(0.011f, 0.4f, 0f), "horizontal guard remains");
            Refuses(new TransformState(Start.Position, new Quat(0f, 0.1f, 0f, 0.9949874f), Start.Scale), "rotation dot guard remains");
            var nonfiniteRejected = false;
            try { Pose(float.NaN, 0f, 0f); } catch (ArgumentException) { nonfiniteRejected = true; }
            Check(nonfiniteRejected, "nonfinite transforms are rejected by the existing constructor before settlement");
            foreach (var height in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
                Check(new NativeSpawnSettlement(Start, height, 0f).Probe(Start, 1f)!.ErrorCode == ModErrorCode.Unavailable, "unsupported height refuses");
            Check(new NativeSpawnSettlement(Start, 2f, float.NaN).Probe(Start, 1f)!.ErrorCode == ModErrorCode.Unavailable, "unavailable fixed clock refuses");
            Check(New().Probe(Start,-1f)!.ErrorCode == ModErrorCode.External, "regressing clock refuses");
            var changing=New();for(var i=1;i<8;i++) Check(changing.Probe(Pose(0f,i*0.1f,0f),i)==null, "bounded changing observations wait");
            Check(changing.Probe(Pose(0f,0.8f,0f),8f)!.ErrorCode==ModErrorCode.External,"unstable eighth epoch refuses");
            var originalCulture=CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("fr-FR");
                var requested=Pose(12345f,23456f,34567f);var actual=Pose(12345f,23456.5f,34567f);
                var text=NativeSpawnSettlement.DescribeOffset(actual,requested,"accepted");
                Check(text.Contains("dy=0.5",StringComparison.Ordinal) && !text.Contains("12345",StringComparison.Ordinal)
                    && !text.Contains("23456",StringComparison.Ordinal) && !text.Contains("34567",StringComparison.Ordinal), "telemetry is invariant and relative only");
            }
            finally { CultureInfo.CurrentCulture=originalCulture; }
            Console.WriteLine("NativeSpawnSettlementTests passed: upward bound, fixed-epoch stability, downward/horizontal/rotation refusal, unsupported shapes, bounded failure and relative telemetry.");
        }
        private static NativeSpawnSettlement New() => new NativeSpawnSettlement(Start,2f,0f);
        private static TransformState Pose(float x,float y,float z) => new TransformState(new Vec3(x,y,z),new Quat(0,0,0,1),new Vec3(1,1,1));
        private static void Refuses(TransformState actual,string reason) => Check(New().Probe(actual,1f)!.ErrorCode==ModErrorCode.External,reason);
        private static void Check(bool value,string reason) { if(!value)throw new InvalidOperationException("Spawn settlement: "+reason); }
    }
}
