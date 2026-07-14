using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Canvas creation with band-allocated sorting orders (process-wide — the bands
    /// coordinate every mod's UI in one table instead of hardcoded magic numbers).
    /// </summary>
    public static class TopiaForgeLayers
    {
        private static readonly TopiaForgeLayerBands Bands = new TopiaForgeLayerBands();

        /// <summary>Allocates the next sorting order in a band, logging on exhaustion.</summary>
        public static int Allocate(TopiaForgeLayerBand band, string ownerName)
        {
            if (!Bands.TryAllocate(band, out var order))
            {
                TopiaForgeLog.Warn("Sorting band " + band + " exhausted while allocating for '" + ownerName + "'; reusing order " + order + ".");
            }

            return order;
        }

        /// <summary>
        /// Creates a ScreenSpaceOverlay canvas in a band with the brand reference
        /// resolution (divided by the accessibility UI scale) and a raycaster that is
        /// enabled only for interactive layers.
        /// </summary>
        public static GameObject CreateCanvas(string name, TopiaForgeLayerBand band, bool interactive, bool persistent)
        {
            TopiaForgeEventSystems.EnsureEventSystem();
            TopiaForgeRuntime.Ensure();

            var root = new GameObject(name, typeof(RectTransform));
            if (persistent)
            {
                Object.DontDestroyOnLoad(root);
            }

            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            Assign(canvas, band, name);

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

        /// <summary>Releases a canvas's allocator slot before the canvas is destroyed.</summary>
        internal static void Release(GameObject root)
        {
            var canvas = root != null ? root.GetComponent<Canvas>() : null;
            if (canvas != null)
            {
                Bands.TryRelease(canvas.sortingOrder);
            }
        }

        /// <summary>
        /// Assigns a previously allocated order. Used only to permute allocator-owned window slots
        /// during focus changes and to repair Unity's hard-coded dropdown popup order.
        /// </summary>
        internal static void AssignAllocatedOrder(Canvas canvas, int sortingOrder)
        {
            if (canvas != null)
            {
                canvas.sortingOrder = sortingOrder;
            }
        }

        private static void Assign(Canvas canvas, TopiaForgeLayerBand band, string ownerName)
        {
            AssignAllocatedOrder(canvas, Allocate(band, ownerName));
        }

        /// <summary>Applies the brand scale mode; re-applied when TopiaForgeTheme.UiScale changes.</summary>
        public static void ApplyScaler(CanvasScaler scaler)
        {
            ApplyScaler(scaler, TopiaForgeTheme.UiScale);
        }

        /// <summary>Applies the brand scale mode for one host's effective scale.</summary>
        internal static void ApplyScaler(CanvasScaler scaler, float uiScale)
        {
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(
                TopiaForgeTokens.ReferenceWidth / uiScale,
                TopiaForgeTokens.ReferenceHeight / uiScale);
            scaler.matchWidthOrHeight = 0.5f;
        }
    }
}
