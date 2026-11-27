using System;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Transient announcement history for a future platform accessibility bridge.</summary>
    internal static class TopiaForgeStingerAnnouncements
    {
        private static readonly string?[] recent = new string?[32];
        private static int next;
        internal static int Count { get; private set; }
        internal static string? Latest => Count == 0 ? null : recent[(next + recent.Length - 1) % recent.Length];

        internal static void Record(string announcement)
        {
            recent[next] = announcement;
            next = (next + 1) % recent.Length;
            if (Count < recent.Length) Count++;
        }

        internal static void Reset()
        {
            Array.Clear(recent, 0, recent.Length);
            Count = 0;
            next = 0;
        }
    }
}
