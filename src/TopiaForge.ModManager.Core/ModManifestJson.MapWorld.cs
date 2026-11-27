using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace TopiaForge.ModManager.Core
{
    public static partial class ModManifestJson
    {
        private static void ValidateRawMap(IReadOnlyList<JsonObjectMerge.RawJsonProperty> content, string path,
            IReadOnlyList<JsonObjectMerge.RawJsonProperty> world, string worldPath)
        {
            foreach (var field in new[] { "bundle", "prefab", "sceneName", "implementation" }) RawForbidden(content, path, field);
            var map = RawText(content, path, "map", 1, 64);
            var file = RawText(content, path, "file", 1, 1024);
            var scene = RawText(content, path, "scene", 1, 128);
            if ((map == null) == (file == null)) RawFailure(path, "requires exactly one of map or file.");
            if (map != null && !Regex.IsMatch(map, @"\A[A-Za-z0-9_-]{1,64}\z"))
                RawFailure(path + ".map", "must be an ASCII map identifier, not a page URL or path.");
            if (scene != null && !Regex.IsMatch(scene, @"\A[A-Za-z0-9_-]{1,128}\z"))
                RawFailure(path + ".scene", "must be an ASCII scene identifier.");
            RawPath(content, path, "file", dll: false);
            if (file != null && !file.EndsWith(".roboworld", StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".json.gz", StringComparison.OrdinalIgnoreCase))
                RawFailure(path + ".file", "must end in .roboworld, .json or .json.gz.");
            RawStrings(world, worldPath, "transitions", 1, 1, (value, at) =>
            {
                if (value != ModTransitions.SceneReplacement) RawFailure(at, "map worlds require scene-replacement.");
            });
        }
    }
}
