using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Robotopia.ModManager.Core
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

        [DataMember(Name = "gameVersion")]
        public string GameVersion { get; set; } = string.Empty;

        [DataMember(Name = "gameVersionRange")]
        public string GameVersionRange { get; set; } = string.Empty;

        [DataMember(Name = "supportedGameVersionRange")]
        public string SupportedGameVersionRange { get; set; } = string.Empty;

        [DataMember(Name = "loaderVersionRange")]
        public string LoaderVersionRange { get; set; } = string.Empty;

        [DataMember(Name = "supportedLoaderVersionRange")]
        public string SupportedLoaderVersionRange { get; set; } = string.Empty;

        [DataMember(Name = "sdkVersionRange")]
        public string SdkVersionRange { get; set; } = string.Empty;

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

        [DataMember(Name = "legacyFolders")]
        public Dictionary<string, string> LegacyFolders { get; set; } = new Dictionary<string, string>();

        [DataMember(Name = "legacyFiles")]
        public Dictionary<string, string> LegacyFiles { get; set; } = new Dictionary<string, string>();

        [DataMember(Name = "legacyPackages")]
        public List<string> LegacyPackages { get; set; } = new List<string>();
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

        [DataMember(Name = "version")]
        public string Version { get; set; } = string.Empty;

        [DataMember(Name = "versionRange")]
        public string VersionRange { get; set; } = string.Empty;

        [DataMember(Name = "optional")]
        public bool Optional { get; set; }
    }

    [DataContract]
    public sealed class ModConflict
    {
        [DataMember(Name = "id", IsRequired = true)]
        public string Id { get; set; } = string.Empty;

        [DataMember(Name = "version")]
        public string Version { get; set; } = string.Empty;

        [DataMember(Name = "versionRange")]
        public string VersionRange { get; set; } = string.Empty;

        [DataMember(Name = "reason")]
        public string Reason { get; set; } = string.Empty;
    }
}
