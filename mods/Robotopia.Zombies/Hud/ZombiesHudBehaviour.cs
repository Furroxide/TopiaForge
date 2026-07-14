using Robotopia.Mods.UnityUi;
using UnityEngine;

namespace Robotopia.Zombies
{
    /// <summary>
    /// The Zombies HUD shell on the QwUi kit. Owns the lifecycle (one UiHost disposed
    /// in OnDestroy), the per-frame pump (live vs conversing vs game over), mode
    /// visibility + raycaster switching, the modal cursor lease, and delegation to the
    /// Hud/ modules. The public surface is unchanged from the legacy Neon HUD.
    /// </summary>
    internal sealed class ZombiesHudBehaviour : MonoBehaviour
    {
        private readonly QwCursorLease cursorLease = new QwCursorLease();

        private ZombiesController? controller;
        private ZombiesConfig? config;
        private UiHost? ui;
        private QwHudLayer? hud;
        private QwContainer? live;

        private ThreatPanel? threatPanel;
        private ReticleLayer? reticle;
        private DamageFeedbackLayer? damageFeedback;
        private ComboMeter? combo;
        private UplinkPanel? uplink;
        private WorldLabelLayer? worldLabels;
        private QwBanner? banner;
        private ConversationModal? conversation;
        private GameOverModal? gameOver;
        private ShopModal? shopModal;
        public void Initialize(ZombiesController controller, ZombiesConfig config)
        {
            this.controller = controller;
            this.config = config;
            BuildUi();
        }

        public void PushSpeech(Vector3 world, string text, QwTone tone)
        {
            worldLabels?.PushSpeech(world, text, tone);
        }

        public void PushFloater(Vector3 world, string text, QwTone tone)
        {
            worldLabels?.PushFloater(world, text, tone);
        }

        public void FlashHitMarker(ZombieHitKind kind)
        {
            reticle?.FlashHitMarker(kind);
        }

        public void FlashCrosshairHit()
        {
            reticle?.FlashCrosshairHit();
        }

        public void SetChargeFraction(float fraction)
        {
            reticle?.SetChargeFraction(fraction);
        }

        public void ShowBanner(string text, QwTone tone)
        {
            if (banner == null)
            {
                return;
            }

            if (string.IsNullOrEmpty(text))
            {
                banner.HideImmediate();
                return;
            }

            banner.SetTone(tone);
            banner.Show(text);
        }

        public void FlashDamage(float bearingDegrees)
        {
            damageFeedback?.FlashDamage(bearingDegrees);
        }

        public void ClearTransient()
        {
            worldLabels?.Clear();
            reticle?.Reset();
            damageFeedback?.Reset();
            banner?.HideImmediate();
        }

        private void Update()
        {
            if (controller == null || config == null || hud == null)
            {
                return;
            }

            hud.SetHudScale(config.HudScale);

            var gameOverActive = controller.GameOver;
            var conversing = controller.Conversing;
            // The live chrome stays visible while the shop is up (its kit window rides its own canvas
            // with its own cursor lease), so the held countdown/credits stay readable behind it.
            live?.SetVisible(!gameOverActive && !conversing);
            gameOver?.SetVisible(gameOverActive);
            conversation?.SetVisible(conversing && !gameOverActive);
            hud.SetInteractive(gameOverActive || conversing);
            cursorLease.SetActive(gameOverActive || conversing);

            // Runs in every mode: syncs window open/close with Controller.Shopping (incl. forced
            // closes on game over/restart) and ticks the pane while open.
            shopModal?.Tick();

            // World labels and the banner tick themselves via kit drivers (as the old
            // world-label pass ran in every mode).
            if (gameOverActive)
            {
                gameOver?.Tick();
                return;
            }

            if (conversing)
            {
                conversation?.Tick();
                return;
            }

            threatPanel?.Tick();
            reticle?.Tick();
            damageFeedback?.Tick();
            combo?.Tick();
            uplink?.Tick();
        }

        private void OnDestroy()
        {
            cursorLease.Release();
            ui?.Dispose();
            ui = null;
            hud = null;
        }

        private void BuildUi()
        {
            if (ui != null || controller == null || config == null)
            {
                return;
            }

            // The behaviour has no IModContext (it only receives controller + config),
            // so the host is created from explicit options; kit logging keeps its
            // process-wide sinks.
            ui = QwUi.Create(new QwUiOptions
            {
                OwnerId = "robotopia.zombies",
                AccessibilityProfile = new QwAccessibilityProfile(
                    highContrast: config.HudHighContrast,
                    motionIntensity: config.HudMotionIntensity),
            });
            hud = ui.HudLayer("zombies");
            hud.Go.name = "RobotopiaZombiesHudCanvas";
            hud.Go.transform.SetParent(transform, false);

            var context = new HudContext(controller, config, ui, hud);

            // Live gameplay chrome (hidden while a modal owns the screen). Build order
            // is draw order: damage feedback overlays the reticle and panels.
            live = hud.Scaled.Stack("Live");
            threatPanel = new ThreatPanel(context, live);
            reticle = new ReticleLayer(context, live);
            damageFeedback = new DamageFeedbackLayer(context, live);
            combo = new ComboMeter(context, live);
            uplink = new UplinkPanel(context, live);

            // The banner rides inside the live stack so it stays hidden during modals,
            // exactly like the legacy liveRoot banner.
            banner = hud.Banner();
            banner.Go.transform.SetParent(live.Go.transform, false);

            worldLabels = new WorldLabelLayer(context);

            // Gameplay modals live on the canvas root (never HUD-scaled), above the
            // world layer. The controller owns ESC/Tab/V and the flow; these only
            // render and expose clicks.
            conversation = new ConversationModal(context, hud);
            gameOver = new GameOverModal(context, hud);
            shopModal = new ShopModal(context);
        }
    }
}
