using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.Zombies
{
    /// <summary>Safe-SDK wave-survival session using opaque, owner-scoped framework services.</summary>
    internal sealed partial class ZombiesController : IDisposable
    {
        private const int MaximumConsecutiveSpawnFailures = 10;

        /// <summary>
        /// How long the main-menu scene load may run before the run is handed back to the player. A stalled load
        /// would otherwise leave the world frozen behind a window with no controls, and Restart refuses this phase.
        /// Not configurable: a player cannot meaningfully tune a soft-lock deadline.
        /// </summary>
        private const float ReturnToMenuTimeoutSeconds = 30f;

        private const float StrandedTimeoutSeconds = 12f;

        private readonly IModContext context;
        private readonly ZombiesConfig config;
        private readonly IRobotAgentService robots;
        private readonly WorldSession session;
        private readonly Func<CancellationToken, Task<OperationResult<SceneSnapshot>>> returnToMenu;
        private readonly ZombieRoster roster;
        private readonly List<ZombieEnemy> enemies = new List<ZombieEnemy>();
        private readonly ZombiesHudPresenter hud;
        private readonly ZombiesShopController shop;
        private readonly ZombiesConversationController conversation;
        private readonly ZombiesGameOverPresenter gameOverPresenter;
        private readonly ITimeControlService? time;
        private readonly IDisposable updateSubscription;
        private readonly IInputAction? fireAction;
        private readonly IInputAction? overrideAction;
        private readonly IInputAction? broadcastAction;
        private readonly IInputAction? shopAction;
        private readonly GameplayPause gameOverPause;

        private readonly PendingOperation<ReachableSpawnResult> spawnSearch =
            new PendingOperation<ReachableSpawnResult>();
        private readonly PendingOperation<SceneSnapshot> returnOperation =
            new PendingOperation<SceneSnapshot>();

        private Random random;
        private IEntity? playerEntity;
        private ITimeLease? superhotDriver;
        private ITimeLease? playerExemption;
        private PlayerHealthSnapshot? startingNativeHealth;
        private IEntity? startingNativeHealthEntity;
        private ZombiesPhase phase = ZombiesPhase.WaitingForWorld;
        private ZombieKind requestedSpawnKind;
        private ZombieKind packKind;
        private float phaseTimer;
        private float spawnTimer;
        private float integrity;
        private float maximumIntegrity;
        private float fireCooldown;
        private float broadcastCooldown;
        private float uplinkRegenTimer;
        private float comboTimer;
        private float chargeSeconds;
        private int wave;
        private int pendingSpawns;
        private int packRemaining;
        private int consecutiveSpawnFailures;
        private int score;
        private int comboCount;
        private int comboMultiplier = 1;
        private int uplinkCharges;
        private float hordePressure;
        private bool charging;
        private bool playerEntityFallbackLogged;
        private bool usingPositionalPlayerFallback;
        private bool nativeHealthWarningLogged;
        private bool spawnFailureWarningLogged;
        private bool hordeMotionSuspendedForConversation;
        private bool disposed;

        public ZombiesController(
            IModContext context,
            ZombiesConfig config,
            IRobotAgentService robots,
            WorldSession session,
            Func<CancellationToken, Task<OperationResult<SceneSnapshot>>> returnToMenu)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.config = config ?? throw new ArgumentNullException(nameof(config));
            this.robots = robots ?? throw new ArgumentNullException(nameof(robots));
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            this.returnToMenu = returnToMenu ?? throw new ArgumentNullException(nameof(returnToMenu));
            roster = new ZombieRoster(config);
            random = CreateRandom();

            if (context.TryGetExtension<ITimeControlService>(out var timeService))
            {
                time = timeService;
            }

            gameOverPause = new GameplayPause(
                context,
                "zombies-game-over",
                time.AsPauseSource(),
                "ZOMBIES_GAME_OVER_CONTROL_FAILED");

            ZombiesHudPresenter? createdHud = null;
            ZombiesGameOverPresenter? createdGameOver = null;
            ZombiesShopController? createdShop = null;
            ZombiesConversationController? createdConversation = null;
            IInputAction? createdFire = null;
            IInputAction? createdOverride = null;
            IInputAction? createdBroadcast = null;
            IInputAction? createdShopAction = null;
            IDisposable? createdUpdate = null;
            try
            {
                createdHud = new ZombiesHudPresenter(context);
                createdGameOver = new ZombiesGameOverPresenter(
                    context,
                    RestartFromUi,
                    BeginReturnToMenu,
                    config.ShopEnabled);
                createdShop = new ZombiesShopController(context, config, time, CanPurchaseShopItem, ApplyShopItem);
                createdConversation = new ZombiesConversationController(context, config, time, ResolveConversation);

                createdFire = RegisterAction(new InputActionDefinition(
                    "zapper-fire",
                    "Fire or charge the SDK zapper",
                    new[] { InputBinding.MouseButton(InputMouseButton.Primary) }));
                if (config.OverrideEnabled)
                {
                    createdOverride = RegisterAction(new InputActionDefinition(
                        "jack-in",
                        "Jack into the targeted infected robot",
                        new[] { InputBinding.Key(config.JackInKey) }));
                    createdBroadcast = RegisterAction(new InputActionDefinition(
                        "stand-down-broadcast",
                        "Broadcast stand-down to nearby infected robots",
                        new[] { InputBinding.Key(config.BroadcastKey) }));
                }

                if (config.ShopEnabled)
                {
                    createdShopAction = RegisterAction(new InputActionDefinition(
                        "field-requisitions",
                        "Open field requisitions between waves",
                        new[] { InputBinding.Key(config.ShopKey) }));
                }

                hud = createdHud;
                gameOverPresenter = createdGameOver;
                shop = createdShop;
                conversation = createdConversation;
                fireAction = createdFire;
                overrideAction = createdOverride;
                broadcastAction = createdBroadcast;
                shopAction = createdShopAction;
                maximumIntegrity = config.PlayerIntegrity;
                integrity = maximumIntegrity;
                uplinkCharges = MaximumUplinkCharges;
                ReportInputConflicts();
                RefreshHud();
                createdUpdate = context.Events.SubscribeUpdate(Update);
                updateSubscription = createdUpdate;
            }
            catch
            {
                createdUpdate?.Dispose();
                createdShopAction?.Dispose();
                createdBroadcast?.Dispose();
                createdOverride?.Dispose();
                createdFire?.Dispose();
                createdConversation?.Dispose();
                createdShop?.Dispose();
                createdGameOver?.Dispose();
                createdHud?.Dispose();
                throw;
            }
        }

        public OperationResult<string> Restart()
        {
            if (disposed)
            {
                return OperationResult<string>.Failure(ModErrorCode.InvalidState, "The Zombies session is not active.");
            }

            if (phase == ZombiesPhase.ReturningToMenu)
            {
                return OperationResult<string>.Failure(
                    ModErrorCode.Conflict,
                    "The current run is already returning to the menu.");
            }

            RestartCore(showToast: true);
            return OperationResult<string>.Success("Zombies run restarted.");
        }

        public OperationResult<string> BroadcastStandDown()
        {
            return TryBroadcastStandDown(showFeedback: true);
        }

        public string DescribeStatus()
        {
            return "phase=" + phase.ToString().ToLowerInvariant()
                + ", wave=" + wave.ToString(CultureInfo.InvariantCulture)
                + ", alive=" + CountActiveNonAllies().ToString(CultureInfo.InvariantCulture)
                + ", pending=" + pendingSpawns.ToString(CultureInfo.InvariantCulture)
                + ", integrity=" + integrity.ToString("0", CultureInfo.InvariantCulture)
                + ", score=" + score.ToString(CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            updateSubscription.Dispose();
            // Forget rather than Cancel: the update loop is gone, so nothing would ever drain these. The mod
            // lifetime is stopping, so the runtime owns whatever the SDK still hands back.
            spawnSearch.Forget();
            returnOperation.Forget();
            shop.Dispose();
            conversation.Dispose();
            hordeMotionSuspendedForConversation = false;
            gameOverPresenter.Dispose();
            fireAction?.Dispose();
            overrideAction?.Dispose();
            broadcastAction?.Dispose();
            shopAction?.Dispose();
            gameOverPause.Dispose();
            superhotDriver?.Dispose();
            superhotDriver = null;
            playerExemption?.Dispose();
            playerExemption = null;
            RestoreNativeHealth();
            ClearEnemies();
            hud.Dispose();
        }

        private IInputAction? RegisterAction(InputActionDefinition definition)
        {
            var result = context.Input.RegisterAction(definition);
            if (result.TryGetValue(out var action))
            {
                return action;
            }

            context.Diagnostics.Report(new DiagnosticEntry(
                "ZOMBIES_INPUT_UNAVAILABLE",
                "Zombies input '" + definition.Name + "' is unavailable.",
                DiagnosticSeverity.Warning,
                result.ErrorMessage));
            return null;
        }

        private void ReportInputConflicts()
        {
            foreach (var conflict in context.Input.GetConflicts())
            {
                if (!IsZombiesAction(conflict.ActionName))
                {
                    continue;
                }

                context.Diagnostics.Report(new DiagnosticEntry(
                    "ZOMBIES_INPUT_CONFLICT",
                    "Zombies action '" + conflict.ActionName + "' shares "
                        + conflict.Binding.Control + " with '" + conflict.OtherActionName + "'.",
                    DiagnosticSeverity.Warning,
                    "Rebind either action; the HUD always shows the effective Zombies binding."));
            }
        }

        private static bool IsZombiesAction(string name) =>
            string.Equals(name, "zapper-fire", StringComparison.Ordinal)
            || string.Equals(name, "jack-in", StringComparison.Ordinal)
            || string.Equals(name, "stand-down-broadcast", StringComparison.Ordinal)
            || string.Equals(name, "field-requisitions", StringComparison.Ordinal)
            || string.Equals(name, "jack-in-voice", StringComparison.Ordinal);

    }
}
