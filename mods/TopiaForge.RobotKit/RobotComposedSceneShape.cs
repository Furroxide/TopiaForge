using System;
using System.Collections.Generic;

namespace TopiaForge.RobotKit
{
    // Data-only qualification, shared by the native bridge and the focused managed tests.
    internal static class RobotComposedSceneShape
    {
        internal static bool TryRead(string json, out Dictionary<string, object?>? scene)
        {
            scene = null;
            if (string.IsNullOrEmpty(json) || json.Length > 65536
                || !(MiniJson.Deserialize(json) is Dictionary<string, object?> value)
                || value.ContainsKey("scenes")
                || !(value.TryGetValue("entities", out var records) && records is Dictionary<string, object?> entities)
                || entities.Count != 1) return false;
            foreach (var record in entities.Values)
            {
                if (!(record is Dictionary<string, object?> entity)
                    || (entity.TryGetValue("parent", out var parent) && parent != null && !Equals(parent, ""))
                    || !(entity.TryGetValue("components", out var raw) && raw is Dictionary<string, object?> parts)
                    || parts.Count != 3
                    || !(parts.TryGetValue("transform", out var transform) && transform is Dictionary<string, object?>)
                    || !(parts.TryGetValue("agent", out var agent) && agent is Dictionary<string, object?>)
                    || !(parts.TryGetValue("humanoid-body-parts", out var body) && body is Dictionary<string, object?> bodyParts)
                    || bodyParts.Count == 0 || bodyParts.Count > 64) return false;
                foreach (var part in bodyParts.Values) if (!(part is string)) return false;
            }
            scene = value;
            return true;
        }

        internal static bool IsMunicipalPath(string? path)
        {
            // Match the observed native inventory classification. The complete path, rather than an
            // unverified basename convention, carries the token; discovery requires one eligible entry.
            return !string.IsNullOrWhiteSpace(path) && path!.Length <= 2048
                && path.IndexOf("MuniciPal", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}