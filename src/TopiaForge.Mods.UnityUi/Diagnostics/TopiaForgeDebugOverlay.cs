using UnityEngine;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Opt-in diagnostics overlay on the debug band: UI frame time, font tier, input
    /// backend, theme state, tween/cursor/dismiss counters, canvas count. Refreshes at
    /// 4 Hz — negligible while open, zero cost while closed.
    /// </summary>
    public static class TopiaForgeDebugOverlay
    {
        private static GameObject? root;
        private static TopiaForgeLabel? body;
        private static UiHost? host;

        public static bool IsOpen => root != null && root.activeSelf;

        public static void Toggle()
        {
            if (root == null)
            {
                Build();
            }
            else
            {
                root.SetActive(!root.activeSelf);
            }
        }

        /// <summary>Releases the debug host, canvas, theme subscription, and widgets.</summary>
        public static void Dispose()
        {
            host?.Dispose();
            host = null;
            root = null;
            body = null;
        }

        private static void Build()
        {
            host = TopiaForgeUi.Create(new TopiaForgeUiOptions
            {
                OwnerId = "io.github.furroxide.topiaforge.ui.debug"
            });
            var layer = host.Layer("debug", TopiaForgeLayerBand.Debug, TopiaForgeScheme.Hud, interactive: false, persistent: true);
            root = layer.Go;

            var panel = layer.Panel(TopiaForgePanelStyle.HudPanel);
            panel.Dock(TopiaForgeCorner.BottomRight).Size(360f, 210f);
            var column = panel.Column(TopiaForgeGap.Xs, TopiaForgeGap.Md);
            column.Label("TOPIAFORGE UI DIAGNOSTICS", TopiaForgeTextStyle.Heading).Tone(TopiaForgeTone.Accent);
            body = column.Label(string.Empty, TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);

            var driver = root.AddComponent<TopiaForgeDebugOverlayDriver>();
            driver.Body = body;
        }
    }

    internal sealed class TopiaForgeDebugOverlayDriver : MonoBehaviour
    {
        public TopiaForgeLabel? Body;

        private float nextRefresh;
        private float frameTimeEma = 16.7f;

        private void Update()
        {
            frameTimeEma = Mathf.Lerp(frameTimeEma, Time.unscaledDeltaTime * 1000f, 0.05f);
            if (Time.unscaledTime < nextRefresh || Body == null)
            {
                return;
            }

            nextRefresh = Time.unscaledTime + 0.25f;
            var canvasCount = Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None).Length;
            Body.SetText(
                "frame: " + frameTimeEma.ToString("0.0") + " ms (" + (1000f / Mathf.Max(0.01f, frameTimeEma)).ToString("0") + " fps)\n" +
                "fonts: " + TopiaForgeFonts.ResolvedTier + "\n" +
                "input: " + (TopiaForgeInput.LegacyAvailable ? "legacy/both" : "input-system") + "\n" +
                "theme: v" + TopiaForgeTheme.Version + "  scale " + TopiaForgeTheme.UiScale.ToString("0.##") +
                "  contrast " + (TopiaForgeTheme.HighContrast ? "on" : "off") +
                "  motion " + TopiaForgeTheme.EffectiveMotion.ToString("0.##") + "\n" +
                "tweens: " + TopiaForgeTween.ActiveCount +
                "  cursor leases: " + TopiaForgeCursor.ActiveLeases +
                "  esc stack: " + TopiaForgeDismissStack.Count + "\n" +
                "canvases: " + canvasCount);
        }
    }
}
