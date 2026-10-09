using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Copy-on-write hotkey ownership; dispatch does not allocate or skip siblings during removal.</summary>
    internal sealed class HotkeyRegistrationStore
    {
        private readonly List<HotkeyRegistration> entries = new List<HotkeyRegistration>();
        internal HotkeyRegistration[] Snapshot { get; private set; } = Array.Empty<HotkeyRegistration>();
        internal HotkeyRegistration Add(string owner, TopiaForgeKey key, Action action)
        {
            var registration = new HotkeyRegistration(owner, key, action);
            entries.Add(registration);
            Snapshot = entries.ToArray();
            return registration;
        }
        internal void Remove(object handle)
        {
            if (!(handle is HotkeyRegistration registration) || !entries.Remove(registration)) return;
            registration.IsActive = false;
            Snapshot = entries.ToArray();
        }
        internal void RemoveOwner(string owner)
        {
            foreach (var registration in Snapshot)
                if (string.Equals(registration.Owner, owner, StringComparison.Ordinal)) Remove(registration);
        }
        internal void Clear()
        {
            foreach (var registration in entries) registration.IsActive = false;
            entries.Clear();
            Snapshot = Array.Empty<HotkeyRegistration>();
        }
    }
    internal sealed class HotkeyRegistration
    {
        internal HotkeyRegistration(string owner, TopiaForgeKey key, Action action)
        { Owner = owner; Key = key; Action = action; }
        internal string Owner { get; }
        internal TopiaForgeKey Key { get; set; }
        internal Action Action { get; }
        internal bool IsActive { get; set; } = true;
    }
}
