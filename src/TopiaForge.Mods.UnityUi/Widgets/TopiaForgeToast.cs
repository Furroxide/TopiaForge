using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UImage = UnityEngine.UI.Image;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Toast notifications: queued, max 4 visible, pooled views stacked top-right on a
    /// shared toast-band canvas, slide+fade motion, auto-dismiss. Dark chip styling so
    /// toasts read over both gameplay and paper surfaces. A longer text wraps to three lines
    /// and its toast grows to hold them. The bookkeeping, which view shows which toast and
    /// where it rests, is <see cref="TopiaForgeToastStack{T}"/>: a view that takes a new
    /// toast is always placed and brought in afresh, whatever it showed before.
    /// </summary>
    public static class TopiaForgeToasts
    {
        private const int MaxVisible = 4;
        private const int MaxQueued = 64;
        private const int MaxTextChars = 512;
        private const float DefaultDuration = 3.5f;
        private const float ToastWidth = 340f;
        private const float ToastHeight = 44f;
        private const float TextInsetX = 14f;
        private const float TextInsetY = 12f;

        // The most text a toast shows: three lines, measured in its own face and size.
        private const string ThreeLines = "A\nA\nA";
        private const float StackGap = 8f;

        private sealed class ToastView
        {
            public TopiaForgeContainer? Root;
            public UImage? Fill;
            public UImage? Ring;
            public TextMeshProUGUI? Label;
        }

        private struct Pending
        {
            public string Text;
            public TopiaForgeTone Tone;
            public bool Plain;
        }

        private static readonly TopiaForgeToastStack<Pending> Stack = new TopiaForgeToastStack<Pending>(MaxVisible, MaxQueued, StackGap);

        // Index-aligned with the stack's views: the Unity half of each pooled view.
        private static readonly List<ToastView> Views = new List<ToastView>();
        private static readonly List<int> Expired = new List<int>(MaxVisible);
        private static TopiaForgeContainer? layer;
        private static bool queueOverflowLogged;

        // Moves on with every reset, so an exit that finishes after one frees nothing in the views that came after it.
        private static int generation;

        /// <summary>Shows a toast (queues when 4 are already visible).</summary>
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

        private static void Enqueue(string text, TopiaForgeTone tone, float duration, bool plain)
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
            Stack.Expire(unscaledDelta, Expired);
            if (Expired.Count == 0)
            {
                return;
            }

            for (var index = 0; index < Expired.Count; index++)
            {
                DismissView(Expired[index]);
            }

            Pump();
        }

        /// <summary>Releases the process-wide toast host, pending work, and pooled views.</summary>
        internal static void Reset()
        {
            try
            {
                TopiaForgeToastHost.Reset();
            }
            finally
            {
                layer = null;
                Stack.Clear();
                Views.Clear();
                queueOverflowLogged = false;
                generation++;
            }
        }

        private static void Pump()
        {
            while (Stack.TryShowNext(out var index, out var pending))
            {
                while (Views.Count <= index)
                {
                    Views.Add(new ToastView());
                }

                Present(index, Views[index], pending);
            }

            Restack();
        }

        private static void Present(int index, ToastView view, Pending pending)
        {
            EnsureLayer();
            if (view.Root == null)
            {
                var root = layer!.Stack("Toast");
                root.Rect.anchorMin = new Vector2(1f, 1f);
                root.Rect.anchorMax = new Vector2(1f, 1f);
                root.Rect.pivot = new Vector2(1f, 1f);
                root.Rect.sizeDelta = new Vector2(ToastWidth, ToastHeight);

                view.Fill = CreateImage(root.Go.transform, "Fill", TopiaForgeSprites.Fill(TopiaForgeRadius.Control));
                view.Ring = CreateImage(root.Go.transform, "Ring", TopiaForgeSprites.Ring(TopiaForgeRadius.Control, TopiaForgeTokens.BorderStandard));

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

                TopiaForgeAnchors.Stretch((RectTransform)labelGo.transform, TextInsetX, TextInsetY, TextInsetX, TextInsetY);
                view.Root = root;
            }

            view.Root.Go.SetActive(true);

            // Views are pooled, so each presentation says whether its text may hold markup.
            view.Label!.richText = !pending.Plain;
            view.Label.parseCtrlCharacters = !pending.Plain;
            view.Label.text = pending.Text;

            // A result that needs more than one line wraps, up to three, and its toast grows to hold them, so a second sentence
            // saying what to do next is read, not cut off. The text box holds three lines at most, so a longer result
            // overflows it and ends its third line in an ellipsis.
            var width = ToastWidth - (2f * TextInsetX);
            var textHeight = Mathf.Min(view.Label.GetPreferredValues(pending.Text, width, 0f).y, view.Label.GetPreferredValues(ThreeLines, width, 0f).y);
            var height = Mathf.Max(ToastHeight, textHeight + (2f * TextInsetY));
            view.Root.Rect.sizeDelta = new Vector2(ToastWidth, height);
            Stack.SetHeight(index, height);

            // Dark chip styling with a tone-colored ring; readable over anything.
            var hudTheme = new TopiaForgeResolvedTheme(TopiaForgeScheme.Hud, null);
            view.Fill!.color = hudTheme.SurfaceAlt;
            view.Ring!.color = hudTheme.ToneColor(pending.Tone);
            view.Label.color = hudTheme.Text;
        }

        private static void DismissView(int index)
        {
            var view = Views[index];
            if (view.Root == null)
            {
                Stack.Release(index);
                return;
            }

            // The exit slides from where a toast rests, so a view's exits never add up.
            var owner = generation;
            TopiaForgeMotion.ToastOut(view.Root, -TopiaForgeTokens.SafeMargin, () =>
            {
                if (owner != generation)
                {
                    return;
                }

                Stack.Release(index);
                if (view.Root != null)
                {
                    view.Root.Go.SetActive(false);
                }

                Pump();
            });
        }

        private static void Restack()
        {
            var restingX = -TopiaForgeTokens.SafeMargin;
            for (var index = 0; index < Views.Count; index++)
            {
                var view = Views[index];
                if (view.Root == null || !Stack.TryPlace(index, out var top, out var fresh))
                {
                    continue;
                }

                var targetY = -TopiaForgeTokens.SafeMargin - top;
                if (fresh)
                {
                    // A new toast, in a new view or in one whose last exit left it faded out and slid aside: place it and
                    // bring it in.
                    view.Root.Rect.anchoredPosition = new Vector2(restingX, targetY);
                    TopiaForgeMotion.ToastIn(view.Root, restingX);
                }
                else
                {
                    var position = view.Root.Rect.anchoredPosition;
                    if (!Mathf.Approximately(position.y, targetY))
                    {
                        TopiaForgeTween.MoveY(view.Root, position.y, targetY, TopiaForgeTokens.DurationFast);
                    }
                }
            }
        }

        private static void EnsureLayer()
        {
            if (layer != null)
            {
                return;
            }

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
