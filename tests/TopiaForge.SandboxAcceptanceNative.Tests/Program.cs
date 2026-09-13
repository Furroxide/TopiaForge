using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.SandboxAcceptance;
using TopiaForge.SandboxAcceptance.Native;

internal static class Program
{
    private static readonly string Challenge = new string('a', 64);
    private static int checks;
    private static void Check(bool condition, string label) { checks++; if (!condition) throw new Exception(label); }
    private static void Reject(Action action, string label) { try { action(); } catch { checks++; return; } throw new Exception("Not rejected: " + label); }
    private static byte[] Request(string extra = "") => Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"challenge\":\"" + Challenge + "\",\"sequence\":1,\"operation\":\"capture\",\"scenarioId\":\"routing\",\"cycle\":1" + extra + "}");
    public static async Task<int> Main()
    {
        Check(SandboxWireCodec.Parse(Request(), Challenge, 1).ScenarioId == "routing", "valid strict request");
        Reject(() => SandboxWireCodec.Parse(Request(",\"extra\":1"), Challenge, 1), "unknown field");
        Reject(() => SandboxWireCodec.Parse(Request(",\"sequence\":1"), Challenge, 1), "duplicate field");
        Reject(() => SandboxWireCodec.Parse(Request(), new string('b', 64), 1), "wrong challenge");
        Reject(() => SandboxWireCodec.Parse(Request(), Challenge, 2), "replay sequence");
        Reject(() => SandboxWireCodec.Parse(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Request()).Replace("\"cycle\":1", "\"cycle\":11")), Challenge, 1), "cycle bound");
        Reject(() => SandboxWireCodec.Parse(new byte[] { 0xff, 0xff }, Challenge, 1), "invalid UTF8");
        Reject(() => SandboxWireCodec.Serialize(double.NaN), "nonfinite fact");
        Reject(() => SandboxWireCodec.Serialize(new object()), "arbitrary object");
        Reject(() => SandboxWireCodec.Serialize(new string('x', 262144)), "reply byte bound");
        using (var data = new MemoryStream())
        { await SandboxPipeServer.WriteFrame(data, Request(), CancellationToken.None); data.Position = 0;
          Check((await SandboxPipeServer.ReadFrame(data, CancellationToken.None)).SequenceEqual(Request()), "byte framing roundtrip"); }
        foreach (var invalid in new[] { new byte[] { 0, 0, 0, 0 }, new byte[] { 255, 255, 255, 127 }, new byte[] { 4, 0, 0, 0, 1 } })
        { try { await SandboxPipeServer.ReadFrame(new MemoryStream(invalid), CancellationToken.None); throw new Exception("accepted bad frame"); }
          catch (InvalidDataException) { checks++; } catch (EndOfStreamException) { checks++; } }
        var config = JsonUtil.Deserialize<SandboxAcceptanceConfig>("{\"enabled\":true,\"challenge\":\"" + Challenge + "\"}");
        Check(config.Enabled && config.Challenge == Challenge, "lowercase config contract");
        var scenario = new SandboxNativeScenarios(); var baseline = Facts();
        Reject(() => scenario.Begin("ten-cycles", 2, 10, 100, baseline), "missing intermediate cycle");
        scenario.Begin("persistence-refusal", 1, 10, 100, baseline);
        scenario.Advance("persistence-refusal", 1, 10, 101, baseline);
        var describe = new Dictionary<string, object?>(); scenario.Describe(describe);
        Check((string)describe["scenarioState"]! == "waiting", "same frame cannot advance");
        scenario.Advance("persistence-refusal", 1, 11, 102, baseline); scenario.Describe(describe);
        Check((string)describe["scenarioState"]! == "observed", "actual refusal fields observed");
        Reject(() => scenario.Begin("persistence-refusal", 1, 12, 103, baseline), "duplicate completed cycle");
        var missing = new SandboxNativeScenarios(); var absent = Facts(); absent.Remove("persistenceIsolationAvailable");
        missing.Begin("persistence-refusal", 1, 10, 100, absent); missing.Advance("persistence-refusal", 1, 11, 101, absent); missing.Describe(describe);
        Check((string)describe["scenarioState"]! == "unavailable", "missing native field does not become zero");
        var deadline = new SandboxNativeScenarios(); deadline.Begin("persistence-refusal", 1, 10, 100, Facts()); deadline.Advance("persistence-refusal", 1, 11, 10101, Facts()); deadline.Describe(describe);
        Check((string)describe["scenarioState"]! == "failed", "native step deadline enforced");
        var routing = new SandboxNativeScenarios(); routing.Begin("routing", 1, 10, 100, Facts()); routing.Advance("routing", 1, 11, 101, Facts()); routing.Describe(describe);
        Check((int)describe["stepIndex"]! == 0, "advance is not an instruction acknowledgement");
        var catalog = new SandboxNativeScenarios(); var inventory = Facts(); inventory["catalog"] = new object[] { new Dictionary<string, object?> { ["rowId"] = "content:test:prop", ["displayName"] = "Synthetic", ["kind"] = "Prop", ["transformCapabilities"] = 7 } }; inventory["robotCatalog"] = Array.Empty<object>();
        catalog.Begin("catalog-editing", 1, 10, 100, inventory); catalog.Describe(describe);
        Check((int)describe["stepCount"]! == 9, "catalog inventory expands every supported transform and removal");
        Console.WriteLine("Sandbox native protocol/progress contract checks: " + checks + " passed. Synthetic facts are not native game evidence."); return 0;
    }
    private static Dictionary<string, object?> Facts() => new Dictionary<string, object?> { ["targetId"] = SandboxAcceptanceMod.SandboxTargetId,
        ["worldSessionId"] = "synthetic-contract-session", ["sessionPhase"] = "Running", ["cleanupErrors"] = Array.Empty<object>(),
        ["mutationSafetyState"] = "Unavailable", ["persistenceIsolationAvailable"] = false, ["creatorSessionCount"] = 0,
        ["activeHostId"] = "io.github.furroxide.topiaforge.sandbox", ["ui"] = new Dictionary<string, object?> { ["widgets"] = Array.Empty<object>() } };
}
