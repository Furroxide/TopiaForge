using System;

namespace TopiaForge.ModManager.Core
{
    /// <summary>Recovery changes process enablement without rewriting the immutable launcher command.</summary>
    public sealed class RuntimeStartupSelection
    {
        internal RuntimeStartupSelection(ProfileLaunchConfigurationV4? requested, bool safeMode, string quarantinedPackageId)
        { Requested = requested; SafeMode = safeMode || requested?.SafeMode == true; QuarantinedPackageId = quarantinedPackageId; }
        public ProfileLaunchConfigurationV4? Requested { get; }
        public bool SafeMode { get; }
        public string QuarantinedPackageId { get; }
        public ManagerState CreateEffectiveState(ManagerState durable)
        {
            var effective = JsonUtil.Clone(durable ?? throw new ArgumentNullException(nameof(durable)));
            effective.LaunchSelection = durable.LaunchSelection;
            ApplyTo(effective);
            return effective;
        }
        public void ApplyTo(ManagerState state)
        {
            Requested?.ApplyTo(state);
            foreach (var mod in state.Mods)
                if (SafeMode || string.Equals(mod.Id, QuarantinedPackageId, StringComparison.OrdinalIgnoreCase)) mod.Enabled = false;
        }
    }
    public static partial class StartupRecoveryPolicy
    {
        public static RuntimeStartupSelection Prepare(ProfileLaunchConfigurationV4? requested, ManagerState durable,
            StartupRecoveryDecision recovery, DateTime utcNow)
        {
            if (durable == null) throw new ArgumentNullException(nameof(durable));
            if (recovery == null) throw new ArgumentNullException(nameof(recovery));
            var quarantined = recovery.QuarantineModId;
            var valid = quarantined.Length == 0 || ManifestValidator.IsValidId(quarantined);
            if (valid && quarantined.Length != 0) ApplyQuarantine(durable, quarantined, recovery.Reason, utcNow);
            return new RuntimeStartupSelection(requested, recovery.SafeMode || !valid, valid ? quarantined : string.Empty);
        }
    }
}
