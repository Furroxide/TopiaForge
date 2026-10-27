using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using TopiaForge.Mods;

namespace TopiaForge.Worlds.Maps
{
    internal sealed class MapDocument
    {
        internal const int MaximumPackedBytes = 8 * 1024 * 1024;
        internal const int MaximumJsonBytes = 32 * 1024 * 1024;
        internal const int MaximumEntities = 50000;
        internal byte[] Packed { get; }
        internal byte[] Json { get; }
        internal string Sha256 { get; }
        internal string Name { get; }
        internal string Modified { get; }
        internal IReadOnlyDictionary<string, MapScene> Scenes { get; }

        internal MapDocument(byte[] packed)
        {
            if (packed == null || packed.Length == 0 || packed.Length > MaximumPackedBytes)
                throw new FormatException("The map exceeds the 8 MiB download limit or is empty. Choose another map.");
            Packed = (byte[])packed.Clone();
            Sha256 = Hash(Packed);
            Json = Unpack(Packed);
            var project = MapJson.Read(Json);
            Name = Text(project, "name", 256);
            Modified = Text(project, "modified", 128);
            var scenes = Object(project, "scenes");
            if (scenes.Count == 0 || scenes.Count > 256) throw Invalid();
            var result = new Dictionary<string, MapScene>(StringComparer.Ordinal);
            var total = 0;
            foreach (var pair in scenes)
            {
                var scene = AsObject(pair.Value);
                RequireId(pair.Key, scene);
                var entities = Object(scene, "entities");
                total += entities.Count;
                if (total > MaximumEntities)
                    throw new FormatException("The map exceeds the 50,000 entity limit. Choose another map.");
                var parents = new Dictionary<string, string>(StringComparer.Ordinal);
                var spawns = new List<MapSpawn>();
                foreach (var entry in entities)
                {
                    var entity = AsObject(entry.Value);
                    RequireId(entry.Key, entity);
                    parents.Add(entry.Key, Text(entity, "parent", 128));
                    var components = Object(entity, "components");
                    if (components.TryGetValue("spawn-location", out var spawn) && spawn != null)
                    {
                        AsObject(spawn);
                        spawns.Add(new MapSpawn(entry.Key, Text(entity, "name", 256)));
                    }
                }
                ValidateParents(parents);
                result.Add(pair.Key, new MapScene(pair.Key, Text(scene, "name", 256), entities.Count, spawns));
            }
            Scenes = result;
        }

        internal MapScene Select(string sceneId)
        {
            if (!Scenes.TryGetValue(sceneId, out var scene))
                throw new MapProblem(ModErrorCode.NotFound, "The map no longer contains the selected scene. Ask the mod's author for an update.");
            return scene;
        }

        private static byte[] Unpack(byte[] bytes)
        {
            if (bytes.Length < 2 || bytes[0] != 0x1f || bytes[1] != 0x8b) return bytes;
            using var input = new MemoryStream(bytes, false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[16384];
            int count;
            while ((count = gzip.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (output.Length + count > MaximumJsonBytes)
                    throw new FormatException("The map exceeds the 32 MiB expanded limit. Choose another map.");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }

        internal static string Hash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
        }
        internal static bool IsId(string? value, int maximum = 128) => value != null && value.Length > 0 && value.Length <= maximum &&
            value.All(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-');
        internal static string Text(Dictionary<string, object?> value, string key, int maximum)
        {
            if (!value.TryGetValue(key, out var raw) || raw == null) return string.Empty;
            if (!(raw is string text) || text.Length > maximum) throw Invalid();
            return text;
        }
        private static Dictionary<string, object?> Object(Dictionary<string, object?> value, string key) =>
            value.TryGetValue(key, out var raw) ? AsObject(raw) : throw Invalid();
        private static Dictionary<string, object?> AsObject(object? value) => value as Dictionary<string, object?> ?? throw Invalid();
        private static void RequireId(string key, Dictionary<string, object?> value)
        {
            var id = Text(value, "id", 128);
            if (!IsId(key) || (id.Length != 0 && id != key)) throw Invalid();
        }
        private static void ValidateParents(Dictionary<string, string> parents)
        {
            // Bound the native importer's recursion as well as the JSON parser's.
            foreach (var entry in parents)
            {
                var current = entry.Value;
                var seen = new HashSet<string>(StringComparer.Ordinal) { entry.Key };
                while (current.Length != 0)
                {
                    if (seen.Count > 128 || !seen.Add(current) || !parents.TryGetValue(current, out var parent)) throw Invalid();
                    current = parent;
                }
            }
        }
        private static FormatException Invalid() => new FormatException("The map has invalid scenes, entity identities or parent links. Ask its creator for a new export.");
    }

    internal sealed class MapScene
    {
        internal MapScene(string id, string name, int entityCount, IReadOnlyList<MapSpawn> spawns)
        { Id = id; Name = name; EntityCount = entityCount; Spawns = spawns; }
        internal string Id { get; }
        internal string Name { get; }
        internal int EntityCount { get; }
        internal IReadOnlyList<MapSpawn> Spawns { get; }
        internal MapSpawn Spawn(string? name)
        {
            var matches = name == null ? Spawns : Spawns.Where(s => s.Name == name).ToArray();
            if (matches.Count == 0) throw new MapProblem(ModErrorCode.InvalidState, "The map has no matching spawn location. Choose another map or add one in the editor.");
            if (matches.Count != 1) throw new MapProblem(ModErrorCode.InvalidState, "The map has several matching spawn locations. Give one a unique editor name and set spawn.markerName.");
            return matches[0];
        }
    }
    internal sealed class MapSpawn
    {
        internal MapSpawn(string id, string name) { Id = id; Name = name; }
        internal string Id { get; }
        internal string Name { get; }
    }
}
