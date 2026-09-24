using System;
using System.Collections.Generic;
using System.Linq;

namespace TopiaForge.SandboxAcceptance.Native
{
    // Protocol v2 postconditions: routing/accessibility, catalog filters, undo, control owners, borrowed faults,
    // graph interaction and the transition toggles.
    internal sealed partial class SandboxNativeScenarios
    {
        private bool ObserveExtended(string action, Dictionary<string, object?> facts)
        {
            switch (action)
            {
                case "duplicate-toggle":
                    return Visible(facts) && SameSession(facts) && Number(facts, "creatorSessionCount") == Number(baseline, "creatorSessionCount") + 1;
                case "focus-next":
                    var focused = FocusedNode(facts);
                    return focused != null && focused != FocusedNode(prior);
                case "camera-hidden":
                    return !Visible(facts) && AimChanged(prior, facts);
                case "text-focus":
                    var field = Widget(facts, WindowSurface, "catalog-search");
                    return field != null && Text(field, "value") == "W" && !PositionMoved(prior, facts);
                case "undo":
                    var remaining = ObjectIds(facts); var previous = ObjectIds(prior);
                    return remaining.Length == previous.Length - 1 && remaining.All(previous.Contains) && !remaining.Contains(lastAddedInstanceId)
                        && Number(facts, "undoDepth") == Number(prior, "undoDepth") - 1;
                case "search-nonmatching":
                    var spawn = Widget(facts, WindowSurface, "spawn-selected");
                    return spawn != null && !Boolean(spawn, "enabled") && CatalogRows(facts).Length == 0;
                case "filter-robots":
                    var robots = Widget(facts, WindowSurface, "catalog-kind");
                    return robots != null && Text(robots, "value") == "Robots" && CatalogRows(facts).All(row => row.StartsWith("robotkit:", StringComparison.Ordinal));
                case "filter-all":
                    var all = Widget(facts, WindowSurface, "catalog-kind");
                    return all != null && Text(all, "value") == "All content";
                case "external-write":
                    var before = Map(prior, "borrowedRobot"); var after = Map(facts, "borrowedRobot");
                    return Number(before, "instanceId") == Number(after, "instanceId") && Near(Transform(after)[0] - Transform(before)[0], 2, 0.01);
                case "destroy-borrowed":
                    return facts.TryGetValue("borrowedRobot", out var borrowed) && borrowed == null && Boolean(facts, "borrowedRobotDestroyed");
                case "register-competing-host":
                    var registered = Map(facts, "competingHost");
                    return Boolean(registered, "registered") && Number(registered, "openCalls") == 0;
                case "unregister-competing-host":
                    return !Boolean(Map(facts, "competingHost"), "registered");
                case "control-cue": return Boolean(facts, "controlCuePlaying");
                case "stop-control-cue": return !Boolean(facts, "controlCuePlaying");
                case "spawn-control-robot":
                    var spawned = Number(facts, "controlRobotInstanceId"); var priorIds = ObjectIds(prior); var currentIds = ObjectIds(facts);
                    return spawned != 0 && currentIds.Contains(spawned) && !priorIds.Contains(spawned) && currentIds.Length == priorIds.Length + 1;
                case "despawn-control-robot":
                    var retired = Number(prior, "controlRobotInstanceId"); var left = ObjectIds(facts);
                    return Number(facts, "controlRobotInstanceId") == 0 && left.Length == ObjectIds(prior).Length - 1 && !left.Contains(retired);
                case "accessibility-high-contrast": return ObserveHighContrast(facts);
                case "accessibility-scale-150": return ObserveScale(facts);
                case "accessibility-reduced-motion":
                    var reduced = Map(facts, "accessibility");
                    return Boolean(reduced, "reducedMotion") && Near(Real(reduced, "motionIntensity"), 0, 0.000001);
                case "accessibility-reset":
                    var reset = Map(facts, "accessibility");
                    return !Boolean(reset, "highContrast") && !Boolean(reset, "reducedMotion") && Near(Real(reset, "uiScale"), 1, 0.001);
                case "stop-before-start":
                    return SameObjects(prior, facts) && ResourcesEqual(prior, facts, includeUi: false);
                case "aim-graph-prop":
                    var aim = Map(facts, "aimToGraphProp");
                    return Boolean(aim, "available") && Boolean(aim, "focused");
                case "interact":
                    var toasts = Maps(facts, "toasts");
                    return Number(facts, "interactionCount") == Number(prior, "interactionCount")
                        && toasts.Any(t => Boolean(t, "visible") && Text(t, "text").Contains("Sandbox acceptance interaction"))
                        && !toasts.Any(t => Text(t, "text").Contains("WRONG BRANCH"));
                default: throw new MissingObservation("unsupported-native-postcondition:" + action);
            }
        }
        // Every Sandbox widget renders in high contrast and each text/button/input widget with both measured
        // colours meets WCAG AA (4.5:1).
        private static bool ObserveHighContrast(Dictionary<string, object?> facts)
        {
            if (!Boolean(Map(facts, "accessibility"), "highContrast")) return false;
            foreach (var widget in SandboxWidgets(facts))
            {
                if (!Boolean(widget, "highContrast")) return false;
                var kind = Text(widget, "kind");
                if (kind != "text" && kind != "button" && kind != "input") continue;
                var foreground = Text(widget, "foreground"); var background = Text(widget, "background");
                if (NativeFactShapes.HasColour(foreground) && NativeFactShapes.HasColour(background) && NativeFactShapes.ContrastRatio(foreground, background) < 4.5) return false;
            }
            return true;
        }
        // UiScale 1.5 and the hide button at least 1.4 times its height at open.
        private bool ObserveScale(Dictionary<string, object?> facts)
        {
            if (!Near(Real(Map(facts, "accessibility"), "uiScale"), 1.5, 0.001) || opened == null) return false;
            var before = Widget(opened, WindowSurface, "hide-workbench"); var after = Widget(facts, WindowSurface, "hide-workbench");
            return before != null && after != null && Real(after, "height") >= Real(before, "height") * 1.4;
        }
    }
}
