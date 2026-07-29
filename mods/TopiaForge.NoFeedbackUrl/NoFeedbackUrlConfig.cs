using System.Runtime.Serialization;

namespace TopiaForge.NoFeedbackUrl
{
    /// <summary>Persisted first-run state for feedback-page suppression.</summary>
    [DataContract]
    public sealed class NoFeedbackUrlConfig
    {
        [DataMember(Name = "hasSeenFirstLaunch")]
        public bool HasSeenFirstLaunch { get; set; }
    }
}
