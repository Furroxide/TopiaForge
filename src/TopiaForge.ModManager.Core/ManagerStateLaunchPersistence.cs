using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TopiaForge.ModManager.Core
{
    public static class ManagerStateLaunchPersistence
    {
        public static ManagerState Load(string path)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { return Missing(path); }
            catch (DirectoryNotFoundException) { return Missing(path); }
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new IOException("Manager state must be an ordinary file: " + path);
            return Parse(JsonUtil.ReadBoundedJsonObject(path));
        }
        private static ManagerState Missing(string path)
        {
            if (File.Exists(path + JsonUtil.BackupSuffix))
                throw new InvalidDataException("Manager state is missing but a backup exists. Restore it explicitly before saving.");
            return Parse("{}");
        }
        public static string Serialize(ManagerState state) => JsonObjectMerge.Merge(JsonUtil.Serialize(state),
            new Dictionary<string, string> { ["launchSelection"] = LaunchTransportJson.WriteSelection(state.LaunchSelection ?? LaunchSelection.UnresolvedLegacy("{}")) });
        public static ManagerState Parse(string json)
        {
            var entries = JsonObjectMerge.ReadProperties(json).ToArray();
            if (entries.GroupBy(entry => entry.Name, StringComparer.Ordinal).Any(group => group.Count() != 1))
                throw new InvalidDataException("Manager state contains duplicate fields.");
            var fields = entries.ToDictionary(entry => entry.Name, entry => entry.RawValue.Trim(), StringComparer.Ordinal);
            ValidateEnvelope(fields);
            // Legacy and versioned selection values are read before typed defaulting can erase them.
            var baseJson = "{" + string.Join(",", entries.Where(entry => entry.Name != "worldLaunch" && entry.Name != "launchSelection" && entry.Name != "autoLoadOnStart")
                .Select(entry => JsonUtil.Serialize(entry.Name) + ":" + entry.RawValue)) + "}";
            var state = JsonUtil.Deserialize<ManagerState>(baseJson);
            if (fields.TryGetValue("launchSelection", out var selection)) state.LaunchSelection = LaunchTransportJson.ReadSelection(selection);
            else
            {
                var legacy = fields.TryGetValue("worldLaunch", out var world) ? "{\"worldLaunch\":" + world + "}" : "{}";
                state.LaunchSelection = LaunchSelection.UnresolvedLegacy(legacy);
            }
            if (fields.TryGetValue("autoLoadOnStart", out var automatic))
            {
                if (automatic != "true" && automatic != "false") throw new InvalidDataException("Manager autoLoadOnStart must be a boolean.");
                state.AutoLoadOnStart = automatic == "true";
            }
            else if (fields.TryGetValue("worldLaunch", out var legacyWorld) && legacyWorld.StartsWith("{", StringComparison.Ordinal))
            {
                var automaticFields = JsonObjectMerge.ReadProperties(legacyWorld).Where(field => field.Name == "autoLoadOnStart").ToArray();
                state.AutoLoadOnStart = automaticFields.Length == 1 && automaticFields[0].RawValue.Trim() == "true";
            }
            return state;
        }
        private static void ValidateEnvelope(IReadOnlyDictionary<string, string> fields)
        {
            if (fields.TryGetValue("schemaVersion", out var version) && version != "0" && version != "1")
                throw new InvalidDataException("Manager schemaVersion must be integer 0 or 1.");
            if (!fields.TryGetValue("mods", out var raw)) return;
            var records = JsonObjectMerge.ReadArrayValues(raw);
            if (records.Count > 4096) throw new InvalidDataException("Manager state cannot contain more than 4096 mod records.");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in records)
            {
                var entries = JsonObjectMerge.ReadProperties(record);
                if (entries.GroupBy(field => field.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
                    throw new InvalidDataException("Manager mod record contains duplicate fields.");
                var identity = entries.SingleOrDefault(field => field.Name == "id");
                if (identity == null || !identity.RawValue.Trim().StartsWith("\"", StringComparison.Ordinal)
                    || !ManifestValidator.IsValidId(JsonUtil.Deserialize<string>(identity.RawValue)))
                    throw new InvalidDataException("Manager mod records must retain a valid package identity; repair the original record.");
                foreach (var field in entries)
                {
                    var value = field.RawValue.Trim();
                    switch (field.Name)
                    {
                        case "enabled":
                        case "versionPinned":
                        case "uninstallPending":
                        case "restartRequired":
                            if (value != "true" && value != "false") throw new InvalidDataException("Manager mod " + field.Name + " must be boolean.");
                            break;
                        case "id":
                        case "version":
                        case "name":
                        case "installedAtUtc":
                        case "updatedAtUtc":
                        case "quarantineReason":
                        case "quarantinedAtUtc":
                            if (!value.StartsWith("\"", StringComparison.Ordinal)) throw new InvalidDataException("Manager mod " + field.Name + " must be a string.");
                            if (field.Name == "id" && !ids.Add(JsonUtil.Deserialize<string>(value))) throw new InvalidDataException("Manager state contains duplicate mod identities.");
                            break;
                    }
                }
            }
        }
    }
}
