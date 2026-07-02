using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

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
            if (entries.TryGetValue(key, out var existing) && string.Equals(existing, value, StringComparison.Ordinal))
            {
                return;
            }

            entries[key] = value;
            Flush();
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
                if (!File.Exists(path))
                {
                    return;
                }

                foreach (var line in File.ReadAllLines(path))
                {
                    var separator = line.IndexOf('\t');
                    if (separator <= 0)
                    {
                        continue;
                    }

                    entries[Unescape(line.Substring(0, separator))] = Unescape(line.Substring(separator + 1));
                }
            }
            catch (Exception ex)
            {
                QwLog.Warn("UI state store unreadable (" + ex.Message + "); starting fresh.");
                entries.Clear();
            }
        }

        private void Flush()
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var builder = new StringBuilder();
                foreach (var entry in entries)
                {
                    builder.Append(Escape(entry.Key)).Append('\t').Append(Escape(entry.Value)).Append('\n');
                }

                var temp = path + ".tmp";
                File.WriteAllText(temp, builder.ToString());
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                File.Move(temp, path);
            }
            catch (Exception ex)
            {
                QwLog.Warn("UI state store write failed (" + ex.Message + ").");
            }
        }

        private static string Escape(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        private static string Unescape(string value)
        {
            var builder = new StringBuilder(value.Length);
            for (var index = 0; index < value.Length; index++)
            {
                var current = value[index];
                if (current != '\\' || index + 1 >= value.Length)
                {
                    builder.Append(current);
                    continue;
                }

                index++;
                builder.Append(value[index] switch
                {
                    't' => '\t',
                    'n' => '\n',
                    'r' => '\r',
                    _ => value[index],
                });
            }

            return builder.ToString();
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
