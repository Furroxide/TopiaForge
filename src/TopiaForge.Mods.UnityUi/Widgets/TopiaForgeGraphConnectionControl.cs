using System;
using TMPro;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    internal sealed class TopiaForgeGraphConnectionControl
    {
        private const float LabelInset = 12f;
        private readonly TopiaForgeRoundedImage fill;
        private readonly TextMeshProUGUI label;
        private readonly TopiaForgeControlFocus? focus;
        private readonly TopiaForgeControlCues cues;

        internal TopiaForgeGraphConnectionControl(TopiaForgeWidget owner, Transform parent, string name, Action action,
            Func<MoveDirection, bool> move)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            fill = TopiaForgeGraphNodeView.AddControlFill(go);
            Button = go.AddComponent<TopiaForgeGraphConnectionButton>();
            Button.targetGraphic = fill;
            Button.onClick.AddListener(() => action());
            Button.Move = move;
            label = TopiaForgeGraphNodeView.CreateLabel(go.transform, "Label", TopiaForgeTextStyle.Label, TextAlignmentOptions.Left);
            label.richText = false;
            label.parseCtrlCharacters = false;
            TopiaForgeAnchors.Stretch(label.rectTransform, LabelInset, 0f, LabelInset, 0f);
            focus = TopiaForgeControlFocus.Install(owner, Button);
            cues = TopiaForgeControlCues.Install(Button, Rect);
        }

        internal TopiaForgeGraphConnectionButton Button { get; }
        internal RectTransform Rect => fill.rectTransform;
        internal string DisplayLabel => label.text;
        internal string SemanticLabel { get; private set; } = string.Empty;
        internal float PreferredWidth => label.GetPreferredValues().x + 2 * LabelInset;

        internal void SetText(string text, string? semanticLabel = null)
        {
            SemanticLabel = semanticLabel ?? text;
            if (!string.Equals(label.text, text, StringComparison.Ordinal)) label.text = text;
        }

        internal void Paint(TopiaForgeResolvedTheme theme, float scale, UiPaintRole backdrop, bool enabled, bool selected = false)
        {
            var role = !enabled ? UiPaintRole.Sunken : selected ? UiPaintRole.Selected : backdrop;
            Button.interactable = enabled;
            focus?.SetBackdrop(theme, backdrop);
            fill.SetShape(theme.Descriptor?.Shape.Control);
            TopiaForgeTypography.ApplyControlText(label, theme, TopiaForgeTextStyle.Label, scale);
            if (theme.FieldPaint(role, UiTone.Neutral, enabled) is { } paint)
            {
                Button.colors = paint.ResolvedStates(Button.colors);
                fill.color = paint.Fill;
                label.color = paint.Content;
            }
            else
            {
                fill.color = !enabled ? theme.SurfaceSunken : selected ? theme.SelectedTint : theme.SurfaceAlt;
                label.color = !enabled ? theme.TextFaint : theme.Text;
            }

            var authoredSelectionCue = false;
            if (enabled && selected)
            {
                var rest = theme.FieldPaint(backdrop, UiTone.Neutral, enabled: true)?.Fill ?? theme.SurfaceAlt;
                authoredSelectionCue = rest.Equals(fill.color);
            }
            cues.Apply(fill.color, label.color, theme.Descriptor?.Shape.Control ?? UiThemePackParser.Default.Shape.Control,
                theme.HighContrast, authoredSelectionCue);
        }
    }

    internal sealed class TopiaForgeGraphConnectionButton : Button
    {
        internal Func<MoveDirection, bool>? Move { get; set; }
        internal Action? Selected { get; set; }

        public override void OnSelect(BaseEventData eventData)
        {
            base.OnSelect(eventData);
            Selected?.Invoke();
        }

        public override void OnMove(AxisEventData eventData)
        {
            if (Move?.Invoke(eventData.moveDir) == true)
                eventData.Use();
            else
                base.OnMove(eventData);
        }
    }
}
