using System.Runtime.Serialization;

namespace TopiaForge.CreatorTools
{
    [DataContract]
    public sealed class CreatorToolsConfig
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

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            Enabled = true;
            ShowSessionHud = true;
            MaximumInstances = 128;
            ConversationEnabled = false;
            ChatMaxTurns = 12;
            ChatTemperature = 0.6f;
        }

        public void Normalize()
        {
            if (MaximumInstances < 1 || MaximumInstances > 256) MaximumInstances = 128;
            if (ChatMaxTurns < 1 || ChatMaxTurns > 24) ChatMaxTurns = 12;
            if (float.IsNaN(ChatTemperature) || ChatTemperature < 0f || ChatTemperature > 2f) ChatTemperature = 0.6f;
        }
    }
}
