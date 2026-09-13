using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.SandboxAcceptance;
using TopiaForge.SandboxAcceptance.Native;

// A deliberately synthetic Sandbox scene that emits facts in the observer's exact protocol-v2 shape. It exists
// only to exercise the progress observer offline; nothing here is native game evidence.
internal sealed class SyntheticScene
{
    internal const string Owner = "io.github.furroxide.topiaforge.sandbox";
    internal const string Window = "sandbox-creator-window";
    internal const string PropRow = "content:dev.topiaforge.sandbox-acceptance:prop";
    internal const string RobotRow = "robotkit:acceptance";
    public bool Visible;
    public string SessionId = "synthetic-session-1";
    public string Phase = "Running";
    public string ActiveHost = "";
    public string HudText = "SESSION ACTIVE  •  0 TARGETS";
    public bool StatusCaption = true;
    public int CreatorSessions, RobotEditLeases, Conversations, Interactions, CursorLeases;
    public int? Controller = 7777;
    public readonly List<Dictionary<string, object?>> Content = new List<Dictionary<string, object?>>();
    public readonly List<Dictionary<string, object?>> Robots = new List<Dictionary<string, object?>>();
    public Dictionary<string, object?>? Borrowed = BorrowedRobot();
    public bool BorrowedDestroyed;
    public float[] Position = { 0f, 0f, 0f };
    public float[] Aim = { 0f, 0f, 1f };
    public bool HighContrast, ReducedMotion, LowContrastWidget;
    public float UiScale = 1f, Motion = 1f, HideHeight = 30f;
    public bool CompetingRegistered;
    public int CompetingOpenCalls;
    public bool ControlCue;
    public int ControlRobot;
    public bool ProjectRunning;
    public int UndoDepth, GraphPlaying;
    public readonly List<int> GraphAudio = new List<int>();
    public readonly List<Dictionary<string, object?>> Toasts = new List<Dictionary<string, object?>>();
    public bool AimAvailable, AimFocused;
    public string CatalogKind = "All content", Search = "", Focused = "";
    public List<string> CatalogRows = new List<string> { PropRow, RobotRow };
    public bool SpawnEnabled = true;
    public readonly List<string> SelectedRoster = new List<string>();
    public List<string> CatalogIds = new List<string> { "dev.topiaforge.sandbox-acceptance:prop" };
    public string VehicleState = "Unavailable";
    private int nextInstance = 1000;

    internal static Dictionary<string, object?> BorrowedRobot() => new Dictionary<string, object?>
    {
        ["instanceId"] = 9001, ["sceneHandle"] = 7, ["transform"] = Identity(),
        ["brain"] = new Dictionary<string, object?> { ["initialState"] = "Standby", ["state"] = "Standby", ["llmDisabled"] = "True",
            ["behaviorTrees"] = new List<object?>(), ["hackedPersonalityId"] = 0, ["hackedPersonalityFingerprint"] = "none" }
    };
    internal static float[] Identity() => new[] { 0f, 0f, 0f, 0f, 0f, 0f, 1f, 1f, 1f, 1f };
    internal Dictionary<string, object?> Brain => (Dictionary<string, object?>)Borrowed!["brain"]!;
    internal float[] BorrowedTransform => (float[])Borrowed!["transform"]!;

    internal int AddEntity(bool robot = false, float[]? transform = null)
    {
        var id = ++nextInstance;
        (robot ? Robots : Content).Add(new Dictionary<string, object?> { ["id"] = "entity-" + id, ["alive"] = true, ["instanceId"] = id,
            ["sceneHandle"] = 5, ["transform"] = transform ?? Identity(), ["scope"] = robot ? "global-robotkit" : "sandbox-owner" });
        return id;
    }
    internal void RemoveEntity(int id) { Content.RemoveAll(e => (int)e["instanceId"]! == id); Robots.RemoveAll(e => (int)e["instanceId"]! == id); }
    internal float[] TransformOf(int id) => (float[])(Content.Concat(Robots).First(e => (int)e["instanceId"]! == id))["transform"]!;
    internal void Toast(string text, string style = "Neutral", bool visible = true) => Toasts.Add(NativeFactShapes.Toast("toast-" + (Toasts.Count + 1), text, style, visible));

    internal void Open() { Visible = true; ActiveHost = Owner; CreatorSessions = 1; CursorLeases = 1; HudText = "SESSION ACTIVE  •  0 TARGETS"; }
    internal void Hide() { Visible = false; ActiveHost = ""; CursorLeases = 0; }
    internal void EndSession() { Visible = false; ActiveHost = ""; CreatorSessions = 0; CursorLeases = 0; HudText = "NO ACTIVE CREATOR SESSION"; Content.Clear(); }
    internal void RunGraph()
    {
        AddEntity(); AddEntity(robot: true); Interactions++; Conversations++; GraphPlaying = 1; ProjectRunning = true; AimAvailable = true;
        GraphAudio.Add(3100 + GraphAudio.Count);
    }
    internal void StopGraph(int prop, int robot) { RemoveEntity(prop); RemoveEntity(robot); Interactions--; Conversations--; GraphPlaying = 0; ProjectRunning = false; AimAvailable = false; }

    internal Dictionary<string, object?> Snapshot()
    {
        var content = Content.Select(Harness.Clone).Cast<object?>().ToList();
        return new Dictionary<string, object?>
        {
            ["targetId"] = SandboxAcceptanceMod.SandboxTargetId, ["worldSessionId"] = SessionId, ["sessionPhase"] = Phase, ["activeHostId"] = ActiveHost,
            ["catalogIds"] = CatalogIds.Cast<object?>().ToList(),
            ["catalog"] = new List<object?> { new Dictionary<string, object?> { ["rowId"] = PropRow, ["contentId"] = "dev.topiaforge.sandbox-acceptance:prop", ["displayName"] = "Acceptance prop", ["kind"] = "Prop", ["transformCapabilities"] = 7 } },
            ["robotCatalog"] = new List<object?> { new Dictionary<string, object?> { ["rowId"] = RobotRow, ["displayName"] = "Acceptance bot", ["kind"] = "Robot", ["transformCapabilities"] = 1 } },
            ["catalogSources"] = new List<object?> { NativeFactShapes.CatalogSource("robotopia.vehicles", "Vehicles", VehicleState, 0) },
            ["sandboxContent"] = content, ["nativeRobots"] = Robots.Select(Harness.Clone).Cast<object?>().ToList(),
            ["nativeProps"] = Content.Select(e => (object?)new Dictionary<string, object?> { ["instanceId"] = e["instanceId"], ["sceneHandle"] = 5, ["active"] = true, ["transform"] = e["transform"] }).ToList(),
            ["ownedObjectCount"] = Content.Count, ["cleanupErrors"] = new List<object?>(),
            ["creatorSessionCount"] = CreatorSessions, ["creatorEditLeaseCount"] = 0, ["robotEditLeaseCount"] = RobotEditLeases, ["conversationCount"] = Conversations,
            ["interactionCount"] = Interactions, ["playerControlLeaseCount"] = 0, ["routingHostCount"] = 1,
            ["borrowedRosterId"] = "robot-native:robot-scene:9001", ["borrowedRobot"] = Borrowed == null ? null : Harness.Clone(Borrowed), ["borrowedRobotDestroyed"] = BorrowedDestroyed,
            ["playerPosition"] = (float[])Position.Clone(), ["playerAim"] = (float[])Aim.Clone(),
            ["mutationSafetyState"] = "Unavailable", ["persistenceIsolationAvailable"] = false,
            ["graphPlayingAudioCount"] = GraphPlaying, ["graphAudioSources"] = GraphAudio.Select(id => (object?)new Dictionary<string, object?> { ["instanceId"] = id, ["playing"] = GraphPlaying > 0, ["clipId"] = 1, ["samplePosition"] = 0 }).ToList(),
            ["audioSourceIds"] = GraphAudio.ToArray(), ["personalityAssetIds"] = new int[0],
            ["toasts"] = Toasts.Select(Harness.Clone).Cast<object?>().ToList(),
            ["accessibility"] = NativeFactShapes.Accessibility(HighContrast, UiScale, ReducedMotion, ReducedMotion ? 0f : Motion),
            ["competingHost"] = NativeFactShapes.CompetingHost(CompetingRegistered, CompetingRegistered ? 2 : 0, CompetingOpenCalls, 0),
            ["controlCuePlaying"] = ControlCue, ["controlRobotInstanceId"] = ControlRobot, ["controllerInstanceId"] = Controller,
            ["projectRunning"] = ProjectRunning, ["undoDepth"] = UndoDepth,
            ["aimToGraphProp"] = AimAvailable ? NativeFactShapes.AimToGraphProp(AimFocused ? 0d : 12d, AimFocused ? 0d : 6d, 3d, AimFocused) : NativeFactShapes.AimUnavailable(),
            ["focusedInteraction"] = NativeFactShapes.FocusedInteraction(true, null),
            ["ui"] = new Dictionary<string, object?> { ["width"] = 1920, ["height"] = 1080, ["hostCount"] = 1, ["ownerCanvasCount"] = 2, ["totalCanvasCount"] = 3,
                ["themeSubscriberCount"] = 1, ["cursorLeaseCount"] = CursorLeases, ["dismissScopeCount"] = 0, ["widgets"] = Widgets() }
        };
    }

    private List<object?> Widgets()
    {
        var rows = new List<object?> { Widget("sandbox-creator-hud", "$body", "text", HudText, visible: true) };
        if (StatusCaption) rows.Add(Widget("sandbox-creator-hud", "status", "text", "Sandbox isolation active.", visible: true));
        if (LowContrastWidget) rows.Add(Widget(Window, "dim", "text", "dim", foreground: "#808080", background: "#8a8a8a"));
        foreach (var node in new[] { "hide-workbench", "catalog-kind", "spawn-selected", "duplicate-selected", "undo-last", "remove-selected", "end-session", "stop-project", "$close", "$scroll-0", "$scroll-1", "$scroll-2" })
            rows.Add(Widget(Window, node, node.StartsWith("$scroll", StringComparison.Ordinal) ? "scroll" : node == "catalog-kind" ? "dropdown" : "button", node,
                enabled: node != "spawn-selected" || SpawnEnabled, value: node == "catalog-kind" ? CatalogKind : "", height: node == "hide-workbench" ? HideHeight : 24f));
        rows.Add(Widget(Window, "catalog-search", "input", Search, value: Search));
        rows.Add(Widget(Window, "catalog-list", "list", ""));
        foreach (var row in CatalogRows) rows.Add(Widget(Window, "catalog-list/" + row, "list-item", row));
        rows.Add(Widget(Window, "roster-list", "list", ""));
        rows.Add(Widget(Window, "roster-list/robot-native:robot-scene:9001", "list-item", "Borrowed"));
        foreach (var selected in SelectedRoster) rows.Add(Widget(Window, "roster-list/" + selected, "list-item", selected, selected: true));
        return rows;
    }

    private Dictionary<string, object?> Widget(string surface, string node, string kind, string text, bool? visible = null, bool enabled = true, bool selected = false,
        string value = "", string foreground = "#000000", string background = "#ffffff", float height = 24f)
        => NativeFactShapes.Widget(surface, node, kind, text, "Body", 10f, 10f, 120f, height, visible ?? Visible, enabled, Focused == node, false,
            HighContrast, ReducedMotion, UiScale, ReducedMotion ? 0f : Motion, selected, value, foreground, background);
}
