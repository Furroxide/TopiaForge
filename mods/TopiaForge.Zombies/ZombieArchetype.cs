using System;
using TopiaForge.Mods;

namespace TopiaForge.Zombies
{
    // The infected-robot cast. Each kind is instantly readable from tint + scale + gait + emote and plays
    // differently (health/speed/attack/score). Kept as a tiny enum so the controller, enemy, and HUD share one
    // vocabulary.
    internal enum ZombieKind
    {
        Grunt,
        Sprinter,
        Brute,
        Runt
    }

    // One fully-resolved archetype: all numbers are absolute (already folded through the config multipliers in the
    // roster) so the rest of the mod never multiplies again. Pure data, no Unity types — the single source of truth
    // for how a kind looks and plays, and trivially unit-testable.
    internal sealed class ZombieArchetype
    {
        public ZombieArchetype(
            ZombieKind kind,
            string displayName,
            RobotColor tint,
            float scale,
            RobotGait gait,
            string emote,
            float health,
            float moveSpeed,
            float attackDamage,
            float attackCooldown,
            float stopDistance,
            int score,
            bool ignoreLightKnockback,
            float headFraction,
            int packMin,
            int packMax,
            float baseResistance)
        {
            Kind = kind;
            DisplayName = displayName;
            Tint = tint;
            Scale = scale;
            Gait = gait;
            Emote = emote;
            Health = health;
            MoveSpeed = moveSpeed;
            AttackDamage = attackDamage;
            AttackCooldown = attackCooldown;
            StopDistance = stopDistance;
            Score = score;
            IgnoreLightKnockback = ignoreLightKnockback;
            HeadFraction = headFraction;
            PackMin = packMin;
            PackMax = packMax;
            BaseResistance = baseResistance;
        }

        public ZombieKind Kind { get; }
        public string DisplayName { get; }
        public RobotColor Tint { get; }
        public float Scale { get; }
        public RobotGait Gait { get; }
        public string Emote { get; }
        public float Health { get; }
        public float MoveSpeed { get; }
        public float AttackDamage { get; }
        public float AttackCooldown { get; }
        public float StopDistance { get; }
        public int Score { get; }
        public bool IgnoreLightKnockback { get; }
        public float HeadFraction { get; }
        public int PackMin { get; }
        public int PackMax { get; }

        // How hard this kind is to OVERRIDE (0..1, subtracted from the persuasion roll). A Brute resists hardest, so a
        // converted Brute is the high-stakes juggernaut payoff; a Runt crumbles. Tunable via config.
        public float BaseResistance { get; }

        // A pack archetype spawns as a burst of PackMin..PackMax over consecutive spawn ticks (the swarm).
        public bool IsPack => PackMax > 1;
    }

    // Builds the four archetypes from config (so every number stays tunable) and rolls which kind spawns for a
    // given wave. Grunt is the baseline; the others gate in by wave and ramp up in weight across three tiers
    // (waves 1-2 / 3-4 / 5+). All identity (colours, the sprinter/runt attack/cooldown character) lives here.
    internal sealed class ZombieRoster
    {
        private readonly ZombieArchetype grunt;
        private readonly ZombieArchetype sprinter;
        private readonly ZombieArchetype brute;
        private readonly ZombieArchetype runt;

        public ZombieRoster(ZombiesConfig config)
        {
            var defaultStop = config.ZombieAttackRange * 0.8f;

            grunt = new ZombieArchetype(
                ZombieKind.Grunt, "Grunt",
                new RobotColor(0.45f, 0.95f, 0.30f, 1f), 1.0f, RobotGait.Run, ":rage:",
                health: config.ZombieHealth,
                moveSpeed: config.ZombieMoveSpeed,
                attackDamage: config.ZombieAttackDamage,
                attackCooldown: config.ZombieAttackCooldownSeconds,
                stopDistance: defaultStop,
                score: config.ScorePerKill,
                ignoreLightKnockback: false,
                headFraction: config.HeadshotHeightFraction,
                packMin: 1, packMax: 1,
                baseResistance: config.OverrideResistGrunt);

            sprinter = new ZombieArchetype(
                ZombieKind.Sprinter, "Sprinter",
                new RobotColor(1.0f, 0.85f, 0.15f, 1f), config.SprinterScale, RobotGait.Sprint, ":zap:",
                health: config.ZombieHealth * config.SprinterHealthMult,
                moveSpeed: config.SprinterSpeed,
                attackDamage: config.ZombieAttackDamage * 0.8f,
                attackCooldown: 0.9f,
                stopDistance: Mathf01(1.6f, defaultStop),
                score: config.SprinterScore,
                ignoreLightKnockback: false,
                headFraction: config.HeadshotHeightFraction,
                packMin: 1, packMax: 1,
                baseResistance: config.OverrideResistSprinter);

            brute = new ZombieArchetype(
                ZombieKind.Brute, "Brute",
                new RobotColor(0.55f, 0.25f, 0.65f, 1f), config.BruteScale, RobotGait.Walk, ":skull:",
                health: config.ZombieHealth * config.BruteHealthMult,
                moveSpeed: config.BruteSpeed,
                attackDamage: config.ZombieAttackDamage * config.BruteAttackMult,
                attackCooldown: 1.6f,
                stopDistance: defaultStop,
                score: config.BruteScore,
                ignoreLightKnockback: true,
                headFraction: config.BruteEasyHeadFraction,
                packMin: 1, packMax: 1,
                baseResistance: config.OverrideResistBrute);

            runt = new ZombieArchetype(
                ZombieKind.Runt, "Runt",
                new RobotColor(0.70f, 1.0f, 0.55f, 1f), config.RuntScale, RobotGait.Sprint, ":bug:",
                health: config.ZombieHealth * config.RuntHealthMult,
                moveSpeed: config.RuntSpeed,
                attackDamage: config.ZombieAttackDamage * 0.5f,
                attackCooldown: 0.8f,
                stopDistance: Mathf01(1.4f, defaultStop),
                score: config.RuntScore,
                ignoreLightKnockback: false,
                headFraction: config.HeadshotHeightFraction,
                packMin: Math.Max(1, config.RuntPackMin),
                packMax: Math.Max(Math.Max(1, config.RuntPackMin), config.RuntPackMax),
                baseResistance: config.OverrideResistRunt);
        }

        public ZombieArchetype Get(ZombieKind kind)
        {
            switch (kind)
            {
                case ZombieKind.Sprinter:
                    return sprinter;
                case ZombieKind.Brute:
                    return brute;
                case ZombieKind.Runt:
                    return runt;
                default:
                    return grunt;
            }
        }

        // Pick the archetype kind that spawns next, given the current wave. Grunt is always eligible; the others
        // gate in by minimum wave and grow in weight as waves escalate. Returns Grunt if nothing else qualifies.
        public ZombieKind PickKind(int wave, Random random)
        {
            var tier = wave <= 2 ? 0 : (wave <= 4 ? 1 : 2);

            // weights[tier]; an entry is only counted once wave >= the kind's minimum wave.
            var gruntWeight = Weight(60, 45, 35, tier);
            var sprinterWeight = wave >= 2 ? Weight(10, 30, 35, tier) : 0;
            var runtWeight = wave >= 3 ? Weight(0, 13, 22, tier) : 0;
            var bruteWeight = wave >= 4 ? Weight(0, 12, 18, tier) : 0;

            var total = gruntWeight + sprinterWeight + runtWeight + bruteWeight;
            if (total <= 0)
            {
                return ZombieKind.Grunt;
            }

            var roll = random.Next(total);
            if ((roll -= gruntWeight) < 0)
            {
                return ZombieKind.Grunt;
            }

            if ((roll -= sprinterWeight) < 0)
            {
                return ZombieKind.Sprinter;
            }

            if ((roll -= runtWeight) < 0)
            {
                return ZombieKind.Runt;
            }

            return ZombieKind.Brute;
        }

        private static int Weight(int tier0, int tier1, int tier2, int tier)
        {
            return tier == 0 ? tier0 : (tier == 1 ? tier1 : tier2);
        }

        // Clamp a desired absolute value to be at most the default stop distance (so a tiny enemy that wants to
        // close tighter never ends up further out than the baseline).
        private static float Mathf01(float desired, float fallback)
        {
            return desired > 0f && desired < fallback ? desired : fallback;
        }
    }
}
