using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TopiaForge.SandboxAcceptance.Native
{
    // Protocol v2 step recipes per cycle (spec section 4). The list is fixed at begin from the admitted
    // catalog; every entry is later observed as its own native postcondition.
    internal sealed partial class SandboxNativeScenarios
    {
        internal static List<SandboxNativeStep> Expand(string id, int cycle, Dictionary<string, object?> facts)
        {
            var result = new List<SandboxNativeStep>();
            void Add(params string[] actions) { result.AddRange(actions.Select(a => new SandboxNativeStep(a))); }
            switch (id)
            {
                case "routing":
                    if (cycle == 1)
                        Add("open", "accessibility-high-contrast", "accessibility-scale-150", "accessibility-reduced-motion", "accessibility-reset",
                            "focus-next", "hide-f5", "reopen", "duplicate-toggle", "end-session");
                    else Add("register-competing-host", "open", "hide-f5", "reopen", "end-session", "unregister-competing-host");
                    break;
                case "catalog-editing":
                    Add("open", "search-nonmatching", "filter-robots", "filter-all");
                    var entries = Maps(facts, "catalog").Concat(Maps(facts, "robotCatalog")).ToArray();
                    if (entries.Length == 0 || entries.Length > 256) throw new InvalidDataException("Admitted catalog must contain 1-256 entries.");
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
                        result.Add(new SandboxNativeStep("undo", row, label));
                        result.Add(new SandboxNativeStep("remove", row, label));
                    }
                    Add("end-session");
                    break;
                case "borrowed-robot":
                    Add("open", "select-borrowed", "edit-transform");
                    if (cycle == 1) Add("edit-personality", "edit-brain");
                    else if (cycle == 2) Add("edit-personality", "external-write");
                    else Add("destroy-borrowed");
                    Add("end-session");
                    break;
                case "source-unload":
                    if (cycle == 1) Add("open", "spawn-prop", "duplicate", "spawn-character", "spawn-control-robot", "unregister-source", "end-session", "despawn-control-robot");
                    else Add("open", "spawn-prop", "run-graph", "unregister-source", "end-session");
                    break;
                case "hide-reopen":
                    Add("open", "spawn-prop", "select-borrowed", "edit-transform", "run-graph", "move-while-visible", "hide-close", "move-player",
                        "camera-hidden", "reopen", "text-focus", "hide-f5", "reopen", "stop-graph", "end-session");
                    break;
                case "persistence-refusal":
                    Add("observe-refusal");
                    break;
                case "graph-rollback":
                    if (cycle == 1)
                        Add("open", "spawn-prop", "control-cue", "run-graph", "hide-f5", "aim-graph-prop", "interact", "reopen", "stop-graph",
                            "stop-control-cue", "end-session");
                    else Add("open", "spawn-prop", "stop-before-start", "run-graph", "stop-graph", "run-graph", "stop-graph", "end-session");
                    break;
                case "lifecycle-routes":
                    Add("open", "spawn-prop", "stop-world-session");
                    if (cycle == 2) Add("toggle-during-transition", "move-player");
                    else if (cycle == 3) Add("toggle-in-menu");
                    break;
                case "ten-cycles":
                    Add("open", "spawn-prop", "select-borrowed", "edit-transform", "edit-personality", "edit-brain", "hide-f5", "move-player",
                        "camera-hidden", "reopen", "run-graph", "stop-graph", "end-session");
                    break;
                default:
                    throw new InvalidDataException("Unknown scenario.");
            }
            return result;
        }
    }
}
