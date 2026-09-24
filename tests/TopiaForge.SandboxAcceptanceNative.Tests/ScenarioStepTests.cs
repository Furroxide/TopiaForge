using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.SandboxAcceptance.Native;

// Cycle counts and the exact per-cycle step recipes of spec section 4.
internal static class ScenarioStepTests
{
    private static readonly (string Id, int Cycle, string[] Steps)[] Recipes =
    {
        ("routing", 1, new[] { "open", "accessibility-high-contrast", "accessibility-scale-150", "accessibility-reduced-motion", "accessibility-reset", "focus-next", "hide-f5", "reopen", "duplicate-toggle", "end-session" }),
        ("routing", 2, new[] { "register-competing-host", "open", "hide-f5", "reopen", "end-session", "unregister-competing-host" }),
        ("borrowed-robot", 1, new[] { "open", "select-borrowed", "edit-transform", "edit-personality", "edit-brain", "end-session" }),
        ("borrowed-robot", 2, new[] { "open", "select-borrowed", "edit-transform", "edit-personality", "external-write", "end-session" }),
        ("borrowed-robot", 3, new[] { "open", "select-borrowed", "edit-transform", "destroy-borrowed", "end-session" }),
        ("source-unload", 1, new[] { "open", "spawn-prop", "duplicate", "spawn-character", "spawn-control-robot", "unregister-source", "end-session", "despawn-control-robot" }),
        ("source-unload", 2, new[] { "open", "spawn-prop", "run-graph", "unregister-source", "end-session" }),
        ("hide-reopen", 1, new[] { "open", "spawn-prop", "select-borrowed", "edit-transform", "run-graph", "move-while-visible", "hide-close", "move-player", "camera-hidden", "reopen", "text-focus", "hide-f5", "reopen", "stop-graph", "end-session" }),
        ("persistence-refusal", 1, new[] { "observe-refusal" }),
        ("graph-rollback", 1, new[] { "open", "spawn-prop", "control-cue", "run-graph", "hide-f5", "aim-graph-prop", "interact", "reopen", "stop-graph", "stop-control-cue", "end-session" }),
        ("graph-rollback", 2, new[] { "open", "spawn-prop", "stop-before-start", "run-graph", "stop-graph", "run-graph", "stop-graph", "end-session" }),
        ("lifecycle-routes", 1, new[] { "open", "spawn-prop", "stop-world-session" }),
        ("lifecycle-routes", 2, new[] { "open", "spawn-prop", "stop-world-session", "toggle-during-transition", "move-player" }),
        ("lifecycle-routes", 3, new[] { "open", "spawn-prop", "stop-world-session", "toggle-in-menu" }),
        ("ten-cycles", 1, new[] { "open", "spawn-prop", "select-borrowed", "edit-transform", "edit-personality", "edit-brain", "hide-f5", "move-player", "camera-hidden", "reopen", "run-graph", "stop-graph", "end-session" })
    };
    private static readonly string[] PerEntry = { "spawn-catalog", "edit-transform", "edit-rotation", "edit-scale", "duplicate", "undo", "remove" };

    internal static void Run()
    {
        var cycles = new Dictionary<string, int> { ["routing"] = 2, ["catalog-editing"] = 1, ["borrowed-robot"] = 3, ["source-unload"] = 2, ["hide-reopen"] = 1,
            ["persistence-refusal"] = 1, ["graph-rollback"] = 2, ["lifecycle-routes"] = 3, ["ten-cycles"] = 10 };
        Harness.Check(SandboxNativeScenarios.Ids.OrderBy(v => v, StringComparer.Ordinal).SequenceEqual(cycles.Keys.OrderBy(v => v, StringComparer.Ordinal)), "nine scenario ids");
        foreach (var pair in cycles) Harness.Check(SandboxNativeScenarios.Cycles(pair.Key) == pair.Value, "cycle count " + pair.Key);
        var facts = new SyntheticScene().Snapshot();
        foreach (var recipe in Recipes)
        {
            var actual = SandboxNativeScenarios.Expand(recipe.Id, recipe.Cycle, facts).Select(step => step.Action).ToArray();
            Harness.Check(actual.SequenceEqual(recipe.Steps), recipe.Id + " cycle " + recipe.Cycle + " recipe: " + string.Join(",", actual));
        }
        for (var cycle = 2; cycle <= 10; cycle++)
            Harness.Check(SandboxNativeScenarios.Expand("ten-cycles", cycle, facts).Select(s => s.Action).SequenceEqual(Recipes.Last().Steps), "ten-cycles cycle " + cycle + " repeats the full cycle");
        Catalog(facts);
        Harness.Reject(() => SandboxNativeScenarios.Expand("vehicles", 1, facts), "unknown scenario");
        Lifecycle(facts);
    }

    private static void Catalog(Dictionary<string, object?> facts)
    {
        var steps = SandboxNativeScenarios.Expand("catalog-editing", 1, facts);
        var actions = steps.Select(s => s.Action).ToArray();
        // Synthetic inventory: the prop supports every transform (7 steps), the robot only position (5 steps).
        var expected = new[] { "open", "search-nonmatching", "filter-robots", "filter-all" }.Concat(PerEntry)
            .Concat(new[] { "spawn-catalog", "edit-transform", "duplicate", "undo", "remove", "end-session" }).ToArray();
        Harness.Check(actions.SequenceEqual(expected), "catalog per-entry loop: " + string.Join(",", actions));
        Harness.Check(steps[4].RowId == SyntheticScene.PropRow && steps[4].Label == "Acceptance prop", "catalog step carries the row id and label");
        Harness.Check(steps[11].RowId == SyntheticScene.RobotRow && steps[11].Label == "Acceptance bot", "robot catalog step carries its row");
        Harness.Check(steps[0].RowId == "" && steps.Last().RowId == "", "fixed steps carry no row");
        var empty = Harness.Clone(facts); empty["catalog"] = new List<object?>(); empty["robotCatalog"] = new List<object?>();
        Harness.Reject(() => SandboxNativeScenarios.Expand("catalog-editing", 1, empty), "empty admitted catalog");
        var scenarios = new SandboxNativeScenarios(); scenarios.Begin("catalog-editing", 1, 10, 100, facts);
        var described = new Dictionary<string, object?>(); scenarios.Describe(described);
        Harness.Check((int)described["stepCount"]! == expected.Length && (string)described["nextAction"]! == "open", "begin serves the expanded recipe");
        Harness.Check(described["graphAudioIds"] is int[] ids && ids.Length == 0 && (int)described["previewedPersonalityId"]! == 0, "measured identities start empty");
    }

    private static void Lifecycle(Dictionary<string, object?> facts)
    {
        var scenarios = new SandboxNativeScenarios();
        Harness.Reject(() => scenarios.Begin("routing", 3, 10, 100, facts), "cycle beyond the recipe count");
        Harness.Reject(() => scenarios.Begin("ten-cycles", 2, 10, 100, facts), "missing intermediate cycle");
        Harness.Reject(() => scenarios.Begin("borrowed-robot", 0, 10, 100, facts), "zero cycle");
        var idle = Harness.Clone(facts); idle["sessionPhase"] = "Idle"; idle["worldSessionId"] = "";
        Harness.Reject(() => scenarios.Begin("routing", 1, 10, 100, idle), "begin requires the running Sandbox target");
        scenarios.Begin("persistence-refusal", 1, 10, 100, facts);
        scenarios.Advance("persistence-refusal", 1, 10, 101, facts);
        var described = new Dictionary<string, object?>(); scenarios.Describe(described);
        Harness.Check((string)described["scenarioState"]! == "waiting" && (string)described["scenarioReason"]! == "fresh-frame-required", "same frame cannot advance");
        scenarios.Advance("persistence-refusal", 1, 11, 102, facts); scenarios.Describe(described);
        Harness.Check((string)described["scenarioState"]! == "observed" && (int)described["completedCycles"]! == 1, "actual refusal fields observed");
        Harness.Reject(() => scenarios.Begin("persistence-refusal", 1, 12, 103, facts), "duplicate completed cycle");
        Harness.Reject(() => scenarios.Advance("persistence-refusal", 1, 13, 104, facts), "advance after completion");
        var missing = new SandboxNativeScenarios(); var absent = Harness.Clone(facts); absent.Remove("persistenceIsolationAvailable");
        missing.Begin("persistence-refusal", 1, 10, 100, absent); missing.Advance("persistence-refusal", 1, 11, 101, absent); missing.Describe(described);
        Harness.Check((string)described["scenarioState"]! == "unavailable" && ((string)described["scenarioReason"]!).Contains("persistenceIsolationAvailable"), "missing native field does not become zero");
        var deadline = new SandboxNativeScenarios(); deadline.Begin("persistence-refusal", 1, 10, 100, facts); deadline.Advance("persistence-refusal", 1, 11, 10101, facts); deadline.Describe(described);
        Harness.Check((string)described["scenarioState"]! == "failed", "native step deadline enforced");
        var routing = new SandboxNativeScenarios(); routing.Begin("routing", 1, 10, 100, facts); routing.Advance("routing", 1, 11, 101, facts); routing.Describe(described);
        Harness.Check((int)described["stepIndex"]! == 0 && ((string)described["scenarioReason"]!).EndsWith(":open", StringComparison.Ordinal), "advance is not an instruction acknowledgement");
        var errored = Harness.Clone(facts); errored["cleanupErrors"] = new List<object?> { "Fixture project save: failed" };
        routing.Advance("routing", 1, 12, 102, errored); routing.Describe(described);
        Harness.Check((string)described["scenarioState"]! == "failed" && (string)described["scenarioReason"]! == "fixture-cleanup-error", "cleanup errors fail the cycle");
    }
}
