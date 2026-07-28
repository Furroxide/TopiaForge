using System.Runtime.Serialization;
using TopiaForge.Mods;

namespace TopiaForge.Zombies
{
    [DataContract]
    public sealed partial class ZombiesConfig : ISelfNormalizingConfig
    {
        // Keep the serialized contract readable here. Defaults and input validation live in responsibility-named
        // partials so mod authors can find the setting surface without wading through persistence mechanics.
        private static readonly ZombiesConfig DocumentedDefaults = CreateDocumentedDefaults();

        public ZombiesConfig()
        {
            SeedDefaults();
        }

        // World/level the Zombies gamemode launches in. Defaults to the generated Open Sandbox so the wave
        // arena gets the framework HDRP sky/exposure/sun. Set it to a world id from the Worlds catalog.json
        // (e.g. "io.github.furroxide.topiaforge.worlds.level.firstlevel") to opt into a specific level.
        [DataMember(Name = "targetWorldId")]
        public string TargetWorldId { get; set; } = WellKnownWorldIds.OpenSandboxWorld;

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

        // --- HUD accessibility --------------------------------------------------------------------------------
        [DataMember(Name = "hudScale")]
        public float HudScale { get; set; }

        [DataMember(Name = "hudMotionIntensity")]
        public float HudMotionIntensity { get; set; }

        [DataMember(Name = "hudHighContrast")]
        public bool HudHighContrast { get; set; }

        // --- OVERRIDE: command the infected robots' AI brain ---------------------------------------------------
        // Master switch for the whole OVERRIDE/BROADCAST verb (false = pure-shooter v0.6.0 behaviour).
        [DataMember(Name = "overrideEnabled")]
        public bool OverrideEnabled { get; set; }

        // Opts JACK IN into RobotKit's multi-turn live conversation service when that service is available. false
        // keeps every single-target outcome deterministic and offline and never reads a token or touches the network.
        [DataMember(Name = "useLiveBrain")]
        public bool UseLiveBrain { get; set; }

        // Schema-1 compatibility only. Migration moves this retired binding to JackInKey, then clears it so new
        // configuration files expose only controls that the runtime actually consumes.
        [DataMember(Name = "overrideKey", EmitDefaultValue = false)]
        private string? LegacyOverrideKey { get; set; }

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

        [DataMember(Name = "brainTemperature")]
        public float BrainTemperature { get; set; }

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

        // --- Superhot mode (powered by TopiaForge.Chronos) -----------------------------------------------------
        // When true, the whole horde + physics only advance as fast as YOU move/aim/fire (and you stay full-speed):
        // a "time moves only when you move" zombies mode. Needs the Chronos time service; a no-op without it.
        [DataMember(Name = "superhotMode")]
        public bool SuperhotMode { get; set; }

        // --- JACK IN: free-form LLM conversation with one robot (freezes the horde) ---------------------------
        // Enables the multi-turn JACK IN conversation window. When off or unavailable, JACK IN still resolves through
        // the immediate deterministic offline path; the zapper and deterministic Q broadcast remain available too.
        [DataMember(Name = "conversationEnabled")]
        public bool ConversationEnabled { get; set; }

        // Allow push-to-talk voice input (transcribed via /agent/stt) in addition to typing. Degrades to text when no
        // microphone/backend is available.
        [DataMember(Name = "useVoiceInput")]
        public bool UseVoiceInput { get; set; }

        // Key to open a channel to the robot under the crosshair (KeyCode name, parsed leniently).
        [DataMember(Name = "jackInKey")]
        public string JackInKey { get; set; } = "E";

        // Hold-to-talk voice key. Text/voice mode is switched with a declarative, focus-safe UI button.
        [DataMember(Name = "voiceKey")]
        public string VoiceKey { get; set; } = "V";

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
        // at zero it defects back to the swarm. JACK IN spends a charge to stabilize a wavering ally.
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

        // --- Between-rounds shop (FIELD REQUISITIONS) ----------------------------------------------------------
        // Kills earn spendable credits alongside score; during the Starting/InterWave prep phases the shop key
        // opens a requisitions window and the prep countdown (and the world, via Chronos) holds while it's open.
        [DataMember(Name = "shopEnabled")]
        public bool ShopEnabled { get; set; }

        // Key that opens the shop during prep phases (KeyCode name, parsed leniently).
        [DataMember(Name = "shopKey")]
        public string ShopKey { get; set; } = "B";

        // Credits earned per awarded score point (awarded = archetype score x combo + headshot bonus).
        [DataMember(Name = "shopCreditsPerScore")]
        public float ShopCreditsPerScore { get; set; }

        [DataMember(Name = "shopRepairPrice")]
        public int ShopRepairPrice { get; set; }

        [DataMember(Name = "shopRepairAmount")]
        public float ShopRepairAmount { get; set; }

        [DataMember(Name = "shopPlatingPrice")]
        public int ShopPlatingPrice { get; set; }

        [DataMember(Name = "shopPlatingBonus")]
        public float ShopPlatingBonus { get; set; }

        [DataMember(Name = "shopZapperGainPrice")]
        public int ShopZapperGainPrice { get; set; }

        // Primary + charged damage multiplier applied per ZAPPER GAIN level (compounding).
        [DataMember(Name = "shopZapperGainMult")]
        public float ShopZapperGainMult { get; set; }

        [DataMember(Name = "shopRapidCoilsPrice")]
        public int ShopRapidCoilsPrice { get; set; }

        // Zapper cooldown multiplier applied per RAPID COILS level (compounding; < 1 shoots faster).
        [DataMember(Name = "shopRapidCoilsMult")]
        public float ShopRapidCoilsMult { get; set; }

        [DataMember(Name = "shopUplinkCellPrice")]
        public int ShopUplinkCellPrice { get; set; }

        [DataMember(Name = "shopUplinkSurgePrice")]
        public int ShopUplinkSurgePrice { get; set; }

        [DataMember(Name = "shopComboStabilizerPrice")]
        public int ShopComboStabilizerPrice { get; set; }

        [DataMember(Name = "shopComboWindowBonusSeconds")]
        public float ShopComboWindowBonusSeconds { get; set; }
    }
}
