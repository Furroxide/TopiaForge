using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UImage = UnityEngine.UI.Image;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>
    /// Rebindable key field: click → "PRESS A KEY…" → next key from either input
    /// backend is captured (ESC cancels). Pairs with QwHotkeys.Rebind.
    /// </summary>
    public sealed class QwKeybindField : QwWidget, IQwThemeAware
    {
        private readonly UImage fill;
        private readonly UImage ring;
        private readonly TextMeshProUGUI keyLabel;
        private readonly TextMeshProUGUI nameLabel;
        private readonly Action<QwKey> onChanged;
        private QwKey key;
        private bool capturing;

        internal QwKeybindField(QwContainer parent, string label, QwKey initial, Action<QwKey> onChangedHandler)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject("Keybind"))
        {
            key = initial;
            onChanged = onChangedHandler;
            QwLayout.ApplyRow(Go, QwGap.Sm, QwGap.None);
            this.FixedHeight(QwTokens.ControlSmHeight);

            var nameGo = new GameObject("Name", typeof(RectTransform));
            nameGo.transform.SetParent(Go.transform, false);
            nameLabel = QwTmp.Create(nameGo);
            nameLabel.fontSize = QwTokens.BodySize;
            nameLabel.alignment = TextAlignmentOptions.Left;
            nameLabel.textWrappingMode = TextWrappingModes.NoWrap;
            var bodyFont = QwFonts.For(QwTextStyle.Body);
            if (bodyFont != null)
            {
                nameLabel.font = bodyFont;
            }

            nameLabel.text = label;
            var nameLayout = nameGo.AddComponent<LayoutElement>();
            nameLayout.flexibleWidth = 1f;

            var chipGo = new GameObject("Chip", typeof(RectTransform));
            chipGo.transform.SetParent(Go.transform, false);
            var chipLayout = chipGo.AddComponent<LayoutElement>();
            chipLayout.minWidth = 130f;
            chipLayout.preferredWidth = 130f;
            chipLayout.minHeight = 26f;
            fill = chipGo.AddComponent<UImage>();
            fill.sprite = QwSprites.Fill(QwRadius.Chip);
            fill.type = UImage.Type.Sliced;

            var ringGo = new GameObject("Ring", typeof(RectTransform));
            ringGo.transform.SetParent(chipGo.transform, false);
            ring = ringGo.AddComponent<UImage>();
            ring.sprite = QwSprites.Ring(QwRadius.Chip, QwTokens.BorderStandard);
            ring.type = UImage.Type.Sliced;
            ring.raycastTarget = false;
            QwAnchors.Stretch((RectTransform)ringGo.transform);

            var keyGo = new GameObject("Key", typeof(RectTransform));
            keyGo.transform.SetParent(chipGo.transform, false);
            keyLabel = QwTmp.Create(keyGo);
            keyLabel.fontSize = QwTokens.CaptionSize;
            keyLabel.alignment = TextAlignmentOptions.Center;
            keyLabel.textWrappingMode = TextWrappingModes.NoWrap;
            var labelFont = QwFonts.For(QwTextStyle.Label);
            if (labelFont != null)
            {
                keyLabel.font = labelFont;
            }

            if (QwFonts.UseFauxBold)
            {
                keyLabel.fontStyle = FontStyles.Bold;
            }

            QwAnchors.Stretch((RectTransform)keyGo.transform, 6f, 2f, 6f, 2f);

            var button = chipGo.AddComponent<Button>();
            button.targetGraphic = fill;
            button.onClick.AddListener(BeginCapture);

            var capture = Go.AddComponent<QwKeybindCapture>();
            capture.Field = this;
            capture.enabled = false;

            Repaint();
        }

        public QwKey Key => key;

        internal bool Capturing => capturing;

        public void SetKey(QwKey next)
        {
            if (key == next)
            {
                return;
            }

            key = next;
            Repaint();
        }

        public void ApplyTheme(QwResolvedTheme theme)
        {
            Repaint();
        }

        private void BeginCapture()
        {
            if (capturing)
            {
                return;
            }

            capturing = true;
            Go.GetComponent<QwKeybindCapture>().enabled = true;
            Repaint();
        }

        internal void CompleteCapture(QwKey captured)
        {
            capturing = false;
            Go.GetComponent<QwKeybindCapture>().enabled = false;
            if (captured != QwKey.None)
            {
                key = captured;
                onChanged(captured);
            }

            Repaint();
        }

        private void Repaint()
        {
            var theme = Theme;
            fill.color = capturing ? theme.SelectedTint : theme.SurfaceSunken;
            ring.color = capturing ? theme.FocusRing : theme.Outline;
            keyLabel.color = capturing ? theme.Text : theme.TextMuted;
            keyLabel.text = capturing ? "PRESS A KEY…" : KeyName(key);
            nameLabel.color = theme.Text;
        }

        private static string KeyName(QwKey value)
        {
            return value == QwKey.None ? "UNBOUND" : value.ToString().ToUpperInvariant();
        }
    }

    /// <summary>Enabled only while capturing; polls for the next pressed key.</summary>
    internal sealed class QwKeybindCapture : MonoBehaviour
    {
        public QwKeybindField? Field;

        private void Update()
        {
            if (Field == null || !Field.Capturing)
            {
                enabled = false;
                return;
            }

            if (QwInput.EscapePressedThisFrame())
            {
                Field.CompleteCapture(QwKey.None);
                return;
            }

            var pressed = QwHotkeys.CapturePressedKey();
            if (pressed != QwKey.None)
            {
                Field.CompleteCapture(pressed);
            }
        }
    }
}
