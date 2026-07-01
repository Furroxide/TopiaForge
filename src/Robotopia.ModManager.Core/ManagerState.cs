using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace Robotopia.ModManager.Core
{
    [DataContract]
    public sealed class ManagerState
    {
        [DataMember(Name = "mods")]
        public List<InstalledModState> Mods { get; set; } = new List<InstalledModState>();

        public InstalledModState? Find(string id)
        {
            return Mods.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public InstalledModState Upsert(ModManifest manifest, bool enabled, bool restartRequired)
        {
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
            existing.Enabled = enabled;
            existing.UninstallPending = false;
            existing.RestartRequired = restartRequired;
            existing.UpdatedAtUtc = DateTime.UtcNow.ToString("O");
            return existing;
        }

        public void Remove(string id)
        {
            Mods.RemoveAll(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public void ClearAppliedRestartRequirements()
        {
            foreach (var mod in Mods.Where(m => !m.UninstallPending))
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
    }
}
