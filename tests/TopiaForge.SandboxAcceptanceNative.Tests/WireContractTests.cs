using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.SandboxAcceptance;
using TopiaForge.SandboxAcceptance.Native;

// Wire codec, framing and the closed protocol-v2 operation vocabulary.
internal static class WireContractTests
{
    private static byte[] Request(string operation = "capture", string extra = "") => Encoding.UTF8.GetBytes(
        "{\"schemaVersion\":1,\"challenge\":\"" + Harness.Challenge + "\",\"sequence\":1,\"operation\":\"" + operation + "\",\"scenarioId\":\"routing\",\"cycle\":1" + extra + "}");

    internal static async Task Run()
    {
        Harness.Check(SandboxWireCodec.Parse(Request(), Harness.Challenge, 1).ScenarioId == "routing", "valid strict request");
        Harness.Reject(() => SandboxWireCodec.Parse(Request(extra: ",\"extra\":1"), Harness.Challenge, 1), "unknown field");
        Harness.Reject(() => SandboxWireCodec.Parse(Request(extra: ",\"sequence\":1"), Harness.Challenge, 1), "duplicate field");
        Harness.Reject(() => SandboxWireCodec.Parse(Request(), new string('b', 64), 1), "wrong challenge");
        Harness.Reject(() => SandboxWireCodec.Parse(Request(), Harness.Challenge, 2), "replay sequence");
        Harness.Reject(() => SandboxWireCodec.Parse(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Request()).Replace("\"cycle\":1", "\"cycle\":11")), Harness.Challenge, 1), "cycle bound");
        Harness.Reject(() => SandboxWireCodec.Parse(new byte[] { 0xff, 0xff }, Harness.Challenge, 1), "invalid UTF8");
        Harness.Reject(() => SandboxWireCodec.Serialize(double.NaN), "nonfinite fact");
        Harness.Reject(() => SandboxWireCodec.Serialize(new object()), "arbitrary object");
        Harness.Reject(() => SandboxWireCodec.Serialize(new string('x', 262144)), "reply byte bound");
        using (var data = new MemoryStream())
        {
            await SandboxPipeServer.WriteFrame(data, Request(), CancellationToken.None);
            data.Position = 0;
            Harness.Check((await SandboxPipeServer.ReadFrame(data, CancellationToken.None)).SequenceEqual(Request()), "byte framing roundtrip");
        }
        foreach (var invalid in new[] { new byte[] { 0, 0, 0, 0 }, new byte[] { 255, 255, 255, 127 }, new byte[] { 4, 0, 0, 0, 1 } })
        {
            try { await SandboxPipeServer.ReadFrame(new MemoryStream(invalid), CancellationToken.None); throw new Exception("accepted bad frame"); }
            catch (InvalidDataException) { Harness.Check(true, "bad frame rejected"); }
            catch (EndOfStreamException) { Harness.Check(true, "truncated frame rejected"); }
        }
        var config = JsonUtil.Deserialize<SandboxAcceptanceConfig>("{\"enabled\":true,\"challenge\":\"" + Harness.Challenge + "\"}");
        Harness.Check(config.Enabled && config.Challenge == Harness.Challenge, "lowercase config contract");
        Vocabulary();
    }

    private static void Vocabulary()
    {
        var manifest = File.ReadAllText(Harness.RepositoryFile(Path.Combine("tests", "TopiaForge.SandboxAcceptanceNative", "driver-actions-v2.json")));
        Harness.Check(manifest.Contains("\"kind\": \"sandbox-native-driver-actions-v2\"", StringComparison.Ordinal), "manifest is the broker's v2 manifest");
        var declared = ManifestOperations(manifest);
        Harness.Check(declared.Length == 19 && declared.SequenceEqual(SandboxWireCodec.Operations), "wire vocabulary equals the 19 manifest operations in order");
        Harness.Check(SandboxWireOperations.Lifecycle.Concat(SandboxWireOperations.Bounded).OrderBy(v => v, StringComparer.Ordinal)
            .SequenceEqual(SandboxWireOperations.All.OrderBy(v => v, StringComparer.Ordinal)), "lifecycle and bounded operations partition the vocabulary");
        foreach (var operation in SandboxWireOperations.Bounded)
        {
            Harness.Check(SandboxWireCodec.Parse(Request(operation), Harness.Challenge, 1).Operation == operation, "bounded operation parses: " + operation);
            Harness.Check(SandboxWireOperations.RequiredAction(operation) == (operation == "request-session-stop" ? "stop-world-session" : operation),
                "bounded operation maps to its observer action: " + operation);
        }
        foreach (var undeclared in new[] { "accessibility-scale-200", "external-write ", "Cleanup", "toggle", "" })
            Harness.Reject(() => SandboxWireCodec.Parse(Request(undeclared), Harness.Challenge, 1), "undeclared operation refused: '" + undeclared + "'");
        foreach (var lifecycle in SandboxWireOperations.Lifecycle)
            Harness.Reject(() => SandboxWireOperations.RequiredAction(lifecycle), "lifecycle operation has no bounded action: " + lifecycle);
        Harness.Check(!SandboxWireOperations.IsBounded("advance") && SandboxWireOperations.IsBounded("destroy-borrowed"), "bounded classification");
    }

    private static string[] ManifestOperations(string json)
    {
        var start = json.IndexOf("\"operations\"", StringComparison.Ordinal);
        var open = json.IndexOf('[', start);
        var close = json.IndexOf(']', open);
        return json.Substring(open + 1, close - open - 1).Split(',').Select(value => value.Trim().Trim('"')).Where(value => value.Length > 0).ToArray();
    }
}
