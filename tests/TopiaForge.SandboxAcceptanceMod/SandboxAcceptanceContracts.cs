using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using TopiaForge.Mods;

namespace TopiaForge.SandboxAcceptance
{
    [DataContract]
    public sealed class SandboxAcceptanceConfig
    {
        [DataMember(Name = "enabled")]
        public bool Enabled { get; set; }
        [DataMember(Name = "challenge")]
        public string Challenge { get; set; } = string.Empty;
    }

    // Only declared fixture preparation and immutable observations cross the safe/native package boundary.
    // This assembly is a test-only contract, not a new public SDK service or a replacement Sandbox host.
    public interface ISandboxAcceptanceFixture
    {
        string Challenge { get; }
        string WorldSessionId { get; }
        long Frame { get; }
        bool Prepared { get; }
        string ProjectId { get; }
        OperationResult<bool> Prepare(ICreatorContentFactory propFactory);
        OperationResult<bool> UnregisterSource();
        OperationResult<bool> RequestSessionStop();
        OperationResult<bool> RequestSessionRestart();
        OperationResult<bool> RequestReturnToMainMenu();
        OperationResult<bool> Cleanup();
        SandboxFixtureSnapshot Capture();
    }

    public sealed class SandboxFixtureSnapshot
    {
        public string WorldSessionId { get; set; } = string.Empty;
        public string SessionPhase { get; set; } = string.Empty;
        public int SessionSequence { get; set; }
        public string TargetId { get; set; } = string.Empty;
        public string GamemodeId { get; set; } = string.Empty;
        public string WorldId { get; set; } = string.Empty;
        public string ActiveHostId { get; set; } = string.Empty;
        public string[] CatalogIds { get; set; } = Array.Empty<string>();
        public string[] RobotTypes { get; set; } = Array.Empty<string>();
        public FixtureEntitySnapshot[] Objects { get; set; } = Array.Empty<FixtureEntitySnapshot>();
        public int CreatedObjects { get; set; }
        public int DisposedObjects { get; set; }
        public string[] CleanupErrors { get; set; } = Array.Empty<string>();
        public string[] UnavailableReasons { get; set; } = Array.Empty<string>();
        public float[] PlayerPosition { get; set; } = Array.Empty<float>();
        public string MutationSafetyState { get; set; } = string.Empty;
        public bool PersistenceIsolationAvailable { get; set; }
    }

    public sealed class FixtureEntitySnapshot
    {
        public string Id { get; set; } = string.Empty;
        public string ContentId { get; set; } = string.Empty;
        public bool Alive { get; set; }
        public float[] Transform { get; set; } = Array.Empty<float>();
    }
}
