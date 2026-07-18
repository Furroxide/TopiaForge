using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UImage = UnityEngine.UI.Image;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Rebindable key field: click → "PRESS A KEY…" → next key from either input
    /// backend is captured (ESC cancels). Pairs with TopiaForgeHotkeys.Rebind.
    /// </summary>
    public sealed class TopiaForgeKeybindField : TopiaForgeWidget, ITopiaForgeThemeAware
    {
        private readonly UImage fill;
        private readonly UImage ring;
        private readonly TextMeshProUGUI keyLabel;
        private readonly TextMeshProUGUI nameLabel;
        private readonly Action<TopiaForgeKey> onChanged;
        private TopiaForgeKey key;
        private bool capturing;

        internal TopiaForgeKeybindField(TopiaForgeContainer parent, string label, TopiaForgeKey initial, Action<TopiaForgeKey> onChangedHandler)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject("Keybind"))
        {
            key = initial;
            onChanged = onChangedHandler;
            TopiaForgeLayout.ApplyRow(Go, TopiaForgeGap.Sm, TopiaForgeGap.None);
            this.FixedHeight(TopiaForgeTokens.ControlSmHeight);

            var nameGo = new GameObject("Name", typeof(RectTransform));
            nameGo.transform.SetParent(Go.transform, false);
            nameLabel = TopiaForgeTmp.Create(nameGo);
            nameLabel.fontSize = TopiaForgeTokens.BodySize;
            nameLabel.alignment = TextAlignmentOptions.Left;
            nameLabel.textWrappingMode = TextWrappingModes.NoWrap;
            var bodyFont = TopiaForgeFonts.For(TopiaForgeTextStyle.Body);
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
            fill.sprite = TopiaForgeSprites.Fill(TopiaForgeRadius.Chip);
            fill.type = UImage.Type.Sliced;

            var ringGo = new GameObject("Ring", typeof(RectTransform));
            ringGo.transform.SetParent(chipGo.transform, false);
            ring = ringGo.AddComponent<UImage>();
            ring.sprite = TopiaForgeSprites.Ring(TopiaForgeRadius.Chip, TopiaForgeTokens.BorderStandard);
            ring.type = UImage.Type.Sliced;
            ring.raycastTarget = false;
            TopiaForgeAnchors.Stretch((RectTransform)ringGo.transform);

            var keyGo = new GameObject("Key", typeof(RectTransform));
            keyGo.transform.SetParent(chipGo.transform, false);
            keyLabel = TopiaForgeTmp.Create(keyGo);
            keyLabel.fontSize = TopiaForgeTokens.CaptionSize;
            keyLabel.alignment = TextAlignmentOptions.Center;
            keyLabel.textWrappingMode = TextWrappingModes.NoWrap;
            var labelFont = TopiaForgeFonts.For(TopiaForgeTextStyle.Label);
            if (labelFont != null)
            {
                keyLabel.font = labelFont;
            }

            if (TopiaForgeFonts.UseFauxBold)
            {
                keyLabel.fontStyle = FontStyles.Bold;
            }

            TopiaForgeAnchors.Stretch((RectTransform)keyGo.transform, 6f, 2f, 6f, 2f);

            var button = chipGo.AddComponent<Button>();
            button.targetGraphic = fill;
            button.onClick.AddListener(BeginCapture);

            var capture = Go.AddComponent<TopiaForgeKeybindCapture>();
            capture.Field = this;
            capture.enabled = false;

            Repaint();
        }

        /// <summary>Gets key.</summary>
        public TopiaForgeKey Key => key;

        internal bool Capturing => capturing;

        /// <summary>Sets the key.</summary>
        public void SetKey(TopiaForgeKey next)
        {
            if (key == next)
            {
                return;
            }

            key = next;
            Repaint();
        }

        /// <summary>Applies the resolved theme to this UI element.</summary>
        public void ApplyTheme(TopiaForgeResolvedTheme theme)
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
            Go.GetComponent<TopiaForgeKeybindCapture>().enabled = true;
            Repaint();
        }

        internal void CompleteCapture(TopiaForgeKey captured)
        {
            capturing = false;
            Go.GetComponent<TopiaForgeKeybindCapture>().enabled = false;
            if (captured != TopiaForgeKey.None)
            {
                key = captured;
                TopiaForgeCallbacks.Invoke(onChanged, captured, "Keybind change");
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

        private static string KeyName(TopiaForgeKey value)
        {
            return value == TopiaForgeKey.None ? "UNBOUND" : value.ToString().ToUpperInvariant();
        }
    }

    /// <summary>Enabled only while capturing; polls for the next pressed key.</summary>
    internal sealed class TopiaForgeKeybindCapture : MonoBehaviour
    {
        public TopiaForgeKeybindField? Field;

        private void Update()
        {
            if (Field == null || !Field.Capturing)
            {
                enabled = false;
                return;
            }

            if (TopiaForgeInput.EscapePressedThisFrame())
            {
                Field.CompleteCapture(TopiaForgeKey.None);
                return;
            }

            var pressed = TopiaForgeHotkeys.CapturePressedKey();
            if (pressed != TopiaForgeKey.None)
            {
                Field.CompleteCapture(pressed);
            }
        }
    }
}
