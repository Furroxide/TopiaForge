using System.Runtime.Serialization;

namespace TopiaForge.SdkAcceptance
{
    [DataContract]
    internal sealed class AcceptanceConfig
    {
        [DataMember(Name = "acceptanceChallenge")]
        public string AcceptanceChallenge { get; set; } = string.Empty;

        [DataMember(Name = "migratedFromSchema1")]
        public bool MigratedFromSchema1 { get; set; }

        [DataMember(Name = "highContrast")]
        public bool HighContrast { get; set; } = true;

        [DataMember(Name = "uiScale")]
        public float UiScale { get; set; } = 1.15f;

        [DataMember(Name = "reducedMotion")]
        public bool ReducedMotion { get; set; } = true;

        [DataMember(Name = "motionIntensity")]
        public float MotionIntensity { get; set; }
    }

    [DataContract]
    internal sealed class AcceptanceState
    {
        [DataMember(Name = "loadCount")]
        public int LoadCount { get; set; }
    }
}
