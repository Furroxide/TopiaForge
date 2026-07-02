using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UImage = UnityEngine.UI.Image;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>
    /// Toast notifications: queued, max 4 visible, pooled views stacked top-right on a
    /// shared toast-band canvas, slide+fade motion, auto-dismiss. Dark chip styling so
    /// toasts read over both gameplay and paper surfaces.
    /// </summary>
    public static class QwToasts
    {
        private const int MaxVisible = 4;
        private const float DefaultDuration = 3.5f;
        private const float ToastWidth = 340f;
        private const float ToastHeight = 44f;
        private const float StackGap = 8f;

        private sealed class ToastView
        {
            public QwContainer? Root;
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
            public QwTone Tone;
            public float Duration;
        }

        private static readonly List<ToastView> Views = new List<ToastView>();
        private static readonly Queue<Pending> Queue = new Queue<Pending>();
        private static QwContainer? layer;

        /// <summary>Shows a toast (queues when 4 are already visible).</summary>
        public static void Show(string text, QwTone tone = QwTone.Neutral, float duration = DefaultDuration)
        {
            QwRuntime.Ensure();
            Queue.Enqueue(new Pending { Text = text, Tone = tone, Duration = duration });
            Pump();
        }

        public static void Success(string text)
        {
            Show(text, QwTone.Success);
        }

        public static void Error(string text)
        {
            Show(text, QwTone.Danger, 5f);
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

                view.Fill = CreateImage(root.Go.transform, "Fill", QwSprites.Fill(QwRadius.Control));
                view.Ring = CreateImage(root.Go.transform, "Ring", QwSprites.Ring(QwRadius.Control, QwTokens.BorderStandard));

                var labelGo = new GameObject("Label", typeof(RectTransform));
                labelGo.transform.SetParent(root.Go.transform, false);
                view.Label = labelGo.AddComponent<TextMeshProUGUI>();
                view.Label.raycastTarget = false;
                view.Label.fontSize = QwTokens.LabelSize;
                view.Label.alignment = TextAlignmentOptions.Left;
                view.Label.textWrappingMode = TextWrappingModes.NoWrap;
                view.Label.overflowMode = TextOverflowModes.Ellipsis;
                var font = QwFonts.For(QwTextStyle.Label);
                if (font != null)
                {
                    view.Label.font = font;
                }

                QwAnchors.Stretch((RectTransform)labelGo.transform, 14f, 4f, 14f, 4f);
                view.Root = root;
            }

            view.Active = true;
            view.Leaving = false;
            view.RemainingSeconds = pending.Duration;
            view.Root.Go.SetActive(true);
            view.Label!.text = pending.Text;

            // Dark chip styling with a tone-colored ring; readable over anything.
            var hudTheme = new QwResolvedTheme(QwScheme.Hud, null);
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
            QwMotion.ToastOut(view.Root, restingX, () =>
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

                var targetY = -QwTokens.SafeMargin - (slot * (ToastHeight + StackGap));
                var restingX = -QwTokens.SafeMargin;
                var position = view.Root.Rect.anchoredPosition;
                if (Mathf.Approximately(position.x, 0f) && Mathf.Approximately(position.y, 0f))
                {
                    // Fresh presentation: place and slide in.
                    view.Root.Rect.anchoredPosition = new Vector2(restingX, targetY);
                    QwMotion.ToastIn(view.Root, restingX);
                }
                else if (!Mathf.Approximately(position.y, targetY))
                {
                    QwTween.MoveY(view.Root, position.y, targetY, QwTokens.DurationFast);
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

            var root = QwLayers.CreateCanvas("QuantumWorksToasts", QwLayerBand.Toast, interactive: false, persistent: true);
            // A standalone host-less container: toasts are process-wide.
            layer = new QwContainer(QwToastHost.Instance, QwScheme.Hud, root);
        }

        private static UImage CreateImage(Transform parent, string name, Sprite sprite)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<UImage>();
            image.sprite = sprite;
            image.type = UImage.Type.Sliced;
            image.raycastTarget = false;
            QwAnchors.Stretch((RectTransform)go.transform);
            return image;
        }
    }

    /// <summary>Minimal process-wide host backing the shared toast layer.</summary>
    internal static class QwToastHost
    {
        private static UiHost? instance;

        public static UiHost Instance => instance ??= QwUi.Create(new QwUiOptions { OwnerId = "quantumworks.toasts" });
    }
}
