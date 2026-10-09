using System.Diagnostics;
using System.Text.Json;

namespace TopiaForge.Acceptance.Windows;

internal sealed class BrokerTranscript
{
    private readonly BrokerRequest request;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly List<object> events = new();
    private int bytes;
    internal List<string> Failures { get; } = new();
    internal bool InputReleased { get; set; } = true;
    internal bool FixtureReleased { get; set; } = true;
    internal List<object> Artifacts { get; } = new();
    internal BrokerTranscript(BrokerRequest request) => this.request = request;
    internal void Add(string scenario, int cycle, int stepIndex, string step, string kind, object data)
    {
        var item = new { sequence = events.Count + 1, scenarioId = scenario, cycle, stepIndex, step, kind, elapsedMilliseconds = clock.ElapsedMilliseconds, data };
        var projected = checked(bytes + JsonSerializer.SerializeToUtf8Bytes(item, BoundedJson.Options).Length);
        if (events.Count >= 11990 || projected > 126 * 1024 * 1024) throw new InvalidDataException("Broker transcript bound exceeded.");
        bytes = projected; events.Add(item);
    }
    internal void Failure(string scenario, int cycle, int stepIndex, string step, Exception error)
    {
        // Messages contain only local protocol errors; never attach game logs or input values.
        var code = error.GetType().Name + ":" + (error is AggregateException ? "cleanup-unconfirmed" : error.Message);
        if (code.Length > 512) code = code[..512];
        if (Failures.Count < 64 && !Failures.Contains(code)) Failures.Add(code);
        try { Add(scenario, cycle, stepIndex, step, "failure", new { code }); } catch (InvalidDataException) { }
    }
    internal void Artifact(string path, string sha256, long length)
    {
        if (Artifacts.Count >= 256) throw new InvalidDataException("Artifact inventory exceeds its bound.");
        Artifacts.Add(new { path, sha256, length });
    }
    internal void Save()
    {
        BoundedJson.WriteNew(BoundedJson.Child(request.OutputRoot, "broker-transcript.json"), new
        {
            schemaVersion = 1, kind = "sandbox-broker-transcript-v1", request.RunId, request.Challenge, request.ManagerSessionId,
            request.Process, request.Identity, events, InputReleased, FixtureReleased, failures = Failures
        });
        BoundedJson.WriteNew(BoundedJson.Child(request.OutputRoot, "broker-result.json"), new
        {
            schemaVersion = 1, kind = "sandbox-broker-result-v1", request.RunId, processId = Environment.ProcessId,
            transcript = new { path = "broker-transcript.json", sha256 = BoundedJson.Hash(Path.Combine(request.OutputRoot, "broker-transcript.json")) },
            artifacts = Artifacts, InputReleased, FixtureReleased, failures = Failures,
            // A broker result never includes acceptance verdicts.
            qualifiesRelease = false
        });
    }
}
