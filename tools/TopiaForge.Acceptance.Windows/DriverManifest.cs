using System.Text.Json;

namespace TopiaForge.Acceptance.Windows;

internal sealed class DriverManifest
{
    internal static readonly string[] ScenarioIds = ["routing", "catalog-editing", "borrowed-robot", "source-unload", "hide-reopen", "persistence-refusal", "graph-rollback", "lifecycle-routes", "ten-cycles"];
    private static readonly string[] AllowedActions = ["open", "hide", "reopen", "move-player", "spawn-prop", "spawn-catalog", "duplicate", "remove", "edit-transform", "edit-rotation", "edit-scale", "select-borrowed", "edit-personality", "edit-brain", "run-graph", "stop-graph", "end-session", "unregister-source", "stop-world-session", "observe-refusal"];
    private static readonly string[] AllowedNodes = ["hide-workbench", "catalog-search", "catalog-list", "spawn-selected", "duplicate-selected", "remove-selected", "nudge-up", "rotation-y", "rotation-w", "scale-x", "scale-y", "scale-z", "apply-transform", "refresh-native", "roster-list", "persona-name", "persona-instructions", "apply-personality", "brain-dormant", "project-list", "load-project", "run-project", "stop-project", "end-session", "confirm"];
    private readonly Dictionary<string, JsonElement[]> actions;
    internal DriverManifest(string path)
    {
        var document = BoundedJson.Read(path);
        BoundedJson.Keys(document, "schemaVersion", "kind", "targetId", "surfaceId", "geometryOrigin", "maximumStepMilliseconds", "maximumScenarioMilliseconds", "operations", "actions", "scenarios", "unavailable", "externalEvidenceRequired", "maximumCatalogScenarioMilliseconds");
        if (BoundedJson.Integer(document, "schemaVersion", 1, 1) != 1 || BoundedJson.Text(document, "kind") != "sandbox-native-driver-actions-v1" || BoundedJson.Text(document, "targetId") != "io.github.furroxide.topiaforge.sandbox.creator.menu" || BoundedJson.Text(document, "surfaceId") != "sandbox-creator-window" || BoundedJson.Text(document, "geometryOrigin") != "bottom-left") throw new InvalidDataException("Unexpected native driver identity.");
        _ = BoundedJson.Integer(document, "maximumStepMilliseconds", 10000, 10000);
        _ = BoundedJson.Integer(document, "maximumScenarioMilliseconds", 180000, 180000);
        var scenarios = document.GetProperty("scenarios").EnumerateArray().ToArray();
        if (!scenarios.Select(s => BoundedJson.Text(s, "id")).SequenceEqual(ScenarioIds)) throw new InvalidDataException("Driver scenarios are missing or reordered.");
        foreach (var scenario in scenarios)
        {
            BoundedJson.Keys(scenario, new[] { "id", "cycles", "steps" }.Concat(scenario.TryGetProperty("inventoryExpansion", out _) ? new[] { "inventoryExpansion" } : Array.Empty<string>()).Concat(scenario.TryGetProperty("routes", out _) ? new[] { "routes", "freshSessionPerCycle" } : Array.Empty<string>()).ToArray());
            var id = BoundedJson.Text(scenario, "id");
            _ = BoundedJson.Integer(scenario, "cycles", Cycles(id), Cycles(id));
            if (scenario.GetProperty("steps").GetArrayLength() is < 1 or > 32) throw new InvalidDataException("Scenario step bound exceeded.");
            foreach (var step in scenario.GetProperty("steps").EnumerateArray()) if (!AllowedActions.Contains(step.GetString()) && !(id == "catalog-editing" && step.GetString() == "$catalog")) throw new InvalidDataException("Undeclared driver action.");
        }
        actions = new(StringComparer.Ordinal);
        foreach (var action in document.GetProperty("actions").EnumerateObject())
        {
            if (!AllowedActions.Contains(action.Name) || action.Value.GetArrayLength() is < 1 or > 12) throw new InvalidDataException("Undeclared or oversized action.");
            var rows = action.Value.EnumerateArray().ToArray();
            foreach (var row in rows) ValidateStep(row);
            actions.Add(action.Name, rows);
        }
        if (!AllowedActions.All(actions.ContainsKey)) throw new InvalidDataException("Driver action inventory is incomplete.");
    }
    internal static int Cycles(string id) => id == "ten-cycles" ? 10 : id == "lifecycle-routes" ? 3 : 1;
    internal JsonElement[] Action(string name) => actions.TryGetValue(name, out var rows) ? rows : throw new InvalidDataException("Observer requested an undeclared action.");
    internal static void ValidateStep(JsonElement step)
    {
        var kind = BoundedJson.Text(step, "kind", 32);
        var optionalSurface = step.TryGetProperty("surfaceId", out _) ? new[] { "surfaceId" } : Array.Empty<string>();
        if (optionalSurface.Length != 0 && !new[] { "sandbox-creator-window", "$modal" }.Contains(BoundedJson.Text(step, "surfaceId"))) throw new InvalidDataException("Undeclared surface.");
        void Keys(params string[] fields) => BoundedJson.Keys(step, new[] { "kind" }.Concat(fields).Concat(optionalSurface).ToArray());
        void Node() { if (!AllowedNodes.Contains(BoundedJson.Text(step, "nodeId"))) throw new InvalidDataException("Undeclared widget."); }
        switch (kind)
        {
            case "key": Keys("key"); if (!new[] { "F5", "Escape", "Tab", "W" }.Contains(BoundedJson.Text(step, "key"))) throw new InvalidDataException("Undeclared key."); break;
            case "click": Keys("nodeId"); Node(); break;
            case "replace-text":
                var textKey = step.TryGetProperty("textFromFact", out _) ? "textFromFact" : "text";
                Keys("nodeId", textKey); Node();
                if (textKey == "textFromFact" && BoundedJson.Text(step, textKey) != "actionCatalogDisplayName") throw new InvalidDataException("Undeclared dynamic text.");
                _ = BoundedJson.Text(step, textKey, 1024); break;
            case "select-list-item":
                var itemKey = step.TryGetProperty("itemIdFromFact", out _) ? "itemIdFromFact" : "itemId";
                Keys("nodeId", itemKey); Node();
                if (itemKey == "itemIdFromFact" && !new[] { "borrowedRosterId", "projectId", "actionCatalogRowId" }.Contains(BoundedJson.Text(step, itemKey))) throw new InvalidDataException("Undeclared dynamic row.");
                _ = BoundedJson.Text(step, itemKey, 512); break;
            case "barrier": Keys("minimumFrames"); _ = BoundedJson.Integer(step, "minimumFrames", 2, 10); break;
            case "request": Keys("operation"); if (!new[] { "unregister-source", "request-session-stop" }.Contains(BoundedJson.Text(step, "operation"))) throw new InvalidDataException("Undeclared lifecycle request."); break;
            case "capture": Keys(); break;
            default: throw new InvalidDataException("Undeclared input operation.");
        }
    }
}
