using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TopiaForge.SandboxAcceptance.Native
{
    internal sealed class SandboxNativeStep
    {
        internal readonly string Action;
        internal readonly string RowId;
        internal readonly string Label;
        internal SandboxNativeStep(string action, string rowId = "", string label = "") { Action = action; RowId = rowId; Label = label; }
    }
    // A progress observer, not a verdict generator. Only fresh independently captured facts can
    // advance a step. The external verifier still owns screenshots, OS-input and persistence evidence.
    internal sealed partial class SandboxNativeScenarios
    {
        internal static readonly string[] Ids = { "routing", "catalog-editing", "borrowed-robot", "source-unload", "hide-reopen",
            "persistence-refusal", "graph-rollback", "lifecycle-routes", "ten-cycles" };
        private readonly Dictionary<string, int> completed = new Dictionary<string, int>(StringComparer.Ordinal);
        private List<SandboxNativeStep> steps = new List<SandboxNativeStep>();
        private Dictionary<string, object?> baseline = new Dictionary<string, object?>();
        private Dictionary<string, object?> prior = new Dictionary<string, object?>();
        private Dictionary<string, object?>? preGraph;
        private Dictionary<string, object?>? opened;
        private string scenario = "";
        private int cycle;
        private int index;
        private long frame;
        private long beganAt;
        private long stepAt;
        private string state = "idle";
        private string reason = "";
        private bool selectingBorrowed;
        internal static int Cycles(string id) => id == "ten-cycles" ? 10 : id == "lifecycle-routes" ? 3 : 1;
        internal void Begin(string id, int requestedCycle, long observedFrame, long milliseconds, Dictionary<string, object?> facts)
        {
            if (state == "waiting") throw new InvalidDataException("The current scenario has not ended.");
            if (!Ids.Contains(id, StringComparer.Ordinal) || requestedCycle < 1 || requestedCycle > Cycles(id)) throw new InvalidDataException("Unknown scenario cycle.");
            var last = completed.TryGetValue(id, out var value) ? value : 0;
            if (requestedCycle != last + 1) throw new InvalidDataException("Missing, repeated or out-of-order cycle.");
            if (Text(facts, "targetId") != SandboxAcceptanceMod.SandboxTargetId || Text(facts, "sessionPhase") != "Running")
                throw new InvalidDataException("The actual Sandbox session must be running.");
            if (Text(facts, "worldSessionId").Length == 0) throw new InvalidDataException("No actual Worlds session identity.");
            steps = Expand(id, facts);
            scenario = id; cycle = requestedCycle; index = 0; frame = observedFrame; beganAt = stepAt = milliseconds;
            baseline = prior = facts; preGraph = null; opened = null; selectingBorrowed = false; reason = ""; state = "waiting";
        }
        internal bool Matches(string id, int requestedCycle) => scenario == id && cycle == requestedCycle && state == "waiting";
        internal string ExpectedAction => state == "waiting" ? steps[index].Action : "";
        internal void Advance(string id, int requestedCycle, long observedFrame, long milliseconds, Dictionary<string, object?> facts)
        {
            if (!Matches(id, requestedCycle)) throw new InvalidDataException("No matching active scenario.");
            if (observedFrame <= frame) { reason = "fresh-frame-required"; return; }
            if (milliseconds - beganAt > (scenario == "catalog-editing" ? 7200000 : 180000) || milliseconds - stepAt > 10000)
            { state = "failed"; reason = "native-postcondition-deadline-exceeded"; return; }
            try
            {
                if (Rows(facts, "cleanupErrors").Length != 0) { state = "failed"; reason = "fixture-cleanup-error"; return; }
                if (!Observe(steps[index].Action, facts)) { reason = "waiting-for-native-postcondition:" + steps[index].Action; return; }
                if (steps[index].Action == "open") opened = facts;
                if (steps[index].Action == "select-borrowed") selectingBorrowed = true;
                if (steps[index].Action == "run-graph") preGraph = prior;
                frame = observedFrame; stepAt = milliseconds; prior = facts; reason = ""; index++;
                if (index == steps.Count) { completed[scenario] = cycle; state = "observed"; }
            }
            catch (MissingObservation exception) { state = "unavailable"; reason = exception.Message; }
        }
        internal void Describe(Dictionary<string, object?> facts)
        {
            facts["scenarioState"] = state; facts["scenarioReason"] = reason; facts["stepIndex"] = index; facts["stepCount"] = steps.Count;
            facts["nextAction"] = ExpectedAction; facts["completedCycles"] = completed.TryGetValue(scenario, out var count) ? count : 0;
            facts["actionCatalogRowId"] = state == "waiting" ? steps[index].RowId : "";
            facts["actionCatalogDisplayName"] = state == "waiting" ? steps[index].Label : "";
        }
        private static List<SandboxNativeStep> Expand(string id, Dictionary<string, object?> facts)
        {
            var result = new List<SandboxNativeStep>();
            void Add(params string[] actions) { result.AddRange(actions.Select(a => new SandboxNativeStep(a))); }
            if (id == "persistence-refusal") { Add("observe-refusal"); return result; }
            Add("open");
            switch (id)
            {
                case "routing": Add("hide", "reopen", "end-session"); break;
                case "catalog-editing":
                    var entries = Maps(facts, "catalog").Concat(Maps(facts, "robotCatalog")).ToArray();
                    if (entries.Length == 0 || entries.Length > 256) throw new InvalidDataException("Admitted catalog must contain 1–256 entries.");
                    foreach (var entry in entries)
                    {
                        var row = Text(entry, "rowId"); var label = Text(entry, "displayName");
                        // Native vehicle absence is explicit metadata; custom validated vehicle adapters remain testable.
                        result.Add(new SandboxNativeStep("spawn-catalog", row, label));
                        var caps = Number(entry, "transformCapabilities");
                        if ((caps & 1) != 0) result.Add(new SandboxNativeStep("edit-transform", row, label));
                        if ((caps & 2) != 0) result.Add(new SandboxNativeStep("edit-rotation", row, label));
                        if ((caps & 4) != 0) result.Add(new SandboxNativeStep("edit-scale", row, label));
                        result.Add(new SandboxNativeStep("duplicate", row, label));
                        result.Add(new SandboxNativeStep("remove", row, label));
                        result.Add(new SandboxNativeStep("remove", row, label));
                    }
                    Add("end-session"); break;
                case "borrowed-robot": Add("select-borrowed", "edit-transform", "edit-personality", "edit-brain", "end-session"); break;
                case "source-unload": Add("spawn-prop", "duplicate", "unregister-source", "end-session"); break;
                case "hide-reopen": Add("spawn-prop", "hide", "move-player", "reopen", "end-session"); break;
                case "graph-rollback": Add("spawn-prop", "run-graph", "stop-graph", "end-session"); break;
                case "lifecycle-routes": Add("spawn-prop", "stop-world-session"); break;
                case "ten-cycles": Add("spawn-prop", "select-borrowed", "edit-transform", "edit-personality", "edit-brain", "hide", "move-player", "reopen", "run-graph", "stop-graph", "end-session"); break;
            }
            return result;
        }
        private sealed class MissingObservation : Exception { internal MissingObservation(string message) : base(message) { } }
        private static object Required(Dictionary<string, object?> map, string key) => map.TryGetValue(key, out var value) && value != null ? value : throw new MissingObservation("native-observation-unavailable:" + key);
        private static string Text(Dictionary<string, object?> map, string key) => Required(map, key) is string value ? value : throw new MissingObservation("native-string-unavailable:" + key);
        private static int Number(Dictionary<string, object?> map, string key) => Required(map, key) is int value ? value : throw new MissingObservation("native-counter-unavailable:" + key);
        private static bool Boolean(Dictionary<string, object?> map, string key) => Required(map, key) is bool value ? value : throw new MissingObservation("native-boolean-unavailable:" + key);
        private static Dictionary<string, object?> Map(Dictionary<string, object?> map, string key) => Required(map, key) as Dictionary<string, object?> ?? throw new MissingObservation("native-object-unavailable:" + key);
        private static object[] Rows(Dictionary<string, object?> map, string key) => Required(map, key) is IEnumerable values ? values.Cast<object>().ToArray() : throw new MissingObservation("native-array-unavailable:" + key);
        private static Dictionary<string, object?>[] Maps(Dictionary<string, object?> map, string key) => Rows(map, key).Select(value => value as Dictionary<string, object?> ?? throw new MissingObservation("native-row-unavailable:" + key)).ToArray();
        private static bool Same(object? a, object? b) => SandboxWireCodec.Serialize(a).SequenceEqual(SandboxWireCodec.Serialize(b));
    }
}
