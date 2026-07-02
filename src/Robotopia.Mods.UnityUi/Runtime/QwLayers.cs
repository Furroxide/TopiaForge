using UnityEngine;
using UnityEngine.UI;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>
    /// Canvas creation with band-allocated sorting orders (process-wide — the bands
    /// coordinate every mod's UI in one table instead of hardcoded magic numbers).
    /// </summary>
    public static class QwLayers
    {
        private static readonly QwLayerBands Bands = new QwLayerBands();

        /// <summary>Allocates the next sorting order in a band, logging on exhaustion.</summary>
        public static int Allocate(QwLayerBand band, string ownerName)
        {
            if (!Bands.TryAllocate(band, out var order))
            {
                QwLog.Warn("Sorting band " + band + " exhausted while allocating for '" + ownerName + "'; reusing order " + order + ".");
            }

            return order;
        }

        /// <summary>
        /// Creates a ScreenSpaceOverlay canvas in a band with the brand reference
        /// resolution (divided by the accessibility UI scale) and a raycaster that is
        /// enabled only for interactive layers.
        /// </summary>
        public static GameObject CreateCanvas(string name, QwLayerBand band, bool interactive, bool persistent)
        {
            QwEventSystems.EnsureEventSystem();
            QwRuntime.Ensure();

            var root = new GameObject(name, typeof(RectTransform));
            if (persistent)
            {
                Object.DontDestroyOnLoad(root);
            }

            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = Allocate(band, name);

            var scaler = root.AddComponent<CanvasScaler>();
            ApplyScaler(scaler);

            var raycaster = root.AddComponent<GraphicRaycaster>();
            raycaster.enabled = interactive;

            var rect = (RectTransform)root.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return root;
        }

        /// <summary>Applies the brand scale mode; re-applied when QwTheme.UiScale changes.</summary>
        public static void ApplyScaler(CanvasScaler scaler)
        {
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(
                QwTokens.ReferenceWidth / QwTheme.UiScale,
                QwTokens.ReferenceHeight / QwTheme.UiScale);
            scaler.matchWidthOrHeight = 0.5f;
        }
    }
}
