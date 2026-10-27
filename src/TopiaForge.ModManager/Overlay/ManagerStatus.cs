using System;

namespace TopiaForge.ModManager
{
    /// <summary>Action feedback expires on unscaled time, including while the overlay is closed.</summary>
    internal sealed class ManagerStatus
    {
        internal const string Default = "Trusted local mode: install only packages you explicitly trust. C# mods execute code.";
        private double remaining;
        internal string Text { get; private set; } = Default;

        internal void Set(string message)
        {
            Text = message;
            remaining = 8;
        }

        internal bool Tick(float elapsed)
        {
            if (remaining <= 0 || elapsed <= 0 || float.IsNaN(elapsed) || float.IsInfinity(elapsed)) return false;
            remaining = Math.Max(0, remaining - elapsed);
            if (remaining > 0) return false;
            Text = Default;
            return true;
        }
    }
}
