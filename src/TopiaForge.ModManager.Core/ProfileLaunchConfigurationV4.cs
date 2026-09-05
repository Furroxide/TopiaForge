using System;
using System.Collections.Generic;
using System.Linq;

namespace TopiaForge.ModManager.Core
{
    /// <summary>Inactive v4 command wire. Production remains on ProfileLaunchConfiguration v3 until both ends activate.</summary>
    public sealed class ProfileLaunchConfigurationV4
    {
        public const int SchemaVersion = 4;

        public ProfileLaunchConfigurationV4(string profileId, int profileRevision, string requestId, string command,
            IEnumerable<PackageIdentity> packages, string digest, bool safeMode, bool inheritManagerModState,
            IEnumerable<string> enabledMods, IReadOnlyDictionary<string, string> selectedVersions,
            LaunchPlanDescriptor? plan = null)
        {
            ProfileId = LaunchContractValues.Token(profileId, nameof(profileId));
            ProfileRevision = LaunchContractValues.Revision(profileRevision, nameof(profileRevision));
            RequestId = LaunchContractValues.Token(requestId, nameof(requestId));
            Command = LaunchContractValues.Choice(command, nameof(command), LaunchContractValues.Commands);
            Packages = LaunchContractValues.Packages(packages);
            Digest = LaunchContractValues.Digest(digest);
            if (Digest != PackageSetDigest.Of(Packages)) throw new ArgumentException("Profile digest must match its package set.");
            SafeMode = safeMode;
            InheritManagerModState = inheritManagerModState;
            var enabled = enabledMods.ToArray();
            if (enabled.Any(id => !ManifestValidator.IsValidId(id))
                || enabled.Distinct(StringComparer.OrdinalIgnoreCase).Count() != enabled.Length)
                throw new ArgumentException("Enabled package ids must be valid and unique.", nameof(enabledMods));
            EnabledMods = LaunchContractValues.Copy(enabled.OrderBy(id => id, StringComparer.Ordinal));
            if (selectedVersions.Any(pair => !ManifestValidator.IsValidId(pair.Key) || !VersionUtil.TryParse(pair.Value, out _))
                || selectedVersions.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != selectedVersions.Count)
                throw new ArgumentException("Selected versions must map unique package ids to exact versions.", nameof(selectedVersions));
            foreach (var pair in selectedVersions) LaunchContractValues.Version(pair.Value, nameof(selectedVersions));
            SelectedVersions = LaunchContractValues.Dictionary(selectedVersions);
            if (!SafeMode && !InheritManagerModState && !new HashSet<string>(EnabledMods, StringComparer.OrdinalIgnoreCase)
                .SetEquals(Packages.Select(package => package.Id)))
                throw new ArgumentException("An explicit enabled selection must equal its effective package set.");
            if (Packages.Any(package => SelectedVersions.Any(pin => string.Equals(pin.Key, package.Id, StringComparison.OrdinalIgnoreCase)
                && pin.Value != package.Version)))
                throw new ArgumentException("A selected version pin disagrees with its effective package identity.");
            Plan = plan;
            if ((Command == "main-menu" && Plan != null) || (Command == "launch-target" && Plan == null)
                || (SafeMode && Command != "main-menu"))
                throw new ArgumentException("Command, safe mode, and launch plan disagree.");
            if (Plan != null && (Plan.Digest != Digest || !LaunchContractValues.SamePackages(Packages, Plan.Packages)))
                throw new ArgumentException("Profile and launch plan package identities must agree.");
        }

        public string ProfileId { get; }
        public int ProfileRevision { get; }
        public string RequestId { get; }
        public string Command { get; }
        public IReadOnlyList<PackageIdentity> Packages { get; }
        public string Digest { get; }
        public bool SafeMode { get; }
        public bool InheritManagerModState { get; }
        public IReadOnlyList<string> EnabledMods { get; }
        public IReadOnlyDictionary<string, string> SelectedVersions { get; }
        public LaunchPlanDescriptor? Plan { get; }
    }
}
