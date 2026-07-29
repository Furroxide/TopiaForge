using System.Collections.Generic;
using System.Runtime.Serialization;

namespace TopiaForge.ModManager.Core
{
    [DataContract]
    public sealed class ModManifest
    {
        /// <summary>The immutable schema selector for the TopiaForge 1.0 manifest contract.</summary>
        public const int ManifestV5SchemaVersion = 5;

        /// <summary>The newest schema emitted by current tooling. Older supported readers must not depend on this.</summary>
        public const int CurrentSchemaVersion = ManifestV5SchemaVersion;

        [DataMember(Name = "$schema", EmitDefaultValue = false)]
        public string SchemaUrl { get; set; } = string.Empty;

        [DataMember(Name = "schemaVersion", IsRequired = true)]
        public int SchemaVersion { get; set; }

        [DataMember(Name = "name", IsRequired = true)]
        public string Id { get; set; } = string.Empty;

        [DataMember(Name = "displayName", IsRequired = true)]
        public string Name { get; set; } = string.Empty;

        [DataMember(Name = "version", IsRequired = true)]
        public string Version { get; set; } = string.Empty;

        [DataMember(Name = "author")]
        public ModAuthor Author { get; set; } = new ModAuthor();

        [DataMember(Name = "description")]
        public string Description { get; set; } = string.Empty;

        [DataMember(Name = "entryAssembly")]
        public string EntryAssembly { get; set; } = string.Empty;

        [DataMember(Name = "entryType")]
        public string EntryType { get; set; } = string.Empty;

        [DataMember(Name = "dependencies")]
        public Dictionary<string, string> Dependencies { get; set; } = new Dictionary<string, string>();

        [DataMember(Name = "optionalDependencies")]
        public Dictionary<string, string> OptionalDependencies { get; set; } = new Dictionary<string, string>();

        [DataMember(Name = "conflicts")]
        public List<ModConflict> Conflicts { get; set; } = new List<ModConflict>();

        [DataMember(Name = "loadAfter")]
        public List<string> LoadAfter { get; set; } = new List<string>();

        [DataMember(Name = "loadBefore")]
        public List<string> LoadBefore { get; set; } = new List<string>();

        [DataMember(Name = "supportedGameVersionRange")]
        public string SupportedGameVersionRange { get; set; } = "*";

        [DataMember(Name = "supportedLoaderVersionRange")]
        public string SupportedLoaderVersionRange { get; set; } = "*";

        [DataMember(Name = "supportedSdkVersionRange")]
        public string SupportedSdkVersionRange { get; set; } = "*";

        [DataMember(Name = "category")]
        public string Category { get; set; } = string.Empty;

        [DataMember(Name = "tags")]
        public List<string> Tags { get; set; } = new List<string>();

        [IgnoreDataMember]
        public string Icon { get; set; } = string.Empty;

        [DataMember(Name = "icon", EmitDefaultValue = false)]
        private string? SerializedIcon
        {
            get => string.IsNullOrEmpty(Icon) ? null : Icon;
            set
            {
                IconWasPresent = true;
                Icon = value ?? string.Empty;
            }
        }

        internal bool IconWasPresent { get; private set; }

        [DataMember(Name = "screenshots")]
        public List<string> Screenshots { get; set; } = new List<string>();

        [DataMember(Name = "homepage")]
        public string Homepage { get; set; } = string.Empty;

        [DataMember(Name = "source")]
        public string Source { get; set; } = string.Empty;

        [IgnoreDataMember]
        public string License { get; set; } = string.Empty;

        [DataMember(Name = "license", EmitDefaultValue = false)]
        private string? SerializedLicense
        {
            get => string.IsNullOrEmpty(License) ? null : License;
            set
            {
                LicenseWasPresent = true;
                License = value ?? string.Empty;
            }
        }

        internal bool LicenseWasPresent { get; private set; }

        [IgnoreDataMember]
        public List<string> LicenseFiles { get; set; } = new List<string>();

        [DataMember(Name = "licenseFiles", EmitDefaultValue = false)]
        private List<string>? SerializedLicenseFiles
        {
            get => LicenseFiles == null || LicenseFiles.Count == 0 ? null : LicenseFiles;
            set
            {
                LicenseFilesWasPresent = true;
                LicenseFiles = value ?? new List<string>();
            }
        }

        internal bool LicenseFilesWasPresent { get; private set; }

        [DataMember(Name = "hashes")]
        public Dictionary<string, string> Hashes { get; set; } = new Dictionary<string, string>();

        [DataMember(Name = "capabilities")]
        public List<string> Capabilities { get; set; } = new List<string>();

        [DataMember(Name = "platforms")]
        public List<string> Platforms { get; set; } = new List<string>();

        [DataMember(Name = "architectures")]
        public List<string> Architectures { get; set; } = new List<string>();

        [DataMember(Name = "contentTargets")]
        public List<string> ContentTargets { get; set; } = new List<string>();

        [DataMember(Name = "builtWith", EmitDefaultValue = false)]
        public ModBuildMetadata? BuiltWith { get; set; }

        [DataMember(Name = "apiAssemblies")]
        public List<string> ApiAssemblies { get; set; } = new List<string>();

        [DataMember(Name = "worldGamemodes")]
        public List<ModGamemode> WorldGamemodes { get; set; } = new List<ModGamemode>();

        [DataMember(Name = "multiplayer", EmitDefaultValue = false)]
        public ModMultiplayerMetadata? Multiplayer { get; set; }

        /// <summary>
        /// True when this manifest opts into the versioned multiplayer contract.
        /// </summary>
        [IgnoreDataMember]
        public bool DeclaresMultiplayer =>
            SchemaVersion == ManifestV5SchemaVersion && Multiplayer != null;

        /// <summary>True for manifests that may only be admitted to a standalone session.</summary>
        [IgnoreDataMember]
        public bool IsStandaloneOnly => !DeclaresMultiplayer;

        [DataMember(Name = "vpmDependencies", EmitDefaultValue = false)]
        private Dictionary<string, string>? UnsupportedVpmDependencies { get; set; }

        [DataMember(Name = "permissions", EmitDefaultValue = false)]
        private List<string>? UnsupportedPermissions { get; set; }

        [DataMember(Name = "id", EmitDefaultValue = false)]
        private string? UnsupportedId { get; set; }

        [DataMember(Name = "title", EmitDefaultValue = false)]
        private string? UnsupportedTitle { get; set; }

        [DataMember(Name = "gameVersion", EmitDefaultValue = false)]
        private string? UnsupportedGameVersion { get; set; }

        [DataMember(Name = "gameVersionRange", EmitDefaultValue = false)]
        private string? UnsupportedGameVersionRange { get; set; }

        [DataMember(Name = "loaderVersionRange", EmitDefaultValue = false)]
        private string? UnsupportedLoaderVersionRange { get; set; }

        [DataMember(Name = "sdkVersionRange", EmitDefaultValue = false)]
        private string? UnsupportedSdkVersionRange { get; set; }

        [DataMember(Name = "packageHashes", EmitDefaultValue = false)]
        private Dictionary<string, string>? UnsupportedPackageHashes { get; set; }

        [DataMember(Name = "gamemodes", EmitDefaultValue = false)]
        private List<object>? UnsupportedGamemodes { get; set; }

        [DataMember(Name = "legacyFolders", EmitDefaultValue = false)]
        private Dictionary<string, string>? UnsupportedLegacyFolders { get; set; }

        [DataMember(Name = "legacyFiles", EmitDefaultValue = false)]
        private Dictionary<string, string>? UnsupportedLegacyFiles { get; set; }

        [DataMember(Name = "legacyPackages", EmitDefaultValue = false)]
        private List<string>? UnsupportedLegacyPackages { get; set; }

        internal IEnumerable<string> UnsupportedFieldNames()
        {
            if (UnsupportedVpmDependencies != null) yield return "vpmDependencies";
            if (UnsupportedPermissions != null) yield return "permissions";
            if (UnsupportedId != null) yield return "id";
            if (UnsupportedTitle != null) yield return "title";
            if (UnsupportedGameVersion != null) yield return "gameVersion";
            if (UnsupportedGameVersionRange != null) yield return "gameVersionRange";
            if (UnsupportedLoaderVersionRange != null) yield return "loaderVersionRange";
            if (UnsupportedSdkVersionRange != null) yield return "sdkVersionRange";
            if (UnsupportedPackageHashes != null) yield return "packageHashes";
            if (UnsupportedGamemodes != null) yield return "gamemodes";
            if (UnsupportedLegacyFolders != null) yield return "legacyFolders";
            if (UnsupportedLegacyFiles != null) yield return "legacyFiles";
            if (UnsupportedLegacyPackages != null) yield return "legacyPackages";
        }

    }

    /// <summary>Transport-neutral multiplayer admission metadata introduced by manifest schema V5.</summary>
    [DataContract]
    public sealed class ModMultiplayerMetadata
    {
        public const string ClientLocalMode = "client-local";
        public const string ServerOnlyMode = "server-only";
        public const string SessionMode = "session";
        public const string RequiredPresence = "required";
        public const string OptionalPresence = "optional";
        public const string ContractLockFileName = "topiaforge.multiplayer.lock.json";
        public const int MaxSynchronizedFiles = 256;

        [DataMember(Name = "mode", IsRequired = true)]
        public string Mode { get; set; } = string.Empty;

        [IgnoreDataMember]
        public string Presence { get; set; } = string.Empty;

        [DataMember(Name = "presence", EmitDefaultValue = false)]
        private string? SerializedPresence
        {
            get => string.IsNullOrEmpty(Presence) ? null : Presence;
            set
            {
                PresenceWasPresent = true;
                Presence = value ?? string.Empty;
            }
        }

        internal bool PresenceWasPresent { get; private set; }

        [DataMember(Name = "protocol", EmitDefaultValue = false)]
        public ModMultiplayerProtocol? Protocol { get; set; }

        [IgnoreDataMember]
        public List<string> SynchronizedFiles { get; set; } = new List<string>();

        [DataMember(Name = "synchronizedFiles", EmitDefaultValue = false)]
        private List<string>? SerializedSynchronizedFiles
        {
            get => SynchronizedFiles == null || SynchronizedFiles.Count == 0 ? null : SynchronizedFiles;
            set
            {
                SynchronizedFilesWasPresent = true;
                SynchronizedFiles = value ?? new List<string>();
            }
        }

        internal bool SynchronizedFilesWasPresent { get; private set; }
    }

    /// <summary>Per-mod wire compatibility independent of the package version.</summary>
    [DataContract]
    public sealed class ModMultiplayerProtocol
    {
        [DataMember(Name = "version", IsRequired = true)]
        public string Version { get; set; } = string.Empty;

        [IgnoreDataMember]
        public string PeerVersionRange { get; set; } = string.Empty;

        [DataMember(Name = "peerVersionRange", EmitDefaultValue = false)]
        private string? SerializedPeerVersionRange
        {
            get => string.IsNullOrEmpty(PeerVersionRange) ? null : PeerVersionRange;
            set
            {
                PeerVersionRangeWasPresent = true;
                PeerVersionRange = value ?? string.Empty;
            }
        }

        internal bool PeerVersionRangeWasPresent { get; private set; }

        /// <summary>
        /// The declared peer range, or the exact local protocol version when the optional range is omitted.
        /// Admission must apply this rule in both directions.
        /// </summary>
        [IgnoreDataMember]
        public string EffectivePeerVersionRange =>
            string.IsNullOrEmpty(PeerVersionRange) ? Version : PeerVersionRange;
    }

    [DataContract]
    public sealed class ModBuildMetadata
    {
        [DataMember(Name = "sdkVersion", EmitDefaultValue = false)]
        public string SdkVersion { get; set; } = string.Empty;

        [DataMember(Name = "loaderVersion", EmitDefaultValue = false)]
        public string LoaderVersion { get; set; } = string.Empty;

        [DataMember(Name = "gameVersion", EmitDefaultValue = false)]
        public string GameVersion { get; set; } = string.Empty;

        [DataMember(Name = "toolVersion", EmitDefaultValue = false)]
        public string ToolVersion { get; set; } = string.Empty;
    }

    [DataContract]
    public sealed class ModAuthor
    {
        [DataMember(Name = "name")]
        public string Name { get; set; } = string.Empty;

        [DataMember(Name = "email")]
        public string Email { get; set; } = string.Empty;

        [DataMember(Name = "url")]
        public string Url { get; set; } = string.Empty;
    }

    [DataContract]
    public sealed class ModDependency
    {
        [DataMember(Name = "id", IsRequired = true)]
        public string Id { get; set; } = string.Empty;

        [DataMember(Name = "versionRange")]
        public string VersionRange { get; set; } = string.Empty;

        [DataMember(Name = "version", EmitDefaultValue = false)]
        private string? UnsupportedVersion { get; set; }

        internal bool HasUnsupportedVersion => UnsupportedVersion != null;

        [DataMember(Name = "optional")]
        public bool Optional { get; set; }
    }

    [DataContract]
    public sealed class ModConflict
    {
        [DataMember(Name = "id", IsRequired = true)]
        public string Id { get; set; } = string.Empty;

        [DataMember(Name = "versionRange")]
        public string VersionRange { get; set; } = string.Empty;

        [DataMember(Name = "version", EmitDefaultValue = false)]
        private string? UnsupportedVersion { get; set; }

        internal bool HasUnsupportedVersion => UnsupportedVersion != null;

        [DataMember(Name = "reason")]
        public string Reason { get; set; } = string.Empty;
    }

    [DataContract]
    public sealed class ModGamemode
    {
        [DataMember(Name = "id", IsRequired = true)]
        public string Id { get; set; } = string.Empty;

        [DataMember(Name = "name", IsRequired = true)]
        public string Name { get; set; } = string.Empty;

        [DataMember(Name = "description")]
        public string Description { get; set; } = string.Empty;
    }
}
