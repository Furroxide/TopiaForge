using System;
using TopiaForge.Mods;

namespace TopiaForge.Zombies
{
    /// <summary>Session-owned state for one infected RobotKit agent.</summary>
    internal sealed class ZombieEnemy : IDisposable
    {
        private const float CorpseLifetimeSeconds = 3f;
        private readonly bool emotesEnabled;
        private bool disposed;
        private bool wavering;
        private bool hasProgressSample;
        private Vec3 lastProgressPosition;

        public ZombieEnemy(
            IRobotAgent agent,
            ZombieArchetype archetype,
            in RobotMind mind,
            bool emotesEnabled)
        {
            Agent = agent ?? throw new ArgumentNullException(nameof(agent));
            Archetype = archetype ?? throw new ArgumentNullException(nameof(archetype));
            Mind = mind;
            this.emotesEnabled = emotesEnabled;
            Health = archetype.Health;
            State = HijackState.Hostile;
        }

        public IRobotAgent Agent { get; }
        public ZombieArchetype Archetype { get; }
        public RobotMind Mind { get; }
        public float Health { get; private set; }
        public float AttackCooldown { get; set; }
        public float AllyAttackCooldown { get; set; }
        public float AllyRetargetTimer { get; set; }
        public string AllyTargetId { get; set; } = string.Empty;
        public HijackState State { get; private set; }
        public float StateTimer { get; private set; }
        public float Loyalty { get; private set; }
        public float AllianceGrace { get; private set; }
        public float RecentlyShotTimer { get; private set; }
        public bool Defeated { get; private set; }
        public bool Scored { get; set; }
        public float CorpseTimer { get; private set; }
        public float StalledSeconds { get; private set; }

        public bool IsActive => !disposed && !Defeated && Agent.IsAlive && Health > 0f;
        public bool IsHostile => IsActive && (State == HijackState.Hostile || State == HijackState.Enraged);
        public bool IsAlly => IsActive && State == HijackState.Allied;
        public bool WasRecentlyShot => RecentlyShotTimer > 0f;
        public float HealthFraction => Archetype.Health <= 0f ? 0f : Math.Max(0f, Health / Archetype.Health);

        /// <param name="byPlayer">
        /// Whether the human fired the shot. Only that sets <see cref="WasRecentlyShot"/>, because its one consumer
        /// is the ground truth handed to the brain — reporting "the human just shot you" for an ally's crossfire
        /// makes the robot argue against something the player did not do.
        /// </param>
        public bool ApplyDamage(float amount, bool byPlayer)
        {
            if (!IsActive || amount <= 0f || float.IsNaN(amount) || float.IsInfinity(amount))
            {
                return false;
            }

            Health = Math.Max(0f, Health - amount);
            if (byPlayer)
            {
                RecentlyShotTimer = 3f;
            }

            return Health <= 0f;
        }

        public void MarkDefeated(RobotDamageType type, string source)
        {
            if (Defeated)
            {
                return;
            }

            Defeated = true;
            Health = 0f;
            CorpseTimer = CorpseLifetimeSeconds;
            Agent.Kill(type, source);
        }

        public void MarkExternallyDefeated()
        {
            if (Defeated)
            {
                return;
            }

            Defeated = true;
            Health = 0f;
            CorpseTimer = 0f;
        }

        public void Freeze(float seconds, bool standDown)
        {
            if (!IsActive)
            {
                return;
            }

            State = HijackState.Frozen;
            StateTimer = Math.Max(0f, seconds);
            Agent.Stop();
            SetEmote(standDown ? ":warning:" : ":snowflake:");
        }

        public void Flee(float seconds)
        {
            if (!IsActive)
            {
                return;
            }

            State = HijackState.Fleeing;
            StateTimer = Math.Max(0f, seconds);
            SetEmote(":dash:");
        }

        public void Enrage(float seconds, ZombiesConfig config)
        {
            if (!IsActive)
            {
                return;
            }

            State = HijackState.Enraged;
            StateTimer = Math.Max(0f, seconds);
            Agent.ConfigureMovement(new RobotMovementSettings(
                Archetype.Gait,
                Archetype.MoveSpeed * config.EnrageSpeedMult,
                config.ZombieTurnSpeed,
                Archetype.StopDistance));
            SetEmote(":rage:");
        }

        public void Convert(ZombiesConfig config, float disposition)
        {
            if (!IsActive)
            {
                return;
            }

            State = HijackState.Allied;
            StateTimer = 0f;
            AllianceGrace = config.ConvertDurationSeconds;
            Loyalty = Clamp(
                config.LoyaltySeedMin
                    + (Clamp(disposition, 0f, 1f) * (config.LoyaltySeedMax - config.LoyaltySeedMin)),
                config.LoyaltySeedMin,
                config.LoyaltySeedMax);
            Agent.Stop();
            Agent.SetTint(new RobotColor(0.25f, 0.75f, 1f, 1f));
            SetEmote(":shield:");
        }

        public void AddLoyalty(float amount, ZombiesConfig config)
        {
            if (IsAlly && amount > 0f)
            {
                Loyalty = Math.Min(1f, Loyalty + amount);
                if (Loyalty > config.LoyaltyWaverThreshold)
                {
                    wavering = false;
                }
            }
        }

        public void PenalizeLoyalty(float amount, ZombiesConfig config)
        {
            if (!IsAlly || amount <= 0f)
            {
                return;
            }

            Loyalty = Math.Max(0f, Loyalty - amount);
            if (Loyalty <= 0f)
            {
                RestoreHostile(config);
            }
        }

        /// <summary>Advances timers and returns true when the corpse/agent can be released.</summary>
        public bool Tick(float worldDelta, ZombiesConfig config)
        {
            if (disposed)
            {
                return true;
            }

            worldDelta = Math.Max(0f, worldDelta);
            RecentlyShotTimer = Math.Max(0f, RecentlyShotTimer - worldDelta);
            if (Defeated)
            {
                CorpseTimer = Math.Max(0f, CorpseTimer - worldDelta);
                return CorpseTimer <= 0f;
            }

            if (!Agent.IsAlive)
            {
                MarkExternallyDefeated();
                return true;
            }

            if (State == HijackState.Allied)
            {
                if (AllianceGrace > 0f)
                {
                    AllianceGrace = Math.Max(0f, AllianceGrace - worldDelta);
                }
                else
                {
                    var decay = config.LoyaltyDecayPerSecond
                        * (1f + (Mind.Corruption * config.LoyaltyCorruptionWeight));
                    Loyalty = Math.Max(0f, Loyalty - (decay * worldDelta));
                }

                if (Loyalty <= 0f)
                {
                    RestoreHostile(config);
                }
                else if (!wavering && Loyalty <= config.LoyaltyWaverThreshold)
                {
                    wavering = true;
                    SetEmote(":warning:");
                }

                return false;
            }

            if (State == HijackState.Hostile)
            {
                return false;
            }

            StateTimer = Math.Max(0f, StateTimer - worldDelta);
            if (StateTimer <= 0f)
            {
                RestoreHostile(config);
            }

            return false;
        }

        public void RestoreHostile(ZombiesConfig config)
        {
            if (!IsActive)
            {
                return;
            }

            State = HijackState.Hostile;
            StateTimer = 0f;
            AllianceGrace = 0f;
            Loyalty = 0f;
            wavering = false;
            AllyTargetId = string.Empty;
            Agent.ConfigureMovement(new RobotMovementSettings(
                Archetype.Gait,
                Archetype.MoveSpeed,
                config.ZombieTurnSpeed,
                Archetype.StopDistance));
            Agent.SetTint(Archetype.Tint);
            SetEmote(Archetype.Emote);
        }

        public bool TrackProgress(float deltaTime, bool expectedToMove, float timeoutSeconds)
        {
            if (!expectedToMove || !IsHostile)
            {
                StalledSeconds = 0f;
                hasProgressSample = false;
                return false;
            }

            var current = Agent.Position;
            if (!hasProgressSample)
            {
                lastProgressPosition = current;
                hasProgressSample = true;
                return false;
            }

            if ((current - lastProgressPosition).LengthSquared >= 0.04f)
            {
                lastProgressPosition = current;
                StalledSeconds = 0f;
                return false;
            }

            StalledSeconds += Math.Max(0f, deltaTime);
            return StalledSeconds >= timeoutSeconds;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Agent.Dispose();
        }

        private void SetEmote(string shortcode)
        {
            if (emotesEnabled)
            {
                Agent.SetEmote(shortcode);
            }
        }

        private static float Clamp(float value, float minimum, float maximum) =>
            value < minimum ? minimum : (value > maximum ? maximum : value);
    }
}
