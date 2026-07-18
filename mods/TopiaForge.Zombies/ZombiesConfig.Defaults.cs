using System.Runtime.Serialization;
using TopiaForge.Mods;

namespace TopiaForge.Zombies
{
    public sealed partial class ZombiesConfig
    {
        // DataContractJsonSerializer constructs instances with FormatterServices.GetUninitializedObject,
        // which bypasses both the constructor and C# property initializers. Seed before reading members so
        // omitted settings retain the same documented defaults as a directly constructed configuration.
        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            SeedDefaults();
        }

        private void SeedDefaults()
        {
            TargetWorldId = WellKnownWorldIds.OpenSandboxWorld;
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
            ZapperDamage = 22f;
            ZapperRange = 42f;
            ZapperCooldownSeconds = 0.16f;
            ZapperImpactForce = 7f;

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

            HudScale = 1f;
            HudMotionIntensity = 1f;
            HudHighContrast = false;

            OverrideEnabled = true;
            // Remote AI is explicit opt-in: installing the mod never reads a token or calls RoboAPI by default.
            UseLiveBrain = false;
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
            BrainTemperature = 0.8f;
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
            ConversationEnabled = false;
            UseVoiceInput = false;
            JackInKey = "E";
            VoiceKey = "V";
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

            ShopEnabled = true;
            ShopKey = "B";
            ShopCreditsPerScore = 1f;
            ShopRepairPrice = 400;
            ShopRepairAmount = 50f;
            ShopPlatingPrice = 900;
            ShopPlatingBonus = 25f;
            ShopZapperGainPrice = 700;
            ShopZapperGainMult = 1.25f;
            ShopRapidCoilsPrice = 700;
            ShopRapidCoilsMult = 0.85f;
            ShopUplinkCellPrice = 1000;
            ShopUplinkSurgePrice = 500;
            ShopComboStabilizerPrice = 600;
            ShopComboWindowBonusSeconds = 0.75f;
        }

        internal void MigrateFrom(int storedSchemaVersion)
        {
            if (storedSchemaVersion < 2 && !string.IsNullOrWhiteSpace(LegacyOverrideKey))
            {
                JackInKey = LegacyOverrideKey!;
            }

            LegacyOverrideKey = null;
        }

        private static ZombiesConfig CreateDocumentedDefaults()
        {
            var defaults = new ZombiesConfig();
            defaults.SeedDefaults();
            return defaults;
        }
    }
}
