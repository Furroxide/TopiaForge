using System;
using TMPro;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService
    {
        private static Action<UiPaintRole> RenderValidationMessage(TopiaForgeContainer parent, string message, UiPaintRole backdrop)
        {
            var label = parent.Label(message, TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Danger);
            label.Go.name = "Validation message";
            var text = label.Go.GetComponent<TMP_Text>();
            text.margin = new Vector4(24f, 0f, 0f, 0f);
            var glyph = new GameObject("Validation glyph", typeof(RectTransform)).AddComponent<Image>();
            glyph.transform.SetParent(label.Rect, false);
            glyph.sprite = TopiaForgeSprites.Glyph(UiGlyph.Warning);
            glyph.raycastTarget = false;
            TopiaForgeAnchors.Place(glyph.rectTransform, 0f, 0f, 18f, 18f);
            Apply(backdrop);
            return Apply;

            void Apply(UiPaintRole role)
            {
                label.SetBackdrop(role);
                glyph.color = label.ResolvedTheme.ForegroundOn(role, UiTone.Danger);
            }
        }
    }
}
