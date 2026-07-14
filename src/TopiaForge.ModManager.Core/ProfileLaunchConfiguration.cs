using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace TopiaForge.ModManager.Core
{
    /// <summary>
    /// One-process profile selection supplied by the standalone launcher.
    /// Applying it always returns a clone so the durable manager state cannot
    /// be overwritten by profile or safe-mode choices.
    /// </summary>
    [DataContract]
    public sealed class ProfileLaunchConfiguration
    {
        public const int CurrentSchemaVersion = 2;
        public const string EnvironmentVariable = "TOPIAFORGE_LAUNCH_PROFILE";

        [DataMember(Name = "schemaVersion")]
        public int SchemaVersion { get; set; }

        [DataMember(Name = "profileId")]
        public string ProfileId { get; set; } = string.Empty;

        [DataMember(Name = "safeMode")]
        public bool SafeMode { get; set; }

        [DataMember(Name = "inheritManagerModState")]
        public bool InheritManagerModState { get; set; }

        [DataMember(Name = "enabledMods")]
        public List<string> EnabledMods { get; set; } = new List<string>();

        [DataMember(Name = "selectedVersions")]
        public Dictionary<string, string> SelectedVersions { get; set; }
            = new Dictionary<string, string>();

        public IReadOnlyList<string> Validate()
        {
            var errors = new List<string>();
            if (SchemaVersion != CurrentSchemaVersion)
            {
                errors.Add("schemaVersion must be " + CurrentSchemaVersion + ".");
            }

            if (!IsValidProfileId(ProfileId))
            {
                errors.Add("profileId must contain 1-128 safe identifier characters.");
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in EnabledMods ?? new List<string>())
            {
                if (!ManifestValidator.IsValidId(id))
                {
                    errors.Add("enabledMods contains an invalid mod id: " + id + ".");
                }
                else if (!seen.Add(id))
                {
                    errors.Add("enabledMods contains duplicate mod id: " + id + ".");
                }
            }

            seen.Clear();
            foreach (var entry in SelectedVersions ?? new Dictionary<string, string>())
            {
                if (!ManifestValidator.IsValidId(entry.Key))
                {
                    errors.Add("selectedVersions contains an invalid mod id: " + entry.Key + ".");
                }
                else if (!seen.Add(entry.Key))
                {
                    errors.Add("selectedVersions contains duplicate mod id: " + entry.Key + ".");
                }

                if (!VersionUtil.TryParse(entry.Value, out _))
                {
                    errors.Add("selectedVersions contains an invalid version for " + entry.Key + ".");
                }
            }

            return errors;
        }

        public ManagerState CreateEffectiveState(ManagerState durableState)
        {
            if (durableState == null)
            {
                throw new ArgumentNullException(nameof(durableState));
            }

            var errors = Validate();
            if (errors.Count != 0)
            {
                throw new SerializationException(string.Join(" ", errors));
            }

            var effective = JsonUtil.Clone(durableState);
            ApplyTo(effective);
            return effective;
        }

        public void ApplyTo(ManagerState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (SafeMode)
            {
                foreach (var mod in state.Mods)
                {
                    mod.Enabled = false;
                }
                return;
            }

            if (!InheritManagerModState)
            {
                var enabled = new HashSet<string>(
                    EnabledMods ?? new List<string>(),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var mod in state.Mods)
                {
                    mod.Enabled = enabled.Contains(mod.Id);
                }

                foreach (var id in enabled.Where(id => state.Find(id) == null))
                {
                    state.Mods.Add(new InstalledModState { Id = id, Enabled = true });
                }
            }

            foreach (var entry in SelectedVersions ?? new Dictionary<string, string>())
            {
                var mod = state.Find(entry.Key);
                if (mod == null)
                {
                    mod = new InstalledModState
                    {
                        Id = entry.Key,
                        Enabled = InheritManagerModState || (EnabledMods ?? new List<string>()).Contains(
                            entry.Key, StringComparer.OrdinalIgnoreCase)
                    };
                    state.Mods.Add(mod);
                }
                mod.Version = entry.Value;
            }
        }

        private static bool IsValidProfileId(string? id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 128 || !IsAsciiLetterOrDigit(id[0]))
            {
                return false;
            }

            for (var index = 1; index < id.Length; index++)
            {
                var character = id[index];
                if (!IsAsciiLetterOrDigit(character) && character != '.' && character != '-' && character != '_')
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsAsciiLetterOrDigit(char value)
        {
            return (value >= 'A' && value <= 'Z') ||
                   (value >= 'a' && value <= 'z') ||
                   (value >= '0' && value <= '9');
        }
    }
}
