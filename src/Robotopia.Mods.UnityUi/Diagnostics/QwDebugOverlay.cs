using UnityEngine;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>
    /// Opt-in diagnostics overlay on the debug band: UI frame time, font tier, input
    /// backend, theme state, tween/cursor/dismiss counters, canvas count. Refreshes at
    /// 4 Hz — negligible while open, zero cost while closed.
    /// </summary>
    public static class QwDebugOverlay
    {
        private static GameObject? root;
        private static QwLabel? body;

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

        private static void Build()
        {
            var host = QwUi.Create(new QwUiOptions { OwnerId = "quantumworks.debug" });
            var layer = host.Layer("debug", QwLayerBand.Debug, QwScheme.Hud, interactive: false, persistent: true);
            root = layer.Go;

            var panel = layer.Panel(QwPanelStyle.HudPanel);
            panel.Dock(QwCorner.BottomRight).Size(360f, 210f);
            var column = panel.Column(QwGap.Xs, QwGap.Md);
            column.Label("QWUI DIAGNOSTICS", QwTextStyle.Heading).Tone(QwTone.Accent);
            body = column.Label(string.Empty, QwTextStyle.Caption).Tone(QwTone.Muted);

            var driver = root.AddComponent<QwDebugOverlayDriver>();
            driver.Body = body;
        }
    }

    internal sealed class QwDebugOverlayDriver : MonoBehaviour
    {
        public QwLabel? Body;

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
                "fonts: " + QwFonts.ResolvedTier + "\n" +
                "input: " + (QwInput.LegacyAvailable ? "legacy/both" : "input-system") + "\n" +
                "theme: v" + QwTheme.Version + "  scale " + QwTheme.UiScale.ToString("0.##") +
                "  contrast " + (QwTheme.HighContrast ? "on" : "off") +
                "  motion " + QwTheme.EffectiveMotion.ToString("0.##") + "\n" +
                "tweens: " + QwTween.ActiveCount +
                "  cursor leases: " + QwCursor.ActiveLeases +
                "  esc stack: " + QwDismissStack.Count + "\n" +
                "canvases: " + canvasCount);
        }
    }
}
