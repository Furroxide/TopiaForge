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
        private const float LabelInset = UiGraphConnectionLabelPages.HorizontalInset;
        private readonly TopiaForgeRoundedImage fill;
        private readonly TextMeshProUGUI label;
        private readonly TopiaForgeControlFocus? focus;
        private readonly TopiaForgeControlCues cues;
        private string? labelSource;
        private string? labelTarget;
        private UiLayoutRect labelBox;
        private UiGraphConnectionLabelPages labelPages;

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
            label.overflowMode = TextOverflowModes.Overflow;
            TopiaForgeAnchors.Stretch(label.rectTransform, LabelInset, 0f, LabelInset, 0f);
            focus = TopiaForgeControlFocus.Install(owner, Button);
            cues = TopiaForgeControlCues.Install(Button, Rect);
        }

        internal TopiaForgeGraphConnectionButton Button { get; }
        internal RectTransform Rect => fill.rectTransform;
        internal TextMeshProUGUI Label => label;
        internal string DisplayLabel => label.text;
        internal string SemanticLabel { get; private set; } = string.Empty;
        internal int LabelPages => labelPages.Count;

        internal void InvalidateLabel() => labelSource = null;

        internal void SetEdge(UiGraphCanvas graph, int index, UiLayoutRect box, int page)
        {
            var labels = UiGraphConnections.Labels(graph, graph.Edges[index]);
            if (labelSource != labels.Source || labelTarget != labels.Target ||
                labelBox.Width != box.Width || labelBox.Height != box.Height)
            {
                labelSource = labels.Source;
                labelTarget = labels.Target;
                labelBox = box;
                SemanticLabel = labels.Full;
                labelPages = UiGraphConnectionLabelPages.Measure(labels, SemanticLabel, box, MeasureText);
            }

            ShowLabelPage(page);
        }

        internal void SetRemoveText(string semantic, UiLayoutRect box)
        {
            SetText(UiGraphConnectionLabelPages.RemoveCaption(semantic, box, MeasureText), semantic);
        }

        internal void ShowLabelPage(int page)
        {
            SetText(labelPages.Display(page), SemanticLabel);
        }

        internal (double Width, double Height) MeasureText(string text)
        {
            var size = label.GetPreferredValues(text, float.PositiveInfinity, 0f);
            return (size.x, size.y);
        }

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
