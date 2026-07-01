using System.Runtime.Serialization;

namespace Robotopia.Zombies
{
    [DataContract]
    public sealed class ZombiesConfig
    {
        public ZombiesConfig()
        {
            SeedDefaults();
        }

        // World/level the Zombies gamemode launches in. Blank lets Robotopia Worlds pick a default playable
        // level (the first real, checkpoint-backed scene). Set it to a world id from the Worlds catalog.json
        // (e.g. "robotopia.level.firstlevel") to pin a specific level.
        [DataMember(Name = "targetWorldId")]
        public string TargetWorldId { get; set; } = string.Empty;

        [DataMember(Name = "startingCountdownSeconds")]
        public float StartingCountdownSeconds { get; set; }

        [DataMember(Name = "interWaveDelaySeconds")]
        public float InterWaveDelaySeconds { get; set; }

        [DataMember(Name = "baseZombiesPerWave")]
        public int BaseZombiesPerWave { get; set; }

        [DataMember(Name = "zombiesPerWaveIncrement")]
        public int ZombiesPerWaveIncrement { get; set; }

        [DataMember(Name = "maxAliveZombies")]
        public int MaxAliveZombies { get; set; }

        [DataMember(Name = "spawnRadius")]
        public float SpawnRadius { get; set; }

        [DataMember(Name = "minSpawnDistance")]
        public float MinSpawnDistance { get; set; }

        [DataMember(Name = "spawnHeightOffset")]
        public float SpawnHeightOffset { get; set; }

        [DataMember(Name = "spawnIntervalSeconds")]
        public float SpawnIntervalSeconds { get; set; }

        [DataMember(Name = "spawnSearchAttempts")]
        public int SpawnSearchAttempts { get; set; }

        [DataMember(Name = "playerIntegrity")]
        public float PlayerIntegrity { get; set; }

        [DataMember(Name = "zombieHealth")]
        public float ZombieHealth { get; set; }

        [DataMember(Name = "zombieMoveSpeed")]
        public float ZombieMoveSpeed { get; set; }

        [DataMember(Name = "zombieTurnSpeed")]
        public float ZombieTurnSpeed { get; set; }

        [DataMember(Name = "zombieAttackRange")]
        public float ZombieAttackRange { get; set; }

        [DataMember(Name = "zombieAttackCooldownSeconds")]
        public float ZombieAttackCooldownSeconds { get; set; }

        [DataMember(Name = "zombieAttackDamage")]
        public float ZombieAttackDamage { get; set; }

        [DataMember(Name = "scorePerKill")]
        public int ScorePerKill { get; set; }

        [DataMember(Name = "zapperDamage")]
        public float ZapperDamage { get; set; }

        [DataMember(Name = "zapperRange")]
        public float ZapperRange { get; set; }

        [DataMember(Name = "zapperCooldownSeconds")]
        public float ZapperCooldownSeconds { get; set; }

        [DataMember(Name = "zapperImpactForce")]
        public float ZapperImpactForce { get; set; }

        [DataMember(Name = "zapperBeamLifetimeSeconds")]
        public float ZapperBeamLifetimeSeconds { get; set; }

        // --- Enemy archetypes (master switch + per-type table) ------------------------------------------------
        // false = legacy uniform-green infected robots (every enemy is the Grunt); a safety/AB switch.
        [DataMember(Name = "archetypesEnabled")]
        public bool ArchetypesEnabled { get; set; }

        [DataMember(Name = "sprinterHealthMult")]
        public float SprinterHealthMult { get; set; }

        [DataMember(Name = "sprinterSpeed")]
        public float SprinterSpeed { get; set; }

        [DataMember(Name = "sprinterScale")]
        public float SprinterScale { get; set; }

        [DataMember(Name = "sprinterScore")]
        public int SprinterScore { get; set; }

        [DataMember(Name = "bruteHealthMult")]
        public float BruteHealthMult { get; set; }

        [DataMember(Name = "bruteSpeed")]
        public float BruteSpeed { get; set; }

        [DataMember(Name = "bruteScale")]
        public float BruteScale { get; set; }

        [DataMember(Name = "bruteScore")]
        public int BruteScore { get; set; }

        [DataMember(Name = "bruteAttackMult")]
        public float BruteAttackMult { get; set; }

        [DataMember(Name = "bruteEasyHeadFraction")]
        public float BruteEasyHeadFraction { get; set; }

        [DataMember(Name = "bruteMaxAlive")]
        public int BruteMaxAlive { get; set; }

        [DataMember(Name = "runtHealthMult")]
        public float RuntHealthMult { get; set; }

        [DataMember(Name = "runtSpeed")]
        public float RuntSpeed { get; set; }

        [DataMember(Name = "runtScale")]
        public float RuntScale { get; set; }

        [DataMember(Name = "runtScore")]
        public int RuntScore { get; set; }

        [DataMember(Name = "runtPackMin")]
        public int RuntPackMin { get; set; }

        [DataMember(Name = "runtPackMax")]
        public int RuntPackMax { get; set; }

        // SetEmote is best-effort native garnish (faces); a master off-switch in case a build dislikes it.
        [DataMember(Name = "enableEnemyEmotes")]
        public bool EnableEnemyEmotes { get; set; }

        // --- Zapper headshots + charged alt-fire --------------------------------------------------------------
        [DataMember(Name = "headshotDamageMultiplier")]
        public float HeadshotDamageMultiplier { get; set; }

        // Fraction of the body height (feet=0, head=1) at/above which a hit counts as a headshot.
        [DataMember(Name = "headshotHeightFraction")]
        public float HeadshotHeightFraction { get; set; }

        [DataMember(Name = "headshotFlatBonusScore")]
        public int HeadshotFlatBonusScore { get; set; }

        [DataMember(Name = "chargeShotEnabled")]
        public bool ChargeShotEnabled { get; set; }

        [DataMember(Name = "chargeShotSeconds")]
        public float ChargeShotSeconds { get; set; }

        [DataMember(Name = "chargeShotDamage")]
        public float ChargeShotDamage { get; set; }

        [DataMember(Name = "chargeShotCooldownSeconds")]
        public float ChargeShotCooldownSeconds { get; set; }

        // A single charged shot pierces every zombie along its line (the answer to swarms).
        [DataMember(Name = "chargeShotPierces")]
        public bool ChargeShotPierces { get; set; }

        // A single hit dealing >= this fraction of the target's max HP knocks it down (native ragdoll).
        [DataMember(Name = "bigHitRagdollFraction")]
        public float BigHitRagdollFraction { get; set; }

        // --- Combo / score economy ----------------------------------------------------------------------------
        [DataMember(Name = "comboWindowSeconds")]
        public float ComboWindowSeconds { get; set; }

        [DataMember(Name = "comboKillsPerTier")]
        public int ComboKillsPerTier { get; set; }

        [DataMember(Name = "comboMaxMultiplier")]
        public int ComboMaxMultiplier { get; set; }

        // --- HUD juice ----------------------------------------------------------------------------------------
        [DataMember(Name = "crosshairBaseGapPixels")]
        public float CrosshairBaseGapPixels { get; set; }

        [DataMember(Name = "crosshairBloomGapPixels")]
        public float CrosshairBloomGapPixels { get; set; }

        [DataMember(Name = "hitMarkerSeconds")]
        public float HitMarkerSeconds { get; set; }

        [DataMember(Name = "floatingNumberRiseSeconds")]
        public float FloatingNumberRiseSeconds { get; set; }

        [DataMember(Name = "floatingNumberMaxConcurrent")]
        public int FloatingNumberMaxConcurrent { get; set; }

        [DataMember(Name = "damageFlashSeconds")]
        public float DamageFlashSeconds { get; set; }

        [DataMember(Name = "damageFlashMaxAlpha")]
        public float DamageFlashMaxAlpha { get; set; }

        [DataMember(Name = "lowIntegrityVignetteThreshold")]
        public float LowIntegrityVignetteThreshold { get; set; }

        [DataMember(Name = "criticalIntegrityThreshold")]
        public float CriticalIntegrityThreshold { get; set; }

        [DataMember(Name = "hudScale")]
        public float HudScale { get; set; }

        [DataMember(Name = "hudMotionIntensity")]
        public float HudMotionIntensity { get; set; }

        [DataMember(Name = "hudHighContrast")]
        public bool HudHighContrast { get; set; }

        // --- Beam ---------------------------------------------------------------------------------------------
        [DataMember(Name = "beamWidthStart")]
        public float BeamWidthStart { get; set; }

        [DataMember(Name = "beamWidthEnd")]
        public float BeamWidthEnd { get; set; }

        [DataMember(Name = "beamJitterAmplitude")]
        public float BeamJitterAmplitude { get; set; }

        // --- OVERRIDE: command the infected robots' AI brain ---------------------------------------------------
        // Master switch for the whole OVERRIDE/BROADCAST verb (false = pure-shooter v0.6.0 behaviour).
        [DataMember(Name = "overrideEnabled")]
        public bool OverrideEnabled { get; set; }

        // When true (and the RobotKit brain service + token are available) a successful cast can consult the live
        // RoboAPI LLM (llama-3.3-70b) to soften the outcome and bark a line. false = fully deterministic + offline,
        // and never reads the token or touches the network. The deterministic outcome is always the authority.
        [DataMember(Name = "useLiveBrain")]
        public bool UseLiveBrain { get; set; }

        // KeyCode names (parsed leniently; fall back to E/Q if invalid).
        [DataMember(Name = "overrideKey")]
        public string OverrideKey { get; set; } = "E";

        [DataMember(Name = "broadcastKey")]
        public string BroadcastKey { get; set; } = "Q";

        [DataMember(Name = "overrideCharges")]
        public int OverrideCharges { get; set; }

        [DataMember(Name = "overrideChargeRegenSeconds")]
        public float OverrideChargeRegenSeconds { get; set; }

        [DataMember(Name = "broadcastChargeCost")]
        public int BroadcastChargeCost { get; set; }

        [DataMember(Name = "broadcastCooldownSeconds")]
        public float BroadcastCooldownSeconds { get; set; }

        [DataMember(Name = "broadcastRadius")]
        public float BroadcastRadius { get; set; }

        [DataMember(Name = "maxConvertedAllies")]
        public int MaxConvertedAllies { get; set; }

        [DataMember(Name = "convertDurationSeconds")]
        public float ConvertDurationSeconds { get; set; }

        [DataMember(Name = "freezeSeconds")]
        public float FreezeSeconds { get; set; }

        [DataMember(Name = "standDownSeconds")]
        public float StandDownSeconds { get; set; }

        [DataMember(Name = "fleeSeconds")]
        public float FleeSeconds { get; set; }

        [DataMember(Name = "enrageSeconds")]
        public float EnrageSeconds { get; set; }

        [DataMember(Name = "enrageSpeedMult")]
        public float EnrageSpeedMult { get; set; }

        [DataMember(Name = "enrageDamageMult")]
        public float EnrageDamageMult { get; set; }

        // A converted ally's attacks against other infected robots.
        [DataMember(Name = "allyDamage")]
        public float AllyDamage { get; set; }

        [DataMember(Name = "allyAttackCooldownSeconds")]
        public float AllyAttackCooldownSeconds { get; set; }

        [DataMember(Name = "allyRetargetSeconds")]
        public float AllyRetargetSeconds { get; set; }

        // How long a robot waits for the live brain answer before committing to the deterministic outcome only.
        [DataMember(Name = "liveBrainWindowSeconds")]
        public float LiveBrainWindowSeconds { get; set; }

        [DataMember(Name = "brainTemperature")]
        public float BrainTemperature { get; set; }

        [DataMember(Name = "barkMaxChars")]
        public int BarkMaxChars { get; set; }

        [DataMember(Name = "speechBubbleSeconds")]
        public float SpeechBubbleSeconds { get; set; }

        // Persuasion tuning: a global difficulty scale (>1 harder), the seeded trait ranges, and corruption ramp.
        [DataMember(Name = "overrideDifficulty")]
        public float OverrideDifficulty { get; set; }

        [DataMember(Name = "suggestibilityMin")]
        public float SuggestibilityMin { get; set; }

        [DataMember(Name = "suggestibilityMax")]
        public float SuggestibilityMax { get; set; }

        [DataMember(Name = "loyaltyMin")]
        public float LoyaltyMin { get; set; }

        [DataMember(Name = "loyaltyMax")]
        public float LoyaltyMax { get; set; }

        [DataMember(Name = "corruptionBase")]
        public float CorruptionBase { get; set; }

        [DataMember(Name = "corruptionPerWave")]
        public float CorruptionPerWave { get; set; }

        [DataMember(Name = "biasAmplitude")]
        public float BiasAmplitude { get; set; }

        // --- Superhot mode (powered by Robotopia.Chronos) -----------------------------------------------------
        // When true, the whole horde + physics only advance as fast as YOU move/aim/fire (and you stay full-speed):
        // a "time moves only when you move" zombies mode. Needs the Chronos time service; a no-op without it.
        [DataMember(Name = "superhotMode")]
        public bool SuperhotMode { get; set; }

        // --- JACK IN: free-form LLM conversation with one robot (freezes the horde) ---------------------------
        // Master switch for the talk-to-a-robot verb. When off (or the brain is unavailable) single-target influence
        // is disabled and the player relies on the zapper + the deterministic Q broadcast.
        [DataMember(Name = "conversationEnabled")]
        public bool ConversationEnabled { get; set; }

        // Allow push-to-talk voice input (transcribed via /agent/stt) in addition to typing. Degrades to text when no
        // microphone/backend is available.
        [DataMember(Name = "useVoiceInput")]
        public bool UseVoiceInput { get; set; }

        // Key to open a channel to the robot under the crosshair (KeyCode name, parsed leniently).
        [DataMember(Name = "jackInKey")]
        public string JackInKey { get; set; } = "E";

        // Hold-to-talk voice key, and the text/voice toggle (mirrors the base game's Tab toggle).
        [DataMember(Name = "voiceKey")]
        public string VoiceKey { get; set; } = "V";

        [DataMember(Name = "toggleInputModeKey")]
        public string ToggleInputModeKey { get; set; } = "Tab";

        // How long (real seconds, unscaled) a single conversation may stay open before it auto-resumes the horde.
        [DataMember(Name = "conversationWindowSeconds")]
        public float ConversationWindowSeconds { get; set; }

        // Time restored to the channel after each non-terminal robot reply, capped at the conversation window.
        [DataMember(Name = "conversationTurnRefillSeconds")]
        public float ConversationTurnRefillSeconds { get; set; }

        // Hard cap on player↔robot exchanges per conversation.
        [DataMember(Name = "conversationMaxTurns")]
        public int ConversationMaxTurns { get; set; }

        // Background "the horde was massing while you talked" pressure: fraction (0..1) accrued per real second the
        // world is frozen, and how strongly full pressure compresses the next spawns (interval down, max-alive up).
        [DataMember(Name = "pressureRampPerSecond")]
        public float PressureRampPerSecond { get; set; }

        [DataMember(Name = "pressureSpawnBoost")]
        public float PressureSpawnBoost { get; set; }

        // Persuasion-meter tuning (the engine-owned CONVERT gate). Seed bias re-centres the JoinMe compliance into a
        // starting disposition; the threshold is raised by archetype resistance; per-turn nudges move it.
        [DataMember(Name = "convSeedBias")]
        public float ConvSeedBias { get; set; }

        [DataMember(Name = "convertThreshold")]
        public float ConvertThreshold { get; set; }

        [DataMember(Name = "convertResistanceWeight")]
        public float ConvertResistanceWeight { get; set; }

        [DataMember(Name = "convertNudge")]
        public float ConvertNudge { get; set; }

        [DataMember(Name = "standDownNudge")]
        public float StandDownNudge { get; set; }

        [DataMember(Name = "fleeNudge")]
        public float FleeNudge { get; set; }

        [DataMember(Name = "refuseNudge")]
        public float RefuseNudge { get; set; }

        [DataMember(Name = "enrageDispositionFloor")]
        public float EnrageDispositionFloor { get; set; }

        // --- Ally "politics": a talked-in ally is a relationship, not a timer (Civ-style loyalty) ---------------
        // Loyalty (0..1) is seeded from how persuaded the robot was, drifts down over time (faster the more corrupt
        // it is), rises when it fights for you, and falls when you shoot it. Below the waver threshold it telegraphs;
        // at zero it defects back to the swarm. Re-jack-in to renegotiate and shore it up.
        [DataMember(Name = "loyaltySeedMin")]
        public float LoyaltySeedMin { get; set; }

        [DataMember(Name = "loyaltySeedMax")]
        public float LoyaltySeedMax { get; set; }

        [DataMember(Name = "loyaltyDecayPerSecond")]
        public float LoyaltyDecayPerSecond { get; set; }

        [DataMember(Name = "loyaltyCorruptionWeight")]
        public float LoyaltyCorruptionWeight { get; set; }

        [DataMember(Name = "loyaltyPerAssist")]
        public float LoyaltyPerAssist { get; set; }

        [DataMember(Name = "loyaltyShotPenalty")]
        public float LoyaltyShotPenalty { get; set; }

        [DataMember(Name = "loyaltyWaverThreshold")]
        public float LoyaltyWaverThreshold { get; set; }

        // Per-archetype resistance to being overridden (0..1). Brute resists hardest; Runt crumbles.
        [DataMember(Name = "overrideResistGrunt")]
        public float OverrideResistGrunt { get; set; }

        [DataMember(Name = "overrideResistSprinter")]
        public float OverrideResistSprinter { get; set; }

        [DataMember(Name = "overrideResistBrute")]
        public float OverrideResistBrute { get; set; }

        [DataMember(Name = "overrideResistRunt")]
        public float OverrideResistRunt { get; set; }

        // DataContractJsonSerializer constructs instances with FormatterServices.GetUninitializedObject,
        // which bypasses both the constructor and C# property initializers. Without this hook any field
        // missing from the JSON would deserialize to 0 and then be clamped to its floor by Normalize()
        // instead of resolving to the intended default. Seed every member before members are read so that
        // absent fields keep their real defaults while present ones still override them.
        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            SeedDefaults();
        }

        private void SeedDefaults()
        {
            TargetWorldId = string.Empty;
            StartingCountdownSeconds = 3f;
            InterWaveDelaySeconds = 8f;
            BaseZombiesPerWave = 5;
            ZombiesPerWaveIncrement = 3;
            MaxAliveZombies = 12;
            SpawnRadius = 28f;
            MinSpawnDistance = 10f;
            SpawnHeightOffset = 0.25f;
            SpawnIntervalSeconds = 0.75f;
            SpawnSearchAttempts = 18;
            PlayerIntegrity = 100f;
            ZombieHealth = 45f;
            ZombieMoveSpeed = 3.2f;
            ZombieTurnSpeed = 480f;
            ZombieAttackRange = 2.35f;
            ZombieAttackCooldownSeconds = 1.1f;
            ZombieAttackDamage = 10f;
            ScorePerKill = 100;
            // Repurposed for the new HP table / snappier feel: was 30 / 0.22 / 0.06.
            ZapperDamage = 22f;
            ZapperRange = 42f;
            ZapperCooldownSeconds = 0.16f;
            ZapperImpactForce = 7f;
            ZapperBeamLifetimeSeconds = 0.10f;

            ArchetypesEnabled = true;
            SprinterHealthMult = 0.4f;
            SprinterSpeed = 6.2f;
            SprinterScale = 0.8f;
            SprinterScore = 130;
            BruteHealthMult = 4.9f;
            BruteSpeed = 2.0f;
            BruteScale = 1.6f;
            BruteScore = 350;
            BruteAttackMult = 2.2f;
            BruteEasyHeadFraction = 0.70f;
            BruteMaxAlive = 2;
            RuntHealthMult = 0.22f;
            RuntSpeed = 5.0f;
            RuntScale = 0.55f;
            RuntScore = 40;
            RuntPackMin = 4;
            RuntPackMax = 6;
            EnableEnemyEmotes = true;

            HeadshotDamageMultiplier = 2.0f;
            HeadshotHeightFraction = 0.78f;
            HeadshotFlatBonusScore = 25;
            ChargeShotEnabled = true;
            ChargeShotSeconds = 0.55f;
            ChargeShotDamage = 55f;
            ChargeShotCooldownSeconds = 0.9f;
            ChargeShotPierces = true;
            BigHitRagdollFraction = 0.45f;

            ComboWindowSeconds = 2.5f;
            ComboKillsPerTier = 4;
            ComboMaxMultiplier = 5;

            CrosshairBaseGapPixels = 6f;
            CrosshairBloomGapPixels = 14f;
            HitMarkerSeconds = 0.12f;
            FloatingNumberRiseSeconds = 0.7f;
            FloatingNumberMaxConcurrent = 16;
            DamageFlashSeconds = 0.35f;
            DamageFlashMaxAlpha = 0.35f;
            LowIntegrityVignetteThreshold = 0.35f;
            CriticalIntegrityThreshold = 0.15f;
            HudScale = 1f;
            HudMotionIntensity = 1f;
            HudHighContrast = false;

            BeamWidthStart = 0.10f;
            BeamWidthEnd = 0.03f;
            BeamJitterAmplitude = 0.12f;

            OverrideEnabled = true;
            UseLiveBrain = true;
            OverrideKey = "E";
            BroadcastKey = "Q";
            OverrideCharges = 3;
            OverrideChargeRegenSeconds = 9f;
            BroadcastChargeCost = 2;
            BroadcastCooldownSeconds = 22f;
            BroadcastRadius = 14f;
            MaxConvertedAllies = 4;
            ConvertDurationSeconds = 14f;
            FreezeSeconds = 2.5f;
            StandDownSeconds = 4.5f;
            FleeSeconds = 3.5f;
            EnrageSeconds = 10f;
            EnrageSpeedMult = 1.35f;
            EnrageDamageMult = 1.6f;
            AllyDamage = 12f;
            AllyAttackCooldownSeconds = 0.8f;
            AllyRetargetSeconds = 0.5f;
            LiveBrainWindowSeconds = 1.6f;
            BrainTemperature = 0.8f;
            BarkMaxChars = 90;
            SpeechBubbleSeconds = 3f;
            OverrideDifficulty = 1f;
            SuggestibilityMin = 0.15f;
            SuggestibilityMax = 0.70f;
            LoyaltyMin = 0.10f;
            LoyaltyMax = 0.70f;
            CorruptionBase = 0.15f;
            CorruptionPerWave = 0.06f;
            BiasAmplitude = 0.12f;
            OverrideResistGrunt = 0.25f;
            OverrideResistSprinter = 0.35f;
            OverrideResistBrute = 0.70f;
            OverrideResistRunt = 0.15f;

            SuperhotMode = false;
            ConversationEnabled = true;
            UseVoiceInput = true;
            JackInKey = "E";
            VoiceKey = "V";
            ToggleInputModeKey = "Tab";
            ConversationWindowSeconds = 22f;
            ConversationTurnRefillSeconds = 4f;
            ConversationMaxTurns = 3;
            PressureRampPerSecond = 0.06f;
            PressureSpawnBoost = 0.6f;
            ConvSeedBias = 0.35f;
            ConvertThreshold = 0.72f;
            ConvertResistanceWeight = 0.3f;
            ConvertNudge = 0.3f;
            StandDownNudge = 0.16f;
            FleeNudge = 0.06f;
            RefuseNudge = -0.14f;
            EnrageDispositionFloor = 0.12f;
            LoyaltySeedMin = 0.55f;
            LoyaltySeedMax = 1.0f;
            LoyaltyDecayPerSecond = 0.012f;
            LoyaltyCorruptionWeight = 0.8f;
            LoyaltyPerAssist = 0.05f;
            LoyaltyShotPenalty = 0.18f;
            LoyaltyWaverThreshold = 0.3f;
        }

        public void Normalize()
        {
            StartingCountdownSeconds = Clamp(StartingCountdownSeconds, 0.5f, 20f);
            InterWaveDelaySeconds = Clamp(InterWaveDelaySeconds, 1f, 60f);
            BaseZombiesPerWave = Clamp(BaseZombiesPerWave, 1, 200);
            ZombiesPerWaveIncrement = Clamp(ZombiesPerWaveIncrement, 0, 100);
            MaxAliveZombies = Clamp(MaxAliveZombies, 1, 80);
            SpawnRadius = Clamp(SpawnRadius, 4f, 250f);
            MinSpawnDistance = Clamp(MinSpawnDistance, 0f, SpawnRadius);
            SpawnHeightOffset = Clamp(SpawnHeightOffset, -4f, 8f);
            SpawnIntervalSeconds = Clamp(SpawnIntervalSeconds, 0.05f, 20f);
            SpawnSearchAttempts = Clamp(SpawnSearchAttempts, 1, 100);
            PlayerIntegrity = Clamp(PlayerIntegrity, 1f, 10000f);
            ZombieHealth = Clamp(ZombieHealth, 1f, 10000f);
            ZombieMoveSpeed = Clamp(ZombieMoveSpeed, 0.25f, 25f);
            ZombieTurnSpeed = Clamp(ZombieTurnSpeed, 30f, 3000f);
            ZombieAttackRange = Clamp(ZombieAttackRange, 0.5f, 20f);
            ZombieAttackCooldownSeconds = Clamp(ZombieAttackCooldownSeconds, 0.05f, 20f);
            ZombieAttackDamage = Clamp(ZombieAttackDamage, 0.1f, 10000f);
            ScorePerKill = Clamp(ScorePerKill, 0, 1000000);
            ZapperDamage = Clamp(ZapperDamage, 0.1f, 10000f);
            ZapperRange = Clamp(ZapperRange, 1f, 1000f);
            ZapperCooldownSeconds = Clamp(ZapperCooldownSeconds, 0.01f, 20f);
            ZapperImpactForce = Clamp(ZapperImpactForce, 0f, 1000f);
            ZapperBeamLifetimeSeconds = Clamp(ZapperBeamLifetimeSeconds, 0.01f, 2f);

            SprinterHealthMult = Clamp(SprinterHealthMult, 0.05f, 50f);
            SprinterSpeed = Clamp(SprinterSpeed, 0.25f, 25f);
            SprinterScale = Clamp(SprinterScale, 0.2f, 6f);
            SprinterScore = Clamp(SprinterScore, 0, 1000000);
            BruteHealthMult = Clamp(BruteHealthMult, 0.05f, 100f);
            BruteSpeed = Clamp(BruteSpeed, 0.25f, 25f);
            BruteScale = Clamp(BruteScale, 0.2f, 6f);
            BruteScore = Clamp(BruteScore, 0, 1000000);
            BruteAttackMult = Clamp(BruteAttackMult, 0.1f, 50f);
            BruteEasyHeadFraction = Clamp(BruteEasyHeadFraction, 0.1f, 1f);
            BruteMaxAlive = Clamp(BruteMaxAlive, 0, 80);
            RuntHealthMult = Clamp(RuntHealthMult, 0.02f, 50f);
            RuntSpeed = Clamp(RuntSpeed, 0.25f, 25f);
            RuntScale = Clamp(RuntScale, 0.15f, 6f);
            RuntScore = Clamp(RuntScore, 0, 1000000);
            RuntPackMin = Clamp(RuntPackMin, 1, 40);
            RuntPackMax = Clamp(RuntPackMax, RuntPackMin, 40);

            HeadshotDamageMultiplier = Clamp(HeadshotDamageMultiplier, 1f, 100f);
            HeadshotHeightFraction = Clamp(HeadshotHeightFraction, 0.1f, 1f);
            HeadshotFlatBonusScore = Clamp(HeadshotFlatBonusScore, 0, 1000000);
            ChargeShotSeconds = Clamp(ChargeShotSeconds, 0.05f, 10f);
            ChargeShotDamage = Clamp(ChargeShotDamage, 0.1f, 10000f);
            ChargeShotCooldownSeconds = Clamp(ChargeShotCooldownSeconds, 0.05f, 20f);
            BigHitRagdollFraction = Clamp(BigHitRagdollFraction, 0.05f, 10f);

            ComboWindowSeconds = Clamp(ComboWindowSeconds, 0.5f, 30f);
            ComboKillsPerTier = Clamp(ComboKillsPerTier, 1, 100);
            ComboMaxMultiplier = Clamp(ComboMaxMultiplier, 1, 100);

            CrosshairBaseGapPixels = Clamp(CrosshairBaseGapPixels, 0f, 60f);
            CrosshairBloomGapPixels = Clamp(CrosshairBloomGapPixels, 0f, 120f);
            HitMarkerSeconds = Clamp(HitMarkerSeconds, 0.02f, 2f);
            FloatingNumberRiseSeconds = Clamp(FloatingNumberRiseSeconds, 0.1f, 5f);
            FloatingNumberMaxConcurrent = Clamp(FloatingNumberMaxConcurrent, 1, 128);
            DamageFlashSeconds = Clamp(DamageFlashSeconds, 0.05f, 3f);
            DamageFlashMaxAlpha = Clamp(DamageFlashMaxAlpha, 0f, 1f);
            LowIntegrityVignetteThreshold = Clamp(LowIntegrityVignetteThreshold, 0f, 1f);
            CriticalIntegrityThreshold = Clamp(CriticalIntegrityThreshold, 0f, LowIntegrityVignetteThreshold);
            HudScale = Clamp(HudScale, 0.75f, 1.35f);
            HudMotionIntensity = Clamp(HudMotionIntensity, 0f, 2f);

            BeamWidthStart = Clamp(BeamWidthStart, 0.005f, 2f);
            BeamWidthEnd = Clamp(BeamWidthEnd, 0.001f, 2f);
            BeamJitterAmplitude = Clamp(BeamJitterAmplitude, 0f, 2f);

            if (string.IsNullOrWhiteSpace(OverrideKey))
            {
                OverrideKey = "E";
            }

            if (string.IsNullOrWhiteSpace(BroadcastKey))
            {
                BroadcastKey = "Q";
            }

            OverrideCharges = Clamp(OverrideCharges, 1, 20);
            OverrideChargeRegenSeconds = Clamp(OverrideChargeRegenSeconds, 0.5f, 120f);
            BroadcastChargeCost = Clamp(BroadcastChargeCost, 1, OverrideCharges);
            BroadcastCooldownSeconds = Clamp(BroadcastCooldownSeconds, 1f, 240f);
            BroadcastRadius = Clamp(BroadcastRadius, 2f, 120f);
            MaxConvertedAllies = Clamp(MaxConvertedAllies, 0, 40);
            ConvertDurationSeconds = Clamp(ConvertDurationSeconds, 1f, 120f);
            FreezeSeconds = Clamp(FreezeSeconds, 0.2f, 60f);
            StandDownSeconds = Clamp(StandDownSeconds, 0.2f, 60f);
            FleeSeconds = Clamp(FleeSeconds, 0.2f, 60f);
            EnrageSeconds = Clamp(EnrageSeconds, 0.5f, 120f);
            EnrageSpeedMult = Clamp(EnrageSpeedMult, 1f, 5f);
            EnrageDamageMult = Clamp(EnrageDamageMult, 1f, 10f);
            AllyDamage = Clamp(AllyDamage, 0.1f, 10000f);
            AllyAttackCooldownSeconds = Clamp(AllyAttackCooldownSeconds, 0.05f, 20f);
            AllyRetargetSeconds = Clamp(AllyRetargetSeconds, 0.1f, 5f);
            LiveBrainWindowSeconds = Clamp(LiveBrainWindowSeconds, 0.2f, 10f);
            BrainTemperature = Clamp(BrainTemperature, 0f, 2f);
            BarkMaxChars = Clamp(BarkMaxChars, 10, 240);
            SpeechBubbleSeconds = Clamp(SpeechBubbleSeconds, 0.5f, 10f);
            OverrideDifficulty = Clamp(OverrideDifficulty, 0.25f, 4f);
            SuggestibilityMin = Clamp(SuggestibilityMin, 0f, 1f);
            SuggestibilityMax = Clamp(SuggestibilityMax, SuggestibilityMin, 1f);
            LoyaltyMin = Clamp(LoyaltyMin, 0f, 1f);
            LoyaltyMax = Clamp(LoyaltyMax, LoyaltyMin, 1f);
            CorruptionBase = Clamp(CorruptionBase, 0f, 1f);
            CorruptionPerWave = Clamp(CorruptionPerWave, 0f, 0.5f);
            BiasAmplitude = Clamp(BiasAmplitude, 0f, 1f);
            OverrideResistGrunt = Clamp(OverrideResistGrunt, 0f, 1f);
            OverrideResistSprinter = Clamp(OverrideResistSprinter, 0f, 1f);
            OverrideResistBrute = Clamp(OverrideResistBrute, 0f, 1f);
            OverrideResistRunt = Clamp(OverrideResistRunt, 0f, 1f);

            if (string.IsNullOrWhiteSpace(JackInKey))
            {
                JackInKey = "E";
            }

            if (string.IsNullOrWhiteSpace(VoiceKey))
            {
                VoiceKey = "V";
            }

            if (string.IsNullOrWhiteSpace(ToggleInputModeKey))
            {
                ToggleInputModeKey = "Tab";
            }

            ConversationWindowSeconds = Clamp(ConversationWindowSeconds, 4f, 120f);
            ConversationTurnRefillSeconds = Clamp(ConversationTurnRefillSeconds, 0f, ConversationWindowSeconds);
            ConversationMaxTurns = Clamp(ConversationMaxTurns, 1, 8);
            PressureRampPerSecond = Clamp(PressureRampPerSecond, 0f, 1f);
            PressureSpawnBoost = Clamp(PressureSpawnBoost, 0f, 0.95f);
            ConvSeedBias = Clamp(ConvSeedBias, 0f, 1f);
            ConvertThreshold = Clamp(ConvertThreshold, 0.1f, 0.97f);
            ConvertResistanceWeight = Clamp(ConvertResistanceWeight, 0f, 1f);
            ConvertNudge = Clamp(ConvertNudge, 0f, 1f);
            StandDownNudge = Clamp(StandDownNudge, 0f, 1f);
            FleeNudge = Clamp(FleeNudge, 0f, 1f);
            RefuseNudge = Clamp(RefuseNudge, -1f, 0f);
            EnrageDispositionFloor = Clamp(EnrageDispositionFloor, 0f, 0.5f);
            LoyaltySeedMin = Clamp(LoyaltySeedMin, 0f, 1f);
            LoyaltySeedMax = Clamp(LoyaltySeedMax, LoyaltySeedMin, 1f);
            LoyaltyDecayPerSecond = Clamp(LoyaltyDecayPerSecond, 0f, 0.5f);
            LoyaltyCorruptionWeight = Clamp(LoyaltyCorruptionWeight, 0f, 4f);
            LoyaltyPerAssist = Clamp(LoyaltyPerAssist, 0f, 1f);
            LoyaltyShotPenalty = Clamp(LoyaltyShotPenalty, 0f, 1f);
            LoyaltyWaverThreshold = Clamp(LoyaltyWaverThreshold, 0f, 0.9f);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
        }
    }
}
