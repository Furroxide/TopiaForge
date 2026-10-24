using System;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Canvas creation with band-allocated sorting orders (process-wide — the bands
    /// coordinate every mod's UI in one table instead of hardcoded magic numbers).
    /// Every slot is recorded in a <see cref="TopiaForgeLayerLedger{TCanvas}"/> and goes back by that
    /// record, whether the kit destroys the canvas or a scene, the game or another mod does.
    /// </summary>
    public static class TopiaForgeLayers
    {
        private static readonly TopiaForgeLayerBands Bands = new TopiaForgeLayerBands();

        // Keyed by the canvas's root object itself rather than by Unity's equality, so a destroyed canvas still finds its record.
        private static readonly TopiaForgeLayerLedger<GameObject> Ledger = new TopiaForgeLayerLedger<GameObject>(Bands);
        private static bool quitting;

        static TopiaForgeLayers()
        {
            Application.quitting += () => quitting = true;
        }

        /// <summary>Gets how many kit canvases hold a sorting slot.</summary>
        internal static int HeldSlots => Ledger.Count;

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
        /// enabled only for interactive layers. Only a canvas that takes input asks for an
        /// event system, so a HUD made before the game's first scene leaves the menu's own
        /// event system in charge.
        /// </summary>
        public static GameObject CreateCanvas(string name, TopiaForgeLayerBand band, bool interactive, bool persistent)
        {
            TopiaForgeRuntime.Ensure();

            var root = new GameObject(name, typeof(RectTransform));
            if (persistent)
            {
                UnityEngine.Object.DontDestroyOnLoad(root);
            }

            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            Claim(root, canvas, band, name);

            var scaler = root.AddComponent<CanvasScaler>();
            ApplyScaler(scaler);

            var raycaster = root.AddComponent<GraphicRaycaster>();
            raycaster.enabled = interactive;
            TopiaForgeEventSystems.Track(raycaster);

            var rect = (RectTransform)root.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return root;
        }

        /// <summary>
        /// Allocates a root canvas's order and records it. A <see cref="TopiaForgeLayerSlot"/> on the canvas gives the
        /// slot back if anything destroys the canvas before the kit releases it.
        /// </summary>
        internal static void Claim(GameObject root, Canvas canvas, TopiaForgeLayerBand band, string ownerName)
        {
            var order = Ledger.Claim(root, band, out var exhausted);
            if (exhausted)
            {
                TopiaForgeLog.Warn("Sorting band " + band + " exhausted while allocating for '" + ownerName + "'; reusing order " + order + ".");
            }

            canvas.sortingOrder = order;
            var slot = root.AddComponent<TopiaForgeLayerSlot>();
            slot.Root = root;
        }

        /// <summary>Calls <paramref name="lost"/> once if the canvas is destroyed outside the kit, before its slot goes back.</summary>
        internal static void WatchLoss(GameObject root, Action<GameObject> lost)
        {
            var slot = root != null ? root.GetComponent<TopiaForgeLayerSlot>() : null;
            if (slot != null)
            {
                slot.Lost = lost;
            }
        }

        /// <summary>
        /// Releases a canvas's slot by what was allocated to it, before or after the canvas is destroyed. Releasing twice,
        /// or a canvas the kit never claimed, does nothing.
        /// </summary>
        internal static void Release(GameObject root)
        {
            Ledger.Release(root);
        }

        /// <summary>Gets the order a kit canvas holds, by its record rather than by reading the canvas.</summary>
        internal static bool TryGetOrder(GameObject root, out int sortingOrder)
        {
            return Ledger.TryGetOrder(root, out sortingOrder);
        }

        /// <summary>
        /// Assigns a previously allocated order. Used only to permute allocator-owned window slots
        /// during focus changes and to repair Unity's hard-coded dropdown popup order. A kit canvas's
        /// record follows, so its slot later goes back as the order it holds.
        /// </summary>
        internal static void AssignAllocatedOrder(Canvas canvas, int sortingOrder)
        {
            if (canvas == null)
            {
                return;
            }

            canvas.sortingOrder = sortingOrder;
            Ledger.Reassign(canvas.gameObject, sortingOrder);
        }

        /// <summary>
        /// A kit canvas was destroyed. One the kit still holds was destroyed outside it: by a scene that unloaded it, the
        /// game or another mod. Its owner hears of it first, then its slot goes back. While the application quits,
        /// nothing is reported, since everything is destroyed then.
        /// </summary>
        internal static void ReleaseDestroyed(GameObject root, Action<GameObject>? lost)
        {
            if (!Ledger.TryGetOrder(root, out _))
            {
                return;
            }

            if (lost != null && !quitting)
            {
                try
                {
                    lost(root);
                }
                catch (Exception exception)
                {
                    TopiaForgeLog.Error(exception, "A kit canvas's owner failed while letting go of the destroyed canvas '" + root.name + "'.");
                }
            }

            Release(root);
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
