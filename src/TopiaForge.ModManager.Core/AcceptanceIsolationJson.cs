using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TopiaForge.ModManager.Core
{
    internal sealed class AcceptanceIsolationJson
    {
        internal const int MaximumBytes = 65536;
        internal readonly IReadOnlyDictionary<string, string> Values;
        internal AcceptanceIsolationJson(string json, params string[] keys)
        {
            if (new UTF8Encoding(false, true).GetByteCount(json) > MaximumBytes)
                throw new InvalidDataException("Acceptance document exceeds its limit.");
            var properties = JsonObjectMerge.ReadProperties(json);
            if (properties.Count != keys.Length || properties.Any(p => !keys.Contains(p.Name, StringComparer.Ordinal))
                || properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Count)
                throw new InvalidDataException("Acceptance document has duplicate, missing or unknown fields.");
            Values = properties.ToDictionary(p => p.Name, p => p.RawValue.Trim(), StringComparer.Ordinal);
        }
        internal string Text(string key, int maximum = 32767)
        {
            var raw = Values[key];
            if (!raw.StartsWith("\"", StringComparison.Ordinal)) throw new InvalidDataException("Acceptance string required: " + key);
            var result = JsonUtil.Deserialize<string>(raw);
            if (string.IsNullOrEmpty(result) || result.Length > maximum || result.Any(c => c < 32))
                throw new InvalidDataException("Invalid acceptance string: " + key);
            return result;
        }
        internal int Integer(string key)
        {
            var raw = Values[key];
            if (!Regex.IsMatch(raw, @"\A(?:0|[1-9][0-9]*)\z") || !int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                throw new InvalidDataException("Acceptance integer required: " + key);
            return value;
        }
        internal string Digest(string key)
        {
            var value = Text(key, 64);
            if (!Regex.IsMatch(value, @"\A[0-9a-f]{64}\z")) throw new InvalidDataException("Invalid acceptance digest: " + key);
            return value;
        }
        internal static string Hash(string value)
        {
            using var hash = SHA256.Create();
            return string.Concat(hash.ComputeHash(new UTF8Encoding(false, true).GetBytes(value)).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }
        internal static string Object(params (string Key, string Raw)[] entries) => "{" + string.Join(",", entries.Select(e => JsonUtil.Serialize(e.Key) + ":" + e.Raw)) + "}";
        internal static string Quote(string value) => JsonUtil.Serialize(value);
        internal static readonly StringComparison PathComparison = Environment.OSVersion.Platform == PlatformID.Win32NT ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        internal static bool SamePath(string a, string b) => string.Equals(LocalPath(a), LocalPath(b), PathComparison);
        internal static string LocalPath(string value)
        {
            if (string.IsNullOrEmpty(value) || !Path.IsPathFullyQualified(value) || value.StartsWith(@"\\", StringComparison.Ordinal)
                || value.Any(c => c < 32 || c == '*' || c == '?') || value.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p == "." || p == ".."))
                throw new InvalidDataException("Acceptance paths must be absolute local paths without aliases.");
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                var components = value.Substring(Path.GetPathRoot(value)!.Length).Split('\\', '/');
                if (components.Any(component => component.EndsWith(".", StringComparison.Ordinal)
                    || component.EndsWith(" ", StringComparison.Ordinal)
                    || component.IndexOfAny(new[] { ':', '<', '>', '\"', '|' }) >= 0
                    || Regex.IsMatch(component, @"\A(?:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
                    throw new InvalidDataException("Acceptance paths contain Windows device or filename aliases.");
            }
            var full = Path.GetFullPath(value);
            if (!string.Equals(full, Path.GetPathRoot(full), PathComparison))
                full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // Inspect every existing ancestor, including those above the game installation.
            var current = full;
            while (!string.IsNullOrEmpty(current))
            {
                try
                {
                    var attributes = File.GetAttributes(current);
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
                        || (current != full && (attributes & FileAttributes.Directory) == 0))
                        throw new InvalidDataException("Acceptance paths contain linked or non-directory ancestors.");
                    if (Environment.OSVersion.Platform == PlatformID.Win32NT) RequireCanonicalWindowsPath(current);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                current = Path.GetDirectoryName(current);
            }
            return full;
        }
        private static void RequireCanonicalWindowsPath(string path)
        {
            // Compare the actual filesystem name to reject 8.3 and substituted-drive aliases.
            // Zero requested access and full sharing keep this a read-only admission check.
            using var handle = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (handle.IsInvalid) throw new IOException("Cannot inspect acceptance path identity.");
            var buffer = new StringBuilder(32768);
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity) throw new IOException("Cannot resolve acceptance path identity.");
            var actual = buffer.ToString();
            if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual.Substring(4);
            if (!string.Equals(actual.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Acceptance paths contain filesystem aliases.");
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
        internal static bool Within(string child, string parent) => LocalPath(child).StartsWith(LocalPath(parent) + Path.DirectorySeparatorChar, PathComparison);
    }
}
