using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UImage = UnityEngine.UI.Image;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Toast notifications: queued, max 4 visible, pooled views stacked up from the bottom-right
    /// corner on a shared toast-band canvas, slide+fade motion, auto-dismiss. The game shows a
    /// level's objective at the top-right, drawn above every kit canvas, and nothing of its own
    /// at the bottom-right. Dark chip styling so toasts read over both gameplay and paper
    /// surfaces. A longer text wraps to three lines and its toast grows to hold them, pushing
    /// the toasts above it up. The bookkeeping, which view shows which toast and
    /// where it rests, is <see cref="TopiaForgeToastStack{T}"/>: a view that takes a new
    /// toast is always placed and brought in afresh, whatever it showed before.
    /// </summary>
    public static partial class TopiaForgeToasts
    {
        private const int MaxVisible = 4;
        private const int MaxQueued = 64;
        private const int MaxTextChars = 512;
        private const float DefaultDuration = 3.5f;
        private const float ToastWidth = 340f;
        private const float ToastHeight = 44f;
        private const float TextInsetX = 14f;
        private const float TextInsetY = 12f;
        private const float GlyphSize = 20f;

        // The most text a toast shows: three lines, measured in its own face and size.
        private const string ThreeLines = "A\nA\nA";
        private const float StackGap = 8f;

        private sealed class ToastView
        {
            public TopiaForgeContainer? Root;
            public UImage? Fill;
            public UImage? Ring;
            public UImage? Glyph;
            public TextMeshProUGUI? Label;
            public Pending Content;
            public float UiScale;
            public float Scale;
            public bool HighContrast;
            public float RestingX;
            public float RestingY;
            public TopiaForgeResolvedTheme? PaintedTheme;
            public Action? ReleaseCapture;
            public uint CaptureGeneration;
        }

        private struct Pending
        {
            public string Text;
            public TopiaForgeTone Tone;
            public bool Plain;
            public UiHost? Viewer;
            public ToastObserver? Observe;
        }

        private static readonly TopiaForgeToastStack<Pending> Stack = new TopiaForgeToastStack<Pending>(MaxVisible, MaxQueued, StackGap);

        // Index-aligned with the stack's views: the Unity half of each pooled view.
        private static readonly List<ToastView> Views = new List<ToastView>();
        private static readonly List<int> Expired = new List<int>(MaxVisible);
        private static TopiaForgeContainer? layer;
        private static ToastView? blockedOffer;
        private static bool queueOverflowLogged;
        private static int appliedThemeVersion;
        private static TopiaForgeResolvedTheme? contrastTheme;
        private static float bottomRightHudTop;
        private static float subtitleReserve;
        private static float appliedHudTop;
        private static float appliedSubtitleReserve;
        private static int appliedScreenWidth;
        private static int appliedScreenHeight;

        // The dock reports height in scale-independent reference units and the subtitle reserve as a screen fraction.
        internal static void SetBottomRightHudTop(float height, float reserve = 0f)
        {
            bottomRightHudTop = height;
            subtitleReserve = reserve;
        }

        // Moves on with every reset, so an exit that finishes after one frees nothing in the views that came after it.
        private static int generation;

        /// <summary>Shows a toast when a view fits on screen, with at most four shown at once.</summary>
        public static void Show(string text, TopiaForgeTone tone = TopiaForgeTone.Neutral, float duration = DefaultDuration)
        {
            Enqueue(text, tone, duration, plain: false);
        }

        /// <summary>
        /// Shows a toast whose text renders exactly as written: markup in it is not parsed. Use it for text another player
        /// wrote, such as a name or a message, once <see cref="TopiaForgePlainText"/> has bounded it.
        /// </summary>
        public static void ShowPlain(string text, TopiaForgeTone tone = TopiaForgeTone.Neutral, float duration = DefaultDuration)
        {
            Enqueue(text, tone, duration, plain: true);
        }

        internal static void ShowPlain(string text, TopiaForgeTone tone, UiHost viewer, float duration = DefaultDuration)
        {
            Enqueue(text, tone, duration, plain: true, viewer);
        }

        private static void Enqueue(string text, TopiaForgeTone tone, float duration, bool plain, UiHost? viewer = null,
            ToastObserver? observe = null)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            TopiaForgeRuntime.Ensure();
            var pending = new Pending
            {
                Text = text.Length > MaxTextChars ? text.Substring(0, MaxTextChars) : text,
                Tone = tone,
                Plain = plain,
                Viewer = viewer,
                Observe = observe,
            };
            if (Stack.Enqueue(pending, Mathf.Clamp(duration, 0.25f, 30f)) && !queueOverflowLogged)
            {
                queueOverflowLogged = true;
                TopiaForgeLog.Warn("Toast queue reached " + MaxQueued + " entries; dropping the oldest queued toast.");
            }

            Pump();
        }

        /// <summary>Shows a success toast.</summary>
        public static void Success(string text)
        {
            Show(text, TopiaForgeTone.Success);
        }

        /// <summary>Writes an error message.</summary>
        public static void Error(string text)
        {
            Show(text, TopiaForgeTone.Danger, 5f);
        }

        internal static void Tick(float unscaledDelta)
        {
            if (layer == null) return;
            var reflow = RefreshMeasurements();
            if (reflow) FitHeldViews(CurrentViewport(), resume: false);

            Stack.Expire(unscaledDelta, Expired);
            for (var index = 0; index < Expired.Count; index++)
            {
                DismissView(Expired[index]);
            }

            if (reflow || Expired.Count > 0) Pump();
        }

        /// <summary>Releases the process-wide toast host, pending work, and pooled views.</summary>
        internal static void Reset()
        {
            try
            {
                foreach (var view in Views) ReleaseCapture(view);
                TopiaForgeToastHost.Reset();
            }
            finally
            {
                layer = null;
                blockedOffer = null;
                contrastTheme = null;
                Stack.Clear();
                Views.Clear();
                queueOverflowLogged = false;
                generation++;
            }
        }

        private static void Pump()
        {
            EnsureLayer();
            RefreshMeasurements();
            var viewport = CurrentViewport();
            blockedOffer = null;
            if (FitHeldViews(viewport, resume: true))
            {
                while (Stack.TryStageNext(out var index, out var pending))
                {
                    while (Views.Count <= index) Views.Add(new ToastView());
                    var view = Views[index];
                    Stage(view, pending);
                    if (!FitsPending(viewport, index))
                    {
                        blockedOffer = view;
                        break;
                    }

                    Stack.CommitStaged(index);
                    Stack.SetHeight(index, MeasuredHeight(view), StackGap * view.Scale);
                }
            }

            Restack(viewport);
        }

        private static void Stage(ToastView view, Pending pending)
        {
            if (view.Root == null)
            {
                var root = layer!.Stack("Toast");
                root.Go.SetActive(false);
                root.Rect.anchorMin = new Vector2(1f, 0f);
                root.Rect.anchorMax = new Vector2(1f, 0f);
                root.Rect.pivot = new Vector2(1f, 0f);
                root.Rect.sizeDelta = new Vector2(ToastWidth, ToastHeight);

                view.Fill = CreateImage(root.Go.transform, "Fill", TopiaForgeSprites.Fill(TopiaForgeRadius.Control));
                view.Ring = CreateImage(root.Go.transform, "Ring", TopiaForgeSprites.Ring(TopiaForgeRadius.Control, TopiaForgeTokens.BorderStandard));
                var glyphGo = new GameObject("Tone glyph", typeof(RectTransform));
                glyphGo.transform.SetParent(root.Go.transform, false);
                view.Glyph = glyphGo.AddComponent<UImage>();
                view.Glyph.raycastTarget = false;
                var glyphRect = (RectTransform)glyphGo.transform;
                glyphRect.anchorMin = glyphRect.anchorMax = glyphRect.pivot = new Vector2(0f, 0.5f);
                glyphRect.anchoredPosition = new Vector2(TextInsetX, 0f);

                var labelGo = new GameObject("Label", typeof(RectTransform));
                labelGo.transform.SetParent(root.Go.transform, false);
                view.Label = TopiaForgeTmp.Create(labelGo);
                view.Label.fontSize = TopiaForgeTokens.LabelSize;
                view.Label.alignment = TextAlignmentOptions.Left;
                view.Label.textWrappingMode = TextWrappingModes.Normal;
                view.Label.overflowMode = TextOverflowModes.Ellipsis;
                var font = TopiaForgeFonts.For(TopiaForgeTextStyle.Label);
                if (font != null)
                {
                    view.Label.font = font;
                }

                view.Root = root;
            }

            view.Content = pending;

            // Views are pooled, so each presentation says whether its text may hold markup.
            view.Label!.richText = !pending.Plain;
            view.Label.parseCtrlCharacters = !pending.Plain;
            view.Label.text = pending.Text;

            ApplyAppearance(view, ViewerSettings(view));
        }

        private static void DismissView(int index)
        {
            var view = Views[index];
            ReleaseCapture(view);
            if (view.Root == null)
            {
                Stack.Release(index);
                return;
            }

            // The exit slides from where a toast rests, so a view's exits never add up.
            var owner = generation;
            TopiaForgeMotion.ToastOut(view.Root, view.RestingX, () =>
            {
                if (owner != generation)
                {
                    return;
                }

                Stack.Release(index);
                if (view.Root != null)
                {
                    view.Root.Go.SetActive(false);
                    view.Content = default;
                }

                Pump();
            }, view.Content.Viewer, view.Scale);
        }

        private static void EnsureLayer()
        {
            if (layer != null)
            {
                return;
            }

            appliedThemeVersion = TopiaForgeTheme.Version;
            layer = TopiaForgeToastHost.Instance.Layer(
                "toasts",
                TopiaForgeLayerBand.Toast,
                TopiaForgeScheme.Hud,
                interactive: false,
                persistent: true);
        }

        private static UImage CreateImage(Transform parent, string name, Sprite sprite)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<UImage>();
            image.sprite = sprite;
            image.type = UImage.Type.Sliced;
            image.raycastTarget = false;
            TopiaForgeAnchors.Stretch((RectTransform)go.transform);
            return image;
        }
    }

    /// <summary>Minimal process-wide host backing the shared toast layer.</summary>
    internal static class TopiaForgeToastHost
    {
        private static UiHost? instance;

        public static UiHost Instance => instance ??= TopiaForgeUi.Create(new TopiaForgeUiOptions
        {
            OwnerId = "io.github.furroxide.topiaforge.ui.toasts"
        });

        public static void Reset()
        {
            var host = instance;
            instance = null;
            host?.Dispose();
        }
    }
}
