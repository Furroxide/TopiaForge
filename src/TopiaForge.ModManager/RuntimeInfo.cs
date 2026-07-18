using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed class RuntimeInfo : IRuntimeInfo
    {
        private static readonly IReadOnlyDictionary<string, string> KnownProviders =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["io.github.furroxide.topiaforge.robotkit"] = "robotkit",
                ["io.github.furroxide.topiaforge.worlds"] = "worlds",
                ["io.github.furroxide.topiaforge.chronos"] = "chronos",
                ["io.github.furroxide.topiaforge.prompts"] = "prompts",
                ["io.github.furroxide.topiaforge.ugc.livesync"] = "ugc"
            };

        private readonly Dictionary<string, SemanticVersion> providerVersions =
            new Dictionary<string, SemanticVersion>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> unavailableCapabilities =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> reportedCapabilityOwners =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly IReadOnlyDictionary<string, string> unavailableCapabilitiesView;
        private readonly SemanticVersion? gameVersion;
        private Action? capabilityRefresher;
        private bool refreshingCapabilities;

        public RuntimeInfo(string? gameVersion = null)
        {
            LoaderVersion = SemanticVersion.Parse(TopiaForgeVersions.LoaderVersion);
            SdkVersion = SemanticVersion.Parse(TopiaForgeVersions.SdkVersion);
            this.gameVersion = SemanticVersion.TryParse(gameVersion, out var parsedGameVersion)
                ? parsedGameVersion
                : (SemanticVersion?)null;
            Platform = DetectPlatform();
            Architecture = NormalizeArchitecture(RuntimeInformation.ProcessArchitecture);
            RuntimeIdentifier = Platform + "-" + Architecture;
            providerVersions["topiaforge.core"] = SdkVersion;
            ProviderVersions = new ReadOnlyDictionary<string, SemanticVersion>(providerVersions);
            unavailableCapabilitiesView = new ReadOnlyDictionary<string, string>(unavailableCapabilities);
        }

        public SemanticVersion LoaderVersion { get; }

        public SemanticVersion SdkVersion { get; }

        public bool TryGetGameVersion(out SemanticVersion version)
        {
            version = gameVersion.GetValueOrDefault();
            return gameVersion.HasValue;
        }

        public string Platform { get; }

        public string Architecture { get; }

        public string RuntimeIdentifier { get; }

        public IReadOnlyDictionary<string, SemanticVersion> ProviderVersions { get; }

        public IReadOnlyDictionary<string, string> UnavailableCapabilities
        {
            get
            {
                RefreshCapabilities();
                return unavailableCapabilitiesView;
            }
        }

        public bool TryGetUnavailableCapability(string capability, out string? reason)
        {
            if (string.IsNullOrWhiteSpace(capability))
            {
                reason = null;
                return false;
            }

            RefreshCapabilities();
            return unavailableCapabilities.TryGetValue(capability, out reason);
        }

        internal void SetCapabilityRefresher(Action? refresher)
        {
            capabilityRefresher = refresher;
        }

        internal void ConfigureProviders(IEnumerable<ModPackage> packages)
        {
            if (packages == null) throw new ArgumentNullException(nameof(packages));
            unavailableCapabilities.Clear();
            reportedCapabilityOwners.Clear();
            foreach (var providerId in providerVersions.Keys
                         .Where(id => !string.Equals(id, "topiaforge.core", StringComparison.OrdinalIgnoreCase))
                         .ToArray())
            {
                providerVersions.Remove(providerId);
            }

            foreach (var entry in KnownProviders)
            {
                providerVersions.Remove(entry.Key);
                var package = packages.FirstOrDefault(candidate =>
                    string.Equals(candidate.Manifest?.Id, entry.Key, StringComparison.OrdinalIgnoreCase));
                unavailableCapabilities[entry.Value] = package == null
                    ? "The optional " + entry.Value + " provider is not installed in the active profile."
                    : package.IsValid
                        ? "The optional " + entry.Value + " provider has not completed loading."
                        : "The optional " + entry.Value + " provider package is invalid.";
            }
        }

        internal void MarkProviderLoaded(ModManifest manifest)
        {
            RemoveReportedCapabilities(manifest.Id);
            if (!SemanticVersion.TryParse(manifest.Version, out var version))
            {
                return;
            }

            providerVersions[manifest.Id] = version;
            if (KnownProviders.TryGetValue(manifest.Id, out var capability))
            {
                unavailableCapabilities.Remove(capability);
            }
        }

        internal void MarkProviderFailed(ModManifest manifest, string reason)
        {
            RemoveReportedCapabilities(manifest.Id);
            providerVersions.Remove(manifest.Id);
            if (!KnownProviders.TryGetValue(manifest.Id, out var capability))
            {
                return;
            }

            unavailableCapabilities[capability] = "The optional " + capability + " provider failed to load: "
                + (string.IsNullOrWhiteSpace(reason) ? "unknown failure" : reason);
        }

        internal void ReportCapabilityAvailability(
            string providerId,
            string capability,
            bool isAvailable,
            string unavailableReason)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                throw new ArgumentException("A provider id is required.", nameof(providerId));
            }

            if (string.IsNullOrWhiteSpace(capability))
            {
                throw new ArgumentException("A capability id is required.", nameof(capability));
            }

            // A package that is missing, invalid, still loading, or failed owns the more authoritative
            // high-level reason installed by ConfigureProviders/MarkProviderFailed. Status probes must not
            // accidentally turn that state into a loaded provider.
            if (!providerVersions.ContainsKey(providerId))
            {
                return;
            }

            reportedCapabilityOwners[capability] = providerId;
            if (isAvailable)
            {
                unavailableCapabilities.Remove(capability);
                return;
            }

            unavailableCapabilities[capability] = string.IsNullOrWhiteSpace(unavailableReason)
                ? "The " + capability + " capability is unavailable in this host."
                : unavailableReason;
        }

        private void RemoveReportedCapabilities(string providerId)
        {
            foreach (var capability in reportedCapabilityOwners
                         .Where(entry => string.Equals(entry.Value, providerId, StringComparison.OrdinalIgnoreCase))
                         .Select(entry => entry.Key)
                         .ToArray())
            {
                reportedCapabilityOwners.Remove(capability);
                unavailableCapabilities.Remove(capability);
            }
        }

        private void RefreshCapabilities()
        {
            if (refreshingCapabilities || capabilityRefresher == null)
            {
                return;
            }

            refreshingCapabilities = true;
            try
            {
                capabilityRefresher();
            }
            finally
            {
                refreshingCapabilities = false;
            }
        }

        private static string DetectPlatform()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return "windows";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return "macos";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return "linux";
            }

            return "unknown";
        }

        private static string NormalizeArchitecture(Architecture architecture)
        {
            switch (architecture)
            {
                case System.Runtime.InteropServices.Architecture.X86:
                    return "x86";
                case System.Runtime.InteropServices.Architecture.X64:
                    return "x64";
                case System.Runtime.InteropServices.Architecture.Arm:
                    return "arm";
                case System.Runtime.InteropServices.Architecture.Arm64:
                    return "arm64";
                default:
                    return architecture.ToString().ToLowerInvariant();
            }
        }
    }
}
