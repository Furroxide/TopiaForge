using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Mutable runtime metadata for capability and compatibility tests.</summary>
    public sealed class FakeRuntimeInfo : IRuntimeInfo
    {
        private readonly Dictionary<string, SemanticVersion> providerVersions =
            new Dictionary<string, SemanticVersion>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> unavailableCapabilities =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private SemanticVersion? gameVersion;

        /// <summary>Creates runtime metadata with stable V1 defaults.</summary>
        public FakeRuntimeInfo()
        {
            LoaderVersion = SemanticVersion.Parse("1.0.0");
            SdkVersion = SemanticVersion.Parse("1.0.0");
            gameVersion = SemanticVersion.Parse("0.0.2227");
            Platform = "test";
            Architecture = "x64";
            RuntimeIdentifier = "test-x64";
        }

        /// <inheritdoc/>
        public SemanticVersion LoaderVersion { get; set; }

        /// <inheritdoc/>
        public SemanticVersion SdkVersion { get; set; }

        /// <inheritdoc/>
        public string Platform { get; set; }

        /// <inheritdoc/>
        public string Architecture { get; set; }

        /// <inheritdoc/>
        public string RuntimeIdentifier { get; set; }

        /// <inheritdoc/>
        public IReadOnlyDictionary<string, SemanticVersion> ProviderVersions => providerVersions;

        /// <inheritdoc/>
        public IReadOnlyDictionary<string, string> UnavailableCapabilities => unavailableCapabilities;

        /// <inheritdoc/>
        public bool TryGetGameVersion(out SemanticVersion version)
        {
            version = gameVersion.GetValueOrDefault();
            return gameVersion.HasValue;
        }

        /// <summary>Sets the detected Robotopia build version.</summary>
        public void SetGameVersion(SemanticVersion version) => gameVersion = version;

        /// <summary>Clears the detected Robotopia build version.</summary>
        public void ClearGameVersion() => gameVersion = null;

        /// <summary>Sets the reported version of an optional capability provider.</summary>
        public void SetProviderVersion(string providerId, SemanticVersion version)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                throw new ArgumentException("A provider id is required.", nameof(providerId));
            }

            providerVersions[providerId] = version;
        }

        /// <summary>Marks a capability unavailable with a plain-language reason.</summary>
        public void SetUnavailableCapability(string capability, string reason)
        {
            if (string.IsNullOrWhiteSpace(capability))
            {
                throw new ArgumentException("A capability id is required.", nameof(capability));
            }

            unavailableCapabilities[capability] = reason ?? string.Empty;
        }

        /// <summary>Removes an unavailable-capability override.</summary>
        public bool SetCapabilityAvailable(string capability) => unavailableCapabilities.Remove(capability);

        /// <inheritdoc/>
        public bool TryGetUnavailableCapability(string capability, out string? reason) =>
            unavailableCapabilities.TryGetValue(capability, out reason);
    }
}
