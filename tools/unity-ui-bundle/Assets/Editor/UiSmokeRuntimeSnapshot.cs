using System;
using System.Collections;
using System.Reflection;

namespace TopiaForge
{
    internal static class UiSmokeRuntimeSnapshot
    {
        private const string ToastOwnerId = "io.github.furroxide.topiaforge.ui.toasts";

        // Count the actual owned entries, then independently verify the dispatch cache agrees.
        internal static int HotkeyCount(Assembly assembly)
        {
            var store = assembly.GetType("TopiaForge.Mods.UnityUi.TopiaForgeHotkeys", true)
                .GetField("Registrations", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var entries = (ICollection)store.GetType().GetField("entries", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(store);
            var dispatch = (Array)store.GetType().GetProperty("Snapshot", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(store);
            if (entries.Count != dispatch.Length) throw new InvalidOperationException("Hotkey ownership and dispatch snapshot diverged.");
            return entries.Count;
        }

        // Explicit observation of the process-wide toast host through the public diagnostics API. The published
        // owner id must match the constant the kit ships, so a renamed host cannot silently hide toasts.
        internal static IDisposable EnableToastDiagnostics(Assembly assembly)
        {
            var published = (string)assembly.GetType("TopiaForge.Mods.UnityUi.TopiaForgeToasts", true)
                .GetField("DiagnosticsOwnerId", BindingFlags.Public | BindingFlags.Static).GetRawConstantValue();
            if (!string.Equals(published, ToastOwnerId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Toast diagnostics owner id diverged: " + published);
            }

            return (IDisposable)Diagnostics(assembly).GetMethod("Enable", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { ToastOwnerId });
        }

        // Exactly one "$toast" node carrying the presented text/tone and the four measured fields of the contract.
        internal static void AssertToastObserved(Assembly assembly, string text, string style)
        {
            var widgets = ToastWidgets(assembly);
            if (widgets.Count != 1) throw new InvalidOperationException("Expected one observed toast, found " + widgets.Count + ".");
            var widget = widgets[0];
            var nodeId = Property<string>(widget, "NodeId");
            long sequence;
            if (!nodeId.StartsWith("toast-", StringComparison.Ordinal)
                || !long.TryParse(nodeId.Substring("toast-".Length), out sequence) || sequence < 1)
            {
                throw new InvalidOperationException("Toast node id is not a monotonic sequence: " + nodeId);
            }

            Expect(Property<string>(widget, "SurfaceId") == "$toast", "toast surface");
            Expect(Property<string>(widget, "Kind") == "toast", "toast kind");
            Expect(Property<string>(widget, "Text") == text, "toast text is the message");
            Expect(Property<string>(widget, "Style") == style, "toast style is the tone name");
            Expect(!Property<bool>(widget, "Selected"), "toasts are never selected");
            Expect(Property<string>(widget, "Value").Length == 0, "toasts carry no value");
            Expect(IsColorText(Property<string>(widget, "Foreground")) && Property<string>(widget, "Foreground").Length == 7,
                "toast foreground is the label colour");
            Expect(IsColorText(Property<string>(widget, "Background")), "toast background is well-formed colour text");
        }

        // Pooled/destroyed toast views must leave the observed owner empty once Unity finishes destroying them.
        internal static int ToastDiagnosticWidgetCount(Assembly assembly)
        {
            return ToastWidgets(assembly).Count;
        }

        private static IList ToastWidgets(Assembly assembly)
        {
            var snapshot = Diagnostics(assembly).GetMethod("Capture", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { ToastOwnerId });
            return (IList)snapshot.GetType().GetProperty("Widgets").GetValue(snapshot);
        }

        private static Type Diagnostics(Assembly assembly)
        {
            return assembly.GetType("TopiaForge.Mods.UnityUi.TopiaForgeUiDiagnostics", true);
        }

        private static T Property<T>(object widget, string name)
        {
            return (T)widget.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance).GetValue(widget);
        }

        private static bool IsColorText(string value)
        {
            if (value == null) return false;
            if (value.Length == 0) return true;
            if (value.Length != 7 || value[0] != '#') return false;
            for (var index = 1; index < value.Length; index++)
            {
                var character = value[index];
                if (!(character >= '0' && character <= '9') && !(character >= 'a' && character <= 'f')) return false;
            }

            return true;
        }

        private static void Expect(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Toast diagnostics: " + message + ".");
        }
    }
}
