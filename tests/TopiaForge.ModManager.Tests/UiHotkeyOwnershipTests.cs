using System;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    internal static class UiHotkeyOwnershipTests
    {
        internal static void Run()
        {
            var store = new HotkeyRegistrationStore();
            var parent = store.Add("package.mod", TopiaForgeKey.F5, () => { });
            var first = store.Add("package.mod", TopiaForgeKey.F5, () => { });
            var second = store.Add("package.mod", TopiaForgeKey.F5, () => { });
            var snapshot = store.Snapshot;
            store.Remove(first);
            Assert(parent.IsActive && second.IsActive && !first.IsActive && store.Snapshot.Length == 2,
                "removing a child host's handle must preserve identical-package parent and sibling hotkeys");
            Assert(snapshot.Length == 3 && !snapshot[1].IsActive,
                "an existing dispatch snapshot must remain stable and suppress removed callbacks");
            store.Remove(first);
            Assert(store.Snapshot.Length == 2, "duplicate handle cleanup is idempotent");
            store.RemoveOwner("package.mod");
            Assert(!parent.IsActive && !second.IsActive && store.Snapshot.Length == 0,
                "explicit package-wide cleanup still removes every package registration");
            Console.WriteLine("UiHotkeyOwnershipTests passed.");
        }
        private static void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }
}
