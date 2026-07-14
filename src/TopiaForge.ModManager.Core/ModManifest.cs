using System.Collections.Generic;
using System.Runtime.Serialization;

namespace TopiaForge.ModManager.Core
{
    [DataContract]
    public sealed class ModManifest
    {
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

        [DataMember(Name = "vpmDependencies")]
        public Dictionary<string, string> VpmDependencies { get; set; } = new Dictionary<string, string>();

        [DataMember(Name = "dependencies")]
        public List<ModDependency> Dependencies { get; set; } = new List<ModDependency>();

        [DataMember(Name = "optionalDependencies")]
        public List<ModDependency> OptionalDependencies { get; set; } = new List<ModDependency>();

        [DataMember(Name = "conflicts")]
        public List<ModConflict> Conflicts { get; set; } = new List<ModConflict>();

        [DataMember(Name = "loadAfter")]
        public List<string> LoadAfter { get; set; } = new List<string>();

        [DataMember(Name = "supportedGameVersionRange")]
        public string SupportedGameVersionRange { get; set; } = string.Empty;

        [DataMember(Name = "supportedLoaderVersionRange")]
        public string SupportedLoaderVersionRange { get; set; } = string.Empty;

        [DataMember(Name = "supportedSdkVersionRange")]
        public string SupportedSdkVersionRange { get; set; } = string.Empty;

        [DataMember(Name = "category")]
        public string Category { get; set; } = string.Empty;

        [DataMember(Name = "tags")]
        public List<string> Tags { get; set; } = new List<string>();

        [DataMember(Name = "icon")]
        public string Icon { get; set; } = string.Empty;

        [DataMember(Name = "screenshots")]
        public List<string> Screenshots { get; set; } = new List<string>();

        [DataMember(Name = "homepage")]
        public string Homepage { get; set; } = string.Empty;

        [DataMember(Name = "source")]
        public string Source { get; set; } = string.Empty;

        [DataMember(Name = "license")]
        public string License { get; set; } = string.Empty;

        [DataMember(Name = "licenseFiles")]
        public List<string> LicenseFiles { get; set; } = new List<string>();

        [DataMember(Name = "hashes")]
        public Dictionary<string, string> Hashes { get; set; } = new Dictionary<string, string>();

        [DataMember(Name = "permissions")]
        public List<string> Permissions { get; set; } = new List<string>();

        [DataMember(Name = "apiAssemblies")]
        public List<string> ApiAssemblies { get; set; } = new List<string>();

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
}
