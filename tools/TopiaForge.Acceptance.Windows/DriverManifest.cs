using System.Text.Json;

namespace TopiaForge.Acceptance.Windows;

/// Reviewed v2 driver recipes. Scenario step lists declare per-cycle order for the
/// verifier; actuation stays bounded to the closed vocabulary regardless of native hints.
internal sealed class DriverManifest
{
    internal const string Kind = "sandbox-native-driver-actions-v2", RetiredKind = "sandbox-native-driver-actions-v1";
    internal static readonly string[] ScenarioIds = ["routing", "catalog-editing", "borrowed-robot", "source-unload", "hide-reopen", "persistence-refusal", "graph-rollback", "lifecycle-routes", "ten-cycles"];
    private static readonly Dictionary<string, int> CycleCounts = new(StringComparer.Ordinal) { ["routing"] = 2, ["catalog-editing"] = 1, ["borrowed-robot"] = 3, ["source-unload"] = 2, ["hide-reopen"] = 1, ["persistence-refusal"] = 1, ["graph-rollback"] = 2, ["lifecycle-routes"] = 3, ["ten-cycles"] = 10 };
    private readonly Dictionary<string, JsonElement[]> actions = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Id, int Cycle), string[]> steps = new();
    internal DriverManifest(string path) : this(BoundedJson.Read(path)) { }
    internal DriverManifest(JsonElement document)
    {
        if (document.ValueKind == JsonValueKind.Object && document.TryGetProperty("kind", out var declared) && declared.ValueKind == JsonValueKind.String && declared.GetString() == RetiredKind)
            throw new InvalidDataException("Driver manifest kind " + RetiredKind + " is retired; " + Kind + " (schemaVersion 2) is required.");
        BoundedJson.Keys(document, "schemaVersion", "kind", "targetId", "surfaceId", "geometryOrigin", "maximumStepMilliseconds", "maximumScenarioMilliseconds", "operations", "actions", "scenarios", "unavailable", "externalEvidenceRequired", "maximumCatalogScenarioMilliseconds");
        if (BoundedJson.Integer(document, "schemaVersion", 2, 2) != 2 || BoundedJson.Text(document, "kind") != Kind || BoundedJson.Text(document, "targetId") != "io.github.furroxide.topiaforge.sandbox.creator.menu" || BoundedJson.Text(document, "surfaceId") != "sandbox-creator-window" || BoundedJson.Text(document, "geometryOrigin") != "bottom-left") throw new InvalidDataException("Unexpected native driver identity.");
        _ = BoundedJson.Integer(document, "maximumStepMilliseconds", 10000, 10000);
        _ = BoundedJson.Integer(document, "maximumScenarioMilliseconds", 180000, 180000);
        _ = BoundedJson.Integer(document, "maximumCatalogScenarioMilliseconds", 7200000, 7200000);
        if (!Strings(document, "operations", 32).SequenceEqual(DriverVocabulary.WireOperations)) throw new InvalidDataException("Driver operations differ from the closed wire vocabulary.");
        _ = Strings(document, "unavailable", 32); _ = Strings(document, "externalEvidenceRequired", 32);
        var scenarios = document.GetProperty("scenarios").EnumerateArray().ToArray();
        if (!scenarios.Select(s => BoundedJson.Text(s, "id")).SequenceEqual(ScenarioIds)) throw new InvalidDataException("Driver scenarios are missing or reordered.");
        foreach (var scenario in scenarios) ReadScenario(scenario);
        foreach (var action in document.GetProperty("actions").EnumerateObject())
        {
            if (!DriverVocabulary.Actions.Contains(action.Name) || action.Value.ValueKind != JsonValueKind.Array || action.Value.GetArrayLength() is < 1 or > 12) throw new InvalidDataException("Undeclared or oversized action.");
            var rows = action.Value.EnumerateArray().ToArray();
            foreach (var row in rows) DriverVocabulary.ValidateStep(row);
            actions.Add(action.Name, rows);
        }
        if (!DriverVocabulary.Actions.All(actions.ContainsKey)) throw new InvalidDataException("Driver action inventory is incomplete.");
    }
    private void ReadScenario(JsonElement scenario)
    {
        var id = BoundedJson.Text(scenario, "id");
        var perCycle = scenario.TryGetProperty("cycleSteps", out _);
        var keys = new List<string> { "id", "cycles", perCycle ? "cycleSteps" : "steps" };
        if (id == "catalog-editing") keys.Add("inventoryExpansion");
        if (id == "lifecycle-routes") keys.AddRange(["routes", "freshSessionPerCycle"]);
        BoundedJson.Keys(scenario, keys.ToArray());
        var cycles = BoundedJson.Integer(scenario, "cycles", Cycles(id), Cycles(id));
        if (perCycle)
        {
            var entries = scenario.GetProperty("cycleSteps").EnumerateArray().ToArray();
            if (entries.Length != cycles) throw new InvalidDataException("Per-cycle steps must cover every declared cycle.");
            for (var index = 0; index < entries.Length; index++)
            {
                BoundedJson.Keys(entries[index], "cycle", "steps");
                if (BoundedJson.Integer(entries[index], "cycle", 1, cycles) != index + 1) throw new InvalidDataException("Per-cycle steps are out of order.");
                steps.Add((id, index + 1), ReadSteps(id, entries[index]));
            }
        }
        else { var shared = ReadSteps(id, scenario); for (var cycle = 1; cycle <= cycles; cycle++) steps.Add((id, cycle), shared); }
        if (id == "catalog-editing") ReadInventoryExpansion(scenario.GetProperty("inventoryExpansion"));
        if (id != "lifecycle-routes") return;
        if (scenario.GetProperty("freshSessionPerCycle").ValueKind != JsonValueKind.True) throw new InvalidDataException("Lifecycle routes require fresh sessions.");
        var routes = scenario.GetProperty("routes").EnumerateArray().ToArray();
        if (routes.Length != cycles) throw new InvalidDataException("Lifecycle routes must cover every cycle.");
        for (var index = 0; index < routes.Length; index++)
        {
            BoundedJson.Keys(routes[index], "cycle", "operation", "method");
            if (BoundedJson.Integer(routes[index], "cycle", 1, cycles) != index + 1 || BoundedJson.Text(routes[index], "operation") != "request-session-stop") throw new InvalidDataException("Undeclared lifecycle route.");
            _ = BoundedJson.Text(routes[index], "method", 64);
        }
    }
    private static string[] ReadSteps(string id, JsonElement owner)
    {
        var list = Strings(owner, "steps", 32);
        foreach (var step in list) if (!DriverVocabulary.Actions.Contains(step) && !(id == "catalog-editing" && step == "$catalog")) throw new InvalidDataException("Undeclared driver action.");
        return list;
    }
    private static void ReadInventoryExpansion(JsonElement expansion)
    {
        BoundedJson.Keys(expansion, "facts", "maximumEntries", "perEntry", "rowFact", "labelFact");
        if (!Strings(expansion, "facts", 2).SequenceEqual(["catalog", "robotCatalog"]) || BoundedJson.Integer(expansion, "maximumEntries", 256, 256) != 256 || BoundedJson.Text(expansion, "rowFact") != "actionCatalogRowId" || BoundedJson.Text(expansion, "labelFact") != "actionCatalogDisplayName") throw new InvalidDataException("Undeclared catalog expansion.");
        var perEntry = expansion.GetProperty("perEntry").EnumerateArray().ToArray();
        if (perEntry.Length is < 1 or > 12) throw new InvalidDataException("Catalog expansion bound exceeded.");
        foreach (var item in perEntry)
        {
            if (item.ValueKind == JsonValueKind.String) { if (!DriverVocabulary.Actions.Contains(item.GetString())) throw new InvalidDataException("Undeclared driver action."); continue; }
            BoundedJson.Keys(item, "ifTransformCapability", "action");
            if (BoundedJson.Integer(item, "ifTransformCapability", 1, 4) is not (1 or 2 or 4) || !DriverVocabulary.Actions.Contains(BoundedJson.Text(item, "action"))) throw new InvalidDataException("Undeclared conditional catalog action.");
        }
    }
    private static string[] Strings(JsonElement owner, string name, int maximum)
    {
        var field = owner.GetProperty(name);
        if (field.ValueKind != JsonValueKind.Array || field.GetArrayLength() < 1 || field.GetArrayLength() > maximum) throw new InvalidDataException("Invalid list: " + name);
        return field.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 and <= 256 } text && !text.Any(char.IsControl) ? text : throw new InvalidDataException("Invalid list entry: " + name)).ToArray();
    }
    internal static int Cycles(string id) => CycleCounts.TryGetValue(id, out var count) ? count : throw new InvalidDataException("Unknown scenario.");
    internal JsonElement[] Action(string name) => actions.TryGetValue(name, out var rows) ? rows : throw new InvalidDataException("Observer requested an undeclared action.");
    internal string[] Steps(string id, int cycle) => steps.TryGetValue((id, cycle), out var list) ? list : throw new InvalidDataException("Undeclared scenario cycle.");
}
