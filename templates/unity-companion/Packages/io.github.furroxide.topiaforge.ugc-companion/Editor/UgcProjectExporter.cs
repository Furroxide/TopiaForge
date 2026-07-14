using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace TopiaForge.UgcCompanion.Editor
{
    /// <summary>
    /// Serializes a Unity scene hierarchy (objects tagged with UGC markers) into the exact UgcExportProject JSON
    /// the game imports — see docs/UgcLiveSync.md for the contract. Writes by hand (no Newtonsoft dependency) and
    /// applies the inverse coordinate handedness so a round-trip through the game's importer is identity:
    /// position X is negated, scale is unchanged, and rotation is conjugated by the Scale(-1,1,1) basis.
    /// </summary>
    public static class UgcProjectExporter
    {
        /// <summary>Builds the project JSON for every <see cref="UgcEntityMarker"/> under <paramref name="root"/>.</summary>
        public static string BuildProjectJson(Transform root, string projectName, string sceneId, string sceneName, string environment)
        {
            if (root == null)
            {
                throw new ArgumentNullException(nameof(root));
            }

            var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            var resolvedSceneId = string.IsNullOrWhiteSpace(sceneId) ? "main" : sceneId;
            var resolvedSceneName = string.IsNullOrWhiteSpace(sceneName) ? root.name : sceneName;

            var sb = new StringBuilder(4096);
            sb.Append('{');
            Prop(sb, "version", "1.0"); sb.Append(',');
            Prop(sb, "name", string.IsNullOrWhiteSpace(projectName) ? root.name : projectName); sb.Append(',');
            Prop(sb, "created", now); sb.Append(',');
            Prop(sb, "modified", now); sb.Append(',');
            sb.Append("\"assets\":{},");
            sb.Append("\"local-assets\":{},");
            sb.Append("\"scenes\":{");
            sb.Append(Escape(resolvedSceneId)).Append(':');
            AppendScene(sb, root, resolvedSceneId, resolvedSceneName, string.IsNullOrWhiteSpace(environment) ? "day" : environment, now);
            sb.Append('}');
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>Builds the JSON and atomically writes it to <paramref name="folder"/> as <c>&lt;projectName&gt;.json</c>.</summary>
        public static string ExportToFolder(Transform root, string folder, string projectName, string sceneId, string sceneName, string environment)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                throw new ArgumentException("Watch folder is not set.", nameof(folder));
            }

            Directory.CreateDirectory(folder);
            var json = BuildProjectJson(root, projectName, sceneId, sceneName, environment);
            var fileName = Sanitize(string.IsNullOrWhiteSpace(projectName) ? root.name : projectName) + ".json";
            var destination = Path.Combine(folder, fileName);

            // Write to a temp file in the same folder, then copy over the target so the game-side watcher never
            // reads a half-written file (it also debounces + validates, but this keeps writes clean).
            var temp = destination + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Copy(temp, destination, overwrite: true);
            File.Delete(temp);
            return destination;
        }

        private static void AppendScene(StringBuilder sb, Transform root, string sceneId, string sceneName, string environment, string now)
        {
            sb.Append('{');
            Prop(sb, "id", sceneId); sb.Append(',');
            Prop(sb, "name", sceneName); sb.Append(',');
            Prop(sb, "environment", environment); sb.Append(',');
            Prop(sb, "created", now); sb.Append(',');
            Prop(sb, "modified", now); sb.Append(',');
            sb.Append("\"entities\":{");

            var markers = root.GetComponentsInChildren<UgcEntityMarker>(includeInactive: true);
            var first = true;
            foreach (var marker in markers)
            {
                if (marker == null)
                {
                    continue;
                }

                if (!first)
                {
                    sb.Append(',');
                }

                first = false;
                sb.Append(Escape(marker.EntityId)).Append(':');
                AppendEntity(sb, root, marker);
            }

            sb.Append('}');
            sb.Append('}');
        }

        private static void AppendEntity(StringBuilder sb, Transform root, UgcEntityMarker marker)
        {
            var t = marker.transform;
            var parentId = FindParentEntityId(t, root);
            var parentTransform = string.IsNullOrEmpty(parentId) ? root : FindEntityTransform(t, root);

            // Transform relative to the parent entity (or scene root), robust to intermediate non-entity transforms.
            var rel = parentTransform.worldToLocalMatrix * t.localToWorldMatrix;
            var position = (Vector3)rel.GetColumn(3);
            var rotation = rel.rotation;
            var scale = rel.lossyScale;

            sb.Append('{');
            Prop(sb, "id", marker.EntityId); sb.Append(',');
            Prop(sb, "name", marker.gameObject.name); sb.Append(',');
            sb.Append("\"parent\":").Append(string.IsNullOrEmpty(parentId) ? "null" : Escape(parentId)).Append(',');
            sb.Append("\"components\":{");
            AppendTransform(sb, position, rotation, scale);
            AppendComponents(sb, marker.gameObject);
            sb.Append('}');
            sb.Append('}');
        }

        private static void AppendTransform(StringBuilder sb, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            // Inverse handedness so the game's UgcVector3Value.ToUnityPosition / UgcRotationHelper round-trips.
            var basis = Matrix4x4.Scale(new Vector3(-1f, 1f, 1f));
            var ugcRotation = (basis * Matrix4x4.Rotate(rotation) * basis).rotation;

            sb.Append("\"transform\":{");
            sb.Append("\"position\":"); AppendVec3(sb, -position.x, position.y, position.z); sb.Append(',');
            sb.Append("\"rotation\":{")
              .Append("\"x\":").Append(F(ugcRotation.x)).Append(',')
              .Append("\"y\":").Append(F(ugcRotation.y)).Append(',')
              .Append("\"z\":").Append(F(ugcRotation.z)).Append(',')
              .Append("\"w\":").Append(F(ugcRotation.w)).Append('}').Append(',');
            sb.Append("\"scale\":"); AppendVec3(sb, scale.x, scale.y, scale.z);
            sb.Append('}');
        }

        private static void AppendComponents(StringBuilder sb, GameObject go)
        {
            var model = go.GetComponent<UgcModelRenderer>();
            if (model != null && !string.IsNullOrWhiteSpace(model.assetId))
            {
                sb.Append(",\"model-renderer\":{");
                Prop(sb, "model", model.assetId);
                sb.Append('}');
            }

            var prefab = go.GetComponent<UgcPrefabInstance>();
            if (prefab != null && !string.IsNullOrWhiteSpace(prefab.assetId))
            {
                sb.Append(",\"prefab-instance\":{");
                Prop(sb, "scene", prefab.scene ?? string.Empty); sb.Append(',');
                Prop(sb, "model", prefab.assetId); sb.Append(',');
                Prop(sb, "accessory", prefab.accessory ?? string.Empty);
                sb.Append('}');
            }

            if (go.GetComponent<UgcSpawnLocationMarker>() != null)
            {
                sb.Append(",\"spawn-location\":{}");
            }

            var poi = go.GetComponent<UgcPoiMarker>();
            if (poi != null)
            {
                sb.Append(",\"poi\":{\"about\":");
                AppendStringArray(sb, poi.about); sb.Append(',');
                Prop(sb, "visualDescription", poi.visualDescription ?? string.Empty);
                sb.Append(",\"hidden\":").Append(poi.hidden ? "true" : "false");
                sb.Append('}');
            }

            var aoi = go.GetComponent<UgcAoiMarker>();
            if (aoi != null)
            {
                sb.Append(",\"aoi\":{\"about\":");
                AppendStringArray(sb, aoi.about); sb.Append(',');
                Prop(sb, "visualDescription", aoi.visualDescription ?? string.Empty);
                sb.Append(",\"size\":"); AppendVec3(sb, aoi.size.x, aoi.size.y, aoi.size.z);
                sb.Append('}');
            }

            var agent = go.GetComponent<UgcAgentMarker>();
            if (agent != null)
            {
                sb.Append(",\"agent\":{\"about\":");
                AppendStringArray(sb, agent.about); sb.Append(',');
                Prop(sb, "visualDescription", agent.visualDescription ?? string.Empty); sb.Append(',');
                Prop(sb, "personality", agent.personality ?? string.Empty);
                sb.Append('}');
            }
        }

        // Returns the parent entity id (nearest ancestor with a marker), or "" for scene-root entities.
        private static string FindParentEntityId(Transform t, Transform root)
        {
            var marker = FindParentMarker(t, root);
            return marker == null ? string.Empty : marker.EntityId;
        }

        private static Transform FindEntityTransform(Transform t, Transform root)
        {
            var marker = FindParentMarker(t, root);
            return marker == null ? root : marker.transform;
        }

        private static UgcEntityMarker FindParentMarker(Transform t, Transform root)
        {
            var current = t.parent;
            while (current != null && current != root)
            {
                var marker = current.GetComponent<UgcEntityMarker>();
                if (marker != null)
                {
                    return marker;
                }

                current = current.parent;
            }

            return null;
        }

        private static void AppendVec3(StringBuilder sb, float x, float y, float z)
        {
            sb.Append("{\"x\":").Append(F(x)).Append(",\"y\":").Append(F(y)).Append(",\"z\":").Append(F(z)).Append('}');
        }

        private static void AppendStringArray(StringBuilder sb, string[] values)
        {
            sb.Append('[');
            if (values != null)
            {
                for (var i = 0; i < values.Length; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    sb.Append(Escape(values[i] ?? string.Empty));
                }
            }

            sb.Append(']');
        }

        private static void Prop(StringBuilder sb, string name, string value)
        {
            sb.Append('"').Append(name).Append("\":").Append(Escape(value));
        }

        private static string F(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string Escape(string value)
        {
            value ??= string.Empty;
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            sb.Append('"');
            return sb.ToString();
        }

        private static string Sanitize(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            }

            var result = sb.ToString().Trim('_');
            return string.IsNullOrEmpty(result) ? "ugc-project" : result;
        }
    }
}
