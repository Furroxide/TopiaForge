using System.Collections.Generic;
using System.Runtime.Serialization;

namespace TopiaForge.ModManager.Core
{
    /// <summary>Bounded machine-readable summary of the most recent loader startup.</summary>
    [DataContract]
    public sealed class LastRunReport
    {
        public const int CurrentSchemaVersion = 1;

        [DataMember(Name = "schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [DataMember(Name = "sessionId")]
        public string SessionId { get; set; } = string.Empty;

        [DataMember(Name = "startedAtUtc")]
        public string StartedAtUtc { get; set; } = string.Empty;

        [DataMember(Name = "completedAtUtc")]
        public string CompletedAtUtc { get; set; } = string.Empty;

        [DataMember(Name = "startupDurationMs")]
        public long StartupDurationMs { get; set; }

        [DataMember(Name = "gameVersion")]
        public string GameVersion { get; set; } = string.Empty;

        [DataMember(Name = "loaderVersion")]
        public string LoaderVersion { get; set; } = string.Empty;

        [DataMember(Name = "sdkVersion")]
        public string SdkVersion { get; set; } = string.Empty;

        [DataMember(Name = "recovery")]
        public string Recovery { get; set; } = string.Empty;

        [DataMember(Name = "rootError")]
        public string RootError { get; set; } = string.Empty;

        [DataMember(Name = "rootExceptionChain")]
        public List<string> RootExceptionChain { get; set; } = new List<string>();

        [DataMember(Name = "stages")]
        public List<LastRunStage> Stages { get; set; } = new List<LastRunStage>();

        [DataMember(Name = "packages")]
        public List<LastRunPackage> Packages { get; set; } = new List<LastRunPackage>();
    }

    /// <summary>Elapsed timing for one authoritative startup stage.</summary>
    [DataContract]
    public sealed class LastRunStage
    {
        [DataMember(Name = "name")]
        public string Name { get; set; } = string.Empty;

        [DataMember(Name = "startedMs")]
        public long StartedMs { get; set; }

        [DataMember(Name = "durationMs")]
        public long DurationMs { get; set; }
    }

    [DataContract]
    public sealed class LastRunPackage
    {
        [DataMember(Name = "id")]
        public string Id { get; set; } = string.Empty;

        [DataMember(Name = "version")]
        public string Version { get; set; } = string.Empty;

        [DataMember(Name = "enabled")]
        public bool Enabled { get; set; }

        [DataMember(Name = "valid")]
        public bool Valid { get; set; }

        [DataMember(Name = "status")]
        public string Status { get; set; } = string.Empty;

        [DataMember(Name = "compatibility")]
        public string Compatibility { get; set; } = string.Empty;

        [DataMember(Name = "compatibilityReasons")]
        public List<string> CompatibilityReasons { get; set; } = new List<string>();

        [DataMember(Name = "selection")]
        public string Selection { get; set; } = string.Empty;

        [DataMember(Name = "loadOrder")]
        public int? LoadOrder { get; set; }

        [DataMember(Name = "sourceSha256")]
        public string SourceSha256 { get; set; } = string.Empty;

        [DataMember(Name = "criticalFiles")]
        public List<LastRunFileDigest> CriticalFiles { get; set; } = new List<LastRunFileDigest>();

        [DataMember(Name = "errors")]
        public List<string> Errors { get; set; } = new List<string>();
    }

    /// <summary>Digest of a load-critical installed package file.</summary>
    [DataContract]
    public sealed class LastRunFileDigest
    {
        [DataMember(Name = "path")]
        public string Path { get; set; } = string.Empty;

        [DataMember(Name = "sha256")]
        public string Sha256 { get; set; } = string.Empty;
    }
}
