using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UImage = UnityEngine.UI.Image;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Toast notifications: queued, max 4 visible, pooled views stacked top-right on a
    /// shared toast-band canvas, slide+fade motion, auto-dismiss. Dark chip styling so
    /// toasts read over both gameplay and paper surfaces.
    /// </summary>
    public static class TopiaForgeToasts
    {
        private const int MaxVisible = 4;
        private const int MaxQueued = 64;
        private const int MaxTextChars = 512;
        private const float DefaultDuration = 3.5f;
        private const float ToastWidth = 340f;
        private const float ToastHeight = 44f;
        private const float StackGap = 8f;

        private sealed class ToastView
        {
            public TopiaForgeContainer? Root;
            public UImage? Fill;
            public UImage? Ring;
            public TextMeshProUGUI? Label;
            public float RemainingSeconds;
            public bool Active;
            public bool Leaving;
        }

        private struct Pending
        {
            public string Text;
            public TopiaForgeTone Tone;
            public float Duration;
        }

        private static readonly List<ToastView> Views = new List<ToastView>();
        private static readonly Queue<Pending> Queue = new Queue<Pending>();
        private static TopiaForgeContainer? layer;
        private static bool queueOverflowLogged;

        /// <summary>Shows a toast (queues when 4 are already visible).</summary>
        public static void Show(string text, TopiaForgeTone tone = TopiaForgeTone.Neutral, float duration = DefaultDuration)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            TopiaForgeRuntime.Ensure();
            if (Queue.Count >= MaxQueued)
            {
                Queue.Dequeue();
                if (!queueOverflowLogged)
                {
                    queueOverflowLogged = true;
                    TopiaForgeLog.Warn("Toast queue reached " + MaxQueued + " entries; dropping the oldest queued toast.");
                }
            }

            Queue.Enqueue(new Pending
            {
                Text = text.Length > MaxTextChars ? text.Substring(0, MaxTextChars) : text,
                Tone = tone,
                Duration = Mathf.Clamp(duration, 0.25f, 30f),
            });
            Pump();
        }

        public static void Success(string text)
        {
            Show(text, TopiaForgeTone.Success);
        }

        public static void Error(string text)
        {
            Show(text, TopiaForgeTone.Danger, 5f);
        }

        internal static void Tick(float unscaledDelta)
        {
            var anyExpired = false;
            for (var index = 0; index < Views.Count; index++)
            {
                var view = Views[index];
                if (!view.Active || view.Leaving)
                {
                    continue;
                }

                view.RemainingSeconds -= unscaledDelta;
                if (view.RemainingSeconds <= 0f)
                {
                    DismissView(view);
                    anyExpired = true;
                }
            }

            if (anyExpired)
            {
                Pump();
            }
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
                Queue.Clear();
                Views.Clear();
                queueOverflowLogged = false;
            }
        }

        private static void Pump()
        {
            while (Queue.Count > 0 && ActiveCount() < MaxVisible)
            {
                var pending = Queue.Dequeue();
                var view = AcquireView();
                Present(view, pending);
            }

            Restack();
        }

        private static int ActiveCount()
        {
            var count = 0;
            for (var index = 0; index < Views.Count; index++)
            {
                if (Views[index].Active)
                {
                    count++;
                }
            }

            return count;
        }

        private static ToastView AcquireView()
        {
            for (var index = 0; index < Views.Count; index++)
            {
                if (!Views[index].Active)
                {
                    return Views[index];
                }
            }

            var view = new ToastView();
            Views.Add(view);
            return view;
        }

        private static void Present(ToastView view, Pending pending)
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
                view.Label.textWrappingMode = TextWrappingModes.NoWrap;
                view.Label.overflowMode = TextOverflowModes.Ellipsis;
                var font = TopiaForgeFonts.For(TopiaForgeTextStyle.Label);
                if (font != null)
                {
                    view.Label.font = font;
                }

                TopiaForgeAnchors.Stretch((RectTransform)labelGo.transform, 14f, 4f, 14f, 4f);
                view.Root = root;
            }

            view.Active = true;
            view.Leaving = false;
            view.RemainingSeconds = pending.Duration;
            view.Root.Go.SetActive(true);
            view.Label!.text = pending.Text;

            // Dark chip styling with a tone-colored ring; readable over anything.
            var hudTheme = new TopiaForgeResolvedTheme(TopiaForgeScheme.Hud, null);
            view.Fill!.color = hudTheme.SurfaceAlt;
            view.Ring!.color = hudTheme.ToneColor(pending.Tone);
            view.Label.color = hudTheme.Text;
        }

        private static void DismissView(ToastView view)
        {
            if (view.Root == null || view.Leaving)
            {
                return;
            }

            view.Leaving = true;
            var restingX = view.Root.Rect.anchoredPosition.x;
            TopiaForgeMotion.ToastOut(view.Root, restingX, () =>
            {
                view.Active = false;
                view.Leaving = false;
                if (view.Root != null)
                {
                    view.Root.Go.SetActive(false);
                }

                Pump();
            });
        }

        private static void Restack()
        {
            var slot = 0;
            for (var index = 0; index < Views.Count; index++)
            {
                var view = Views[index];
                if (!view.Active || view.Root == null || view.Leaving)
                {
                    continue;
                }

                var targetY = -TopiaForgeTokens.SafeMargin - (slot * (ToastHeight + StackGap));
                var restingX = -TopiaForgeTokens.SafeMargin;
                var position = view.Root.Rect.anchoredPosition;
                if (Mathf.Approximately(position.x, 0f) && Mathf.Approximately(position.y, 0f))
                {
                    // Fresh presentation: place and slide in.
                    view.Root.Rect.anchoredPosition = new Vector2(restingX, targetY);
                    TopiaForgeMotion.ToastIn(view.Root, restingX);
                }
                else if (!Mathf.Approximately(position.y, targetY))
                {
                    TopiaForgeTween.MoveY(view.Root, position.y, targetY, TopiaForgeTokens.DurationFast);
                }

                slot++;
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
