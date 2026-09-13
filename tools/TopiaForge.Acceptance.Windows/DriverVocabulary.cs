using System.Text.Json;
using System.Text.RegularExpressions;

namespace TopiaForge.Acceptance.Windows;

/// Closed v2 actuation vocabulary. Every bound is a refusal, never a substitution.
internal static class DriverVocabulary
{
    internal const int ScrollTicksPerAttempt = 3, ScrollAttempts = 20, MouseMoveBound = 400;
    internal const int KeyHoldMinimumMilliseconds = 50, KeyHoldMaximumMilliseconds = 1000;
    internal const int AimGainMinimum = 1, AimGainMaximum = 20, AimIterationsMaximum = 40;
    internal const int WheelDelta = 120;
    internal static readonly string[] Keys = ["F5", "Escape", "Tab", "W", "Down", "Up", "Return", "Space", "MouseLeft"];
    internal static readonly string[] Surfaces = ["sandbox-creator-window", "$modal"];
    internal static readonly string[] ScrollContainers = ["$scroll-0", "$scroll-1", "$scroll-2"];
    internal static readonly string[] Nodes = ["hide-workbench", "catalog-search", "catalog-list", "spawn-selected", "duplicate-selected", "remove-selected", "nudge-up", "rotation-y", "rotation-w", "scale-x", "scale-y", "scale-z", "apply-transform", "refresh-native", "roster-list", "persona-name", "persona-instructions", "apply-personality", "brain-dormant", "project-list", "load-project", "run-project", "stop-project", "end-session", "confirm", "undo-last", "catalog-kind", "$close", "$scroll-0", "$scroll-1", "$scroll-2"];
    internal static readonly string[] RequestOperations = ["unregister-source", "request-session-stop", "external-write", "destroy-borrowed", "register-competing-host", "unregister-competing-host", "control-cue", "stop-control-cue", "spawn-control-robot", "despawn-control-robot", "accessibility-high-contrast", "accessibility-scale-150", "accessibility-reduced-motion", "accessibility-reset"];
    internal static readonly string[] WireOperations = ["prepare", "begin", "capture", "advance", .. RequestOperations, "cleanup"];
    internal static readonly string[] AimFacts = ["aimToGraphProp"];
    internal static readonly string[] DynamicTexts = ["actionCatalogDisplayName"];
    internal static readonly string[] DynamicRows = ["borrowedRosterId", "projectId", "actionCatalogRowId"];
    internal static readonly string[] Actions = ["open", "reopen", "hide-f5", "hide-close", "duplicate-toggle", "focus-next", "move-player", "move-while-visible", "camera-hidden", "text-focus", "spawn-prop", "spawn-character", "spawn-catalog", "search-nonmatching", "filter-robots", "filter-all", "duplicate", "undo", "remove", "edit-transform", "edit-rotation", "edit-scale", "select-borrowed", "edit-personality", "edit-brain", "run-graph", "stop-graph", "stop-before-start", "aim-graph-prop", "interact", "end-session", "unregister-source", "stop-world-session", "toggle-during-transition", "toggle-in-menu", "observe-refusal", "external-write", "destroy-borrowed", "register-competing-host", "unregister-competing-host", "control-cue", "stop-control-cue", "spawn-control-robot", "despawn-control-robot", "accessibility-high-contrast", "accessibility-scale-150", "accessibility-reduced-motion", "accessibility-reset"];
    /// Steps followed by a screenshot; "prepare" names the capture taken right after begin.
    internal static readonly string[] CapturedSteps = ["open", "reopen", "hide-f5", "hide-close", "end-session", "stop-world-session", "accessibility-high-contrast", "accessibility-scale-150", "accessibility-reduced-motion", "accessibility-reset", "duplicate-toggle", "toggle-during-transition", "toggle-in-menu", "search-nonmatching", "filter-robots", "filter-all"];
    internal static readonly string[] AudioSteps = ["run-graph", "stop-graph", "control-cue", "stop-control-cue"];

    internal static void ValidateStep(JsonElement step)
    {
        var kind = BoundedJson.Text(step, "kind", 32);
        var optionalSurface = step.TryGetProperty("surfaceId", out _) ? new[] { "surfaceId" } : Array.Empty<string>();
        if (optionalSurface.Length != 0 && !Surfaces.Contains(BoundedJson.Text(step, "surfaceId"))) throw new InvalidDataException("Undeclared surface.");
        void Fields(params string[] fields) => BoundedJson.Keys(step, new[] { "kind" }.Concat(fields).Concat(optionalSurface).ToArray());
        void Node() { if (!Nodes.Contains(BoundedJson.Text(step, "nodeId"))) throw new InvalidDataException("Undeclared widget."); }
        void DeclaredKey() { if (!Keys.Contains(BoundedJson.Text(step, "key"))) throw new InvalidDataException("Undeclared key."); }
        switch (kind)
        {
            case "key": Fields("key"); DeclaredKey(); break;
            case "click": Fields("nodeId"); Node(); break;
            case "replace-text":
                var textKey = step.TryGetProperty("textFromFact", out _) ? "textFromFact" : "text";
                Fields("nodeId", textKey); Node();
                if (textKey == "textFromFact" && !DynamicTexts.Contains(BoundedJson.Text(step, textKey))) throw new InvalidDataException("Undeclared dynamic text.");
                if (textKey == "text") _ = ReplacementText(step); break;
            case "select-list-item":
                var itemKey = step.TryGetProperty("itemIdFromFact", out _) ? "itemIdFromFact" : "itemId";
                Fields("nodeId", itemKey); Node();
                if (itemKey == "itemIdFromFact" && !DynamicRows.Contains(BoundedJson.Text(step, itemKey))) throw new InvalidDataException("Undeclared dynamic row.");
                _ = BoundedJson.Text(step, itemKey, 512); break;
            case "barrier": Fields("minimumFrames"); _ = BoundedJson.Integer(step, "minimumFrames", 2, 10); break;
            case "request": Fields("operation"); if (!RequestOperations.Contains(BoundedJson.Text(step, "operation"))) throw new InvalidDataException("Undeclared lifecycle request."); break;
            case "capture": Fields(); break;
            case "scroll-into-view":
                var rowKey = step.TryGetProperty("itemIdFromFact", out _) ? new[] { "itemIdFromFact" } : Array.Empty<string>();
                Fields(new[] { "nodeId", "containerId" }.Concat(rowKey).ToArray()); Node();
                if (rowKey.Length != 0 && !DynamicRows.Contains(BoundedJson.Text(step, "itemIdFromFact"))) throw new InvalidDataException("Undeclared dynamic row.");
                if (!ScrollContainers.Contains(BoundedJson.Text(step, "containerId"))) throw new InvalidDataException("Undeclared scroll container.");
                if (BoundedJson.Text(step, "containerId") == BoundedJson.Text(step, "nodeId")) throw new InvalidDataException("A scroll container cannot be its own target.");
                break;
            case "mouse-move": Fields("dx", "dy"); _ = SignedInteger(step, "dx", -MouseMoveBound, MouseMoveBound); _ = SignedInteger(step, "dy", -MouseMoveBound, MouseMoveBound); break;
            case "key-hold": Fields("key", "milliseconds"); DeclaredKey(); _ = BoundedJson.Integer(step, "milliseconds", KeyHoldMinimumMilliseconds, KeyHoldMaximumMilliseconds); break;
            case "aim":
                Fields("fact", "maxIterations", "gain");
                if (!AimFacts.Contains(BoundedJson.Text(step, "fact"))) throw new InvalidDataException("Undeclared aim fact.");
                _ = BoundedJson.Integer(step, "maxIterations", 1, AimIterationsMaximum);
                _ = BoundedJson.Integer(step, "gain", AimGainMinimum, AimGainMaximum); break;
            default: throw new InvalidDataException("Undeclared input operation.");
        }
    }
    /// Literal replacement text; an empty string clears the field.
    internal static string ReplacementText(JsonElement step)
    {
        var field = step.GetProperty("text");
        if (field.ValueKind != JsonValueKind.String) throw new InvalidDataException("Expected text: text");
        var text = field.GetString()!;
        if (text.Length > 1024 || text.Any(char.IsControl)) throw new InvalidDataException("Invalid text: text");
        return text;
    }
    internal static int SignedInteger(JsonElement value, string name, int minimum, int maximum)
    {
        var field = value.GetProperty(name);
        if (!Regex.IsMatch(field.GetRawText(), "^-?[0-9]+$") || !field.TryGetInt32(out var number) || number < minimum || number > maximum)
            throw new InvalidDataException("Invalid integer: " + name);
        return number;
    }
}
