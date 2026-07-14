using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>Unity-free bounded/atomic codec behind <see cref="QwFileStateStore"/>.</summary>
    internal static class QwStateFileCodec
    {
        internal const int MaxFileBytes = 256 * 1024;
        internal const int MaxEntries = 1024;
        internal const int MaxKeyChars = 256;
        internal const int MaxValueChars = 4096;
        private const int MaxEncodedLineChars = (MaxKeyChars + MaxValueChars) * 2 + 1;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static Dictionary<string, string> Load(string path)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!File.Exists(path))
            {
                return result;
            }

            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                throw new InvalidDataException("UI state must be a regular file.");
            }

            string text;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length < 0 || stream.Length > MaxFileBytes)
                {
                    throw new InvalidDataException("UI state exceeds " + MaxFileBytes + " bytes.");
                }

                var bytes = new byte[(int)stream.Length];
                var offset = 0;
                while (offset < bytes.Length)
                {
                    var count = stream.Read(bytes, offset, bytes.Length - offset);
                    if (count == 0)
                    {
                        throw new InvalidDataException("UI state was truncated while reading.");
                    }

                    offset += count;
                }

                if (stream.ReadByte() != -1 || stream.Length != bytes.Length)
                {
                    throw new InvalidDataException("UI state changed while reading.");
                }

                try
                {
                    text = StrictUtf8.GetString(bytes);
                }
                catch (DecoderFallbackException ex)
                {
                    throw new InvalidDataException("UI state is not strict UTF-8.", ex);
                }
            }

            using var reader = new StringReader(text);
            string? line;
            var lineNumber = 0;
            while ((line = reader.ReadLine()) != null)
            {
                lineNumber++;
                if (lineNumber > MaxEntries)
                {
                    throw new InvalidDataException("UI state contains more than " + MaxEntries + " entries.");
                }

                if (line.Length == 0)
                {
                    continue;
                }

                if (line.Length > MaxEncodedLineChars)
                {
                    throw new InvalidDataException("UI state line " + lineNumber + " exceeds the entry limit.");
                }

                var separator = line.IndexOf('\t');
                if (separator <= 0 || line.IndexOf('\t', separator + 1) >= 0)
                {
                    throw new InvalidDataException("UI state line " + lineNumber + " is malformed.");
                }

                var key = Unescape(line.Substring(0, separator), lineNumber);
                var value = Unescape(line.Substring(separator + 1), lineNumber);
                ValidateEntry(key, value);
                if (!result.TryAdd(key, value))
                {
                    throw new InvalidDataException("UI state contains duplicate key '" + key + "'.");
                }
            }

            return result;
        }

        public static void Save(string path, IReadOnlyDictionary<string, string> entries)
        {
            if (entries.Count > MaxEntries)
            {
                throw new InvalidDataException("UI state contains more than " + MaxEntries + " entries.");
            }

            var keys = new List<string>(entries.Keys);
            keys.Sort(StringComparer.Ordinal);
            var builder = new StringBuilder();
            for (var index = 0; index < keys.Count; index++)
            {
                var key = keys[index];
                var value = entries[key];
                ValidateEntry(key, value);
                builder.Append(Escape(key)).Append('\t').Append(Escape(value)).Append('\n');
            }

            byte[] bytes;
            try
            {
                bytes = StrictUtf8.GetBytes(builder.ToString());
            }
            catch (EncoderFallbackException ex)
            {
                throw new InvalidDataException("UI state contains invalid Unicode.", ex);
            }

            if (bytes.Length > MaxFileBytes)
            {
                throw new InvalidDataException("UI state exceeds " + MaxFileBytes + " bytes.");
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            var backup = path + ".bak-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(
                    temp,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    16 * 1024,
                    FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }

                if (!File.Exists(path))
                {
                    File.Move(temp, path);
                }
                else
                {
                    try
                    {
                        File.Replace(temp, path, backup);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        ReplaceWithRollback(temp, path, backup);
                    }
                }
            }
            finally
            {
                TryDelete(temp);
                // If replacement succeeded, the unique backup is no longer needed. If the target disappeared,
                // preserve/restore it instead of deleting the only known-good state.
                if (File.Exists(backup))
                {
                    if (File.Exists(path))
                    {
                        TryDelete(backup);
                    }
                    else
                    {
                        File.Move(backup, path);
                    }
                }
            }
        }

        public static bool IsValidEntry(string? key, string? value)
        {
            return key != null && value != null && key.Length > 0 && key.Length <= MaxKeyChars &&
                   value.Length <= MaxValueChars && key.IndexOf('\0') < 0 && value.IndexOf('\0') < 0;
        }

        private static void ValidateEntry(string? key, string? value)
        {
            if (!IsValidEntry(key, value))
            {
                throw new InvalidDataException(
                    "UI state key/value exceeds limits or contains an invalid null character.");
            }
        }

        private static void ReplaceWithRollback(string temp, string path, string backup)
        {
            File.Move(path, backup);
            try
            {
                File.Move(temp, path);
            }
            catch
            {
                if (!File.Exists(path) && File.Exists(backup))
                {
                    File.Move(backup, path);
                }

                throw;
            }
        }

        private static string Escape(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        private static string Unescape(string value, int lineNumber)
        {
            var builder = new StringBuilder(value.Length);
            for (var index = 0; index < value.Length; index++)
            {
                var current = value[index];
                if (current != '\\')
                {
                    builder.Append(current);
                    continue;
                }

                if (++index >= value.Length)
                {
                    throw new InvalidDataException("UI state line " + lineNumber + " ends in an escape character.");
                }

                switch (value[index])
                {
                    case '\\':
                        builder.Append('\\');
                        break;
                    case 't':
                        builder.Append('\t');
                        break;
                    case 'n':
                        builder.Append('\n');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    default:
                        throw new InvalidDataException(
                            "UI state line " + lineNumber + " contains an invalid escape sequence.");
                }
            }

            return builder.ToString();
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // A unique orphan cannot be mistaken for live state and is safer than hiding the write result.
            }
        }
    }
}
