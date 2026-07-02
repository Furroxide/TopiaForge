using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UImage = UnityEngine.UI.Image;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>
    /// Hover tooltips: one shared panel per process (dark panel + orange border +
    /// radius 14, the launcher tooltipTheme) on a top-band canvas, shown after a 450ms
    /// hover and clamped to the screen. Attach via the .Tooltip("text") chainer.
    /// </summary>
    public static class QwTooltip
    {
        internal const float HoverDelaySeconds = 0.45f;

        private static GameObject? panelRoot;
        private static RectTransform? panelRect;
        private static UImage? fill;
        private static UImage? ring;
        private static TextMeshProUGUI? label;

        /// <summary>Adds a hover tooltip to any widget.</summary>
        public static T Tooltip<T>(this T widget, string text) where T : QwWidget
        {
            var behaviour = widget.Go.GetComponent<QwTooltipTrigger>() ?? widget.Go.AddComponent<QwTooltipTrigger>();
            behaviour.Text = text;
            behaviour.enabled = false;

            // The trigger needs a raycast target to receive hover events.
            if (widget.Go.GetComponent<UnityEngine.UI.Graphic>() == null)
            {
                var catcher = widget.Go.AddComponent<UImage>();
                catcher.color = Color.clear;
                catcher.raycastTarget = true;
            }

            return widget;
        }

        internal static void Show(string text, Vector2 screenPosition)
        {
            EnsurePanel();
            if (panelRoot == null || label == null || panelRect == null)
            {
                return;
            }

            label.text = text;
            panelRoot.SetActive(true);

            var size = label.GetPreferredValues(text, 360f, 0f);
            panelRect.sizeDelta = new Vector2(Mathf.Min(360f, size.x + 24f), size.y + 16f);
            Position(screenPosition);
        }

        internal static void Move(Vector2 screenPosition)
        {
            if (panelRoot != null && panelRoot.activeSelf)
            {
                Position(screenPosition);
            }
        }

        internal static void Hide()
        {
            if (panelRoot != null)
            {
                panelRoot.SetActive(false);
            }
        }

        private static void Position(Vector2 screenPosition)
        {
            if (panelRect == null)
            {
                return;
            }

            var canvas = panelRect.GetComponentInParent<Canvas>();
            var scale = canvas != null ? canvas.scaleFactor : 1f;
            var size = panelRect.sizeDelta * scale;
            var offset = new Vector2(14f, -22f) * scale;
            var position = screenPosition + offset;

            position.x = Mathf.Clamp(position.x, 4f, Screen.width - size.x - 4f);
            position.y = Mathf.Clamp(position.y, size.y + 4f, Screen.height - 4f);
            panelRect.position = new Vector3(position.x, position.y, 0f);
        }

        private static void EnsurePanel()
        {
            if (panelRoot != null)
            {
                return;
            }

            var canvasRoot = QwLayers.CreateCanvas("QuantumWorksTooltips", QwLayerBand.Toast, interactive: false, persistent: true);

            panelRoot = new GameObject("Tooltip", typeof(RectTransform));
            panelRoot.transform.SetParent(canvasRoot.transform, false);
            panelRect = (RectTransform)panelRoot.transform;
            panelRect.pivot = new Vector2(0f, 1f);

            fill = panelRoot.AddComponent<UImage>();
            fill.sprite = QwSprites.Fill(QwRadius.Tip);
            fill.type = UImage.Type.Sliced;
            fill.raycastTarget = false;
            fill.color = new Color(0x2D / 255f, 0x37 / 255f, 0x48 / 255f, 0.98f); // brand DarkPanel

            var ringGo = new GameObject("Ring", typeof(RectTransform));
            ringGo.transform.SetParent(panelRoot.transform, false);
            ring = ringGo.AddComponent<UImage>();
            ring.sprite = QwSprites.Ring(QwRadius.Tip, QwTokens.BorderStandard);
            ring.type = UImage.Type.Sliced;
            ring.raycastTarget = false;
            ring.color = new Color(0xFF / 255f, 0x7A / 255f, 0x11 / 255f, 1f); // brand Launch
            QwAnchors.Stretch((RectTransform)ringGo.transform);

            var labelGo = new GameObject("Label", typeof(RectTransform));
            labelGo.transform.SetParent(panelRoot.transform, false);
            label = labelGo.AddComponent<TextMeshProUGUI>();
            label.raycastTarget = false;
            label.fontSize = QwTokens.CaptionSize;
            label.alignment = TextAlignmentOptions.Left;
            label.textWrappingMode = TextWrappingModes.Normal;
            label.color = Color.white;
            var font = QwFonts.For(QwTextStyle.Body);
            if (font != null)
            {
                label.font = font;
            }

            QwAnchors.Stretch((RectTransform)labelGo.transform, 12f, 8f, 12f, 8f);
            panelRoot.SetActive(false);
        }
    }

    /// <summary>
    /// Per-widget hover trigger. The component stays disabled (no Update cost) until
    /// the pointer enters; it disables itself again on exit.
    /// </summary>
    internal sealed class QwTooltipTrigger : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public string Text = string.Empty;

        private float hoverStart;
        private bool shown;

        public void OnPointerEnter(PointerEventData eventData)
        {
            hoverStart = Time.unscaledTime;
            shown = false;
            enabled = true;
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            enabled = false;
            shown = false;
            QwTooltip.Hide();
        }

        private void Update()
        {
            var mouse = QwInputPointer.Position();
            if (!shown)
            {
                if (Time.unscaledTime - hoverStart >= QwTooltip.HoverDelaySeconds)
                {
                    shown = true;
                    QwTooltip.Show(Text, mouse);
                }
            }
            else
            {
                QwTooltip.Move(mouse);
            }
        }

        private void OnDisable()
        {
            if (shown)
            {
                shown = false;
                QwTooltip.Hide();
            }
        }
    }

    /// <summary>Dual-backend mouse position (legacy Input vs InputSystem).</summary>
    internal static class QwInputPointer
    {
        public static Vector2 Position()
        {
            if (QwInput.LegacyAvailable)
            {
                return Input.mousePosition;
            }

            var mouse = UnityEngine.InputSystem.Mouse.current;
            return mouse != null ? mouse.position.ReadValue() : Vector2.zero;
        }
    }
}
