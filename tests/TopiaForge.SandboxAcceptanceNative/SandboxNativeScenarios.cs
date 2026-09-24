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
        private readonly HashSet<int> graphAudioIds = new HashSet<int>();
        private List<SandboxNativeStep> steps = new List<SandboxNativeStep>();
        private Dictionary<string, object?> baseline = new Dictionary<string, object?>();
        private Dictionary<string, object?> prior = new Dictionary<string, object?>();
        private Dictionary<string, object?>? preGraph;
        private Dictionary<string, object?>? opened;
        private Dictionary<string, object?>? externalWrite;
        private string scenario = "";
        private int cycle;
        private int index;
        private long frame;
        private long beganAt;
        private long stepAt;
        private string state = "idle";
        private string reason = "";
        private bool selectingBorrowed;
        private int lastAddedInstanceId;
        private int previewedPersonalityId;
        internal static int Cycles(string id)
        {
            switch (id)
            {
                case "routing": case "source-unload": case "graph-rollback": return 2;
                case "borrowed-robot": case "lifecycle-routes": return 3;
                case "ten-cycles": return 10;
                default: return 1;
            }
        }
        internal IReadOnlyList<SandboxNativeStep> Steps => steps;
        internal void Begin(string id, int requestedCycle, long observedFrame, long milliseconds, Dictionary<string, object?> facts)
        {
            if (state == "waiting") throw new InvalidDataException("The current scenario has not ended.");
            if (!Ids.Contains(id, StringComparer.Ordinal) || requestedCycle < 1 || requestedCycle > Cycles(id)) throw new InvalidDataException("Unknown scenario cycle.");
            var last = completed.TryGetValue(id, out var value) ? value : 0;
            if (requestedCycle != last + 1) throw new InvalidDataException("Missing, repeated or out-of-order cycle.");
            if (Text(facts, "targetId") != SandboxAcceptanceMod.SandboxTargetId || Text(facts, "sessionPhase") != "Running")
                throw new InvalidDataException("The actual Sandbox session must be running.");
            if (Text(facts, "worldSessionId").Length == 0) throw new InvalidDataException("No actual Worlds session identity.");
            steps = Expand(id, requestedCycle, facts);
            scenario = id; cycle = requestedCycle; index = 0; frame = observedFrame; beganAt = stepAt = milliseconds;
            baseline = prior = facts; preGraph = null; opened = null; externalWrite = null; selectingBorrowed = false;
            lastAddedInstanceId = 0; previewedPersonalityId = 0; graphAudioIds.Clear(); reason = ""; state = "waiting";
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
                TrackGraphAudio(facts);
                var action = steps[index].Action;
                if (!Observe(action, facts)) { reason = "waiting-for-native-postcondition:" + action; return; }
                Record(action, facts);
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
            // Measured identities the verifier recomputes independently: graph audio hosts seen while the cycle's
            // graph ran and the personality asset previewed on the borrowed robot.
            facts["graphAudioIds"] = graphAudioIds.OrderBy(value => value).ToArray();
            facts["previewedPersonalityId"] = previewedPersonalityId;
        }
        // Step-boundary bookkeeping; only a satisfied postcondition reaches here.
        private void Record(string action, Dictionary<string, object?> facts)
        {
            if (action == "open") opened = facts;
            if (action == "select-borrowed") selectingBorrowed = true;
            if (action == "run-graph") preGraph = prior;
            if (action == "external-write") externalWrite = facts;
            if (action == "edit-personality") previewedPersonalityId = Number(Map(Map(facts, "borrowedRobot"), "brain"), "hackedPersonalityId");
        }
        private void TrackGraphAudio(Dictionary<string, object?> facts)
        {
            if (!facts.TryGetValue("graphAudioSources", out var value) || !(value is IEnumerable rows)) return;
            foreach (var row in rows)
                if (row is Dictionary<string, object?> map && map.TryGetValue("instanceId", out var id) && id is int instanceId) graphAudioIds.Add(instanceId);
        }
        private sealed class MissingObservation : Exception { internal MissingObservation(string message) : base(message) { } }
    }
}
