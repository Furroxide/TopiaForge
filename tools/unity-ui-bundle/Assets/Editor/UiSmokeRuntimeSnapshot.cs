using System;
using System.Collections;
using System.Reflection;

namespace TopiaForge
{
    internal static class UiSmokeRuntimeSnapshot
    {
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
    }
}
