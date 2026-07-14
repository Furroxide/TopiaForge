using System;
using System.Collections.Generic;
using Robotopia.Mods;
using Robotopia.Mods.UnityUi;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Robotopia.Zombies
{
    internal sealed class ZombiesController : IDisposable
    {
        private const float MaxPrefabRetryInterval = 30f;

        // If the reachability search cannot place a zombie this many times in a row (e.g. the player is somewhere
        // with no reachable ground nearby), drop that spawn so the wave still progresses instead of hanging — we
        // never fall back to an unreachable placement.
        private const int MaxConsecutiveSpawnFailures = 10;

        private readonly IModContext context;
        private readonly ZombiesConfig config;
        private readonly IRobotAgentService? robots;
        private readonly IRobotBrainQueryService? brains;
        private readonly IRobotConversationService? conversations;
        private readonly ITimeControlService? timeControl;
        private readonly IPlayerDialogueInputService? dialogueInput;
        private readonly OverrideTuning tuning;
        private readonly ConversationTuning convTuning;
        private readonly List<ZombieEnemyController> zombies = new List<ZombieEnemyController>();
        private readonly System.Random random = new System.Random();
        private readonly ZombieRoster roster;

        private GameObject? root;
        private ZombiesHudBehaviour? hud;
        private ZapperController? zapper;
        private OverrideController? overrideController;
        private IRobotBrainQuery? pendingBroadcastQuery;
        private float broadcastQueryDeadline;
        private IReachableSpawn? pendingPlacement;
        private float playerIntegrity;
        private float stateTimer;
        private float spawnTimer;
        private float prefabRetryTimer;
        private float prefabRetryInterval;
        private float stallTimer;
        private int wave;
        private int remainingToSpawn;
        private int score;
        private int consecutiveSpawnFailures;
        private int comboCount;
        private int comboMultiplier = 1;
        private float lastComboKillTime;
        private ZombieKind packKind;
        private int packRemaining;
        private ZombiesState state;
        private bool disposed;
        private bool missingRobotsLogged;
        private bool sceneHandlerRegistered;

        // --- JACK-IN conversation state (the freeze + the live talk) ------------------------------------------
        private bool conversing;
        private ITimeLease? conversationFreeze; // Chronos world-freeze (native robots + physics) while the channel is open
        private ITimeLease? superhotDriverLease; // Chronos "time moves when you move" driver (Superhot mode)
        private ITimeLease? superhotExemptLease;  // keeps the player full-speed while the world crawls
        private ZombieEnemyController? conversingTarget;
        private IRobotConversation? activeConversation;
        private TextInputBuffer conversationInput = new TextInputBuffer(160);
        private ConversationInputMode inputMode = ConversationInputMode.Text;
        private IVoiceCapture? voiceCapture;
        private float conversationDeadline;        // Time.unscaledTime — survives the freeze
        private float disposition;                 // 0..1 persuasion meter (engine-owned CONVERT gate)
        private float convertThresholdForTarget;
        private int processedTurns;
        private bool angeredThisConversation;
        private string lastRobotReply = string.Empty;
        private string conversationStatus = string.Empty;
        private string lastPlayerLine = string.Empty;
        private float pressure;                    // 0..1 "horde massed while you talked" tax

        // --- SHOP: between-rounds requisitions (holds the prep countdown while open) --------------------------
        private readonly ShopWallet wallet = new ShopWallet();
        private readonly ZombiesRunUpgrades upgrades = new ZombiesRunUpgrades();
        private readonly IReadOnlyList<ShopItem> shopCatalog;
        private readonly KeyCode shopKey;
        private bool shopping;
        private ITimeLease? shopFreeze;            // Chronos world-freeze while browsing (same discipline as JACK-IN)
        private int runSerial;                     // bumps per run so the HUD can reset per-run purchase counts

        // Set by the owning mod so a self-terminating path (Return to Menu) tears the session down through the
        // owner (which nulls its reference) rather than the controller disposing itself behind the owner's back.
        public System.Action? SessionEnded;

        public ZombiesController(IModContext context, ZombiesConfig config)
        {
            this.context = context;
            this.config = config;
            roster = new ZombieRoster(config);
            robots = context.GetService<IRobotAgentService>();
            brains = context.GetService<IRobotBrainQueryService>();
            conversations = context.GetService<IRobotConversationService>();
            dialogueInput = context.GetService<IPlayerDialogueInputService>();
            timeControl = context.GetService<ITimeControlService>();
            tuning = new OverrideTuning(
                config.SuggestibilityMin,
                config.SuggestibilityMax,
                config.LoyaltyMin,
                config.LoyaltyMax,
                config.CorruptionBase,
                config.CorruptionPerWave,
                config.BiasAmplitude,
                config.OverrideDifficulty);
            convTuning = new ConversationTuning(
                config.ConvSeedBias,
                config.ConvertThreshold,
                config.ConvertResistanceWeight,
                config.ConvertNudge,
                config.StandDownNudge,
                config.FleeNudge,
                config.RefuseNudge,
                config.EnrageDispositionFloor);
            shopCatalog = ZombiesShopCatalog.Build(config);
            shopKey = ParseKey(config.ShopKey, KeyCode.B);
        }

        public int Wave => wave;
        public int Score => score;
        public int RemainingToSpawn => remainingToSpawn;
        public int ComboCount => comboCount;
        public int ComboMultiplier => comboMultiplier;

        // 0..1 progress toward the next combo tier (full once at the max multiplier); drives the combo meter fill.
        public float ComboTierProgress
        {
            get
            {
                if (comboCount <= 0)
                {
                    return 0f;
                }

                if (comboMultiplier >= config.ComboMaxMultiplier)
                {
                    return 1f;
                }

                if (config.ComboKillsPerTier <= 0)
                {
                    return 0f;
                }

                // Fill 1/N..N/N across a tier's kills so the bar reaches full ON the level-up kill, then resets —
                // rather than dropping to 0 at the tier boundary (comboCount % N == 0).
                var into = ((comboCount - 1) % config.ComboKillsPerTier) + 1;
                return (float)into / config.ComboKillsPerTier;
            }
        }

        // 0..1 time remaining in the combo window before it lapses; drives the draining decay bar.
        public float ComboWindowRemaining
        {
            get
            {
                if (comboCount <= 0 || EffectiveComboWindowSeconds <= 0f)
                {
                    return 0f;
                }

                var elapsed = Time.time - lastComboKillTime;
                return Mathf.Clamp01(1f - (elapsed / EffectiveComboWindowSeconds));
            }
        }

        // Live count of alive zombies per archetype, for the HUD's "G/S/B/R" mix tally.
        public void GetArchetypeTally(out int grunts, out int sprinters, out int brutes, out int runts)
        {
            grunts = 0;
            sprinters = 0;
            brutes = 0;
            runts = 0;
            for (var index = 0; index < zombies.Count; index++)
            {
                var zombie = zombies[index];
                if (zombie == null || !zombie.IsHostile)
                {
                    continue; // converted allies are not part of the threat mix
                }

                switch (zombie.Kind)
                {
                    case ZombieKind.Sprinter:
                        sprinters++;
                        break;
                    case ZombieKind.Brute:
                        brutes++;
                        break;
                    case ZombieKind.Runt:
                        runts++;
                        break;
                    default:
                        grunts++;
                        break;
                }
            }
        }

        public int AliveCount
        {
            get
            {
                var count = 0;
                for (var index = 0; index < zombies.Count; index++)
                {
                    var zombie = zombies[index];
                    if (zombie != null && zombie.IsAlive)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        // Alive infected robots that still threaten the player (everything except converted allies). Wave-clear and
        // the spawn watchdog gate on this, so converting robots never strands a wave.
        public int HostileCount
        {
            get
            {
                var count = 0;
                for (var index = 0; index < zombies.Count; index++)
                {
                    var zombie = zombies[index];
                    if (zombie != null && zombie.IsHostile)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        // Converted allies currently fighting for the player.
        public int ConvertedAllyCount
        {
            get
            {
                var count = 0;
                for (var index = 0; index < zombies.Count; index++)
                {
                    var zombie = zombies[index];
                    if (zombie != null && zombie.IsAlly)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        // Allies whose loyalty is wavering (re-jack-in to renegotiate before they defect).
        public int WaveringAllyCount
        {
            get
            {
                var count = 0;
                for (var index = 0; index < zombies.Count; index++)
                {
                    var zombie = zombies[index];
                    if (zombie != null && zombie.IsAlly && zombie.Wavering)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        // --- OVERRIDE service surface (used by ZombieEnemyController / OverrideController) ----------------------
        // A live brain query is possible (the player opted in, the RobotKit service is registered, and a token is
        // resolvable). Robots check this before spending a call.
        public bool BrainAvailable => config.UseLiveBrain && brains != null && brains.IsAvailable;

        public IRobotBrainQuery? BeginBrainQuery(BrainQueryRequest request)
        {
            return brains?.BeginQuery(request);
        }

        public bool CanConvert()
        {
            return ConvertedAllyCount < config.MaxConvertedAllies;
        }

        // The nearest hostile to a converted ally, so it can hunt the swarm; null when none remain.
        public ZombieEnemyController? GetNearestHostile(ZombieEnemyController self)
        {
            ZombieEnemyController? best = null;
            var bestSq = float.MaxValue;
            var origin = self.WorldPosition;
            for (var index = 0; index < zombies.Count; index++)
            {
                var zombie = zombies[index];
                if (zombie == null || zombie == self || !zombie.IsHostile)
                {
                    continue;
                }

                var sq = (zombie.WorldPosition - origin).sqrMagnitude;
                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = zombie;
                }
            }

            return best;
        }

        // An ally hit on a hostile robot (routes through mod-tracked HP / native death, never the player's score).
        public void DamageZombie(ZombieEnemyController target, float amount, Vector3 hitPoint)
        {
            if (disposed || target == null)
            {
                return;
            }

            target.ApplyExternalDamage(amount, hitPoint);
        }

        // Broadcast one command to every overridable robot within radius of the player. Each resolves deterministically
        // (no per-robot brain call); a single amortized brain query gives the whole swarm one spoken line.
        public void BroadcastCommand(OverrideCommand command, float radius)
        {
            if (disposed || state == ZombiesState.GameOver)
            {
                return;
            }

            if (!TryGetChaseTargetPosition(out var center))
            {
                return;
            }

            var radiusSq = radius * radius;
            var count = 0;
            foreach (var zombie in zombies.ToArray())
            {
                if (zombie != null && zombie.IsOverridable && (zombie.WorldPosition - center).sqrMagnitude <= radiusSq)
                {
                    if (zombie.TryOverride(command, false))
                    {
                        count++;
                    }
                }
            }

            if (count <= 0)
            {
                hud?.ShowBanner("BROADCAST · no targets", QwTone.Muted);
                return;
            }

            hud?.ShowBanner("BROADCAST · " + OverrideLabel(command), QwTone.Primary);

            if (BrainAvailable)
            {
                var request = OverridePrompt.BuildBroadcast(command, wave, count, config.BrainTemperature);
                pendingBroadcastQuery = brains!.BeginQuery(request);
                broadcastQueryDeadline = Time.time + config.LiveBrainWindowSeconds + 1f;
            }
        }

        public void PushOverrideSpeech(Vector3 world, string text, QwTone tone)
        {
            hud?.PushSpeech(world, text, tone);
        }

        public void ShowOverrideBanner(string text, QwTone tone)
        {
            hud?.ShowBanner(text, tone);
        }

        public void ShowOverrideHint(string text)
        {
            hud?.ShowBanner(text, QwTone.Muted);
        }

        // HUD pass-throughs for the override charge/cooldown/command display.
        public bool OverrideHudEnabled => overrideController != null && overrideController.Enabled;
        public int OverrideCharges => overrideController?.Charges ?? 0;
        public int OverrideMaxCharges => overrideController?.MaxCharges ?? 0;
        public float OverrideRegenFraction => overrideController?.RegenFraction ?? 1f;
        public float BroadcastReadyFraction => overrideController?.BroadcastReadyFraction ?? 1f;
        public bool OverrideAimingHijackable => overrideController?.AimingHijackable ?? false;
        public OverrideCommand SelectedOverrideCommand => overrideController?.Selected ?? OverrideCommand.JoinMe;

        // --- JACK IN: free-form LLM conversation that freezes the horde ----------------------------------------

        // The world is held by an open conversation channel. Every infected robot's Update short-circuits on this
        // (the same seam as GameOver), and the wave machine/spawning pause.
        public bool Conversing => conversing;

        // The talk verb can run: enabled in config, the conversation service is registered, and a backend token is
        // resolvable. When false the JACK-IN key is inert and the player relies on the zapper + Q broadcast.
        public bool ConversationAvailable =>
            !disposed && config.ConversationEnabled && conversations != null && conversations.IsAvailable;

        // HUD-facing conversation state (read by ZombiesHudBehaviour for the chat panel).
        public string ConversationTargetName => conversingTarget?.ChassisName ?? "robot";
        public string ConversationReply => lastRobotReply;
        public string ConversationStatus => conversationStatus;
        public string ConversationPlayerEcho => conversing && inputMode == ConversationInputMode.Text ? conversationInput.Text : lastPlayerLine;
        public bool ConversationThinking => activeConversation != null && activeConversation.IsThinking;
        public bool ConversationVoiceMode => inputMode == ConversationInputMode.Voice;
        public bool ConversationVoiceRecording => voiceCapture != null && voiceCapture.IsRecording;
        public bool ConversationVoiceAvailable => config.UseVoiceInput && dialogueInput != null && dialogueInput.IsVoiceAvailable;
        public float ConversationDisposition => disposition;
        public float ConversationConvertThreshold => convertThresholdForTarget;
        public int ConversationTurn => activeConversation?.TurnCount ?? 0;
        public int ConversationMaxTurns => config.ConversationMaxTurns;
        public float ConversationWindowFraction
        {
            get
            {
                if (!conversing || config.ConversationWindowSeconds <= 0f)
                {
                    return 0f;
                }

                return Mathf.Clamp01((conversationDeadline - Time.unscaledTime) / config.ConversationWindowSeconds);
            }
        }

        // 0..1 background horde pressure built up while frozen (denser spawns on resume).
        public float Pressure => pressure;

        // --- SHOP: between-rounds requisitions -------------------------------------------------------------------

        // The shop window is open: the wave machine (incl. the prep stateTimer), spawning, combo decay and
        // broadcast polling are all held via the Update early-return — the same seam as Conversing.
        public bool Shopping => shopping;

        public int Credits => wallet.Balance;
        public ShopWallet Wallet => wallet;
        public ZombiesRunUpgrades Upgrades => upgrades;
        public IReadOnlyList<ShopItem> ShopCatalog => shopCatalog;

        // Bumps once per fresh run; the HUD's shop modal resets its per-run purchase counts when it moves.
        public int RunSerial => runSerial;

        // The shop can open: enabled, and the round is in a prep phase (never mid-wave, never over a channel).
        public bool ShopAvailable =>
            !disposed && config.ShopEnabled && !conversing
            && (state == ZombiesState.Starting || state == ZombiesState.InterWave);

        // Opens the requisitions window: hold the world through Chronos and suspend the player, exactly like
        // JACK-IN. The prep countdown is held by the Update gate regardless of whether Chronos is present.
        public void OpenShop()
        {
            if (shopping || !ShopAvailable)
            {
                return;
            }

            shopping = true;
            shopFreeze = timeControl?.Freeze("zombies-shop");
            robots?.SetPlayerControlsEnabled(false);
            hud?.ShowBanner("REQUISITIONS OPEN", QwTone.Warning);
        }

        // The single teardown path (button, ESC, restart, dispose, scene swap). Idempotent; releases the
        // freeze lease and hands control back unless game-over owns the cursor (mirrors EndConversation).
        private void CloseShop(bool aborted)
        {
            if (!shopping)
            {
                return;
            }

            shopFreeze?.Release();
            shopFreeze = null;
            shopping = false;

            if (state != ZombiesState.GameOver)
            {
                robots?.SetPlayerControlsEnabled(true);
                RestorePlayCursor();
            }

            if (!aborted)
            {
                hud?.ShowBanner("REQUISITIONS CLOSED", QwTone.Muted);
            }
        }

        // The HUD wires the shop window's Closed event here, so ESC and the X button resume identically.
        public void CloseShopFromHud()
        {
            CloseShop(aborted: false);
        }

        // Purchase gate for items that can be redundant; the shop pane consults this per frame (cards dim)
        // and ShopTransactions re-checks it at click time.
        public bool CanPurchaseShopItem(ShopItem item)
        {
            switch (item.Id)
            {
                case ZombiesShopCatalog.RepairId:
                    return playerIntegrity < MaxPlayerIntegrity - 0.001f;
                case ZombiesShopCatalog.UplinkSurgeId:
                    return OverrideCharges < OverrideMaxCharges;
                default:
                    return true;
            }
        }

        // Applies a purchased item. Upgrades land in the per-run modifier state (never the shared config);
        // every consumer reads them live, so effects apply from the very next shot/regen/decay check.
        public void ApplyShopItem(ShopItem item)
        {
            if (disposed)
            {
                return;
            }

            switch (item.Id)
            {
                case ZombiesShopCatalog.RepairId:
                    playerIntegrity = Mathf.Min(MaxPlayerIntegrity, playerIntegrity + config.ShopRepairAmount);
                    break;
                case ZombiesShopCatalog.PlatingId:
                    upgrades.BonusMaxIntegrity += config.ShopPlatingBonus;
                    playerIntegrity = MaxPlayerIntegrity; // new plating ships fully charged
                    break;
                case ZombiesShopCatalog.ZapperGainId:
                    upgrades.ZapperDamageMult *= config.ShopZapperGainMult;
                    break;
                case ZombiesShopCatalog.RapidCoilsId:
                    upgrades.ZapperCooldownMult *= config.ShopRapidCoilsMult;
                    break;
                case ZombiesShopCatalog.UplinkCellId:
                    upgrades.BonusUplinkCharges++;
                    overrideController?.RefillCharges(); // the fresh cell arrives charged
                    break;
                case ZombiesShopCatalog.UplinkSurgeId:
                    overrideController?.RefillCharges();
                    break;
                case ZombiesShopCatalog.ComboStabilizerId:
                    upgrades.ComboWindowBonusSeconds += config.ShopComboWindowBonusSeconds;
                    break;
                default:
                    context.Logger.Warn("Zombies shop: unknown item '" + item.Id + "' purchased; no effect applied.");
                    break;
            }

            context.Logger.Info("Zombies shop: bought " + item.Name + " for " + item.Price + " credits (" + wallet.Balance + " left).");
        }

        // While the shop holds the round, only defensive teardown runs — the window itself is event-driven.
        private void UpdateShopping()
        {
            if (state == ZombiesState.GameOver)
            {
                CloseShop(aborted: true);
            }
        }

        // Prep phases poll the shop key. The cursor gate keeps a free-cursor surface (pause menu, another
        // window) from eating a stray B as an open command — the same guard OverrideController uses.
        private void PollShopKey()
        {
            if (config.ShopEnabled && Cursor.lockState == CursorLockMode.Locked && Input.GetKeyDown(shopKey))
            {
                OpenShop();
            }
        }

        // Open a channel to one infected robot, freezing the horde. Returns false (with a hint) when it can't start.
        // The uplink charge is spent by the caller (OverrideController) only on a true return.
        public bool BeginConversation(ZombieEnemyController target)
        {
            if (disposed || conversing || shopping || state == ZombiesState.GameOver || target == null || !target.IsOverridable)
            {
                return false;
            }

            if (!ConversationAvailable || conversations == null)
            {
                ShowOverrideHint("JACK-IN: channel offline");
                return false;
            }

            var isAlly = target.IsAlly;
            var request = ConversationDirector.BuildRequest(
                target.ChassisName,
                target.Mind,
                wave,
                target.HealthFraction,
                target.RecentlyShot,
                isAlly,
                target.Loyalty,
                config.BrainTemperature,
                config.ConversationMaxTurns);

            activeConversation = conversations.BeginConversation(request);
            conversingTarget = target;
            conversing = true;
            processedTurns = 0;
            angeredThisConversation = false;
            lastRobotReply = string.Empty;
            lastPlayerLine = string.Empty;
            conversationStatus = isAlly ? "Ally channel open — keep them with you." : "Channel open — say something.";
            // A first conversion seeds from the robot's psychology; a renegotiation seeds from the ally's loyalty.
            disposition = isAlly
                ? Mathf.Clamp01(target.Loyalty)
                : ConversationDirector.SeedDisposition(target.Mind, convTuning);
            convertThresholdForTarget = ConversationDirector.ConvertThreshold(target.BaseResistance, convTuning);
            conversationDeadline = Time.unscaledTime + config.ConversationWindowSeconds;
            conversationInput.Clear();
            inputMode = ConversationVoiceAvailable && config.UseVoiceInput ? ConversationInputMode.Voice : ConversationInputMode.Text;

            // Freeze the WHOLE world through Chronos — native robots, physics and timers all halt at timeScale 0
            // (the per-entity guard below still gates mod attack-logic and is the fallback when Chronos is absent).
            conversationFreeze = timeControl?.Freeze("zombies-jackin");

            // Suspend the player and free the cursor for the modal chat panel (mirrors the game-over flow).
            robots?.SetPlayerControlsEnabled(false);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            hud?.ShowBanner("CHANNEL OPEN · " + target.ChassisName, QwTone.Accent);
            return true;
        }

        private void UpdateConversation(float deltaTime)
        {
            // The target vanished (destroyed/despawned out from under us) — tear down cleanly.
            if (conversingTarget == null || !conversingTarget.IsAlive)
            {
                EndConversation(ConversationExit.TargetLost);
                return;
            }

            AccruePressure(deltaTime);

            // The FPS controller may try to re-lock the cursor each frame; keep it free for the panel.
            if (Cursor.lockState != CursorLockMode.None)
            {
                Cursor.lockState = CursorLockMode.None;
            }

            if (!Cursor.visible)
            {
                Cursor.visible = true;
            }

            // Drain a completed turn exactly once.
            if (activeConversation != null && activeConversation.TurnReady && activeConversation.TurnCount > processedTurns)
            {
                processedTurns = activeConversation.TurnCount;
                HandleTurn(activeConversation.LastReply, ConversationDirector.Parse(activeConversation.LastDecision));
                if (!conversing)
                {
                    return; // a terminal outcome ended the conversation
                }

                conversationDeadline = ConversationDirector.RefillDeadline(
                    Time.unscaledTime,
                    conversationDeadline,
                    config.ConversationWindowSeconds,
                    config.ConversationTurnRefillSeconds);
            }

            ReadConversationInput();
            if (!conversing)
            {
                return;
            }

            // Out of turns with no deal struck → resolve and resume.
            if (activeConversation != null && activeConversation.Ended && !activeConversation.IsThinking &&
                activeConversation.TurnCount > 0 && processedTurns >= activeConversation.TurnCount)
            {
                EndConversation(ConversationExit.OutOfTurns);
                return;
            }

            // The window lapsed → the horde resumes whether or not you finished talking.
            if (Time.unscaledTime >= conversationDeadline)
            {
                EndConversation(ConversationExit.Timeout);
            }
        }

        private void HandleTurn(string reply, ConversationDecision decision)
        {
            if (conversingTarget == null)
            {
                return;
            }

            var trimmed = reply ?? string.Empty;
            if (trimmed.Length > 0)
            {
                lastRobotReply = trimmed;
                hud?.PushSpeech(conversingTarget.HeadAnchorWorld, ClampReply(trimmed), QwTone.Neutral);
            }
            else if (decision == ConversationDecision.Unknown)
            {
                // The brain was unavailable/garbled this turn — keep the channel alive with a neutral status.
                conversationStatus = "…static on the channel…";
            }

            disposition = ConversationDirector.Nudge(disposition, decision, convTuning);
            if (disposition <= config.EnrageDispositionFloor && decision == ConversationDecision.Refuse)
            {
                angeredThisConversation = true;
            }

            // Renegotiating an existing ally: positive decisions reaffirm loyalty, FLEE makes it leave.
            if (conversingTarget.IsAlly)
            {
                switch (decision)
                {
                    case ConversationDecision.Convert:
                        // A firm reaffirm is gated by the same disposition threshold a first conversion is, so
                        // the brain can't lock in loyalty the player hasn't earned.
                        if (disposition >= convertThresholdForTarget)
                        {
                            conversingTarget.ReinforceLoyalty(disposition);
                            EndConversation(ConversationExit.Success);
                            return;
                        }

                        conversationStatus = "They're not fully sold — push harder.";
                        break;

                    case ConversationDecision.StandDown:
                        conversingTarget.ReinforceLoyalty(disposition);
                        EndConversation(ConversationExit.Success);
                        return;

                    case ConversationDecision.Flee:
                        conversingTarget.DefectFromPlayer();
                        EndConversation(ConversationExit.Success);
                        return;

                    default:
                        conversationStatus = "It's not convinced — make your case.";
                        break;
                }

                return;
            }

            switch (decision)
            {
                case ConversationDecision.Convert:
                    if (disposition >= convertThresholdForTarget && CanConvert())
                    {
                        conversingTarget.ConvertViaConversation(disposition);
                        EndConversation(ConversationExit.Success);
                        return;
                    }

                    conversationStatus = CanConvert() ? "It's wavering — push a little more." : "Ally roster is full.";
                    break;

                case ConversationDecision.StandDown:
                    conversingTarget.PacifyViaConversation();
                    EndConversation(ConversationExit.Success);
                    return;

                case ConversationDecision.Flee:
                    conversingTarget.FleeViaConversation();
                    EndConversation(ConversationExit.Success);
                    return;

                case ConversationDecision.Refuse:
                    conversationStatus = angeredThisConversation ? "It's furious — careful." : "It refuses. Try another angle.";
                    break;

                default:
                    break;
            }
        }

        private void ReadConversationInput()
        {
            // Leave the channel (resume the horde with no deal).
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                EndConversation(ConversationExit.Leave);
                return;
            }

            // Toggle text/voice (mirrors the base game's Tab toggle), when voice is available.
            if (Input.GetKeyDown(ParseKey(config.ToggleInputModeKey, KeyCode.Tab)))
            {
                ToggleInputMode();
            }

            // While the mic is actively recording, never let keystrokes leak into the text buffer — even if the
            // voice service drops mid-record (then ConversationVoiceAvailable would be false).
            if (voiceCapture != null && voiceCapture.IsRecording)
            {
                ReadVoiceInput();
                return;
            }

            if (inputMode == ConversationInputMode.Voice && ConversationVoiceAvailable)
            {
                ReadVoiceInput();
                return;
            }

            // Typed text: accumulate this frame's characters; submit on Return.
            conversationInput.Append(Input.inputString);
            if (conversationInput.ConsumeSubmit())
            {
                SubmitConversationLine(conversationInput.Text);
            }
        }

        private void ReadVoiceInput()
        {
            var voiceKey = ParseKey(config.VoiceKey, KeyCode.V);

            if (voiceCapture == null && Input.GetKeyDown(voiceKey) && dialogueInput != null)
            {
                voiceCapture = dialogueInput.BeginVoiceCapture();
                conversationStatus = "Listening…";
                return;
            }

            if (voiceCapture == null)
            {
                return;
            }

            if (voiceCapture.IsRecording && Input.GetKeyUp(voiceKey))
            {
                voiceCapture.Stop();
                conversationStatus = "Transcribing…";
                return;
            }

            if (voiceCapture.IsComplete)
            {
                var heard = voiceCapture.Found ? voiceCapture.Text : string.Empty;
                voiceCapture = null;
                if (!string.IsNullOrWhiteSpace(heard))
                {
                    SubmitConversationLine(heard);
                }
                else
                {
                    conversationStatus = "Didn't catch that — try again.";
                }
            }
        }

        private void SubmitConversationLine(string text)
        {
            if (activeConversation == null || activeConversation.IsThinking || string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            lastPlayerLine = text.Trim();
            activeConversation.Submit(text);
            conversationInput.Clear();
            conversationStatus = string.Empty;
        }

        public void ToggleInputMode()
        {
            if (inputMode == ConversationInputMode.Text)
            {
                if (!ConversationVoiceAvailable)
                {
                    conversationStatus = "Voice unavailable (no mic).";
                    return;
                }

                inputMode = ConversationInputMode.Voice;
            }
            else
            {
                voiceCapture?.Cancel();
                voiceCapture = null;
                inputMode = ConversationInputMode.Text;
            }
        }

        // HUD button hooks (the panel can also drive these by click).
        public void SubmitConversationFromHud() => SubmitConversationLine(conversationInput.Text);
        public void SubmitConversationTextFromHud(string text) => SubmitConversationLine(text);
        public void LeaveConversationFromHud() => EndConversation(ConversationExit.Leave);

        // The single teardown path: every exit (success, timeout, out-of-turns, player leave, target lost, or an
        // abort from dispose/scene-load/game-over) routes through here, so a JACK-IN can never strand the world
        // frozen. An unsettled exit (you ran out of time/turns or walked away) resolves the robot's mood first.
        private void EndConversation(ConversationExit exit)
        {
            if (!conversing)
            {
                return;
            }

            var resolveMood = exit == ConversationExit.Timeout || exit == ConversationExit.OutOfTurns || exit == ConversationExit.Leave;
            if (resolveMood && conversingTarget != null && conversingTarget.IsAlive)
            {
                if (conversingTarget.IsAlly)
                {
                    // A renegotiation that didn't close: reaffirm them at wherever the talk landed — only an actively
                    // soured talk (disposition driven to the floor) loses them. Merely opening a channel and timing
                    // out must NOT defect a loyal ally.
                    if (disposition <= config.EnrageDispositionFloor)
                    {
                        conversingTarget.DefectFromPlayer();
                    }
                    else
                    {
                        conversingTarget.ReinforceLoyalty(disposition);
                    }
                }
                else if (angeredThisConversation && disposition <= config.EnrageDispositionFloor)
                {
                    conversingTarget.EnrageViaConversation();
                }
                else if (disposition >= convertThresholdForTarget * 0.85f)
                {
                    conversingTarget.PacifyViaConversation(); // wavering — it disengages a moment
                }
                else
                {
                    conversingTarget.ResumeHostileFromConversation();
                }
            }

            activeConversation?.End();
            activeConversation = null;
            voiceCapture?.Cancel();
            voiceCapture = null;
            conversationFreeze?.Release(); // lift the Chronos world-freeze (restores timeScale/fixedDeltaTime)
            conversationFreeze = null;
            conversingTarget = null;
            conversing = false;
            processedTurns = 0;
            angeredThisConversation = false;
            conversationInput.Clear();
            lastRobotReply = string.Empty;
            conversationStatus = string.Empty;

            // Hand the player back control. (Game-over owns its own cursor/control handling, so don't fight it.)
            if (state != ZombiesState.GameOver)
            {
                robots?.SetPlayerControlsEnabled(true);
                RestorePlayCursor();
            }

            if (exit != ConversationExit.Aborted)
            {
                hud?.ShowBanner("CHANNEL CLOSED", QwTone.Muted);
            }
        }

        private void AccruePressure(float deltaTime)
        {
            pressure = Mathf.Min(1f, pressure + (config.PressureRampPerSecond * deltaTime));
        }

        private void DecayPressure(float deltaTime)
        {
            if (pressure > 0f)
            {
                pressure = Mathf.Max(0f, pressure - (config.PressureRampPerSecond * 0.5f * deltaTime));
            }
        }

        // Spawn density rises with built-up pressure: the interval shrinks and the alive-cap grows, so the horde the
        // player unfreezes into is denser the longer they talked.
        private int EffectiveMaxAlive =>
            config.MaxAliveZombies + Mathf.RoundToInt(pressure * config.PressureSpawnBoost * config.MaxAliveZombies);

        private float EffectiveSpawnInterval =>
            config.SpawnIntervalSeconds * Mathf.Max(0.2f, 1f - (pressure * config.PressureSpawnBoost));

        private static string ClampReply(string text)
        {
            return text.Length > 90 ? text.Substring(0, 90) : text;
        }

        private static KeyCode ParseKey(string name, KeyCode fallback)
        {
            if (!string.IsNullOrWhiteSpace(name) && System.Enum.TryParse<KeyCode>(name.Trim(), true, out var parsed))
            {
                return parsed;
            }

            return fallback;
        }

        public static string OverrideLabel(OverrideCommand command)
        {
            switch (command)
            {
                case OverrideCommand.JoinMe:
                    return "JOIN ME";
                case OverrideCommand.Freeze:
                    return "FREEZE";
                case OverrideCommand.GetOut:
                    return "GET OUT";
                default:
                    return "STAND DOWN";
            }
        }

        public float PlayerIntegrity => playerIntegrity;
        public float MaxPlayerIntegrity => config.PlayerIntegrity + upgrades.BonusMaxIntegrity;
        public bool GameOver => state == ZombiesState.GameOver;
        public bool IsActive => !disposed && root != null;
        public bool HasRealPlayer => robots != null && robots.TryGetPlayerPosition(out _);
        public float ZapperReadyFraction => zapper != null ? zapper.ReadyFraction : 1f;

        public string StateText
        {
            get
            {
                switch (state)
                {
                    case ZombiesState.WaitingForPrefab:
                        return "Waiting for robots";
                    case ZombiesState.Starting:
                        return shopping
                            ? "Wave 1 holds while you shop"
                            : "Wave 1 starts in " + Mathf.CeilToInt(stateTimer) + ShopHint();
                    case ZombiesState.Spawning:
                        return "Wave " + wave;
                    case ZombiesState.InterWave:
                        return shopping
                            ? "Next wave holds while you shop"
                            : "Next wave in " + Mathf.CeilToInt(stateTimer) + ShopHint();
                    case ZombiesState.GameOver:
                        return "Game over - press R to restart";
                    default:
                        return "Idle";
                }
            }
        }

        // Appended to the prep countdown line so the shop is discoverable without a tutorial. Built once —
        // StateText runs every HUD frame.
        private string shopHintCache = string.Empty;

        private string ShopHint()
        {
            if (!config.ShopEnabled)
            {
                return string.Empty;
            }

            if (shopHintCache.Length == 0)
            {
                shopHintCache = "  //  [" + config.ShopKey.ToUpperInvariant() + "] SHOP";
            }

            return shopHintCache;
        }

        // The player position if resolved, else the active camera's (so a camera-only scene can still be played
        // with the camera-mounted zapper). Returns false only when there is neither a player nor a camera.
        public bool TryGetChaseTargetPosition(out Vector3 position)
        {
            if (robots != null && robots.TryGetPlayerPosition(out var player))
            {
                position = new Vector3(player.X, player.Y, player.Z);
                return true;
            }

            var camera = Camera.main;
            if (camera != null)
            {
                position = camera.transform.position;
                return true;
            }

            position = Vector3.zero;
            return false;
        }

        // The live player GameObject, so a zombie can chase it natively (the agent tracks and re-paths to it as it
        // moves). Returns false when there is no real player (the camera-only fallback uses the position instead).
        public bool TryGetChaseTargetObject(out object gameObject)
        {
            if (robots != null && robots.TryGetPlayerObject(out gameObject))
            {
                return true;
            }

            gameObject = null!;
            return false;
        }

        public void Start(WorldSession session)
        {
            root = new GameObject("Robotopia Zombies Session");
            UnityEngine.Object.DontDestroyOnLoad(root);

            hud = root.AddComponent<ZombiesHudBehaviour>();
            hud.Initialize(this, config);

            zapper = root.AddComponent<ZapperController>();
            zapper.Initialize(this, config, context.Logger);

            overrideController = root.AddComponent<OverrideController>();
            overrideController.Initialize(this, config, context.Logger);

            SceneManager.sceneLoaded += OnSceneLoaded;
            sceneHandlerRegistered = true;

            playerIntegrity = config.PlayerIntegrity;
            if (robots == null)
            {
                context.Logger.Warn("Zombies: IRobotAgentService is unavailable (is the Robotopia RobotKit mod installed and loaded first?); enemies cannot spawn.");
            }

            if (RobotsAvailable())
            {
                BeginStartingCountdown();
            }
            else
            {
                EnterWaitingForPrefab();
            }

            ApplySuperhotMode();
            context.Logger.Info("Zombies session started for world " + session.WorldId + ".");
        }

        public void Update(float deltaTime)
        {
            if (disposed || root == null)
            {
                return;
            }

            // While a JACK-IN channel holds the world, the wave machine, spawning, combo decay and broadcast polling
            // are all paused — only the conversation advances. The infected robots freeze themselves via the
            // controller.Conversing guard in their own Update.
            if (conversing)
            {
                UpdateConversation(deltaTime);
                return;
            }

            // While the requisitions window is open the whole round holds — prep stateTimer, spawning, combo
            // decay, pressure and broadcast polling. This mod-side gate is what pauses the prep countdown even
            // when Chronos (and its world-freeze) is absent.
            if (shopping)
            {
                UpdateShopping();
                return;
            }

            DecayPressure(deltaTime);
            RemoveMissingZombies();
            CheckComboDecay();
            PollBroadcastQuery();

            switch (state)
            {
                case ZombiesState.WaitingForPrefab:
                    UpdateWaitingForPrefab(deltaTime);
                    break;
                case ZombiesState.Starting:
                    UpdateStarting(deltaTime);
                    break;
                case ZombiesState.Spawning:
                    UpdateSpawning(deltaTime);
                    break;
                case ZombiesState.InterWave:
                    UpdateInterWave(deltaTime);
                    break;
                case ZombiesState.GameOver:
                    UpdateGameOver();
                    break;
            }
        }

        public void DamagePlayer(float damage, ZombieEnemyController zombie)
        {
            if (disposed || state == ZombiesState.GameOver || robots == null)
            {
                return;
            }

            // Only a resolved player is a valid victim. Zombies fall back to chasing the camera for movement,
            // but the round must not be lost to proximity with a free/spectator camera.
            if (!robots.TryGetPlayerPosition(out _))
            {
                return;
            }

            playerIntegrity = Mathf.Max(0f, playerIntegrity - damage);
            robots.DamagePlayer(damage, "Zombies");
            FlashPlayerDamage(zombie);
            if (playerIntegrity <= 0.001f)
            {
                BeginGameOver("Player overrun by infected robots.");
            }
        }

        public void OnZombieKilled(ZombieEnemyController zombie, bool headshot)
        {
            if (disposed)
            {
                return;
            }

            zombies.Remove(zombie);

            // Chain kills within the combo window to raise the multiplier; a tier-up fires a banner. The awarded
            // score is the archetype's base value times the multiplier, plus a flat headshot bonus.
            var previousMultiplier = comboMultiplier;
            comboCount++;
            lastComboKillTime = Time.time;
            comboMultiplier = ComputeMultiplier(comboCount);

            var awarded = (zombie.Score * comboMultiplier) + (headshot ? config.HeadshotFlatBonusScore : 0);
            score += awarded;
            // Spendable shop credits accrue beside score, so buying never dents the run stat.
            wallet.Earn(Mathf.RoundToInt(awarded * config.ShopCreditsPerScore));

            var floaterText = comboMultiplier > 1 ? "+" + awarded + " x" + comboMultiplier : "+" + awarded;
            hud?.PushFloater(zombie.HeadAnchorWorld, floaterText, QwTone.Success);

            if (comboMultiplier > previousMultiplier && comboMultiplier >= 2)
            {
                hud?.ShowBanner(ComboBannerText(comboMultiplier), ComboBannerColor(comboMultiplier));
            }

            if (remainingToSpawn <= 0 && HostileCount == 0 && pendingPlacement == null && state == ZombiesState.Spawning)
            {
                BeginInterWave();
            }
        }

        // A hit landed on a zombie: pop the damage number, flash the hit marker, and kick the crosshair. Called by
        // the enemy for every connect (lethal hits report here AND through OnZombieKilled for the score floater).
        public void ReportHit(Vector3 worldPoint, int damage, ZombieHitKind kind)
        {
            if (disposed || hud == null)
            {
                return;
            }

            var headshot = kind == ZombieHitKind.Headshot || kind == ZombieHitKind.HeadshotKill;
            var text = headshot ? damage + "!" : damage.ToString();
            hud.PushFloater(worldPoint, text, headshot ? QwTone.Warning : QwTone.Neutral);
            hud.FlashHitMarker(kind);
            hud.FlashCrosshairHit();
        }

        // The zapper reports its 0..1 charge so the crosshair can show a charge meter.
        public void SetChargeFraction(float fraction)
        {
            hud?.SetChargeFraction(fraction);
        }

        // A zombie reported it cannot reach the target (stuck). Despawn it (no score — the player did not defeat it)
        // and remove it so an unreachable straggler cannot keep the wave from ever completing.
        public void OnZombieStranded(ZombieEnemyController zombie)
        {
            if (disposed)
            {
                return;
            }

            context.Logger.Info("Zombies removed an infected robot that could not reach the player (stuck) so the wave can advance.");
            zombies.Remove(zombie);
            zombie.SuppressScore();
            zombie.Despawn();
            if (remainingToSpawn <= 0 && HostileCount == 0 && state == ZombiesState.Spawning)
            {
                BeginInterWave();
            }
        }

        public void ReturnToMenu()
        {
            if (disposed)
            {
                return;
            }

            context.Logger.Info("Zombies returning to the main menu. Final score: " + score + ".");
            // The menu scene wants a usable pointer; leave the cursor unlocked from the game-over screen.
            ReflectionUtil.LoadScene(GameScenes.MainMenuSceneName, context.Logger);
            NotifySessionEnded();
        }

        public void Restart()
        {
            if (disposed)
            {
                return;
            }

            EndConversation(ConversationExit.Aborted);
            CloseShop(aborted: true);
            ResumePlayerControls();
            RestorePlayCursor();
            ClearZombies();
            hud?.ClearTransient();
            ResetRunCounters();
            playerIntegrity = config.PlayerIntegrity;
            if (RobotsAvailable())
            {
                BeginStartingCountdown();
            }
            else
            {
                EnterWaitingForPrefab();
            }

            context.Logger.Info("Zombies session restarted.");
        }

        private void NotifySessionEnded()
        {
            var callback = SessionEnded;
            if (callback == null)
            {
                Dispose();
                return;
            }

            try
            {
                callback();
            }
            catch (Exception ex)
            {
                context.Logger.Error(ex, "Zombies session-end callback failed; disposing the controller locally.");
                Dispose();
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            SessionEnded = null;
            EndConversation(ConversationExit.Aborted);
            CloseShop(aborted: true);
            ReleaseSuperhotMode();
            if (sceneHandlerRegistered)
            {
                SceneManager.sceneLoaded -= OnSceneLoaded;
                sceneHandlerRegistered = false;
            }

            ResumePlayerControls();
            ClearZombies();
            if (root != null)
            {
                UnityEngine.Object.Destroy(root);
                root = null;
            }
        }

        private bool RobotsAvailable()
        {
            return robots != null && robots.IsAvailable;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (disposed || mode != LoadSceneMode.Single)
            {
                return;
            }

            // A non-gameplay scene (menu/boot/loader) means the player left the world — end the session instead
            // of re-arming the HUD/Superhot over the menu. Normally a no-op: the Worlds service ends the session
            // earlier in this same sceneLoaded dispatch and this controller is already disposed (guard above).
            // This is the backstop for a missed provider-side end (e.g. endSessionOnMenuScene turned off).
            if (GameScenes.IsNonGameplayScene(scene.name))
            {
                context.Logger.Info("Zombies ending its session: non-gameplay scene '" + scene.name + "' loaded.");
                NotifySessionEnded();

                return;
            }

            // The session root is DontDestroyOnLoad, so leftover zombies would otherwise bleed into the next
            // scene. Reset and re-resolve everything for the new scene. (RobotKit clears its own robots/player
            // resolution on its scene-loaded hook.)
            EndConversation(ConversationExit.Aborted);
            CloseShop(aborted: true); // Chronos force-resets leases on scene change; drop ours before it goes stale
            ClearZombies();
            hud?.ClearTransient();
            playerIntegrity = config.PlayerIntegrity;
            ResetRunCounters();
            EnterWaitingForPrefab();
            // Chronos force-resets its leases on a scene change, so re-acquire the Superhot effect for the new scene.
            ApplySuperhotMode();
            context.Logger.Info("Zombies reset for newly loaded scene '" + scene.name + "'.");
        }

        // Acquire (or re-acquire) the Superhot effect from Chronos when the mode is on: a "time moves when you move"
        // driver over the world plus a player exemption so you stay full-speed. Idempotent and a no-op when the mode
        // is off or the time service is unavailable.
        private void ApplySuperhotMode()
        {
            ReleaseSuperhotMode();
            if (!config.SuperhotMode || timeControl == null || !timeControl.IsAvailable)
            {
                return;
            }

            superhotDriverLease = timeControl.SetDriver("zombies-superhot", new SuperhotTimeDriver());
            superhotExemptLease = timeControl.ExemptPlayer("zombies-superhot");
            hud?.ShowBanner("SUPERHOT", QwTone.Danger);
        }

        private void ReleaseSuperhotMode()
        {
            superhotDriverLease?.Release();
            superhotDriverLease = null;
            superhotExemptLease?.Release();
            superhotExemptLease = null;
        }

        private void UpdateWaitingForPrefab(float deltaTime)
        {
            prefabRetryTimer -= deltaTime;
            if (prefabRetryTimer > 0f)
            {
                return;
            }

            if (RobotsAvailable())
            {
                context.Logger.Info("Zombies found spawnable robots after waiting.");
                BeginStartingCountdown();
                return;
            }

            // Back off so a genuinely unavailable service does not trigger a full-scene scan every couple seconds.
            prefabRetryInterval = Mathf.Min(prefabRetryInterval * 2f, MaxPrefabRetryInterval);
            prefabRetryTimer = prefabRetryInterval;
        }

        private void UpdateStarting(float deltaTime)
        {
            PollShopKey();
            if (shopping)
            {
                return; // opened this frame — don't consume prep time under the shop
            }

            stateTimer -= deltaTime;
            if (stateTimer <= 0f)
            {
                BeginNextWave();
            }
        }

        private void UpdateSpawning(float deltaTime)
        {
            // Resolve an in-flight reachable-spawn search first: spawn at the confirmed point, or — if no reachable
            // point was found — retry on the next interval WITHOUT consuming the wave budget, so the spawn budget is
            // never spent on a doomed (inaccessible) zombie. A long run of failures drops the spawn to keep the wave
            // moving (see ResolveFailedPlacement).
            if (pendingPlacement != null && pendingPlacement.IsComplete)
            {
                var placement = pendingPlacement;
                pendingPlacement = null;
                if (placement.Found && FinishSpawn(placement.Position))
                {
                    remainingToSpawn--;
                    consecutiveSpawnFailures = 0;
                }
                else
                {
                    ResolveFailedPlacement();
                }

                // FinishSpawn can transition the state (e.g. EnterWaitingForPrefab if the prefab momentarily
                // vanished). Don't run the rest of the spawning logic against a state we've already left this tick.
                if (state != ZombiesState.Spawning)
                {
                    return;
                }
            }

            if (remainingToSpawn > 0 && AliveCount < EffectiveMaxAlive && pendingPlacement == null)
            {
                spawnTimer -= deltaTime;
                if (spawnTimer <= 0f)
                {
                    BeginSpawnPlacement();
                    spawnTimer = EffectiveSpawnInterval;
                }
            }

            var hostileCount = HostileCount;
            if (remainingToSpawn <= 0 && hostileCount == 0 && pendingPlacement == null)
            {
                BeginInterWave();
                return;
            }

            // Watchdog: only force-advance when the wave genuinely cannot progress at all — there is neither a
            // player NOR a camera, so zombies cannot spawn, chase, or be shot. We deliberately do NOT gate on
            // "no real player": in a camera-only scene the player can still clear the wave with the camera-mounted
            // zapper, so auto-clearing there would break legitimate play. A player who is simply not shooting must
            // not trip this either. Clearing lets the round continue.
            var unfinished = remainingToSpawn > 0 || hostileCount > 0;
            if (unfinished && !TryGetChaseTargetPosition(out _))
            {
                stallTimer += deltaTime;
                var threshold = Mathf.Max(5f, config.SpawnIntervalSeconds * 4f);
                if (stallTimer >= threshold)
                {
                    context.Logger.Warn("Zombies wave " + wave + " stalled with no player/camera (" + remainingToSpawn + " unspawned, " + hostileCount + " hostile); clearing and advancing.");
                    ClearZombies();
                    BeginInterWave();
                }
            }
            else
            {
                stallTimer = 0f;
            }
        }

        private void UpdateInterWave(float deltaTime)
        {
            PollShopKey();
            if (shopping)
            {
                return; // opened this frame — don't consume prep time under the shop
            }

            stateTimer -= deltaTime;
            if (stateTimer <= 0f)
            {
                BeginNextWave();
            }
        }

        private void UpdateGameOver()
        {
            // Keep the cursor free while the game-over screen is up (in case the FPS controller re-locks it),
            // so the Restart / Return to Menu buttons stay clickable. The R key is a keyboard shortcut.
            if (Cursor.lockState != CursorLockMode.None)
            {
                Cursor.lockState = CursorLockMode.None;
            }

            if (!Cursor.visible)
            {
                Cursor.visible = true;
            }

            if (Input.GetKeyDown(KeyCode.R))
            {
                Restart();
            }
        }

        private static void RestorePlayCursor()
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void EnterWaitingForPrefab()
        {
            // Deliberately does NOT reset run counters: this can be entered mid-run if robots become unavailable,
            // and wiping wave/score there would erase live progress. Fresh-run entry points (Start, Restart,
            // OnSceneLoaded) reset counters themselves; the success path resets via BeginStartingCountdown.
            state = ZombiesState.WaitingForPrefab;
            prefabRetryTimer = 0f;
            prefabRetryInterval = 2f;
            if (!missingRobotsLogged)
            {
                missingRobotsLogged = true;
                context.Logger.Warn("No spawnable robots available yet; Zombies will wait without spawning enemies.");
            }
        }

        private void BeginStartingCountdown()
        {
            state = ZombiesState.Starting;
            stateTimer = config.StartingCountdownSeconds;
            ResetRunCounters();
        }

        private void ResetRunCounters()
        {
            wave = 0;
            remainingToSpawn = 0;
            score = 0;
            stallTimer = 0f;
            consecutiveSpawnFailures = 0;
            pendingPlacement = null;
            comboCount = 0;
            comboMultiplier = 1;
            lastComboKillTime = 0f;
            packKind = ZombieKind.Grunt;
            packRemaining = 0;
            pendingBroadcastQuery = null;
            pressure = 0f;
            // Wallet and upgrades reset BEFORE the override state so charges re-seed against the
            // un-upgraded max; runSerial tells the HUD's shop modal to clear its purchase counts.
            wallet.Reset();
            upgrades.Reset();
            runSerial++;
            overrideController?.ResetState();
        }

        private void BeginNextWave()
        {
            wave++;
            remainingToSpawn = config.BaseZombiesPerWave + ((wave - 1) * config.ZombiesPerWaveIncrement);
            spawnTimer = 0f;
            stallTimer = 0f;
            consecutiveSpawnFailures = 0;
            pendingPlacement = null;
            packKind = ZombieKind.Grunt;
            packRemaining = 0;
            state = ZombiesState.Spawning;
            hud?.ShowBanner("WAVE " + wave, QwTone.Accent);
            context.Logger.Info("Zombies wave " + wave + " started with " + remainingToSpawn + " infected robots.");
        }

        private void BeginInterWave()
        {
            state = ZombiesState.InterWave;
            stateTimer = config.InterWaveDelaySeconds;
            stallTimer = 0f;
            pendingPlacement = null;
            packKind = ZombieKind.Grunt;
            packRemaining = 0;
            hud?.ShowBanner("WAVE CLEAR", QwTone.Success);
            context.Logger.Info("Zombies wave " + wave + " cleared. Score: " + score + ".");
        }

        private void BeginGameOver(string reason)
        {
            EndConversation(ConversationExit.Aborted);
            CloseShop(aborted: true);
            state = ZombiesState.GameOver;
            remainingToSpawn = 0;
            pendingPlacement = null;
            // Suspend the player's first-person controller so it stops re-locking the cursor and reading
            // move/look input, then free the cursor so the game-over screen's buttons are clickable.
            robots?.SetPlayerControlsEnabled(false);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            context.Logger.Info("Zombies game over: " + reason + " Final score: " + score + ".");
        }

        private void ResumePlayerControls()
        {
            robots?.SetPlayerControlsEnabled(true);
        }

        // Kick off an asynchronous search (driven by RobotKit, reusing the game's own pathfinder) for a spawn point
        // near the chase target that is on walkable ground AND has a complete path back to the player. The result is
        // polled in UpdateSpawning; nothing is spawned until a reachable point is confirmed.
        private void BeginSpawnPlacement()
        {
            if (robots == null || root == null)
            {
                EnterWaitingForPrefab();
                return;
            }

            if (!TryGetChaseTargetPosition(out var targetPosition))
            {
                // No player or camera to anchor a spawn ring; the watchdog handles a persistent no-target stall.
                return;
            }

            var request = new ReachableSpawnRequest(new Vec3(targetPosition.x, targetPosition.y, targetPosition.z))
            {
                MinRadius = config.MinSpawnDistance,
                MaxRadius = config.SpawnRadius,
                MaxCandidates = config.SpawnSearchAttempts,
                HeightOffset = config.SpawnHeightOffset
            };
            pendingPlacement = robots.BeginFindReachableSpawn(request);
        }

        // Spawn one infected robot at a confirmed-reachable point. Returns false if the robot could not be created.
        private bool FinishSpawn(Vec3 spawnVec)
        {
            if (robots == null || root == null)
            {
                EnterWaitingForPrefab();
                return false;
            }

            var spawnPosition = new Vector3(spawnVec.X, spawnVec.Y, spawnVec.Z);

            // Face the live target (it may have moved while the search ran); fall back to keeping prefab rotation.
            Vec3? facingVec = null;
            if (TryGetChaseTargetPosition(out var targetPosition))
            {
                var facing = targetPosition - spawnPosition;
                facing.y = 0f;
                if (facing.sqrMagnitude > 0.001f)
                {
                    facingVec = new Vec3(facing.x, facing.y, facing.z);
                }
            }

            // Pick this spawn's archetype: continue an in-progress Runt pack, else a fresh wave-weighted roll
            // (Brutes capped so a wave never becomes all-tanks). The archetype's tint/scale/gait/speed make it
            // readable at a glance; the mod drives a dormant-brain robot and ZombieEnemyController owns gameplay.
            var archetype = roster.Get(ChooseSpawnKind());
            if (archetype.IsPack && packRemaining <= 0)
            {
                packKind = archetype.Kind;
                packRemaining = random.Next(archetype.PackMin, archetype.PackMax + 1);
            }

            var request = new RobotAgentSpawnRequest(spawnVec, facingVec)
            {
                BrainMode = RobotBrainMode.Dormant,
                Gait = archetype.Gait,
                MoveSpeed = archetype.MoveSpeed,
                TurnSpeed = config.ZombieTurnSpeed,
                StopDistance = archetype.StopDistance,
                Scale = archetype.Scale,
                Tint = archetype.Tint,
                Name = "Zombies " + archetype.DisplayName,
                Interaction = RobotInteractionOptions.DisableNativeTalk()
            };

            var agent = robots.Spawn(request);
            if (agent == null)
            {
                EnterWaitingForPrefab();
                return false;
            }

            if (config.EnableEnemyEmotes)
            {
                agent.SetEmote(archetype.Emote);
            }

            var robot = (GameObject)agent.GameObject;
            var enemy = robot.AddComponent<ZombieEnemyController>();
            enemy.Initialize(this, config, archetype, agent, RobotMind.Seed(random, wave, tuning));
            zombies.Add(enemy);

            // Burn down the pack counter so the next ticks keep spawning the same swarm until it is exhausted.
            if (packRemaining > 0)
            {
                packRemaining--;
                if (packRemaining <= 0)
                {
                    packKind = ZombieKind.Grunt;
                }
            }

            return true;
        }

        // Choose the next spawn's kind: an in-progress pack continues; otherwise a wave-weighted roll, with the
        // Brute count capped. Archetypes can be disabled entirely (legacy uniform Grunts).
        private ZombieKind ChooseSpawnKind()
        {
            if (!config.ArchetypesEnabled)
            {
                return ZombieKind.Grunt;
            }

            if (packRemaining > 0)
            {
                return packKind;
            }

            var kind = roster.PickKind(wave, random);
            if (kind == ZombieKind.Brute && CountAliveKind(ZombieKind.Brute) >= config.BruteMaxAlive)
            {
                kind = ZombieKind.Grunt;
            }

            return kind;
        }

        private int CountAliveKind(ZombieKind kind)
        {
            var count = 0;
            for (var index = 0; index < zombies.Count; index++)
            {
                var zombie = zombies[index];
                if (zombie != null && zombie.IsAlive && zombie.Kind == kind)
                {
                    count++;
                }
            }

            return count;
        }

        // No reachable spawn point was found (or the spawn failed). Retry next interval, but if this keeps happening
        // the player is somewhere with no reachable ground nearby — drop the spawn so the wave still advances rather
        // than retrying forever. We never place an unreachable zombie as a fallback.
        private void ResolveFailedPlacement()
        {
            consecutiveSpawnFailures++;
            if (consecutiveSpawnFailures >= MaxConsecutiveSpawnFailures)
            {
                consecutiveSpawnFailures = 0;
                if (remainingToSpawn > 0)
                {
                    remainingToSpawn--;
                    context.Logger.Warn("Zombies could not find a reachable spawn point near the player after " +
                        MaxConsecutiveSpawnFailures + " attempts; dropping one infected robot from wave " + wave + " to keep it moving.");
                }
            }
        }

        // Surface the swarm's amortized broadcast line once the (single) crowd brain query lands, or drop it when the
        // window lapses. Purely flavour — the per-robot broadcast outcomes were already resolved deterministically.
        private void PollBroadcastQuery()
        {
            if (pendingBroadcastQuery == null)
            {
                return;
            }

            if (!pendingBroadcastQuery.IsComplete && Time.time < broadcastQueryDeadline)
            {
                return;
            }

            var query = pendingBroadcastQuery;
            pendingBroadcastQuery = null;
            if (query.IsComplete && query.Result.Available && query.Result.Succeeded &&
                query.Result.TryGet("bark", out var bark) && !string.IsNullOrEmpty(bark))
            {
                var max = config.BarkMaxChars;
                hud?.ShowBanner(bark.Length > max ? bark.Substring(0, max) : bark, QwTone.Primary);
            }
        }

        // Lapse the combo when no kill has landed within the window.
        // The combo window is config plus whatever COMBO STABILIZER levels the player bought this run.
        private float EffectiveComboWindowSeconds => config.ComboWindowSeconds + upgrades.ComboWindowBonusSeconds;

        private void CheckComboDecay()
        {
            if (comboCount > 0 && Time.time - lastComboKillTime > EffectiveComboWindowSeconds)
            {
                comboCount = 0;
                comboMultiplier = 1;
            }
        }

        private int ComputeMultiplier(int combo)
        {
            if (combo <= 0 || config.ComboKillsPerTier <= 0)
            {
                return 1;
            }

            var multiplier = 1 + (combo / config.ComboKillsPerTier);
            return multiplier > config.ComboMaxMultiplier ? config.ComboMaxMultiplier : multiplier;
        }

        private static string ComboBannerText(int multiplier)
        {
            switch (multiplier)
            {
                case 2:
                    return "DOUBLE ZAP!";
                case 3:
                    return "TRIPLE ZAP!";
                case 4:
                    return "OVERLOAD!";
                default:
                    return multiplier >= 5 ? "MELTDOWN!!" : "COMBO x" + multiplier;
            }
        }

        private static QwTone ComboBannerColor(int multiplier)
        {
            switch (multiplier)
            {
                case 2:
                    return QwTone.Accent;
                case 3:
                    return QwTone.Warning;
                case 4:
                    return QwTone.Primary;
                default:
                    return QwTone.Danger;
            }
        }

        // Flash the screen red and point a damage wedge from the camera toward the attacking zombie (signed bearing
        // in degrees: 0 = dead ahead, positive = to the right).
        private void FlashPlayerDamage(ZombieEnemyController zombie)
        {
            if (hud == null)
            {
                return;
            }

            var bearing = 0f;
            var camera = Camera.main;
            if (camera != null && zombie != null)
            {
                var toAttacker = zombie.WorldPosition - camera.transform.position;
                toAttacker.y = 0f;
                var forward = camera.transform.forward;
                forward.y = 0f;
                if (toAttacker.sqrMagnitude > 0.0001f && forward.sqrMagnitude > 0.0001f)
                {
                    bearing = Vector3.SignedAngle(forward, toAttacker, Vector3.up);
                }
            }

            hud.FlashDamage(bearing);
        }

        private void ClearZombies()
        {
            // Drop any in-flight spawn placement so a search that completes after teardown cannot spawn into the
            // next wave/scene. (RobotKit tears the underlying search down itself on scene change / dispose.)
            pendingPlacement = null;
            pendingBroadcastQuery = null;

            foreach (var zombie in zombies.ToArray())
            {
                if (zombie != null)
                {
                    zombie.SuppressScore();
                    // Make it inert immediately and destroy its robot so a deferred frame cannot deal damage.
                    zombie.Despawn();
                }
            }

            zombies.Clear();
        }

        private void RemoveMissingZombies()
        {
            var removed = zombies.RemoveAll(item => item == null || !item.IsAlive);
            if (removed > 0)
            {
                context.Logger.Debug("Zombies removed " + removed + " infected robot(s) that vanished without being defeated.");
            }
        }
    }

    internal enum ZombiesState
    {
        WaitingForPrefab,
        Starting,
        Spawning,
        InterWave,
        GameOver
    }

    // How the player is currently speaking to the robot in an open JACK-IN channel.
    internal enum ConversationInputMode
    {
        Text,
        Voice
    }

    // Why a JACK-IN channel closed — drives whether the robot's mood is resolved on the way out.
    internal enum ConversationExit
    {
        Success,    // a terminal decision (convert/stand-down/flee) was applied
        Timeout,    // the conversation window lapsed
        OutOfTurns, // the turn cap was reached with no deal
        Leave,      // the player walked away (Esc)
        TargetLost, // the robot vanished mid-conversation
        Aborted     // teardown from dispose/scene-load/game-over (no mood resolution)
    }
}
