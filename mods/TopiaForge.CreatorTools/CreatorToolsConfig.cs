using System.Runtime.Serialization;
using TopiaForge.Mods;

namespace TopiaForge.CreatorTools
{
    [DataContract]
    public sealed class CreatorToolsConfig : ISelfNormalizingConfig
    {
        [DataMember(Name = "enabled")]
        public bool Enabled { get; set; } = true;

        [DataMember(Name = "showSessionHud")]
        public bool ShowSessionHud { get; set; } = true;

        [DataMember(Name = "maximumInstances")]
        public int MaximumInstances { get; set; } = 128;

        [DataMember(Name = "conversationEnabled")]
        public bool ConversationEnabled { get; set; }

        [DataMember(Name = "chatMaxTurns")]
        public int ChatMaxTurns { get; set; } = 12;

        [DataMember(Name = "chatTemperature")]
        public float ChatTemperature { get; set; } = 0.6f;

        /// <summary>
        /// One-run 64-hex challenge provisioned by the release acceptance
        /// harness. Empty in ordinary play, which keeps the acceptance recorder
        /// completely inert so no session can produce release evidence by
        /// accident.
        /// </summary>
        [DataMember(Name = "acceptanceChallenge")]
        public string AcceptanceChallenge { get; set; } = string.Empty;

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            Enabled = true;
            ShowSessionHud = true;
            MaximumInstances = 128;
            ConversationEnabled = false;
            ChatMaxTurns = 12;
            ChatTemperature = 0.6f;
            AcceptanceChallenge = string.Empty;
        }

        public void Normalize()
        {
            if (MaximumInstances < 1 || MaximumInstances > 256) MaximumInstances = 128;
            if (ChatMaxTurns < 1 || ChatMaxTurns > 24) ChatMaxTurns = 12;
            if (float.IsNaN(ChatTemperature) || ChatTemperature < 0f || ChatTemperature > 2f) ChatTemperature = 0.6f;
            // A malformed challenge is cleared rather than trusted, so evidence
            // can never be emitted against a value the harness did not write.
            if (AcceptanceChallenge == null) AcceptanceChallenge = string.Empty;
            if (AcceptanceChallenge.Length != 0 && AcceptanceChallenge.Length != 64)
            {
                AcceptanceChallenge = string.Empty;
            }
        }
    }
}
