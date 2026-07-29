namespace TopiaForge.Zombies
{
    // Per-run state bought from the between-rounds shop. ZombiesController reads these live alongside the matching
    // config values; the shared, disk-persisted ZombiesConfig is never mutated, so purchases cannot leak across
    // restarts, scene resets, or config saves. Reset alongside the run counters.
    internal sealed class ZombiesRunUpgrades
    {
        public float ZapperDamageMult = 1f;
        public float ZapperCooldownMult = 1f;
        public float BonusMaxIntegrity;
        public int BonusUplinkCharges;
        public float ComboWindowBonusSeconds;

        public void Reset()
        {
            ZapperDamageMult = 1f;
            ZapperCooldownMult = 1f;
            BonusMaxIntegrity = 0f;
            BonusUplinkCharges = 0;
            ComboWindowBonusSeconds = 0f;
        }
    }
}
