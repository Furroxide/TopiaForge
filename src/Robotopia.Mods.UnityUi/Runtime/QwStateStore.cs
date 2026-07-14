using System;
using System.Collections.Generic;
using System.IO;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>Persistence seam for window rects and other small UI state.</summary>
    public interface IQwStateStore
    {
        bool TryRead(string key, out string value);
        void Write(string key, string value);
    }

    /// <summary>
    /// File-backed store writing tab-separated escaped lines into the owner's data
    /// directory (a real per-mod folder that uninstall cleans up — deliberately not
    /// PlayerPrefs/registry). Writes are atomic (temp file + move).
    /// </summary>
    public sealed class QwFileStateStore : IQwStateStore
    {
        private readonly string path;
        private readonly Dictionary<string, string> entries = new Dictionary<string, string>(StringComparer.Ordinal);
        private bool loaded;

        public QwFileStateStore(string directory)
        {
            path = Path.Combine(directory, "qwui.state");
        }

        public bool TryRead(string key, out string value)
        {
            EnsureLoaded();
            return entries.TryGetValue(key, out value!);
        }

        public void Write(string key, string value)
        {
            EnsureLoaded();
            if (!QwStateFileCodec.IsValidEntry(key, value))
            {
                QwLog.Warn("UI state write ignored because its key/value exceeds the bounded state contract.");
                return;
            }

            var hadExisting = entries.TryGetValue(key, out var existing);
            if (hadExisting && string.Equals(existing, value, StringComparison.Ordinal))
            {
                return;
            }

            entries[key] = value;
            if (!Flush())
            {
                if (hadExisting)
                {
                    entries[key] = existing!;
                }
                else
                {
                    entries.Remove(key);
                }
            }
        }

        private void EnsureLoaded()
        {
            if (loaded)
            {
                return;
            }

            loaded = true;
            try
            {
                foreach (var entry in QwStateFileCodec.Load(path))
                {
                    entries.Add(entry.Key, entry.Value);
                }
            }
            catch (Exception ex)
            {
                QwLog.Warn("UI state store unreadable (" + ex.Message + "); starting fresh.");
                entries.Clear();
            }
        }

        private bool Flush()
        {
            try
            {
                QwStateFileCodec.Save(path, entries);
                return true;
            }
            catch (Exception ex)
            {
                QwLog.Warn("UI state store write failed (" + ex.Message + ").");
                return false;
            }
        }

    }

    /// <summary>In-memory store for hosts created without a data directory.</summary>
    public sealed class QwMemoryStateStore : IQwStateStore
    {
        private readonly Dictionary<string, string> entries = new Dictionary<string, string>(StringComparer.Ordinal);

        public bool TryRead(string key, out string value)
        {
            return entries.TryGetValue(key, out value!);
        }

        public void Write(string key, string value)
        {
            entries[key] = value;
        }
    }
}
