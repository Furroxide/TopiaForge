using System;
using System.Collections.Generic;
using System.Linq;

namespace TopiaForge.ModManager.Core
{
    /// <summary>An exact package selection whose manifest is captured independently of caller state.</summary>
    public sealed class ResolvedPackage
    {
        private readonly ModManifest snapshot;

        public ResolvedPackage(string id, string version, ModManifest manifest)
        {
            Identity = new PackageIdentity(id, version);
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            if (!string.Equals(id, manifest.Id, StringComparison.OrdinalIgnoreCase) || version != manifest.Version)
                throw new ArgumentException("Selected identity must agree with its manifest.", nameof(manifest));
            snapshot = ModManifestJson.CopyForLaunch(manifest);
        }

        public PackageIdentity Identity { get; }
        public string Id => Identity.Id;
        public string Version => Identity.Version;
        public ModManifest Manifest => ModManifestJson.CopyForLaunch(snapshot);
        internal ModManifest Snapshot => snapshot;
    }

    public sealed class InstallFacts
    {
        public InstallFacts(string platform = "", string architecture = "", string contentTarget = "", string gameVersion = "")
        {
            Platform = platform ?? string.Empty;
            Architecture = architecture ?? string.Empty;
            ContentTarget = contentTarget ?? string.Empty;
            GameVersion = gameVersion ?? string.Empty;
        }

        public string Platform { get; }
        public string Architecture { get; }
        public string ContentTarget { get; }
        public string GameVersion { get; }
    }

    /// <summary>The exact enabled selection and selected disabled packages, without registry or filesystem access.</summary>
    public sealed class EffectiveProfile
    {
        public EffectiveProfile(string profileId, int revision, IReadOnlyList<ResolvedPackage> packages,
            InstallFacts? install = null, IReadOnlyList<ResolvedPackage>? disabledPackages = null)
        {
            ProfileId = LaunchContractValues.Token(profileId, nameof(profileId));
            Revision = LaunchContractValues.Revision(revision, nameof(revision));
            Packages = LaunchContractValues.Copy(packages);
            DisabledPackages = LaunchContractValues.Copy(disabledPackages);
            Install = install ?? new InstallFacts();
        }

        public string ProfileId { get; }
        public int Revision { get; }
        public IReadOnlyList<ResolvedPackage> Packages { get; }
        public IReadOnlyList<ResolvedPackage> DisabledPackages { get; }
        public InstallFacts Install { get; }
    }

    public static partial class ModManifestJson
    {
        internal static ModManifest CopyForLaunch(ModManifest manifest)
        {
            var copy = JsonUtil.Clone(manifest);
            NormalizeCollections(copy);
            NormalizeContributions(copy);
            return copy;
        }
    }
}
