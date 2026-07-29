using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Last-resort diagnostics when TMP is unusable: one legacy-Text banner naming the
    /// log to check. This is deliberately the only place in the kit allowed to use
    /// UnityEngine.UI.Text — it is a diagnostic veneer, not a rendering path.
    /// </summary>
    internal static class TopiaForgeSafeMode
    {
        private static GameObject? banner;

        public static void Engage(string message)
        {
            if (banner != null)
            {
                return;
            }

            try
            {
                var root = new GameObject("TopiaForgeSafeModeBanner", typeof(RectTransform));
                Object.DontDestroyOnLoad(root);
                var canvas = root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                TopiaForgeLayers.AssignAllocatedOrder(
                    canvas,
                    TopiaForgeLayers.Allocate(TopiaForgeLayerBand.Debug, "TopiaForgeSafeModeBanner"));

                var panel = new GameObject("Panel", typeof(RectTransform));
                panel.transform.SetParent(root.transform, false);
                var image = panel.AddComponent<Image>();
                image.color = new Color(0.78f, 0.24f, 0.30f, 0.95f);
                var panelRect = (RectTransform)panel.transform;
                panelRect.anchorMin = new Vector2(0f, 1f);
                panelRect.anchorMax = new Vector2(1f, 1f);
                panelRect.pivot = new Vector2(0.5f, 1f);
                panelRect.anchoredPosition = Vector2.zero;
                panelRect.sizeDelta = new Vector2(0f, 34f);

                var textGo = new GameObject("Text", typeof(RectTransform));
                textGo.transform.SetParent(panel.transform, false);
                var text = textGo.AddComponent<Text>();
                text.font = Font.CreateDynamicFontFromOSFont("Arial", 14);
                text.text = message;
                text.color = Color.white;
                text.alignment = TextAnchor.MiddleCenter;
                var textRect = (RectTransform)textGo.transform;
                textRect.anchorMin = Vector2.zero;
                textRect.anchorMax = Vector2.one;
                textRect.offsetMin = Vector2.zero;
                textRect.offsetMax = Vector2.zero;

                banner = root;
            }
            catch
            {
                // Even the safe mode failed; the log line from the caller is all we have.
            }
        }

        internal static void Reset()
        {
            if (banner != null)
            {
                TopiaForgeLayers.Release(banner);
                Object.Destroy(banner);
            }

            banner = null;
        }
    }
}
