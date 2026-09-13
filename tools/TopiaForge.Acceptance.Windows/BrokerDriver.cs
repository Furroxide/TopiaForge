using System.Diagnostics;
using System.Text.Json;

namespace TopiaForge.Acceptance.Windows;

/// Actuation is restricted to this fixture's measured controls. Native progress
/// is a scheduling hint; only the separate verifier computes acceptance results.
internal sealed class BrokerDriver
{
    private readonly BrokerRequest request;
    private readonly DeviceProfile profile;
    private readonly DriverManifest manifest;
    private readonly BrokerTranscript transcript;
    private readonly OwnedGameProbe game;
    private readonly NativePipe pipe;
    private readonly WindowsInput input;
    private JsonElement reply;
    private string scenario = "routing", step = "prepare";
    private int cycle = 1, stepIndex, artifactIndex;
    private JsonElement Facts => reply.GetProperty("facts");
    internal BrokerDriver(BrokerRequest request, DeviceProfile profile, DriverManifest manifest, BrokerTranscript transcript, OwnedGameProbe game, NativePipe pipe, WindowsInput input)
    { this.request = request; this.profile = profile; this.manifest = manifest; this.transcript = transcript; this.game = game; this.pipe = pipe; this.input = input; }

    internal async Task Execute(CancellationToken cancellation)
    {
        Exception? failure = null;
        try
        {
            await pipe.Connect(cancellation);
            foreach (var id in DriverManifest.ScenarioIds)
                for (var currentCycle = 1; currentCycle <= DriverManifest.Cycles(id); currentCycle++)
                {
                    scenario = id; cycle = currentCycle; stepIndex = 0; step = "prepare";
                    using var scenarioDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    scenarioDeadline.CancelAfter(TimeSpan.FromMilliseconds(id == "catalog-editing" ? 7200000 : 180000));
                    var token = scenarioDeadline.Token;
                    transcript.FixtureReleased = false;
                    await Observe("prepare", token);
                    var preparation = Stopwatch.StartNew();
                    while (!Facts.GetProperty("prepared").GetBoolean())
                    {
                        if (preparation.Elapsed > TimeSpan.FromSeconds(120)) throw new TimeoutException("Fixture preparation did not complete.");
                        await Task.Delay(100, token); await Observe("capture", token);
                    }
                    await Barrier(2, token);
                    using var persistence = new PersistenceObserver(request.PersistentDataRoot, profile.PersistenceFiles);
                    RecordPersistence("before", persistence.Snapshot());
                    await Observe("begin", token);
                    Capture();
                    var actionCount = 0;
                    while (Text(Facts, "scenarioState") == "waiting")
                    {
                        if (++actionCount > 2048) throw new InvalidDataException("Scenario action bound exceeded.");
                        stepIndex = BoundedJson.Integer(Facts, "stepIndex", 0, 2048);
                        step = Text(Facts, "nextAction");
                        using var stepDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                        stepDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                        await Act(step, stepDeadline.Token);
                        await Barrier(2, stepDeadline.Token);
                        if (step is "run-graph" or "stop-graph") await CaptureAudio(stepDeadline.Token);
                        if (step is "open" or "reopen" or "hide" or "end-session" or "stop-world-session") Capture();
                        do
                        {
                            await Observe("advance", stepDeadline.Token);
                            if (Text(Facts, "scenarioState") != "waiting" || BoundedJson.Integer(Facts, "stepIndex", 0, 2048) != stepIndex) break;
                            await Task.Delay(100, stepDeadline.Token);
                        } while (true);
                    }
                    if (Text(Facts, "scenarioState") != "observed") throw new InvalidOperationException("Native progress stopped: " + Text(Facts, "scenarioState"));
                    await CleanupFixture(token);
                    await Task.Delay(200, token);
                    RecordPersistence("after", persistence.Snapshot());
                }
        }
        catch (Exception error) { failure = error; transcript.Failure(scenario, cycle, stepIndex, step, error); }
        finally
        {
            // Cancellation never skips release. An unresponsive peer still cannot
            // cause PID-based termination: the parent owns the original process.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            if (!transcript.FixtureReleased)
                try { await CleanupFixture(cleanup.Token); } catch (Exception error) { transcript.Failure(scenario, cycle, stepIndex, "cleanup", error); }
            try { input.Dispose(); } catch (Exception error) { transcript.Failure(scenario, cycle, stepIndex, "input-release", error); }
            transcript.InputReleased = input.Released;
            try { transcript.Add(scenario, cycle, stepIndex, "cleanup", "cleanup", new { inputReleased = transcript.InputReleased, fixtureReleased = transcript.FixtureReleased }); }
            catch (Exception error) { transcript.Failure(scenario, cycle, stepIndex, "cleanup-record", error); }
            finally { transcript.Save(); }
        }
        if (failure != null || transcript.Failures.Count != 0 || !transcript.InputReleased || !transcript.FixtureReleased)
            throw new InvalidOperationException("Broker run is incomplete; retained private evidence describes the failure.");
    }
    private async Task Observe(string operation, CancellationToken cancellation)
    {
        var exchange = await pipe.Exchange(operation, scenario, cycle, cancellation);
        reply = exchange.Response;
        transcript.Add(scenario, cycle, stepIndex, step, "observation", new { request = exchange.Request, response = exchange.Response });
        if (Facts.ValueKind != JsonValueKind.Object || !Facts.GetProperty("operationAccepted").GetBoolean()) throw new InvalidOperationException("Native fixture refused the bounded operation.");
    }
    private async Task Barrier(int minimumFrames, CancellationToken cancellation)
    {
        var frame = BoundedJson.Integer(reply, "frame", 0, int.MaxValue);
        do { await Task.Delay(50, cancellation); await Observe("capture", cancellation); }
        while (BoundedJson.Integer(reply, "frame", 0, int.MaxValue) - frame < minimumFrames);
    }
    private async Task Act(string action, CancellationToken cancellation)
    {
        // Dynamic labels/row IDs are captured once at the action boundary.
        var actionFacts = Facts;
        foreach (var atom in manifest.Action(action))
        {
            var kind = Text(atom, "kind");
            var surface = atom.TryGetProperty("surfaceId", out _) ? Text(atom, "surfaceId") : "sandbox-creator-window";
            var node = atom.TryGetProperty("nodeId", out _) ? Text(atom, "nodeId") : "";
            var frame = BoundedJson.Integer(reply, "frame", 0, int.MaxValue);
            var before = input.SentEvents;
            switch (kind)
            {
                case "key": await input.Tap(Text(atom, "key"), cancellation); break;
                case "click": await Click(surface, node, cancellation); break;
                case "replace-text":
                    await Click(surface, node, cancellation); await Barrier(2, cancellation);
                    var field = Widget(surface, node);
                    if (!field.GetProperty("focused").GetBoolean()) throw new InvalidOperationException("Text entry did not receive actual focus.");
                    var text = atom.TryGetProperty("textFromFact", out _) ? Text(actionFacts, Text(atom, "textFromFact")) : Text(atom, "text");
                    await input.ReplaceText(text, cancellation); break;
                case "select-list-item":
                    var row = atom.TryGetProperty("itemIdFromFact", out _) ? Text(actionFacts, Text(atom, "itemIdFromFact")) : Text(atom, "itemId");
                    await Click(surface, node + "/" + row, cancellation); break;
                case "barrier": await Barrier(BoundedJson.Integer(atom, "minimumFrames", 2, 10), cancellation); break;
                case "request": await Observe(Text(atom, "operation"), cancellation); break;
                case "capture": await Observe("capture", cancellation); break;
                default: throw new InvalidDataException("Undeclared actuation kind.");
            }
            transcript.Add(scenario, cycle, stepIndex, step, "input", new { action, kind, surfaceId = surface, nodeId = node, observedFrame = frame, sentEvents = input.SentEvents - before });
            await Barrier(2, cancellation);
        }
    }
    private async Task Click(string surface, string node, CancellationToken cancellation)
    {
        var widget = Widget(surface, node);
        await input.Click(widget.GetProperty("x").GetDouble(), widget.GetProperty("y").GetDouble(), widget.GetProperty("width").GetDouble(), widget.GetProperty("height").GetDouble(), cancellation);
    }
    private JsonElement Widget(string surface, string node)
    {
        if (node.Length > 768 || node.Any(char.IsControl)) throw new InvalidDataException("Invalid dynamic widget ID.");
        var ui = Facts.GetProperty("ui");
        if (BoundedJson.Integer(ui, "width", profile.Width, profile.Width) != profile.Width || BoundedJson.Integer(ui, "height", profile.Height, profile.Height) != profile.Height) throw new InvalidDataException("Native and Windows client geometry disagree.");
        var widgets = ui.GetProperty("widgets");
        if (widgets.GetArrayLength() > 2048) throw new InvalidDataException("Widget observation bound exceeded.");
        var found = widgets.EnumerateArray().Where(w => w.GetProperty("surfaceId").GetString() == surface && w.GetProperty("nodeId").GetString() == node).ToArray();
        if (found.Length != 1 || !found[0].GetProperty("visible").GetBoolean() || !found[0].GetProperty("enabled").GetBoolean() || found[0].GetProperty("clipped").GetBoolean()) throw new InvalidOperationException("Requested widget is absent, disabled, duplicated or clipped.");
        return found[0];
    }
    private void Capture()
    {
        var capture = ScreenCapture.Capture(game, profile, request.OutputRoot, "screen-" + (++artifactIndex).ToString("D4") + ".bmp");
        transcript.Artifact(capture.Path, capture.Sha256, capture.Length);
        transcript.Add(scenario, cycle, stepIndex, step, "capture", capture);
        if (capture.DistinctSampleColors < 8) throw new InvalidOperationException("Native screenshot is blank or unusable.");
    }
    private async Task CaptureAudio(CancellationToken cancellation)
    {
        var audio = await LoopbackCapture.Capture(game, profile, request.OutputRoot, "audio-" + (++artifactIndex).ToString("D4") + ".wav", 1000, cancellation);
        transcript.Artifact(audio.Path, audio.Sha256, audio.Length);
        transcript.Add(scenario, cycle, stepIndex, step, "audio", audio);
        await Observe("capture", cancellation);
    }
    private void RecordPersistence(string phase, PersistenceFact fact) => transcript.Add(scenario, cycle, stepIndex, step, "persistence", new { phase, fact.Files, fact.ChangedPaths, fact.Overflow, fact.UnexpectedWrite });
    private async Task CleanupFixture(CancellationToken cancellation)
    {
        await Observe("cleanup", cancellation); await Barrier(2, cancellation);
        transcript.FixtureReleased = !Facts.GetProperty("prepared").GetBoolean()
            && BoundedJson.Integer(Facts, "ownedObjectCount", 0, int.MaxValue) == 0
            && BoundedJson.Integer(Facts, "nativeCleanupPendingObjects", 0, int.MaxValue) == 0
            && Facts.GetProperty("nativeProps").GetArrayLength() == 0 && Facts.GetProperty("cleanupErrors").GetArrayLength() == 0;
        if (!transcript.FixtureReleased) throw new InvalidOperationException("Fixture-owned native resources did not release.");
    }
    private static string Text(JsonElement value, string name) => BoundedJson.Text(value, name, 1024);
}
