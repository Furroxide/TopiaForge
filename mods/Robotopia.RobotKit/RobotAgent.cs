using System.Threading;
using Robotopia.Mods;
using UnityEngine;

namespace Robotopia.RobotKit
{
    // A live standard-agent robot. Movement is the game's OWN locomotion: an intent (MoveTo/Chase) is carried out
    // by native WalkSession.Walk, which pathfinds, follows, repaths as a chased target moves, recovers from being
    // stuck, and drives the walk animation. This class is a thin driver around that native walk plus the visual
    // and combat overrides a mod typically needs; everything else (collision, grounding, slopes, separation) is
    // owned by the native LocomotionController.
    internal sealed class RobotAgent : IRobotAgent
    {
        private enum Intent
        {
            None,
            MoveTo,
            Chase
        }

        private const float ReachedEpsilon = 0.1f;
        // Hysteresis so a target hovering on the stop-distance boundary does not flip-flop between walk and idle.
        private const float ReachHysteresis = 0.5f;
        // Minimum seconds between native walk (re)starts, so an unreachable target does not pathfind every frame.
        // Mirrors the native WalkSession.REPATH_INTERVAL.
        private const float WalkRetryCooldown = 0.25f;
        // Nominal standing height (metres) of the native robot at scale 1, used only to estimate the head when no
        // renderers can be sampled (e.g. mid-teardown). Real heads come from the live rendered bounds.
        private const float NominalHeadHeight = 1.6f;

        private readonly string id;
        private readonly GameObject go;
        private readonly Transform transform;
        private readonly GameReflection.BrainStateSnapshot? nativeBrainSnapshot;
        private RobotBrainMode brainMode;
        private readonly IModLogger logger;

        private object? head;
        private bool headResolved;

        private Intent intent;
        private Vector3 moveToPosition;
        private GameObject? chaseTarget;

        private object? pendingWalkAwaiter;
        private CancellationTokenSource? walkCts;

        private bool isMoving;
        private bool hasReachedTarget;
        private bool despawned;
        private float nextWalkAttemptTime;

        private Renderer[]? renderers;
        private MaterialPropertyBlock? tintBlock;

        private float moveSpeed;
        private float turnSpeed;
        private float stopDistance;
        private RobotGait gait;
        private RobotInteractionOptions interaction;

        private Behaviour? nativeSpeakable;
        private bool nativeSpeakableResolved;
        private bool nativeSpeakableCaptured;
        private bool nativeSpeakableOriginalEnabled;
        private bool nativeSpeakDistanceCaptured;
        private float nativeSpeakDistanceOriginal;

        private Component? nativeAgentHead;
        private bool nativeAgentHeadResolved;
        private bool nativeAgentHeadTalkDisabled;
        private RobotInteractionBridge? interactionBridge;

        public RobotAgent(
            string id,
            GameObject go,
            RobotAgentSpawnRequest request,
            IModLogger logger,
            GameReflection.BrainStateSnapshot? nativeBrainSnapshot = null)
        {
            this.id = id;
            this.go = go;
            this.logger = logger;
            this.nativeBrainSnapshot = nativeBrainSnapshot;
            transform = go.transform;
            brainMode = request.BrainMode;
            moveSpeed = request.MoveSpeed;
            turnSpeed = request.TurnSpeed;
            stopDistance = request.StopDistance;
            gait = request.Gait;
            interaction = (request.Interaction ?? RobotInteractionOptions.NativeTalk()).Clone();
        }

        public string Id => id;
        public object GameObject => go;
        public bool IsAlive => !despawned && go != null && !GameReflection.HasKilledComponent(go);
        public Vec3 Position => go != null ? ToVec3(transform.position) : Vec3.Zero;
        public Vec3 HeadPosition => ResolveHeadPosition();
        public RobotBrainMode BrainMode => brainMode;

        // Runtime brain switch. To Dormant: suppress the native brain so mod intents take over (the reprogram
        // path). To Autonomous: clear mod intents and best-effort wake the native brain back up.
        public void SetBrainMode(RobotBrainMode mode)
        {
            if (mode == brainMode || !IsAlive)
            {
                return;
            }

            if (mode == RobotBrainMode.Autonomous)
            {
                Stop();
            }

            brainMode = mode;
            GameReflection.ApplyBrainMode(go, mode, nativeBrainSnapshot, logger);
        }
        public bool IsMoving => isMoving;
        public bool HasReachedTarget => hasReachedTarget;

        public float MoveSpeed
        {
            get => moveSpeed;
            set
            {
                moveSpeed = Mathf.Max(0f, value);
                ApplySpeeds();
            }
        }

        public float TurnSpeed
        {
            get => turnSpeed;
            set
            {
                turnSpeed = Mathf.Max(0f, value);
                ApplySpeeds();
            }
        }

        public float StopDistance
        {
            get => stopDistance;
            set => stopDistance = Mathf.Max(0f, value);
        }

        public RobotGait Gait
        {
            get => gait;
            set
            {
                gait = value;
                ApplySpeeds();
            }
        }

        public void MoveTo(Vec3 position)
        {
            var target = new Vector3(position.X, position.Y, position.Z);
            if (intent != Intent.MoveTo || (moveToPosition - target).sqrMagnitude > 0.04f)
            {
                CancelWalk();
            }

            intent = Intent.MoveTo;
            moveToPosition = target;
            chaseTarget = null;
            hasReachedTarget = false;
        }

        public void Chase(object targetGameObject)
        {
            var target = targetGameObject as GameObject;
            if (target == null)
            {
                return;
            }

            if (intent != Intent.Chase || !ReferenceEquals(chaseTarget, target))
            {
                CancelWalk();
                hasReachedTarget = false;
            }

            intent = Intent.Chase;
            chaseTarget = target;
        }

        public void Stop()
        {
            intent = Intent.None;
            chaseTarget = null;
            hasReachedTarget = false;
            CancelWalk();
        }

        public void SetTint(RobotColor color)
        {
            if (go == null)
            {
                return;
            }

            renderers ??= go.GetComponentsInChildren<Renderer>(true);
            tintBlock ??= new MaterialPropertyBlock();
            var c = new Color(color.R, color.G, color.B, color.A);
            foreach (var renderer in renderers)
            {
                if (renderer == null)
                {
                    continue;
                }

                renderer.GetPropertyBlock(tintBlock);
                // HDRP/Lit uses _BaseColor and _EmissiveColor. (A MaterialPropertyBlock cannot enable the
                // emission keyword, so the glow only shows if the source material already has emission on.)
                tintBlock.SetColor("_BaseColor", c);
                tintBlock.SetColor("_EmissiveColor", c * 0.35f);
                renderer.SetPropertyBlock(tintBlock);
            }
        }

        public void SetEmote(string emojiShortcode)
        {
            if (go != null)
            {
                GameReflection.StartEmote(go, emojiShortcode, logger);
            }
        }

        public void SetName(string name)
        {
            if (go != null && !string.IsNullOrEmpty(name))
            {
                go.name = name;
            }
        }

        public void SetScale(float scale)
        {
            if (go != null && scale > 0f)
            {
                transform.localScale = Vector3.one * scale;
            }
        }

        public void SetInteraction(RobotInteractionOptions options)
        {
            interaction = (options ?? RobotInteractionOptions.NativeTalk()).Clone();
            ApplyInteraction();
        }

        public bool ApplyDamage(float amount, RobotDamageType type, string source)
        {
            return IsAlive && GameReflection.ApplyDamage(go, amount, type, source, logger);
        }

        public void Kill(RobotDamageType type, string source)
        {
            if (!IsAlive)
            {
                return;
            }

            // Drive the native death pipeline (ragdoll + corpse cleanup) by dealing lethal damage to native
            // Health. If that did not actually kill it — no Health, immune to the damage type, or an
            // essential/respawning object — destroy it outright so the agent is removed regardless.
            GameReflection.ApplyDamage(go, 1_000_000f, type, source, logger);
            if (!GameReflection.HasKilledComponent(go))
            {
                Despawn();
            }
        }

        public void Ragdoll()
        {
            if (IsAlive)
            {
                LocomotionBridge.ForceRagdoll(go);
            }
        }

        public void Knockback(Vec3 impulse)
        {
            if (IsAlive)
            {
                LocomotionBridge.ReceiveForce(go, new Vector3(impulse.X, impulse.Y, impulse.Z));
            }
        }

        public void Despawn()
        {
            if (despawned)
            {
                return;
            }

            despawned = true;
            // Release the walk now: there will be no later Step to drain it, so cancel + dispose the CTS here.
            ReleaseWalk();
            ResetInteractionOverrides();
            if (go != null)
            {
                UnityEngine.Object.Destroy(go);
            }
        }

        // Apply spawn-request speed overrides once the robot is live (the LocomotionController exists by then).
        public void OnActivated()
        {
            headResolved = false;
            head = null;
            ApplySpeeds();
            ApplyInteraction();
        }

        // Per-frame driver (called by the service tick): keep a native walk running toward the current intent and
        // harvest its completion. The native LocomotionController owns the actual motion between calls.
        public void Step()
        {
            if (despawned || go == null)
            {
                return;
            }

            // The robot died natively (lava, another hazard, or a Kill): release our walk and let the corpse clean up.
            if (GameReflection.HasKilledComponent(go))
            {
                ReleaseWalk();
                isMoving = false;
                return;
            }

            if (brainMode == RobotBrainMode.Autonomous)
            {
                return; // the native brain drives this robot; mod intents are inert.
            }

            // Drain a finished (or cancelled) walk before considering a new one, then start a short cooldown so an
            // unreachable target (which fails almost instantly) does not trigger a fresh native pathfind every frame.
            if (pendingWalkAwaiter != null &&
                LocomotionBridge.PollWalk(pendingWalkAwaiter) == LocomotionBridge.WalkPoll.Done)
            {
                pendingWalkAwaiter = null;
                walkCts?.Dispose();
                walkCts = null;
                nextWalkAttemptTime = Time.time + WalkRetryCooldown;
            }

            if (intent == Intent.None)
            {
                isMoving = pendingWalkAwaiter != null;
                return;
            }

            if (intent == Intent.Chase && chaseTarget == null)
            {
                Stop();
                return;
            }

            var targetPosition = intent == Intent.Chase ? chaseTarget!.transform.position : moveToPosition;
            var position = transform.position;
            var flat = new Vector3(targetPosition.x - position.x, 0f, targetPosition.z - position.z);
            var distance = flat.magnitude;
            var reachThreshold = Mathf.Max(ReachedEpsilon, stopDistance);

            // Hysteresis: once arrived, stay "reached" until the target pulls clearly away again, so a target
            // hovering on the boundary does not flip-flop the robot between walking and idle.
            if (hasReachedTarget)
            {
                if (distance > reachThreshold + ReachHysteresis)
                {
                    hasReachedTarget = false;
                }
            }
            else if (distance <= reachThreshold)
            {
                hasReachedTarget = true;
            }

            if (hasReachedTarget)
            {
                // Let any in-flight walk finish on its own — the native walk stops within minStopDistance and
                // idles. Cancelling here would force a decelerate-to-zero and a cold re-acceleration next time.
                isMoving = pendingWalkAwaiter != null;
                return;
            }

            if (pendingWalkAwaiter != null)
            {
                isMoving = true; // already walking; for a Chase, the native walk repaths to the moving target
                return;
            }

            if (Time.time < nextWalkAttemptTime)
            {
                isMoving = false; // honour the post-completion cooldown before starting another walk
                return;
            }

            if (!headResolved)
            {
                head = LocomotionBridge.ResolveHead(go);
                headResolved = true;
            }

            // WalkSession requires the locomotion to be in control (AgentSync); while ragdolled/falling, wait.
            if (head == null || !LocomotionBridge.IsInControl(go))
            {
                isMoving = false;
                return;
            }

            walkCts = new CancellationTokenSource();
            var targetObject = intent == Intent.Chase ? chaseTarget : null;
            var awaiter = LocomotionBridge.BeginWalk(
                head, targetObject, targetPosition, stopDistance, ToBridgeGait(gait), walkCts.Token);
            if (awaiter != null)
            {
                pendingWalkAwaiter = awaiter;
                isMoving = true;
            }
            else
            {
                walkCts.Dispose();
                walkCts = null;
                isMoving = false;
                nextWalkAttemptTime = Time.time + WalkRetryCooldown; // could not start; back off before retrying
            }
        }

        private void CancelWalk()
        {
            // Signal cancellation but keep the awaiter so the next Step drains it (GetResult once) — avoids
            // orphaning a pooled UniTask source. A new walk is only started once the old one has drained.
            if (walkCts is { IsCancellationRequested: false })
            {
                try
                {
                    walkCts.Cancel();
                }
                catch (System.Exception ex)
                {
                    logger.Debug("RobotKit native walk cancellation failed: " + ex.Message);
                }
            }
        }

        // Tear-down release used on the death/despawn paths, where there is no later Step to drain the walk:
        // best-effort drain the awaiter (returns a completed pooled UniTask source) and dispose the CTS.
        private void ReleaseWalk()
        {
            if (pendingWalkAwaiter != null)
            {
                LocomotionBridge.PollWalk(pendingWalkAwaiter);
                pendingWalkAwaiter = null;
            }

            if (walkCts != null)
            {
                try
                {
                    walkCts.Cancel();
                }
                catch (System.Exception ex)
                {
                    logger.Debug("RobotKit native walk cleanup cancellation failed: " + ex.Message);
                }

                walkCts.Dispose();
                walkCts = null;
            }
        }

        private void ApplySpeeds()
        {
            if (go != null)
            {
                LocomotionBridge.ApplySpeeds(go, ToBridgeGait(gait), moveSpeed, turnSpeed);
            }
        }

        private void ApplyInteraction()
        {
            if (go == null)
            {
                return;
            }

            RemoveCustomInteractionBridge();
            var custom = interaction.CustomInteraction;
            if (custom != null)
            {
                ApplyNativeTalk(RobotNativeTalkMode.Disabled, 0f);
                InstallCustomInteractionBridge(custom);
                return;
            }

            ApplyNativeTalk(interaction.NativeTalkMode, interaction.NativeTalkDistance);
        }

        private void ApplyNativeTalk(RobotNativeTalkMode mode, float nativeTalkDistance)
        {
            var speakable = ResolveNativeSpeakable();
            var disabled = mode == RobotNativeTalkMode.Disabled;
            if (speakable != null)
            {
                CaptureNativeSpeakable(speakable);
                if (disabled)
                {
                    speakable.enabled = false;
                }
                else
                {
                    ApplyNativeSpeakDistance(speakable, nativeTalkDistance);
                    speakable.enabled = nativeSpeakableOriginalEnabled;
                }
            }

            if (disabled)
            {
                PushDisableNativeTalk();
            }
            else
            {
                PopDisableNativeTalk();
            }
        }

        private void CaptureNativeSpeakable(Behaviour speakable)
        {
            if (!nativeSpeakableCaptured)
            {
                nativeSpeakableOriginalEnabled = speakable.enabled;
                nativeSpeakableCaptured = true;
            }

            if (!nativeSpeakDistanceCaptured &&
                GameReflection.GetFieldValue(speakable, "maxSpeakDistance") is float distance)
            {
                nativeSpeakDistanceOriginal = distance;
                nativeSpeakDistanceCaptured = true;
            }
        }

        private void ApplyNativeSpeakDistance(Behaviour speakable, float distance)
        {
            if (distance > 0f)
            {
                GameReflection.SetFieldValue(speakable, "maxSpeakDistance", distance);
                return;
            }

            if (nativeSpeakDistanceCaptured)
            {
                GameReflection.SetFieldValue(speakable, "maxSpeakDistance", nativeSpeakDistanceOriginal);
            }
        }

        private void InstallCustomInteractionBridge(RobotCustomInteraction custom)
        {
            var interactable = GameReflection.FindComponent(go, "Interactable");
            if (interactable == null)
            {
                logger.Debug("RobotKit custom interaction could not install: native Interactable not found.");
                return;
            }

            interactionBridge = interactable.gameObject.AddComponent<RobotInteractionBridge>();
            interactionBridge.Configure(this, custom, logger);
        }

        private void RemoveCustomInteractionBridge()
        {
            if (interactionBridge == null)
            {
                return;
            }

            interactionBridge.enabled = false;
            UnityEngine.Object.Destroy(interactionBridge);
            interactionBridge = null;
        }

        private void ResetInteractionOverrides()
        {
            RemoveCustomInteractionBridge();
            PopDisableNativeTalk();

            var speakable = ResolveNativeSpeakable();
            if (speakable == null || !nativeSpeakableCaptured)
            {
                return;
            }

            if (nativeSpeakDistanceCaptured)
            {
                GameReflection.SetFieldValue(speakable, "maxSpeakDistance", nativeSpeakDistanceOriginal);
            }

            speakable.enabled = nativeSpeakableOriginalEnabled;
        }

        private Behaviour? ResolveNativeSpeakable()
        {
            if (!nativeSpeakableResolved)
            {
                nativeSpeakable = GameReflection.FindComponent(go, "Speakable") as Behaviour;
                nativeSpeakableResolved = true;
            }

            return nativeSpeakable;
        }

        private Component? ResolveNativeAgentHead()
        {
            if (!nativeAgentHeadResolved)
            {
                nativeAgentHead = GameReflection.FindComponent(go, "AgentHead");
                nativeAgentHeadResolved = true;
            }

            return nativeAgentHead;
        }

        private void PushDisableNativeTalk()
        {
            if (nativeAgentHeadTalkDisabled)
            {
                return;
            }

            var agentHead = ResolveNativeAgentHead();
            if (agentHead != null && GameReflection.Invoke(agentHead, "PushDisableTalkTo", logger))
            {
                nativeAgentHeadTalkDisabled = true;
            }
        }

        private void PopDisableNativeTalk()
        {
            if (!nativeAgentHeadTalkDisabled)
            {
                return;
            }

            var agentHead = ResolveNativeAgentHead();
            if (agentHead != null)
            {
                GameReflection.Invoke(agentHead, "PopDisableTalkTo", logger);
            }

            nativeAgentHeadTalkDisabled = false;
        }

        private static LocomotionBridge.Gait ToBridgeGait(RobotGait gait)
        {
            return gait switch
            {
                RobotGait.Walk => LocomotionBridge.Gait.Walk,
                RobotGait.Sprint => LocomotionBridge.Gait.Sprint,
                _ => LocomotionBridge.Gait.Run
            };
        }

        // Top-centre of the robot's live rendered bounds (scale-aware), pulled down slightly from the very top so
        // it sits on the head rather than an antenna tip — a stable headshot reference and world anchor for combat
        // HUD. Reuses the renderer cache populated by SetTint; falls back to the feet position plus the nominal
        // height (scaled) when no renderers resolve, e.g. mid-teardown.
        private Vec3 ResolveHeadPosition()
        {
            if (go == null)
            {
                return Position;
            }

            renderers ??= go.GetComponentsInChildren<Renderer>(true);
            var hasBounds = false;
            var bounds = default(Bounds);
            foreach (var renderer in renderers)
            {
                if (renderer == null)
                {
                    continue;
                }

                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            if (!hasBounds)
            {
                var basePos = transform.position;
                return new Vec3(basePos.x, basePos.y + (NominalHeadHeight * transform.localScale.y), basePos.z);
            }

            var head = bounds.center;
            head.y = bounds.max.y - (bounds.size.y * 0.12f);
            return ToVec3(head);
        }

        private static Vec3 ToVec3(Vector3 value)
        {
            return new Vec3(value.x, value.y, value.z);
        }
    }
}
