using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TopiaForge.Mods.Internal
{
    internal sealed partial class AuthoredMapIndex
    {
        private static MapAnchorResolution Failure(ModErrorCode code, string message) =>
            new MapAnchorResolution(Array.Empty<ResolvedAnchor>(), false, message, code);
        public MapAnchorResolution ResolveAnchors(string packagePath)
        {
            if (!IsAlive) return Failure(ModErrorCode.InvalidState, "This map's world session has ended.");
            if (!AuthoredText.PackagePath(packagePath)) return Failure(ModErrorCode.InvalidArgument, "anchors file: use a safe package-relative path.");
            if (readAnchors == null) return Failure(ModErrorCode.InvalidArgument, "Anchors file '" + packagePath + "' is unavailable in this package.");
            try
            {
                var read = readAnchors(packagePath);
                if (!read.TryGetValue(out var bytes)) return Failure(ModErrorCode.InvalidArgument, "Anchors file '" + packagePath + "' is missing or unreadable.");
                return ResolveAnchors(ReadAnchors(bytes));
            }
            catch (Exception error) when (error is FormatException || error is ArgumentException)
            { return Failure(ModErrorCode.InvalidArgument, "Anchors file '" + packagePath + "': " + error.Message); }
        }
        internal static IReadOnlyList<MapAnchor> ReadAnchors(byte[] bytes)
        {
            if (bytes.Length > 256 * 1024) throw AuthoredProject.Invalid("anchors size");
            var root = MapJson.Read(bytes);
            if (!(AuthoredProject.Value(root, "schemaVersion") is double version) || version != 1)
                throw AuthoredProject.Invalid("anchors schemaVersion");
            if (!(AuthoredProject.Value(root, "anchors") is List<object?> entries) || entries.Count > 1024)
                throw AuthoredProject.Invalid("anchors");
            var result = new List<MapAnchor>();
            foreach (var entry in entries)
            {
                var data = AuthoredProject.Object(entry, "anchor");
                var rawRequired = AuthoredProject.Value(data, "required");
                if (rawRequired != null && !(rawRequired is bool)) throw AuthoredProject.Invalid("required");
                var rawPosition = AuthoredProject.Value(data, "editorPosition");
                result.Add(new MapAnchor(AuthoredProject.Text(data, "key", 64), AuthoredProject.Text(data, "entityId", 64),
                    AuthoredProject.OptionalText(data, "name", 128), AuthoredProject.OptionalText(data, "modelId", 256),
                    rawPosition == null ? (Vec3?)null : AuthoredProject.Vector(rawPosition, Vec3.Zero, "editorPosition"),
                    rawRequired == null || (bool)rawRequired, AuthoredProject.Value(data, "positionTolerance") == null ? 1f :
                        AuthoredProject.Number(AuthoredProject.Value(data, "positionTolerance"), "positionTolerance")));
            }
            return result.AsReadOnly();
        }
        public MapAnchorResolution ResolveAnchors(IEnumerable<MapAnchor> anchors)
        {
            if (!IsAlive) return Failure(ModErrorCode.InvalidState, "This map's world session has ended.");
            if (anchors == null) return Failure(ModErrorCode.InvalidArgument, "anchors must be a bounded collection.");
            var outcomes = new List<ResolvedAnchor>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var report = new StringBuilder();
            var complete = true;
            foreach (var anchor in anchors)
            {
                if (anchor == null || outcomes.Count >= 1024 || !keys.Add(anchor.Key))
                    return Failure(ModErrorCode.InvalidArgument, "anchors must contain at most 1024 unique keys.");
                var match = AnchorMatch.Missing;
                AuthoredEntity? found = null;
                if (entities.TryGetValue(anchor.EntityId, out found)) match = AnchorMatch.Id;
                else if (anchor.Name != null && anchor.ModelId != null && anchor.EditorPosition.HasValue)
                {
                    var candidates = FindByModel(anchor.ModelId).Where(e => e.Name == anchor.Name &&
                        Distance(e.EditorTransform.Position, anchor.EditorPosition.Value) <= anchor.PositionTolerance).Take(2).ToArray();
                    if (candidates.Length == 1) { found = candidates[0]; match = AnchorMatch.NameModelAndPosition; }
                    else if (candidates.Length > 1) match = AnchorMatch.Ambiguous;
                }
                var changed = found != null && (found.Id != anchor.EntityId || anchor.Name != null && found.Name != anchor.Name ||
                    anchor.ModelId != null && found.ModelId != anchor.ModelId || anchor.EditorPosition.HasValue &&
                    Distance(found.EditorTransform.Position, anchor.EditorPosition.Value) > 0.001);
                var alive = found != null && found.Entity.IsAlive;
                if (anchor.Required && !alive) complete = false;
                outcomes.Add(new ResolvedAnchor(anchor, match, found, changed));
                report.Append(anchor.Key).Append(": ").Append(match);
                if (changed) report.Append(" (changed since survey)");
                if (found != null && !alive) report.Append(" (live marker missing)");
                report.AppendLine(".");
            }
            report.Append("Run topiaforge world survey <world> to re-survey.");
            return new MapAnchorResolution(outcomes.AsReadOnly(), complete, report.ToString());
        }
        private static double Distance(Vec3 left, Vec3 right)
        { var x = (double)left.X - right.X; var y = (double)left.Y - right.Y; var z = (double)left.Z - right.Z; return Math.Sqrt(x * x + y * y + z * z); }
    }
}
