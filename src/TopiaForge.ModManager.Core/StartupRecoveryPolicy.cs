using System;
using System.Collections.Generic;
using System.Linq;

namespace TopiaForge.ModManager.Core
{
    /// <summary>
    /// Applies startup-journal recovery after a one-shot launcher profile has been consumed. Recovery always
    /// takes precedence over caller-selected enablement, while preserving every profile choice that is safe to
    /// honor for the current process.
    /// </summary>
    public static class StartupRecoveryPolicy
    {
        /// <summary>
        /// Applies <paramref name="recovery"/> to durable state and returns the effective one-shot profile.
        /// The supplied profile is never mutated.
        /// </summary>
        public static ProfileLaunchConfiguration? Apply(
            ProfileLaunchConfiguration? requestedProfile,
            ManagerState durableState,
            StartupRecoveryDecision recovery,
            DateTime utcNow)
        {
            if (durableState == null)
            {
                throw new ArgumentNullException(nameof(durableState));
            }

            if (recovery == null)
            {
                throw new ArgumentNullException(nameof(recovery));
            }

            var quarantineModId = recovery.QuarantineModId ?? string.Empty;
            var quarantineIsValid = quarantineModId.Length == 0 || ManifestValidator.IsValidId(quarantineModId);
            var forceSafeMode = recovery.SafeMode || !quarantineIsValid;
            if (quarantineModId.Length > 0 && quarantineIsValid)
            {
                ApplyQuarantine(durableState, quarantineModId, recovery.Reason, utcNow);
            }

            if (!forceSafeMode && quarantineModId.Length == 0)
            {
                return requestedProfile;
            }

            if (requestedProfile == null)
            {
                return forceSafeMode
                    ? new ProfileLaunchConfiguration
                    {
                        SchemaVersion = ProfileLaunchConfiguration.CurrentSchemaVersion,
                        ProfileId = "automatic-startup-recovery",
                        SafeMode = true,
                        InheritManagerModState = false
                    }
                    : null;
            }

            return new ProfileLaunchConfiguration
            {
                SchemaVersion = requestedProfile.SchemaVersion,
                ProfileId = requestedProfile.ProfileId,
                SafeMode = forceSafeMode || requestedProfile.SafeMode,
                InheritManagerModState = requestedProfile.InheritManagerModState,
                EnabledMods = (requestedProfile.EnabledMods ?? new List<string>())
                    .Where(id => quarantineModId.Length == 0 ||
                        !string.Equals(id, quarantineModId, StringComparison.OrdinalIgnoreCase))
                    .ToList(),
                SelectedVersions = new Dictionary<string, string>(
                    requestedProfile.SelectedVersions ?? new Dictionary<string, string>(),
                    StringComparer.OrdinalIgnoreCase)
            };
        }

        private static void ApplyQuarantine(
            ManagerState durableState,
            string modId,
            string reason,
            DateTime utcNow)
        {
            durableState.Mods = durableState.Mods ?? new List<InstalledModState>();
            var quarantined = durableState.Find(modId);
            if (quarantined == null)
            {
                quarantined = new InstalledModState { Id = modId };
                durableState.Mods.Add(quarantined);
            }

            quarantined.Enabled = false;
            quarantined.RestartRequired = false;
            quarantined.QuarantineReason = reason ?? string.Empty;
            quarantined.QuarantinedAtUtc = utcNow.ToUniversalTime().ToString("O");
        }
    }
}
