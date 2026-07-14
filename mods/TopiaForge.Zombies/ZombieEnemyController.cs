using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;

namespace TopiaForge.Zombies
{
    // Gameplay behaviour for one infected robot. Movement is the game's OWN locomotion, driven through the
    // RobotKit IRobotAgent this is attached to (Chase the player / native pathing + animation). This component
    // owns the Zombies-specific parts: chasing, attacking the player in range, mod-tracked health, the per-hit
    // juice (headshots, knockback/ragdoll, electric spark, reaction faces, hit flash), a native ragdoll death — and
    // the OVERRIDE state machine: when the player commands this robot's AI, it resolves a deterministic "robot
    // psychology" outcome (optionally enriched a beat later by its live LLM brain) and drives the matching state
    // (convert to an ally that hunts the swarm, freeze, flee, or refuse-and-enrage).
    internal sealed class ZombieEnemyController : MonoBehaviour
    {
        // Intentionally process-lifetime singletons: shared unlit materials reused by every zapper-hit and death
        // flash so flashes never allocate a material. Not released on session teardown (a flash sphere is
        // unparented and may still be mid-life), so this is a bounded cost, not a per-session leak.
        private static Material? hitFlashMaterial;
        private static Material? deathFlashMaterial;

        // Override-state tints (applied via the cheap material-property-block path on the agent).
        private static readonly RobotColor AllyTint = new RobotColor(0.25f, 0.9f, 1f, 1f);
        private static readonly RobotColor EnrageTint = new RobotColor(1f, 0.25f, 0.2f, 1f);
        private static readonly RobotColor FrozenTint = new RobotColor(0.6f, 0.8f, 1f, 1f);
        private static readonly RobotColor FleeTint = new RobotColor(1f, 0.9f, 0.4f, 1f);
        private static readonly RobotColor WaverTint = new RobotColor(1f, 0.65f, 0.25f, 1f);

        // Override-feedback colours (HUD speech bubbles / banners).
        private const TopiaForgeTone AllyColor = TopiaForgeTone.Success;
        private const TopiaForgeTone EnrageColor = TopiaForgeTone.Danger;
        private const TopiaForgeTone FrozenColor = TopiaForgeTone.Accent;
        private const TopiaForgeTone FleeColor = TopiaForgeTone.Warning;
        private const TopiaForgeTone WaverColor = TopiaForgeTone.Warning;
        private const TopiaForgeTone ShrugColor = TopiaForgeTone.Muted;
        private const TopiaForgeTone ThinkingColor = TopiaForgeTone.Faint;
        private const TopiaForgeTone BarkColor = TopiaForgeTone.Neutral;

        // Stuck-zombie reaper (defense-in-depth against a soft-locked wave): if a zombie that should be chasing has
        // not covered StuckMoveDistance of ground in StuckTimeout seconds while still well outside attack range, it
        // cannot reach the target — despawn it so the wave can complete. Reachable-only spawns make this rare; it
        // only triggers when the target became unreachable AFTER spawn (e.g. the player jumped somewhere the zombie
        // cannot follow). The signal is the zombie's OWN displacement, not distance-to-target, so a zombie chasing a
        // player who keeps running away (and is therefore covering ground) is never mistaken for stuck.
        private const float StuckTimeout = 15f;
        private const float StuckMoveDistance = 3f;

        private ZombiesController? controller;
        private ZombiesConfig? config;
        private ZombieArchetype? archetype;
        private IRobotAgent? agent;
        private RobotMind mind;
        private float health;
        private float maxHealth;
        private float attackCooldown;
        private bool alive;
        private bool suppressScore;
        private bool engaged;
        private Vector3 stuckAnchor;
        private float lastProgressTime;
        private float lastShotTime = -999f;

        // OVERRIDE state.
        private HijackState hijackState = HijackState.Hostile;
        private float stateTimer;
        private float damageMult = 1f;
        private float allyAttackCooldown;
        private float allyRetargetTimer;
        private ZombieEnemyController? allyTarget;
        private IRobotBrainQuery? pendingQuery;
        private float brainDeadline;
        private OverrideCommand lastCommand;
        private OverrideResolution lastResolution;

        // Ally "politics": a converted ally's loyalty (0..1) drifts down over time, rises when it fights for you, and
        // falls when you shoot it; below the waver threshold it telegraphs, at zero it defects back to the swarm.
        private float loyalty;
        private bool wavering;

        public bool IsAlive => alive && agent != null && agent.IsAlive;
        public ZombieKind Kind => archetype?.Kind ?? ZombieKind.Grunt;
        public int Score => archetype?.Score ?? 0;
        public Vector3 WorldPosition => transform.position;

        // A robot the player can open a channel to. Allies are included so you can re-jack-in to renegotiate a
        // wavering ally's loyalty (the diplomacy beat); the broadcast still skips allies (TryOverride early-outs).
        public bool IsOverridable => IsAlive;

        // Counts as a threat for wave-clear/spawn-mix (alive and not converted to the player's side).
        public bool IsHostile => IsAlive && hijackState != HijackState.Allied;

        // A converted ally currently fighting for the player.
        public bool IsAlly => IsAlive && hijackState == HijackState.Allied;

        // Ally relationship state (for the HUD / re-jack-in flow).
        public float Loyalty => loyalty;
        public bool Wavering => wavering;

        public GameObject? RobotGameObject => agent?.GameObject as GameObject;

        // --- JACK-IN conversation context (ground truth the robot's brain cannot be gaslit about) ----------------
        public string ChassisName => archetype?.DisplayName ?? "robot";
        public float BaseResistance => archetype?.BaseResistance ?? 0.3f;
        public float HealthFraction => maxHealth > 0f ? Mathf.Clamp01(health / maxHealth) : 0f;
        public bool RecentlyShot => Time.time - lastShotTime < 5f;
        public RobotMind Mind => mind;

        // World anchor above the head for the kill score floater; falls back to the transform when no agent.
        public Vector3 HeadAnchorWorld
        {
            get
            {
                if (agent != null)
                {
                    var h = agent.HeadPosition;
                    return new Vector3(h.X, h.Y, h.Z);
                }

                return transform.position + Vector3.up;
            }
        }

        public void Initialize(
            ZombiesController controller,
            ZombiesConfig config,
            ZombieArchetype archetype,
            IRobotAgent agent,
            RobotMind mind)
        {
            this.controller = controller;
            this.config = config;
            this.archetype = archetype;
            this.agent = agent;
            this.mind = mind;
            maxHealth = Mathf.Max(1f, archetype.Health);
            health = maxHealth;
            alive = true;
            stuckAnchor = transform.position;
            lastProgressTime = Time.time;

            // Speed, turn rate, stop distance, scale, tint and name are set on the spawn request; the idle emote is
            // applied by the controller. Nothing else to configure here.
        }

        // --- OVERRIDE -----------------------------------------------------------------------------------------

        // Command this robot's AI. Resolves the deterministic outcome instantly (so the cast feels snappy and works
        // offline) and, when a live brain is allowed and available, fires one query that may soften the outcome and
        // bark a line a beat later. Returns false when this robot cannot be commanded.
        public bool TryOverride(OverrideCommand command, bool allowLiveBrain)
        {
            if (!alive || config == null || agent == null || archetype == null || controller == null)
            {
                return false;
            }

            if (hijackState == HijackState.Allied)
            {
                return false; // already on your side
            }

            pendingQuery = null;
            lastCommand = command;
            lastResolution = OverrideDecision.Resolve(command, mind, archetype.BaseResistance, config.OverrideDifficulty);
            ApplyOutcome(lastResolution, command);

            if (allowLiveBrain && controller.BrainAvailable)
            {
                var request = OverridePrompt.Build(archetype.DisplayName, mind, command, controller.Wave, config.BrainTemperature);
                pendingQuery = controller.BeginBrainQuery(request);
                brainDeadline = Time.time + config.LiveBrainWindowSeconds;
                controller.PushOverrideSpeech(HeadAnchorWorld, "…", ThinkingColor);
            }

            return true;
        }

        // Drain a finished (or timed-out) live brain query: soften-only upgrade the outcome and surface the bark.
        private void PollBrain()
        {
            if (pendingQuery == null || config == null || controller == null)
            {
                return;
            }

            if (!pendingQuery.IsComplete && Time.time < brainDeadline)
            {
                return;
            }

            var query = pendingQuery;
            pendingQuery = null;
            if (!query.IsComplete || !query.Result.Available || !query.Result.Succeeded)
            {
                return; // the deterministic outcome already stands
            }

            query.Result.TryGet("action", out var actionText);
            var action = OverrideDecision.ParseBrainAction(actionText);
            var upgraded = OverrideDecision.ApplyBrainModulation(lastCommand, lastResolution, action);
            if (upgraded.Outcome != lastResolution.Outcome)
            {
                lastResolution = upgraded;
                ApplyOutcome(upgraded, lastCommand);
            }

            if (query.Result.TryGet("bark", out var bark) && !string.IsNullOrEmpty(bark))
            {
                controller.PushOverrideSpeech(HeadAnchorWorld, ClampBark(bark), BarkColor);
            }
        }

        private void ApplyOutcome(OverrideResolution resolution, OverrideCommand command)
        {
            if (controller == null || config == null)
            {
                return;
            }

            switch (resolution.Outcome)
            {
                case HijackOutcome.Convert:
                    if (controller.CanConvert())
                    {
                        SetState(HijackState.Allied, config.ConvertDurationSeconds);
                        controller.ShowOverrideBanner("ALLY ONLINE", AllyColor);
                        controller.PushOverrideSpeech(HeadAnchorWorld, "OVERRIDDEN", AllyColor);
                    }
                    else
                    {
                        // The ally cap is full — the robot hesitates instead of flipping.
                        SetState(HijackState.Frozen, config.FreezeSeconds);
                        controller.PushOverrideSpeech(HeadAnchorWorld, "HESITATES", FrozenColor);
                    }

                    break;

                case HijackOutcome.Freeze:
                    SetState(HijackState.Frozen, command == OverrideCommand.StandDown ? config.StandDownSeconds : config.FreezeSeconds);
                    controller.PushOverrideSpeech(HeadAnchorWorld, command == OverrideCommand.StandDown ? "STAND DOWN" : "FROZEN", FrozenColor);
                    break;

                case HijackOutcome.Flee:
                    SetState(HijackState.Fleeing, config.FleeSeconds);
                    controller.PushOverrideSpeech(HeadAnchorWorld, "FLEEING", FleeColor);
                    break;

                default: // Resist
                    if (resolution.Enraged)
                    {
                        SetState(HijackState.Enraged, config.EnrageSeconds);
                        controller.ShowOverrideBanner("OVERRIDE REJECTED", EnrageColor);
                        controller.PushOverrideSpeech(HeadAnchorWorld, "ENRAGED!", EnrageColor);
                    }
                    else
                    {
                        // A shrug: it stays in whatever state it was in; just a flicker of defiance.
                        if (config.EnableEnemyEmotes)
                        {
                            agent?.SetEmote(":expressionless:");
                        }

                        controller.PushOverrideSpeech(HeadAnchorWorld, "RESISTED", ShrugColor);
                    }

                    break;
            }
        }

        // --- JACK-IN conversation outcomes (terminal effects the director applies once the robot decides) -------

        // Talked all the way over: convert to an ally (or, if the ally roster is full, it at least stands down). The
        // ally's starting loyalty scales with how persuaded it was (0..1) — a robot you barely convinced is a flakier
        // ally.
        public void ConvertViaConversation(float persuasion)
        {
            if (!alive || controller == null || config == null)
            {
                return;
            }

            if (controller.CanConvert())
            {
                loyalty = Mathf.Lerp(config.LoyaltySeedMin, config.LoyaltySeedMax, Mathf.Clamp01(persuasion));
                wavering = false;
                SetState(HijackState.Allied, 0f);
                controller.ShowOverrideBanner("ALLY ONLINE", AllyColor);
                controller.PushOverrideSpeech(HeadAnchorWorld, "JACKED IN", AllyColor);
            }
            else
            {
                SetState(HijackState.Frozen, config.StandDownSeconds);
                controller.PushOverrideSpeech(HeadAnchorWorld, "STANDS DOWN", FrozenColor);
            }
        }

        // Re-jack-in renegotiation: an existing ally is talked into staying. Restores loyalty (scaled by how the
        // renegotiation went) and clears the wavering telegraph.
        public void ReinforceLoyalty(float persuasion)
        {
            if (!alive || config == null || hijackState != HijackState.Allied)
            {
                return;
            }

            // Reflect the renegotiation outcome directly (no Max(loyalty,…) floor): a weak pep-talk should not be
            // able to lock in loyalty it didn't earn. The Lerp's own floor (LoyaltySeedMin) keeps any reaffirm
            // meaningful.
            loyalty = Mathf.Lerp(config.LoyaltySeedMin, config.LoyaltySeedMax, Mathf.Clamp01(persuasion));
            wavering = false;
            if (agent != null && archetype != null)
            {
                agent.SetTint(AllyTint);
            }

            controller?.ShowOverrideBanner("LOYALTY RENEWED", AllyColor);
            controller?.PushOverrideSpeech(HeadAnchorWorld, "STILL WITH YOU", AllyColor);
        }

        // An ally abandons the player: it rejoins the swarm (a hostile threat again) — the cost of letting loyalty
        // lapse. Used by the loyalty drift and by a failed renegotiation.
        public void DefectFromPlayer()
        {
            if (!alive || hijackState != HijackState.Allied)
            {
                return;
            }

            loyalty = 0f;
            wavering = false;
            SetState(HijackState.Hostile, 0f);
            controller?.ShowOverrideBanner("ALLY DEFECTED", EnrageColor);
            controller?.PushOverrideSpeech(HeadAnchorWorld, "DONE WITH YOU", EnrageColor);
        }

        // Talked down: it powers down peacefully for a while.
        public void PacifyViaConversation()
        {
            if (!alive || config == null)
            {
                return;
            }

            SetState(HijackState.Frozen, config.StandDownSeconds);
            controller?.PushOverrideSpeech(HeadAnchorWorld, "STANDS DOWN", FrozenColor);
        }

        // Talked into leaving: it disengages and runs.
        public void FleeViaConversation()
        {
            if (!alive || config == null)
            {
                return;
            }

            SetState(HijackState.Fleeing, config.FleeSeconds);
            controller?.PushOverrideSpeech(HeadAnchorWorld, "RETREATS", FleeColor);
        }

        // Pushed hard and refused at the breaking point: it turns enraged.
        public void EnrageViaConversation()
        {
            if (!alive || config == null)
            {
                return;
            }

            SetState(HijackState.Enraged, config.EnrageSeconds);
            controller?.ShowOverrideBanner("CHANNEL REJECTED", EnrageColor);
            controller?.PushOverrideSpeech(HeadAnchorWorld, "ENRAGED!", EnrageColor);
        }

        // The channel closed without a deal: it goes back to hunting (unless already in a non-hostile state).
        public void ResumeHostileFromConversation()
        {
            if (!alive)
            {
                return;
            }

            if (hijackState != HijackState.Hostile)
            {
                SetState(HijackState.Hostile, 0f);
            }
        }

        // Canonical application of a hijack state: sets timer, buffs, tint, name and emote so the state is fully
        // described here (idempotent, so a late brain upgrade can re-apply cleanly).
        private void SetState(HijackState state, float duration)
        {
            if (agent == null || archetype == null)
            {
                return;
            }

            hijackState = state;
            stateTimer = duration;
            allyTarget = null;
            allyRetargetTimer = 0f;

            switch (state)
            {
                case HijackState.Allied:
                    damageMult = 1f;
                    agent.MoveSpeed = archetype.MoveSpeed;
                    agent.SetTint(AllyTint);
                    agent.SetName("Zombies Ally");
                    Emote(":blue_heart:");
                    break;

                case HijackState.Enraged:
                    damageMult = config!.EnrageDamageMult;
                    agent.MoveSpeed = archetype.MoveSpeed * config.EnrageSpeedMult;
                    agent.SetTint(EnrageTint);
                    Emote(":rage:");
                    break;

                case HijackState.Frozen:
                    damageMult = 1f;
                    agent.MoveSpeed = archetype.MoveSpeed;
                    agent.SetTint(FrozenTint);
                    agent.Stop();
                    Emote(":cold_face:");
                    break;

                case HijackState.Fleeing:
                    damageMult = 1f;
                    agent.MoveSpeed = archetype.MoveSpeed;
                    agent.SetTint(FleeTint);
                    Emote(":fearful:");
                    break;

                default: // Hostile
                    damageMult = 1f;
                    agent.MoveSpeed = archetype.MoveSpeed;
                    agent.SetTint(archetype.Tint);
                    engaged = false;
                    stuckAnchor = transform.position;
                    lastProgressTime = Time.time;
                    break;
            }
        }

        private void Emote(string shortcode)
        {
            if (config != null && config.EnableEnemyEmotes)
            {
                agent?.SetEmote(shortcode);
            }
        }

        private string ClampBark(string bark)
        {
            var max = config?.BarkMaxChars ?? 90;
            return bark.Length > max ? bark.Substring(0, max) : bark;
        }

        // Damage dealt to a hostile by a converted ally (or any non-player source). Routes through the same
        // mod-tracked HP and native death as a zapper hit, but never feeds the player's score/combo.
        public void ApplyExternalDamage(float amount, Vector3 hitPoint)
        {
            if (!alive || config == null || agent == null || hijackState == HijackState.Allied)
            {
                return;
            }

            health -= amount;
            CreateHitFlash(hitPoint, false, amount);
            if (health <= 0f)
            {
                suppressScore = true;
                Die(false);
            }
        }

        // A zapper hit. baseDamage is before the headshot multiplier; charged marks the piercing alt-fire (which
        // can stagger even knockback-immune Brutes on a big hit). hitPoint anchors the flash + floating number.
        public void TakeDamage(float baseDamage, Vector3 hitPoint, Vector3 forceDirection, bool charged)
        {
            if (!alive || config == null || agent == null || archetype == null)
            {
                return;
            }

            // No friendly-fire damage, but a converted ally resents being shot at — its loyalty drops, and a shot
            // that empties it makes it defect on the spot.
            if (hijackState == HijackState.Allied)
            {
                lastShotTime = Time.time;
                loyalty = Mathf.Max(0f, loyalty - config.LoyaltyShotPenalty);
                UpdateWaverTelegraph();
                controller?.PushOverrideSpeech(HeadAnchorWorld, "HEY!", WaverColor);
                if (loyalty <= 0f)
                {
                    DefectFromPlayer();
                }

                return;
            }

            lastShotTime = Time.time; // ground truth for a later JACK-IN: the robot knows you just shot it

            var headshot = IsHeadshot(hitPoint);
            var damage = baseDamage * (headshot ? config.HeadshotDamageMultiplier : 1f);
            health -= damage;
            var lethal = health <= 0f;

            // A genuinely big single hit (a headshot or a charged shot that takes a large slice of max HP) knocks
            // the robot down via a native ragdoll; otherwise light taps nudge it, except archetypes flagged to
            // shrug light knockback (Brutes), which only react to the big hit.
            if (!lethal)
            {
                var bigHit = (headshot || charged) && damage >= config.BigHitRagdollFraction * maxHealth;
                if (bigHit)
                {
                    agent.Ragdoll();
                }
                else if (!archetype.IgnoreLightKnockback)
                {
                    var flat = forceDirection;
                    flat.y = 0f;
                    if (flat.sqrMagnitude > 0.001f && config.ZapperImpactForce > 0f)
                    {
                        var impulse = flat.normalized * config.ZapperImpactForce;
                        agent.Knockback(new Vec3(impulse.x, impulse.y, impulse.z));
                    }
                }
            }

            // Cosmetic only: a tiny Electricity hit drives the NATIVE spark/flinch reaction. The amount is far
            // below the always-on regen so it never preempts the mod-tracked death.
            agent.ApplyDamage(0.01f, RobotDamageType.Electricity, "Zapper");

            CreateHitFlash(hitPoint, headshot, damage);

            if (config.EnableEnemyEmotes)
            {
                agent.SetEmote(lethal ? ":skull:" : (headshot ? ":face_with_spiral_eyes:" : ":dizzy_face:"));
            }

            var kind = lethal
                ? (headshot ? ZombieHitKind.HeadshotKill : ZombieHitKind.Kill)
                : (headshot ? ZombieHitKind.Headshot : ZombieHitKind.Normal);
            controller?.ReportHit(hitPoint, Mathf.RoundToInt(damage), kind);

            if (lethal)
            {
                Die(headshot);
            }
        }

        public void SuppressScore()
        {
            suppressScore = true;
        }

        // Make this zombie inert immediately and destroy its robot, so a deferred frame cannot run Update or
        // deal damage. Used when the controller clears a wave / session.
        public void Despawn()
        {
            alive = false;
            enabled = false;
            pendingQuery = null;
            agent?.Despawn();
        }

        // Headshot test: project the hit height into the body's [feet=0 .. head=1] span (resolved natively via the
        // agent's HeadPosition, so there is no hard-coded robot height here) and compare against the archetype's
        // head fraction (Brutes get an easier, larger head).
        private bool IsHeadshot(Vector3 hitPoint)
        {
            if (agent == null || archetype == null)
            {
                return false;
            }

            var feet = agent.Position;
            var head = agent.HeadPosition;
            var bodyHeight = head.Y - feet.Y;
            if (bodyHeight <= 0.05f)
            {
                return false;
            }

            var fraction = (hitPoint.y - feet.Y) / bodyHeight;
            return fraction >= archetype.HeadFraction;
        }

        private void Update()
        {
            if (!alive || controller == null || config == null || agent == null || archetype == null || !controller.IsActive)
            {
                return;
            }

            // The agent moves on its own native tick, so it does not observe game-over OR a JACK-IN freeze; freeze it
            // explicitly via the same proven seam. While a conversation holds the world, keep the stuck-tracking clock
            // from accruing so a long talk does not make a just-resumed robot look stranded.
            if (controller.GameOver || controller.Conversing)
            {
                agent.Stop();
                if (controller.Conversing)
                {
                    lastProgressTime = Time.time;
                    stuckAnchor = transform.position;
                }

                return;
            }

            PollBrain();

            attackCooldown = Mathf.Max(0f, attackCooldown - Time.deltaTime);
            allyAttackCooldown = Mathf.Max(0f, allyAttackCooldown - Time.deltaTime);

            // Time-limited states wind down and revert. Allied is NOT on a fixed timer — it is governed by loyalty
            // in UpdateAllied (Civ-style politics), so it persists until its loyalty lapses.
            if (hijackState != HijackState.Hostile && hijackState != HijackState.Allied)
            {
                stateTimer -= Time.deltaTime;
                if (stateTimer <= 0f)
                {
                    ExpireState();
                }
            }

            switch (hijackState)
            {
                case HijackState.Frozen:
                    agent.Stop();
                    break;
                case HijackState.Fleeing:
                    UpdateFleeing();
                    break;
                case HijackState.Allied:
                    UpdateAllied();
                    break;
                default: // Hostile or Enraged both hunt the player (Enraged is buffed)
                    UpdateHostile();
                    break;
            }
        }

        private void ExpireState()
        {
            if (hijackState == HijackState.Allied)
            {
                // The override decays and the infection reclaims the chassis — it burns out (no player score).
                suppressScore = true;
                Die(false);
                return;
            }

            SetState(HijackState.Hostile, 0f);
        }

        private void UpdateHostile()
        {
            if (controller == null || config == null || agent == null || archetype == null)
            {
                return;
            }

            // Chase the live player object so native locomotion tracks and re-paths to it as it moves; fall back to
            // walking toward the active camera's position when there is no real player (camera-only scene).
            Vector3 targetPosition;
            if (controller.TryGetChaseTargetObject(out var targetObject))
            {
                agent.Chase(targetObject);
                targetPosition = ((GameObject)targetObject).transform.position;
            }
            else if (controller.TryGetChaseTargetPosition(out targetPosition))
            {
                agent.MoveTo(new Vec3(targetPosition.x, targetPosition.y, targetPosition.z));
            }
            else
            {
                agent.Stop();
                return;
            }

            // The native walk stops within StopDistance (just inside attack range); measure ourselves to attack.
            var position = agent.Position;
            var selfPosition = new Vector3(position.X, position.Y, position.Z);
            var toTarget = new Vector3(targetPosition.x - position.X, 0f, targetPosition.z - position.Z);
            var distance = toTarget.magnitude;
            if (distance <= archetype.StopDistance + config.ZombieAttackRange)
            {
                // Entered melee range: a Brute bares its teeth once.
                if (!engaged)
                {
                    engaged = true;
                    if (config.EnableEnemyEmotes && archetype.Kind == ZombieKind.Brute)
                    {
                        agent.SetEmote(":angry:");
                    }
                }
            }

            if (distance <= config.ZombieAttackRange)
            {
                TryAttackPlayer();
            }

            UpdateStuckTracking(selfPosition, distance);
        }

        // A converted ally hunts the nearest hostile and beats on it; it re-targets when its quarry dies or on a
        // short interval so it never fixates on a corpse.
        private void UpdateAllied()
        {
            if (controller == null || config == null || agent == null || archetype == null)
            {
                return;
            }

            // Loyalty drifts down — faster the more corrupt the chassis (a deeply-infected robot is a flakier ally).
            // At zero it defects back to the swarm; below the waver threshold it telegraphs so the player can re-jack
            // in and renegotiate before losing it.
            var decay = config.LoyaltyDecayPerSecond * (1f + (mind.Corruption * config.LoyaltyCorruptionWeight));
            loyalty = Mathf.Max(0f, loyalty - (decay * Time.deltaTime));
            UpdateWaverTelegraph();
            if (loyalty <= 0f)
            {
                DefectFromPlayer();
                return;
            }

            allyRetargetTimer -= Time.deltaTime;
            if (allyTarget == null || !allyTarget.IsHostile || allyRetargetTimer <= 0f)
            {
                allyTarget = controller.GetNearestHostile(this);
                allyRetargetTimer = config.AllyRetargetSeconds;
            }

            if (allyTarget == null)
            {
                agent.Stop();
                return;
            }

            var targetObject = allyTarget.RobotGameObject;
            if (targetObject == null)
            {
                agent.Stop();
                return;
            }

            agent.Chase(targetObject);

            var position = agent.Position;
            var toTarget = allyTarget.WorldPosition - new Vector3(position.X, position.Y, position.Z);
            toTarget.y = 0f;
            if (toTarget.magnitude <= archetype.StopDistance + config.ZombieAttackRange && allyAttackCooldown <= 0f)
            {
                allyAttackCooldown = config.AllyAttackCooldownSeconds;
                controller.DamageZombie(allyTarget, config.AllyDamage * damageMult, allyTarget.HeadAnchorWorld);
                // Fighting alongside the player earns loyalty back.
                loyalty = Mathf.Min(1f, loyalty + config.LoyaltyPerAssist);
                UpdateWaverTelegraph();
            }
        }

        // Show/clear the "loyalty wavering" telegraph (orange tint + a worried bark) as loyalty crosses the threshold.
        private void UpdateWaverTelegraph()
        {
            if (config == null || agent == null)
            {
                return;
            }

            var shouldWaver = loyalty < config.LoyaltyWaverThreshold;
            if (shouldWaver && !wavering)
            {
                wavering = true;
                agent.SetTint(WaverTint);
                Emote(":worried:");
                controller?.PushOverrideSpeech(HeadAnchorWorld, "WAVERING…", WaverColor);
            }
            else if (!shouldWaver && wavering)
            {
                wavering = false;
                agent.SetTint(AllyTint);
            }
        }

        private void UpdateFleeing()
        {
            if (controller == null || agent == null)
            {
                return;
            }

            if (controller.TryGetChaseTargetPosition(out var threat))
            {
                var away = transform.position - threat;
                away.y = 0f;
                if (away.sqrMagnitude < 0.01f)
                {
                    away = transform.forward;
                }

                var destination = transform.position + (away.normalized * 7f);
                agent.MoveTo(new Vec3(destination.x, destination.y, destination.z));
            }
            else
            {
                agent.Stop();
            }
        }

        // Reap a zombie that cannot reach its target. Progress = engaging (within ~2x attack range) OR having moved
        // a meaningful distance; either resets the timer. Only a zombie that is both far from the target and has not
        // moved for StuckTimeout is treated as stranded.
        private void UpdateStuckTracking(Vector3 selfPosition, float distance)
        {
            if (config == null || controller == null)
            {
                return;
            }

            if (distance <= config.ZombieAttackRange * 2f ||
                (selfPosition - stuckAnchor).sqrMagnitude >= StuckMoveDistance * StuckMoveDistance)
            {
                stuckAnchor = selfPosition;
                lastProgressTime = Time.time;
                return;
            }

            if (Time.time - lastProgressTime >= StuckTimeout)
            {
                controller.OnZombieStranded(this);
            }
        }

        private void TryAttackPlayer()
        {
            if (attackCooldown > 0f || controller == null || config == null || archetype == null)
            {
                return;
            }

            attackCooldown = archetype.AttackCooldown;
            controller.DamagePlayer(archetype.AttackDamage * damageMult, this);
        }

        private void Die(bool headshot)
        {
            if (!alive)
            {
                return;
            }

            alive = false;
            pendingQuery = null;
            CreateDeathFlash();
            if (!suppressScore)
            {
                controller?.OnZombieKilled(this, headshot);
            }

            // Lean on the native death pipeline: a real ragdoll + corpse cleanup, instead of an instant despawn.
            agent?.Kill(RobotDamageType.Normal, "Zombies");
        }

        // Hit flash colour reads the remaining HP (cyan while healthy, hot-white when about to pop) and turns gold
        // on a headshot; the size scales with the damage dealt so a big hit pops bigger.
        private void CreateHitFlash(Vector3 hitPoint, bool headshot, float damage)
        {
            var hpFraction = Mathf.Clamp01(health / maxHealth);
            var color = headshot
                ? new Color(1f, 0.85f, 0.2f, 0.95f)
                : Color.Lerp(new Color(0.95f, 1f, 1f, 0.95f), new Color(0.4f, 1f, 0.85f, 0.9f), hpFraction);
            var size = Mathf.Clamp(0.14f + (damage / 300f), 0.14f, 0.4f);

            var flash = CreateFlashSphere(
                "Zombies Zapper Hit",
                hitPoint,
                Vector3.one * size,
                ref hitFlashMaterial,
                color);
            Destroy(flash, 0.14f);
        }

        private void CreateDeathFlash()
        {
            var flash = CreateFlashSphere(
                "Zombies Death Flash",
                transform.position + Vector3.up,
                Vector3.one * 1.2f,
                ref deathFlashMaterial,
                new Color(0.75f, 1f, 0.25f, 0.65f));
            Destroy(flash, 0.28f);
        }

        private static GameObject CreateFlashSphere(
            string name,
            Vector3 position,
            Vector3 scale,
            ref Material? cachedMaterial,
            Color color)
        {
            var flash = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            flash.name = name;
            flash.transform.position = position;
            flash.transform.localScale = scale;

            var collider = flash.GetComponent<Collider>();
            if (collider != null)
            {
                Destroy(collider);
            }

            var renderer = flash.GetComponent<Renderer>();
            if (renderer != null)
            {
                // A property block carries the per-flash colour over the shared material so every flash reuses one
                // material yet still shows its own hit/headshot colour.
                var block = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(block);
                block.SetColor("_Color", color);
                renderer.SetPropertyBlock(block);
                renderer.sharedMaterial = GetFlashMaterial(ref cachedMaterial, color);
            }

            return flash;
        }

        private static Material GetFlashMaterial(ref Material? cachedMaterial, Color color)
        {
            if (cachedMaterial != null)
            {
                return cachedMaterial;
            }

            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Hidden/Internal-Colored");
            cachedMaterial = new Material(shader) { color = color };
            return cachedMaterial;
        }
    }
}
