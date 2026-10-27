using System;
using System.Collections.Generic;
using System.Linq;

namespace TopiaForge.Mods.Internal
{
    internal static class AuthoredText
    {
        internal static bool IsId(string? value, int maximum = 64) => value != null && value.Length > 0 && value.Length <= maximum &&
            value.All(c => c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '_');
        internal static void RequireId(string value, string field)
        { if (!IsId(value)) throw new ArgumentException(field + " must be a bounded ASCII id."); }
        internal static void RequireText(string value, int limit, string field)
        { if (value == null || value.Length > limit || value.Any(char.IsControl)) throw new ArgumentException(field + " must be bounded plain text."); }
        internal static bool PackagePath(string? value) => value != null && value.Length > 0 && value.Length <= 240 &&
            value.Split('/').All(p => p.Length > 0 && p != "." && p != "..") &&
            !value.Any(c => char.IsControl(c) || "\\:*?\"<>|".IndexOf(c) >= 0);
    }

    internal sealed class AuthoredRecord
    {
        internal AuthoredRecord(string id, string sceneId, string name, string? parentId, string? modelId,
            string? prefabUri, MapTransform transform, AuthoredComponents components)
        { Id = id; SceneId = sceneId; Name = name; ParentId = parentId; ModelId = modelId; PrefabUri = prefabUri; Transform = transform; Components = components; }
        internal string Id { get; }
        internal string SceneId { get; }
        internal string Name { get; }
        internal string? ParentId { get; }
        internal string? ModelId { get; }
        internal string? PrefabUri { get; }
        internal MapTransform Transform { get; }
        internal AuthoredComponents Components { get; }
    }

    internal sealed class AuthoredScene
    {
        internal AuthoredScene(string id, string name, string environment, IReadOnlyList<AuthoredRecord> records)
        { Id = id; Name = name; Environment = environment; Records = records; }
        internal string Id { get; }
        internal string Name { get; }
        internal string Environment { get; }
        internal IReadOnlyList<AuthoredRecord> Records { get; }
    }

    // Shared by native indexing and the testing kit. No game assembly, filesystem or network dependency.
    internal sealed class AuthoredProject
    {
        private AuthoredProject(string name, string modified, IReadOnlyDictionary<string, AuthoredScene> scenes)
        { Name = name; Modified = modified; Scenes = scenes; }
        internal string Name { get; }
        internal string Modified { get; }
        internal IReadOnlyDictionary<string, AuthoredScene> Scenes { get; }
        internal static AuthoredProject Read(byte[] json)
        {
            if (json == null || json.Length == 0 || json.Length > 32 * 1024 * 1024) throw Invalid("project size");
            return Read(MapJson.Read(json));
        }
        internal static AuthoredProject Read(Dictionary<string, object?> root)
        {
            var scenes = Object(Value(root, "scenes"), "scenes");
            if (scenes.Count == 0 || scenes.Count > 256) throw Invalid("scenes");
            var result = new Dictionary<string, AuthoredScene>(StringComparer.Ordinal);
            var total = 0;
            foreach (var pair in scenes)
            {
                var scene = Object(pair.Value, "scene");
                Identity(pair.Key, scene, "scene id", 128);
                var records = Object(Value(scene, "entities"), "entities");
                if ((total += records.Count) > 50000) throw Invalid("entity count");
                var byId = new Dictionary<string, AuthoredRecord>(StringComparer.Ordinal);
                foreach (var item in records)
                {
                    var entity = Object(item.Value, "entity");
                    Identity(item.Key, entity, "entity id");
                    var components = Object(Value(entity, "components"), "components");
                    var flags = AuthoredComponents.None;
                    string? model = null, prefab = null;
                    foreach (var component in components)
                    {
                        if (component.Value == null) continue;
                        var data = Object(component.Value, "component");
                        switch (component.Key)
                        {
                            case "model-renderer": flags |= AuthoredComponents.ModelRenderer; model = OptionalText(data, "model", 256); break;
                            case "prefab-instance": flags |= AuthoredComponents.PrefabInstance; prefab = OptionalText(data, "uri", 512); break;
                            case "spawn-location": flags |= AuthoredComponents.SpawnLocation; break;
                            case "poi": flags |= AuthoredComponents.Poi; break;
                            case "aoi": flags |= AuthoredComponents.Aoi; break;
                            case "agent": flags |= AuthoredComponents.Agent; break;
                        }
                    }
                    var parent = OptionalText(entity, "parent", 64);
                    if (parent != null && !AuthoredText.IsId(parent)) throw Invalid("parent");
                    byId.Add(item.Key, new AuthoredRecord(item.Key, pair.Key, Text(entity, "name", 128), parent,
                        model, prefab, Transform(Value(components, "transform")), flags));
                }
                var ordered = new List<AuthoredRecord>();
                var visited = new HashSet<string>(StringComparer.Ordinal);
                var visiting = new HashSet<string>(StringComparer.Ordinal);
                var parentDepth = new Dictionary<string, int>(StringComparer.Ordinal);
                void Visit(AuthoredRecord record, int depth)
                {
                    if (visited.Contains(record.Id)) return;
                    if (depth > 128 || !visiting.Add(record.Id)) throw Invalid("parent links");
                    var height = 0;
                    if (record.ParentId != null)
                    {
                        if (!byId.TryGetValue(record.ParentId, out var parent)) throw Invalid("parent links");
                        Visit(parent, depth + 1);
                        height = parentDepth[parent.Id] + 1;
                        if (height > 128) throw Invalid("parent links");
                    }
                    parentDepth.Add(record.Id, height);
                    visiting.Remove(record.Id); visited.Add(record.Id); ordered.Add(record);
                }
                foreach (var record in byId.Values.OrderBy(r => r.Id, StringComparer.Ordinal)) Visit(record, 0);
                result.Add(pair.Key, new AuthoredScene(pair.Key, Text(scene, "name", 128), Text(scene, "environment", 128), ordered.AsReadOnly()));
            }
            return new AuthoredProject(Text(root, "name", 256), Text(root, "modified", 128), result);
        }
        internal static MapTransform Transform(object? raw)
        {
            if (raw == null) return MapTransform.Identity;
            var data = Object(raw, "transform");
            var position = Vector(Value(data, "position"), Vec3.Zero, "position");
            var scale = Vector(Value(data, "scale"), new Vec3(1, 1, 1), "scale");
            var rotation = Value(data, "rotation");
            float? w = null;
            if (rotation is List<object?> list && list.Count == 4) w = Number(list[3], "rotation.w");
            if (rotation is Dictionary<string, object?> obj && Value(obj, "w") != null) w = Number(Value(obj, "w"), "rotation.w");
            var xyz = Vector(rotation, Vec3.Zero, "rotation", allowFour: true);
            if (!w.HasValue) return new MapTransform(position, xyz, scale);
            var quaternion = new Quat(xyz.X, xyz.Y, xyz.Z, w.Value);
            if (quaternion.LengthSquared <= 0.000001f) throw Invalid("rotation");
            return new MapTransform(position, quaternion, scale);
        }
        internal static Vec3 Vector(object? raw, Vec3 fallback, string field, bool allowFour = false)
        {
            if (raw == null) return fallback;
            if (raw is List<object?> list && (list.Count == 3 || allowFour && list.Count == 4))
                return new Vec3(Number(list[0], field), Number(list[1], field), Number(list[2], field));
            var data = Object(raw, field);
            return new Vec3(Number(Value(data, "x") ?? 0d, field), Number(Value(data, "y") ?? 0d, field), Number(Value(data, "z") ?? 0d, field));
        }
        internal static float Number(object? raw, string field)
        {
            if (!(raw is double number) || double.IsNaN(number) || double.IsInfinity(number) || number > float.MaxValue || number < -float.MaxValue)
                throw Invalid(field);
            return (float)number;
        }
        internal static Dictionary<string, object?> Object(object? value, string field) => value as Dictionary<string, object?> ?? throw Invalid(field);
        internal static object? Value(Dictionary<string, object?> data, string key) => data.TryGetValue(key, out var value) ? value : null;
        internal static string Text(Dictionary<string, object?> data, string key, int maximum)
        {
            var value = Value(data, key);
            if (value == null) return string.Empty;
            if (!(value is string text) || text.Length > maximum || text.Any(char.IsControl)) throw Invalid(key);
            return text;
        }
        internal static string? OptionalText(Dictionary<string, object?> data, string key, int maximum)
        { var text = Text(data, key, maximum); return text.Length == 0 ? null : text; }
        private static void Identity(string key, Dictionary<string, object?> data, string field, int maximum = 64)
        {
            var id = Text(data, "id", maximum);
            if (!AuthoredText.IsId(key, maximum) || id.Length != 0 && id != key) throw Invalid(field);
        }
        internal static FormatException Invalid(string field) => new FormatException("Invalid map field: " + field + ". Re-export the map or refresh its survey.");
    }
}
