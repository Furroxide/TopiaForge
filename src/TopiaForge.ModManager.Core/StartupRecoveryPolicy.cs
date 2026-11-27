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
    public static partial class StartupRecoveryPolicy
    {
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
