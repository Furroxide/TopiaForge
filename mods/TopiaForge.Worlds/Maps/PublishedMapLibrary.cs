using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace TopiaForge.Worlds.Maps
{
    // A local list of explicit published sources. No catalogue request or credentials.
    internal sealed class PublishedMapSource
    {
        internal const string Family = "io.github.furroxide.topiaforge.worlds.map";
        internal PublishedMapSource(string map, string scene, string name)
        { Map = map; Scene = scene; Name = name; }
        internal string Map { get; }
        internal string Scene { get; }
        internal string Name { get; }
        internal string WorldId
        {
            get
            {
                // The exact-source digest distinguishes case-sensitive map ids inside the
                // declaration system's case-insensitive namespace, and different scenes.
                using var sha = SHA256.Create();
                var hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Map + "\n" + Scene))).Replace("-", "").ToLowerInvariant();
                var readable = Family + "." + hash.Substring(0, 12) + "." + Map;
                return readable.Length <= 96 ? readable : Family + ".h" + hash.Substring(0, 40);
            }
        }
    }

    internal static class PublishedMapLibrary
    {
        internal const int MaximumBytes = 1024 * 1024;
        internal const int MaximumSources = 4096;
        internal static IReadOnlyList<PublishedMapSource> Read(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked source library.");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
            byte[] bytes;
            try
            {
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (input.Length <= 0 || input.Length > MaximumBytes) throw Invalid();
                using var output = new MemoryStream();
                var buffer = new byte[16384]; int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (output.Length + count > MaximumBytes) throw Invalid();
                    output.Write(buffer, 0, count);
                }
                bytes = output.ToArray();
            }
            catch (FileNotFoundException) { return Array.Empty<PublishedMapSource>(); }
            catch (DirectoryNotFoundException) { return Array.Empty<PublishedMapSource>(); }
            return Parse(bytes, token);
        }
        internal static IReadOnlyList<PublishedMapSource> Parse(byte[] bytes, CancellationToken token)
        {
            if (bytes.Length == 0 || bytes.Length > MaximumBytes) throw Invalid();
            var root = MapJson.Read(bytes);
            if (root.Count != 2 || !root.TryGetValue("schemaVersion", out var version) || !(version is double number) || number != 1
                || !root.TryGetValue("sources", out var raw) || !(raw is List<object?> rows) || rows.Count > MaximumSources) throw Invalid();
            var sources = new List<PublishedMapSource>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                if (!(row is Dictionary<string, object?> fields) || fields.Count != 3) throw Invalid();
                var map = Text(fields, "map", 64); var scene = Text(fields, "scene", 128); var name = Text(fields, "name", 128);
                if (!MapDocument.IsId(map, 64) || !MapDocument.IsId(scene) || string.IsNullOrWhiteSpace(name)) throw Invalid();
                var source = new PublishedMapSource(map, scene, name);
                if (!keys.Add(map + "\n" + scene) || !ids.Add(source.WorldId)) throw Invalid();
                sources.Add(source);
            }
            return Array.AsReadOnly(sources.OrderBy(s => s.WorldId, StringComparer.Ordinal).ToArray());
        }
        private static string Text(Dictionary<string, object?> fields, string key, int maximum)
        {
            if (!fields.TryGetValue(key, out var raw) || !(raw is string text) || text.Length == 0 || text.Length > maximum
                || text.Any(char.IsControl)) throw Invalid();
            return text;
        }
        private static FormatException Invalid() => new FormatException("The saved map list is invalid. Repair it in launcher Setup or with world source.");
    }
}
