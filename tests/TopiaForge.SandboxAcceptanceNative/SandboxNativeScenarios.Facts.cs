using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace TopiaForge.SandboxAcceptance.Native
{
    // Typed fact accessors and shared predicates. A missing or mistyped required fact raises MissingObservation,
    // which turns the scenario unavailable instead of substituting a default.
    internal sealed partial class SandboxNativeScenarios
    {
        internal const string WindowSurface = "sandbox-creator-window";
        private static object Required(Dictionary<string, object?> map, string key) => map.TryGetValue(key, out var value) && value != null ? value : throw new MissingObservation("native-observation-unavailable:" + key);
        private static string Text(Dictionary<string, object?> map, string key) => Required(map, key) is string value ? value : throw new MissingObservation("native-string-unavailable:" + key);
        private static int Number(Dictionary<string, object?> map, string key) => Required(map, key) is int value ? value : throw new MissingObservation("native-counter-unavailable:" + key);
        private static bool Boolean(Dictionary<string, object?> map, string key) => Required(map, key) is bool value ? value : throw new MissingObservation("native-boolean-unavailable:" + key);
        private static Dictionary<string, object?> Map(Dictionary<string, object?> map, string key) => Required(map, key) as Dictionary<string, object?> ?? throw new MissingObservation("native-object-unavailable:" + key);
        private static object[] Rows(Dictionary<string, object?> map, string key) => Required(map, key) is IEnumerable values ? values.Cast<object>().ToArray() : throw new MissingObservation("native-array-unavailable:" + key);
        private static Dictionary<string, object?>[] Maps(Dictionary<string, object?> map, string key) => Rows(map, key).Select(value => value as Dictionary<string, object?> ?? throw new MissingObservation("native-row-unavailable:" + key)).ToArray();
        private static double Real(Dictionary<string, object?> map, string key) => Real(Required(map, key), key);
        private static double Real(object? value, string key)
        {
            switch (value)
            {
                case int number: return number;
                case float number: return number;
                case double number: return number;
                case long number: return number;
                default: throw new MissingObservation("native-number-unavailable:" + key);
            }
        }
        private static double[] Vector(Dictionary<string, object?> map, string key, int length)
        {
            var values = Rows(map, key).Select(value => Real(value, key)).ToArray();
            if (values.Length != length || values.Any(value => double.IsNaN(value) || double.IsInfinity(value))) throw new MissingObservation("native-vector-unavailable:" + key);
            return values;
        }
        private static double[] Transform(Dictionary<string, object?> map) => Vector(map, "transform", 10);
        private static bool Same(object? a, object? b) => SandboxWireCodec.Serialize(a).SequenceEqual(SandboxWireCodec.Serialize(b));
        private static bool Near(double value, double expected, double tolerance) => Math.Abs(value - expected) < tolerance;

        private static Dictionary<string, object?>[] Widgets(Dictionary<string, object?> facts) => Maps(Map(facts, "ui"), "widgets");
        private static Dictionary<string, object?>[] SandboxWidgets(Dictionary<string, object?> facts) =>
            Widgets(facts).Where(w => Text(w, "surfaceId").StartsWith("sandbox-creator", StringComparison.Ordinal)).ToArray();
        private static Dictionary<string, object?>? Widget(Dictionary<string, object?> facts, string surface, string node)
        {
            var found = Widgets(facts).Where(w => Text(w, "surfaceId") == surface && Text(w, "nodeId") == node).ToArray();
            return found.Length == 1 ? found[0] : null;
        }
        private static string[] CatalogRows(Dictionary<string, object?> facts) => Widgets(facts)
            .Where(w => Text(w, "surfaceId") == WindowSurface && Text(w, "nodeId").StartsWith("catalog-list/", StringComparison.Ordinal))
            .Select(w => Text(w, "nodeId").Substring("catalog-list/".Length)).ToArray();
        private static bool Visible(Dictionary<string, object?> facts) => Widgets(facts).Any(w => Text(w, "surfaceId") == WindowSurface && Boolean(w, "visible"));
        private static bool Hud(Dictionary<string, object?> facts, string text) => Widgets(facts)
            .Any(w => Text(w, "surfaceId") == "sandbox-creator-hud" && Boolean(w, "visible") && !Boolean(w, "clipped") && Text(w, "text").Contains(text));
        private static string? FocusedNode(Dictionary<string, object?> facts)
        {
            var focused = SandboxWidgets(facts).Where(w => Boolean(w, "focused")).ToArray();
            return focused.Length == 1 ? Text(focused[0], "nodeId") : null;
        }
        private bool SameSession(Dictionary<string, object?> facts) => Text(facts, "worldSessionId") == Text(baseline, "worldSessionId");
        private bool CachedUiReleased(Dictionary<string, object?> facts)
        {
            if (opened == null) return false;
            foreach (var key in new[] { "hostCount", "ownerCanvasCount", "totalCanvasCount", "themeSubscriberCount" })
                if (Number(Map(opened, "ui"), key) != Number(Map(facts, "ui"), key)) return false;
            foreach (var key in new[] { "cursorLeaseCount", "dismissScopeCount" })
                if (Number(Map(baseline, "ui"), key) != Number(Map(facts, "ui"), key)) return false;
            return true;
        }
        private static Dictionary<string, object?>[] Objects(Dictionary<string, object?> facts) => Maps(facts, "sandboxContent")
            .Concat(Maps(facts, "nativeRobots")).Where(o => Boolean(o, "alive")).GroupBy(o => Number(o, "instanceId")).Select(g => g.First()).ToArray();
        private static int[] ObjectIds(Dictionary<string, object?> facts) => Objects(facts).Select(o => Number(o, "instanceId")).OrderBy(v => v).ToArray();
        private static bool SameObjects(Dictionary<string, object?> a, Dictionary<string, object?> b) => ObjectIds(a).SequenceEqual(ObjectIds(b));
        private static bool ResourcesEqual(Dictionary<string, object?> a, Dictionary<string, object?> b, bool includeUi)
        {
            foreach (var key in new[] { "creatorSessionCount", "creatorEditLeaseCount", "robotEditLeaseCount", "conversationCount", "interactionCount", "playerControlLeaseCount", "routingHostCount" })
                if (Number(a, key) != Number(b, key)) return false;
            if (includeUi)
                foreach (var key in new[] { "hostCount", "ownerCanvasCount", "totalCanvasCount", "themeSubscriberCount", "cursorLeaseCount", "dismissScopeCount" })
                    if (Number(Map(a, "ui"), key) != Number(Map(b, "ui"), key)) return false;
            return true;
        }
        private static bool PositionMoved(Dictionary<string, object?> before, Dictionary<string, object?> after)
        {
            double[] old, current;
            try { old = Vector(before, "playerPosition", 3); current = Vector(after, "playerPosition", 3); }
            catch (MissingObservation) { throw new MissingObservation("actual-player-position-unavailable"); }
            return Math.Abs(old[0] - current[0]) > 0.005 || Math.Abs(old[2] - current[2]) > 0.005;
        }
        private static bool AimChanged(Dictionary<string, object?> before, Dictionary<string, object?> after)
        {
            var old = Vector(before, "playerAim", 3); var current = Vector(after, "playerAim", 3);
            return Enumerable.Range(0, 3).Any(axis => Math.Abs(old[axis] - current[axis]) > 0.001);
        }
        // Same native identity, identical brain observation and a transform restored within 0.0001.
        private static bool RestoredBorrowed(Dictionary<string, object?> before, Dictionary<string, object?> after)
        {
            if (!(Required(before, "borrowedRobot") is Dictionary<string, object?> original) || !(Required(after, "borrowedRobot") is Dictionary<string, object?> current))
                return false;
            return Number(original, "instanceId") == Number(current, "instanceId") && Number(original, "sceneHandle") == Number(current, "sceneHandle")
                && Same(Required(original, "brain"), Required(current, "brain")) && SameTransform(original, current, 0.0001);
        }
        private static bool SameTransform(Dictionary<string, object?> a, Dictionary<string, object?> b, double tolerance)
        {
            var first = Transform(a); var second = Transform(b);
            return Enumerable.Range(0, 10).All(axis => Near(first[axis], second[axis], tolerance));
        }
    }
}
