using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace TopiaForge.ModManager.Core
{
    [DataContract]
    public sealed class ManagerState
    {
        public const int CurrentSchemaVersion = 1;

        [DataMember(Name = "schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [DataMember(Name = "mods")]
        public List<InstalledModState> Mods { get; set; } = new List<InstalledModState>();

        /// <summary>
        /// Migrates the pre-envelope state and removes malformed or duplicate records before any package
        /// operation consumes them. The last record wins so an interrupted append/merge cannot resurrect an
        /// older selection later in the document.
        /// </summary>
        public void Normalize()
        {
            if (SchemaVersion == 0)
            {
                SchemaVersion = CurrentSchemaVersion;
            }
            else if (SchemaVersion != CurrentSchemaVersion)
            {
                throw new System.Runtime.Serialization.SerializationException(
                    "Unsupported manager state schemaVersion " + SchemaVersion + ". Expected " + CurrentSchemaVersion + ".");
            }

            var source = Mods ?? new List<InstalledModState>();
            if (source.Count > 4096)
            {
                throw new SerializationException("Manager state cannot contain more than 4096 mod records.");
            }

            var normalized = new Dictionary<string, InstalledModState>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in source)
            {
                if (mod == null || string.IsNullOrWhiteSpace(mod.Id) || !ManifestValidator.IsValidId(mod.Id))
                {
                    continue;
                }

                mod.Id = mod.Id.Trim();
                mod.Name = mod.Name ?? string.Empty;
                mod.Version = mod.Version ?? string.Empty;
                mod.InstalledAtUtc = mod.InstalledAtUtc ?? string.Empty;
                mod.UpdatedAtUtc = mod.UpdatedAtUtc ?? string.Empty;
                mod.QuarantineReason = mod.QuarantineReason ?? string.Empty;
                mod.QuarantinedAtUtc = mod.QuarantinedAtUtc ?? string.Empty;
                normalized[mod.Id] = mod;
            }

            Mods = normalized.Values
                .OrderBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public InstalledModState? Find(string id)
        {
            return (Mods ?? new List<InstalledModState>())
                .FirstOrDefault(m => m != null && string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public InstalledModState Upsert(ModManifest manifest, bool enabled, bool restartRequired)
        {
            Mods = Mods ?? new List<InstalledModState>();
            var existing = Find(manifest.Id);
            if (existing == null)
            {
                existing = new InstalledModState
                {
                    Id = manifest.Id,
                    InstalledAtUtc = DateTime.UtcNow.ToString("O")
                };
                Mods.Add(existing);
            }

            existing.Name = manifest.Name;
            existing.Version = manifest.Version;
            existing.VersionPinned = false;
            existing.Enabled = enabled;
            existing.UninstallPending = false;
            existing.RestartRequired = restartRequired;
            existing.UpdatedAtUtc = DateTime.UtcNow.ToString("O");
            return existing;
        }

        public void Remove(string id)
        {
            Mods = Mods ?? new List<InstalledModState>();
            Mods.RemoveAll(m => m == null || string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public void ClearAppliedRestartRequirements()
        {
            foreach (var mod in (Mods ?? new List<InstalledModState>()).Where(m => m != null && !m.UninstallPending))
            {
                mod.RestartRequired = false;
            }
        }
    }

    [DataContract]
    public sealed class InstalledModState
    {
        [DataMember(Name = "id")]
        public string Id { get; set; } = string.Empty;

        [DataMember(Name = "name")]
        public string Name { get; set; } = string.Empty;

        [DataMember(Name = "version")]
        public string Version { get; set; } = string.Empty;

        /// <summary>
        /// True only for an exact, externally requested version (currently a one-shot launcher profile).
        /// Missing pinned bytes fail closed; ordinary installed state is automatically reconciled to the
        /// highest valid compatible version found on disk.
        /// </summary>
        [DataMember(Name = "versionPinned")]
        public bool VersionPinned { get; set; }

        [DataMember(Name = "enabled")]
        public bool Enabled { get; set; } = true;

        [DataMember(Name = "restartRequired")]
        public bool RestartRequired { get; set; }

        [DataMember(Name = "uninstallPending")]
        public bool UninstallPending { get; set; }

        [DataMember(Name = "installedAtUtc")]
        public string InstalledAtUtc { get; set; } = string.Empty;

        [DataMember(Name = "updatedAtUtc")]
        public string UpdatedAtUtc { get; set; } = string.Empty;

        [DataMember(Name = "quarantineReason")]
        public string QuarantineReason { get; set; } = string.Empty;

        [DataMember(Name = "quarantinedAtUtc")]
        public string QuarantinedAtUtc { get; set; } = string.Empty;
    }
}
