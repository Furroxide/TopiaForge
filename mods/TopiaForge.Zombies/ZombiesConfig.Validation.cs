namespace TopiaForge.Zombies
{
    public sealed partial class ZombiesConfig
    {
        public void Normalize()
        {
            // Comparisons do not reject NaN. Restore non-finite input to its documented default before clamping.
            var defaults = DocumentedDefaults;

            TargetWorldId = string.IsNullOrWhiteSpace(TargetWorldId)
                ? TopiaForge.Mods.WellKnownWorldIds.OpenSandboxWorld
                : TargetWorldId.Trim();

            StartingCountdownSeconds = ClampFinite(StartingCountdownSeconds, defaults.StartingCountdownSeconds, 0.5f, 20f);
            InterWaveDelaySeconds = ClampFinite(InterWaveDelaySeconds, defaults.InterWaveDelaySeconds, 1f, 60f);
            BaseZombiesPerWave = Clamp(BaseZombiesPerWave, 1, 200);
            ZombiesPerWaveIncrement = Clamp(ZombiesPerWaveIncrement, 0, 100);
            MaxAliveZombies = Clamp(MaxAliveZombies, 1, 80);
            SpawnRadius = ClampFinite(SpawnRadius, defaults.SpawnRadius, 4f, 250f);
            MinSpawnDistance = ClampFinite(MinSpawnDistance, defaults.MinSpawnDistance, 0f, SpawnRadius);
            SpawnHeightOffset = ClampFinite(SpawnHeightOffset, defaults.SpawnHeightOffset, -4f, 8f);
            SpawnIntervalSeconds = ClampFinite(SpawnIntervalSeconds, defaults.SpawnIntervalSeconds, 0.05f, 20f);
            SpawnSearchAttempts = Clamp(SpawnSearchAttempts, 1, 100);
            PlayerIntegrity = ClampFinite(PlayerIntegrity, defaults.PlayerIntegrity, 1f, 10000f);
            ZombieHealth = ClampFinite(ZombieHealth, defaults.ZombieHealth, 1f, 10000f);
            ZombieMoveSpeed = ClampFinite(ZombieMoveSpeed, defaults.ZombieMoveSpeed, 0.25f, 25f);
            ZombieTurnSpeed = ClampFinite(ZombieTurnSpeed, defaults.ZombieTurnSpeed, 30f, 3000f);
            ZombieAttackRange = ClampFinite(ZombieAttackRange, defaults.ZombieAttackRange, 0.5f, 20f);
            ZombieAttackCooldownSeconds = ClampFinite(ZombieAttackCooldownSeconds, defaults.ZombieAttackCooldownSeconds, 0.05f, 20f);
            ZombieAttackDamage = ClampFinite(ZombieAttackDamage, defaults.ZombieAttackDamage, 0.1f, 10000f);
            ScorePerKill = Clamp(ScorePerKill, 0, 1000000);
            ZapperDamage = ClampFinite(ZapperDamage, defaults.ZapperDamage, 0.1f, 10000f);
            ZapperRange = ClampFinite(ZapperRange, defaults.ZapperRange, 1f, 1000f);
            ZapperCooldownSeconds = ClampFinite(ZapperCooldownSeconds, defaults.ZapperCooldownSeconds, 0.01f, 20f);
            ZapperImpactForce = ClampFinite(ZapperImpactForce, defaults.ZapperImpactForce, 0f, 1000f);

            SprinterHealthMult = ClampFinite(SprinterHealthMult, defaults.SprinterHealthMult, 0.05f, 50f);
            SprinterSpeed = ClampFinite(SprinterSpeed, defaults.SprinterSpeed, 0.25f, 25f);
            SprinterScale = ClampFinite(SprinterScale, defaults.SprinterScale, 0.2f, 6f);
            SprinterScore = Clamp(SprinterScore, 0, 1000000);
            BruteHealthMult = ClampFinite(BruteHealthMult, defaults.BruteHealthMult, 0.05f, 100f);
            BruteSpeed = ClampFinite(BruteSpeed, defaults.BruteSpeed, 0.25f, 25f);
            BruteScale = ClampFinite(BruteScale, defaults.BruteScale, 0.2f, 6f);
            BruteScore = Clamp(BruteScore, 0, 1000000);
            BruteAttackMult = ClampFinite(BruteAttackMult, defaults.BruteAttackMult, 0.1f, 50f);
            BruteEasyHeadFraction = ClampFinite(BruteEasyHeadFraction, defaults.BruteEasyHeadFraction, 0.1f, 1f);
            BruteMaxAlive = Clamp(BruteMaxAlive, 0, 80);
            RuntHealthMult = ClampFinite(RuntHealthMult, defaults.RuntHealthMult, 0.02f, 50f);
            RuntSpeed = ClampFinite(RuntSpeed, defaults.RuntSpeed, 0.25f, 25f);
            RuntScale = ClampFinite(RuntScale, defaults.RuntScale, 0.15f, 6f);
            RuntScore = Clamp(RuntScore, 0, 1000000);
            RuntPackMin = Clamp(RuntPackMin, 1, 40);
            RuntPackMax = Clamp(RuntPackMax, RuntPackMin, 40);

            HeadshotDamageMultiplier = ClampFinite(HeadshotDamageMultiplier, defaults.HeadshotDamageMultiplier, 1f, 100f);
            HeadshotHeightFraction = ClampFinite(HeadshotHeightFraction, defaults.HeadshotHeightFraction, 0.1f, 1f);
            HeadshotFlatBonusScore = Clamp(HeadshotFlatBonusScore, 0, 1000000);
            ChargeShotSeconds = ClampFinite(ChargeShotSeconds, defaults.ChargeShotSeconds, 0.05f, 10f);
            ChargeShotDamage = ClampFinite(ChargeShotDamage, defaults.ChargeShotDamage, 0.1f, 10000f);
            ChargeShotCooldownSeconds = ClampFinite(ChargeShotCooldownSeconds, defaults.ChargeShotCooldownSeconds, 0.05f, 20f);
            BigHitRagdollFraction = ClampFinite(BigHitRagdollFraction, defaults.BigHitRagdollFraction, 0.05f, 10f);

            ComboWindowSeconds = ClampFinite(ComboWindowSeconds, defaults.ComboWindowSeconds, 0.5f, 30f);
            ComboKillsPerTier = Clamp(ComboKillsPerTier, 1, 100);
            ComboMaxMultiplier = Clamp(ComboMaxMultiplier, 1, 100);

            HudScale = ClampFinite(HudScale, defaults.HudScale, 0.75f, 1.35f);
            HudMotionIntensity = ClampFinite(HudMotionIntensity, defaults.HudMotionIntensity, 0f, 2f);
            BroadcastKey = NormalizeKeyName(BroadcastKey, defaults.BroadcastKey);

            OverrideCharges = Clamp(OverrideCharges, 1, 20);
            OverrideChargeRegenSeconds = ClampFinite(OverrideChargeRegenSeconds, defaults.OverrideChargeRegenSeconds, 0.5f, 120f);
            BroadcastChargeCost = Clamp(BroadcastChargeCost, 1, OverrideCharges);
            BroadcastCooldownSeconds = ClampFinite(BroadcastCooldownSeconds, defaults.BroadcastCooldownSeconds, 1f, 240f);
            BroadcastRadius = ClampFinite(BroadcastRadius, defaults.BroadcastRadius, 2f, 120f);
            MaxConvertedAllies = Clamp(MaxConvertedAllies, 0, 40);
            ConvertDurationSeconds = ClampFinite(ConvertDurationSeconds, defaults.ConvertDurationSeconds, 1f, 120f);
            FreezeSeconds = ClampFinite(FreezeSeconds, defaults.FreezeSeconds, 0.2f, 60f);
            StandDownSeconds = ClampFinite(StandDownSeconds, defaults.StandDownSeconds, 0.2f, 60f);
            FleeSeconds = ClampFinite(FleeSeconds, defaults.FleeSeconds, 0.2f, 60f);
            EnrageSeconds = ClampFinite(EnrageSeconds, defaults.EnrageSeconds, 0.5f, 120f);
            EnrageSpeedMult = ClampFinite(EnrageSpeedMult, defaults.EnrageSpeedMult, 1f, 5f);
            EnrageDamageMult = ClampFinite(EnrageDamageMult, defaults.EnrageDamageMult, 1f, 10f);
            AllyDamage = ClampFinite(AllyDamage, defaults.AllyDamage, 0.1f, 10000f);
            AllyAttackCooldownSeconds = ClampFinite(AllyAttackCooldownSeconds, defaults.AllyAttackCooldownSeconds, 0.05f, 20f);
            AllyRetargetSeconds = ClampFinite(AllyRetargetSeconds, defaults.AllyRetargetSeconds, 0.1f, 5f);
            BrainTemperature = ClampFinite(BrainTemperature, defaults.BrainTemperature, 0f, 2f);
            OverrideDifficulty = ClampFinite(OverrideDifficulty, defaults.OverrideDifficulty, 0.25f, 4f);
            SuggestibilityMin = ClampFinite(SuggestibilityMin, defaults.SuggestibilityMin, 0f, 1f);
            SuggestibilityMax = ClampFinite(SuggestibilityMax, defaults.SuggestibilityMax, SuggestibilityMin, 1f);
            LoyaltyMin = ClampFinite(LoyaltyMin, defaults.LoyaltyMin, 0f, 1f);
            LoyaltyMax = ClampFinite(LoyaltyMax, defaults.LoyaltyMax, LoyaltyMin, 1f);
            CorruptionBase = ClampFinite(CorruptionBase, defaults.CorruptionBase, 0f, 1f);
            CorruptionPerWave = ClampFinite(CorruptionPerWave, defaults.CorruptionPerWave, 0f, 0.5f);
            BiasAmplitude = ClampFinite(BiasAmplitude, defaults.BiasAmplitude, 0f, 1f);
            OverrideResistGrunt = ClampFinite(OverrideResistGrunt, defaults.OverrideResistGrunt, 0f, 1f);
            OverrideResistSprinter = ClampFinite(OverrideResistSprinter, defaults.OverrideResistSprinter, 0f, 1f);
            OverrideResistBrute = ClampFinite(OverrideResistBrute, defaults.OverrideResistBrute, 0f, 1f);
            OverrideResistRunt = ClampFinite(OverrideResistRunt, defaults.OverrideResistRunt, 0f, 1f);

            JackInKey = NormalizeKeyName(JackInKey, defaults.JackInKey);
            VoiceKey = NormalizeKeyName(VoiceKey, defaults.VoiceKey);
            ConversationWindowSeconds = ClampFinite(ConversationWindowSeconds, defaults.ConversationWindowSeconds, 4f, 120f);
            ConversationTurnRefillSeconds = ClampFinite(ConversationTurnRefillSeconds, defaults.ConversationTurnRefillSeconds, 0f, ConversationWindowSeconds);
            ConversationMaxTurns = Clamp(ConversationMaxTurns, 1, 8);
            PressureRampPerSecond = ClampFinite(PressureRampPerSecond, defaults.PressureRampPerSecond, 0f, 1f);
            PressureSpawnBoost = ClampFinite(PressureSpawnBoost, defaults.PressureSpawnBoost, 0f, 0.95f);
            ConvSeedBias = ClampFinite(ConvSeedBias, defaults.ConvSeedBias, 0f, 1f);
            ConvertThreshold = ClampFinite(ConvertThreshold, defaults.ConvertThreshold, 0.1f, 0.97f);
            ConvertResistanceWeight = ClampFinite(ConvertResistanceWeight, defaults.ConvertResistanceWeight, 0f, 1f);
            ConvertNudge = ClampFinite(ConvertNudge, defaults.ConvertNudge, 0f, 1f);
            StandDownNudge = ClampFinite(StandDownNudge, defaults.StandDownNudge, 0f, 1f);
            FleeNudge = ClampFinite(FleeNudge, defaults.FleeNudge, 0f, 1f);
            RefuseNudge = ClampFinite(RefuseNudge, defaults.RefuseNudge, -1f, 0f);
            EnrageDispositionFloor = ClampFinite(EnrageDispositionFloor, defaults.EnrageDispositionFloor, 0f, 0.5f);
            LoyaltySeedMin = ClampFinite(LoyaltySeedMin, defaults.LoyaltySeedMin, 0f, 1f);
            LoyaltySeedMax = ClampFinite(LoyaltySeedMax, defaults.LoyaltySeedMax, LoyaltySeedMin, 1f);
            LoyaltyDecayPerSecond = ClampFinite(LoyaltyDecayPerSecond, defaults.LoyaltyDecayPerSecond, 0f, 0.5f);
            LoyaltyCorruptionWeight = ClampFinite(LoyaltyCorruptionWeight, defaults.LoyaltyCorruptionWeight, 0f, 4f);
            LoyaltyPerAssist = ClampFinite(LoyaltyPerAssist, defaults.LoyaltyPerAssist, 0f, 1f);
            LoyaltyShotPenalty = ClampFinite(LoyaltyShotPenalty, defaults.LoyaltyShotPenalty, 0f, 1f);
            LoyaltyWaverThreshold = ClampFinite(LoyaltyWaverThreshold, defaults.LoyaltyWaverThreshold, 0f, 0.9f);

            ShopKey = NormalizeKeyName(ShopKey, defaults.ShopKey);
            ShopCreditsPerScore = ClampFinite(ShopCreditsPerScore, defaults.ShopCreditsPerScore, 0f, 10f);
            ShopRepairPrice = Clamp(ShopRepairPrice, 0, 1000000);
            ShopRepairAmount = ClampFinite(ShopRepairAmount, defaults.ShopRepairAmount, 1f, 10000f);
            ShopPlatingPrice = Clamp(ShopPlatingPrice, 0, 1000000);
            ShopPlatingBonus = ClampFinite(ShopPlatingBonus, defaults.ShopPlatingBonus, 1f, 1000f);
            ShopZapperGainPrice = Clamp(ShopZapperGainPrice, 0, 1000000);
            ShopZapperGainMult = ClampFinite(ShopZapperGainMult, defaults.ShopZapperGainMult, 1f, 5f);
            ShopRapidCoilsPrice = Clamp(ShopRapidCoilsPrice, 0, 1000000);
            ShopRapidCoilsMult = ClampFinite(ShopRapidCoilsMult, defaults.ShopRapidCoilsMult, 0.5f, 1f);
            ShopUplinkCellPrice = Clamp(ShopUplinkCellPrice, 0, 1000000);
            ShopUplinkSurgePrice = Clamp(ShopUplinkSurgePrice, 0, 1000000);
            ShopComboStabilizerPrice = Clamp(ShopComboStabilizerPrice, 0, 1000000);
            ShopComboWindowBonusSeconds = ClampFinite(ShopComboWindowBonusSeconds, defaults.ShopComboWindowBonusSeconds, 0f, 10f);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
        }

        private static float ClampFinite(float value, float fallback, float min, float max)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                value = fallback;
            }

            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
        }

        private static string NormalizeKeyName(string? value, string fallback)
        {
            var candidate = value?.Trim();
            if (string.IsNullOrEmpty(candidate))
            {
                return fallback;
            }

            // Validate against backend-neutral keys rather than an engine-specific key enum.
            if (candidate.Length == 1 && candidate[0] >= 'A' && candidate[0] <= 'Z')
            {
                return candidate;
            }

            if (candidate.Length == 1 && candidate[0] >= 'a' && candidate[0] <= 'z')
            {
                return char.ToUpperInvariant(candidate[0]).ToString();
            }

            if (candidate.Length == 6
                && candidate.StartsWith("Alpha", System.StringComparison.OrdinalIgnoreCase)
                && candidate[5] >= '0'
                && candidate[5] <= '9')
            {
                return "Alpha" + candidate[5];
            }

            if (candidate.Length == 2
                && (candidate[0] == 'F' || candidate[0] == 'f')
                && candidate[1] >= '1'
                && candidate[1] <= '9')
            {
                return "F" + candidate[1];
            }

            if (candidate.Length == 3
                && (candidate[0] == 'F' || candidate[0] == 'f')
                && candidate[1] == '1'
                && candidate[2] >= '0'
                && candidate[2] <= '2')
            {
                return "F1" + candidate[2];
            }

            foreach (var knownName in KnownNamedKeys)
            {
                if (string.Equals(candidate, knownName, System.StringComparison.OrdinalIgnoreCase))
                {
                    return knownName;
                }
            }

            return fallback;
        }

        private static readonly string[] KnownNamedKeys =
        {
            "Tab", "Space", "Enter", "Backspace", "Delete", "Home", "End", "PageUp", "PageDown",
            "UpArrow", "DownArrow", "LeftArrow", "RightArrow"
        };
    }
}
