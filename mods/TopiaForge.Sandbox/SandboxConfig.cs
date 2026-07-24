using System.Runtime.Serialization;

namespace TopiaForge.Sandbox
{
    [DataContract]
    public sealed class SandboxConfig
    {
        public SandboxConfig()
        {
            SeedDefaults();
        }

        [DataMember(Name = "spawnMenuKey")]
        public string SpawnMenuKey { get; set; } = "F5";

        [DataMember(Name = "undoKey")]
        public string UndoKey { get; set; } = "Z";

        [DataMember(Name = "freezeKey")]
        public string FreezeKey { get; set; } = "F";

        // How far ahead of the camera a spawn-placement ray may land before falling back to a fixed
        // in-front-of-player distance.
        [DataMember(Name = "spawnDistanceMax")]
        public float SpawnDistanceMax { get; set; } = 40f;

        // Hard cap on live spawned objects (props + robots) so a runaway spawn spree cannot melt the frame.
        [DataMember(Name = "maxSpawnedObjects")]
        public int MaxSpawnedObjects { get; set; } = 200;

        // "Dormant" spawns statue-like robots the mod owns; "Autonomous" lets the game's own brain drive them.
        [DataMember(Name = "defaultRobotBrainMode")]
        public string DefaultRobotBrainMode { get; set; } = "Dormant";

        [DataMember(Name = "showHud")]
        public bool ShowHud { get; set; } = true;

        // Remote programming conversations are opt-in because they read the player's Robotopia token and call
        // RoboAPI. Deterministic roster actions (FOLLOW ME / IDLE) remain available while this is off.
        [DataMember(Name = "conversationEnabled")]
        public bool ConversationEnabled { get; set; }

        // Microphone capture and remote speech-to-text are a separate explicit opt-in.
        [DataMember(Name = "voiceInputEnabled")]
        public bool VoiceInputEnabled { get; set; }

        // Programming chat: how many exchanges before the robot loses interest, its reply sampling temperature,
        // and the push-to-talk key while the explicitly enabled chat is in voice mode.
        [DataMember(Name = "chatMaxTurns")]
        public int ChatMaxTurns { get; set; } = 12;

        [DataMember(Name = "chatTemperature")]
        public float ChatTemperature { get; set; } = 0.6f;

        [DataMember(Name = "voiceKey")]
        public string VoiceKey { get; set; } = "V";

        // Whether spawned autonomous robots get the REPROGRAM interaction (which replaces the game's native talk
        // prompt on them). Off = autonomous robots keep native talk and cannot be reprogrammed by interaction.
        [DataMember(Name = "reprogramAutonomousRobots")]
        public bool ReprogramAutonomousRobots { get; set; } = true;

        // Idle robots that pass close to each other exchange a quick greeting (emotes + a toast). Free — no
        // brain tokens involved.
        [DataMember(Name = "ambientGreetings")]
        public bool AmbientGreetings { get; set; } = true;

        // Upgrade greetings to a short LLM-generated exchange between the two robots. Each exchange spends one
        // brain token, so this is opt-in.
        [DataMember(Name = "ambientBanter")]
        public bool AmbientBanter { get; set; } = false;

        // Global minimum seconds between LLM banter exchanges (also stamped on failed attempts).
        [DataMember(Name = "banterCooldownSeconds")]
        public float BanterCooldownSeconds { get; set; } = 90f;

        // DataContractJsonSerializer bypasses the constructor (GetUninitializedObject), so absent members
        // would come up null/zero. Seed real defaults first; present members still override them.
        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            SeedDefaults();
        }

        public void Normalize()
        {
            if (string.IsNullOrWhiteSpace(SpawnMenuKey))
            {
                SpawnMenuKey = "F5";
            }

            if (string.IsNullOrWhiteSpace(UndoKey))
            {
                UndoKey = "Z";
            }

            if (string.IsNullOrWhiteSpace(FreezeKey))
            {
                FreezeKey = "F";
            }

            if (SpawnDistanceMax < 5f || float.IsNaN(SpawnDistanceMax))
            {
                SpawnDistanceMax = 40f;
            }

            if (MaxSpawnedObjects < 1)
            {
                MaxSpawnedObjects = 200;
            }

            if (string.IsNullOrWhiteSpace(DefaultRobotBrainMode))
            {
                DefaultRobotBrainMode = "Dormant";
            }

            if (ChatMaxTurns < 1)
            {
                ChatMaxTurns = 12;
            }

            if (ChatTemperature <= 0f || float.IsNaN(ChatTemperature))
            {
                ChatTemperature = 0.6f;
            }

            if (string.IsNullOrWhiteSpace(VoiceKey))
            {
                VoiceKey = "V";
            }

            if (BanterCooldownSeconds < 30f || float.IsNaN(BanterCooldownSeconds))
            {
                BanterCooldownSeconds = 90f;
            }
        }

        private void SeedDefaults()
        {
            SpawnMenuKey = "F5";
            UndoKey = "Z";
            FreezeKey = "F";
            SpawnDistanceMax = 40f;
            MaxSpawnedObjects = 200;
            DefaultRobotBrainMode = "Dormant";
            ShowHud = true;
            ConversationEnabled = false;
            VoiceInputEnabled = false;
            ChatMaxTurns = 12;
            ChatTemperature = 0.6f;
            VoiceKey = "V";
            ReprogramAutonomousRobots = true;
            AmbientGreetings = true;
            AmbientBanter = false;
            BanterCooldownSeconds = 90f;
        }
    }
}
