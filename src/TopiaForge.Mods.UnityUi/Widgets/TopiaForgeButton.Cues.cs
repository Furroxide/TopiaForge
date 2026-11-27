using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    public sealed partial class TopiaForgeButton
    {
        private Image? selectionCue;
        private TopiaForgeRoundedImage? pressCue;
        private Color cueInk;
        private float cueCorner;
        private float cueStroke;

        private void ConfigureCues(TopiaForgeResolvedTheme theme, float? radius)
        {
            var ghost = style == TopiaForgeButtonStyle.Ghost;
            Color32 baseFill = ghost ? theme.Backplate(backdrop) : fill.color;
            var colors = button.colors;
            var tints = ghost ? new UiThemeStateTints(1f, 1f, 1f)
                : new UiThemeStateTints(colors.highlightedColor.r, colors.selectedColor.r, colors.pressedColor.r);
            var cues = UiControlPaint.ButtonCues(new UiThemeColor(baseFill.r, baseFill.g, baseFill.b), tints);
            cueInk = label != null ? label.color : iconImage!.color;
            cueCorner = Mathf.Max(0f, (radius ?? UiThemePackParser.Default.Shape.Control) - UiControlPaint.ButtonPressInset);
            cueStroke = UiControlPaint.ButtonPressStroke(theme.HighContrast);
            if (selectionCue != null) selectionCue.color = cueInk;
            if (pressCue != null) ApplyPressPaint(pressCue);
            button.ConfigureCues(cues);
        }

        private void ShowCues(UiButtonCue cues)
        {
            if ((cues & UiButtonCue.Selection) != 0 && selectionCue == null)
            {
                var mark = new GameObject(TopiaForgeCueButton.SelectionName, typeof(RectTransform));
                mark.transform.SetParent(body, false);
                mark.transform.SetSiblingIndex(fill.transform.GetSiblingIndex() + 1);
                selectionCue = mark.AddComponent<Image>();
                selectionCue.raycastTarget = false;
                selectionCue.color = cueInk;
                var rect = selectionCue.rectTransform;
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = new Vector2(0f, 1f);
                rect.offsetMin = new Vector2(UiControlPaint.ButtonSelectionLeft, UiControlPaint.ButtonSelectionTop);
                rect.offsetMax = new Vector2(UiControlPaint.ButtonSelectionLeft + UiControlPaint.ButtonSelectionWidth,
                    -UiControlPaint.ButtonSelectionTop);
            }
            if ((cues & UiButtonCue.Press) != 0 && pressCue == null)
            {
                var mark = new GameObject(TopiaForgeCueButton.PressName, typeof(RectTransform));
                mark.transform.SetParent(body, false);
                mark.transform.SetSiblingIndex(fill.transform.GetSiblingIndex() + 1);
                pressCue = mark.AddComponent<TopiaForgeRoundedImage>();
                pressCue.raycastTarget = false;
                var inset = UiControlPaint.ButtonPressInset;
                TopiaForgeAnchors.Stretch(pressCue.rectTransform, inset, inset, inset, inset);
                ApplyPressPaint(pressCue);
            }
            if (selectionCue != null) selectionCue.gameObject.SetActive((cues & UiButtonCue.Selection) != 0);
            if (pressCue != null) pressCue.gameObject.SetActive((cues & UiButtonCue.Press) != 0);
        }

        private void ApplyPressPaint(TopiaForgeRoundedImage mark)
        {
            mark.color = cueInk;
            mark.SetShape(cueCorner, cueStroke);
        }
    }
}
