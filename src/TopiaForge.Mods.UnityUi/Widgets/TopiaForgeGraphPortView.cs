using TMPro;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    internal sealed class TopiaForgeGraphPortView
    {
        private readonly TopiaForgeGraphCanvas owner;
        private readonly GameObject go;
        private readonly TopiaForgeRoundedImage fill;
        private readonly Image indicator;
        private readonly TextMeshProUGUI label;
        private readonly Button button;
        private readonly TopiaForgeControlFocus? focus;
        private string nodeId = string.Empty;
        private UiGraphPort port = null!;

        public TopiaForgeGraphPortView(TopiaForgeGraphCanvas owner, RectTransform parent)
        {
            this.owner = owner;
            go = new GameObject("Port", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            Rect = (RectTransform)go.transform;
            fill = TopiaForgeGraphNodeView.AddControlFill(go);
            button = go.AddComponent<Button>();
            button.targetGraphic = fill;
            button.onClick.AddListener(Select);

            var indicatorGo = new GameObject("Indicator", typeof(RectTransform));
            indicatorGo.transform.SetParent(go.transform, false);
            indicator = indicatorGo.AddComponent<Image>();
            indicator.sprite = TopiaForgeSprites.Circle();
            indicator.raycastTarget = false;

            label = TopiaForgeGraphNodeView.CreateLabel(go.transform, "Label", TopiaForgeTextStyle.Label, TextAlignmentOptions.Left);
            focus = TopiaForgeControlFocus.Install(owner, button);
        }

        public RectTransform Rect { get; }
        public string PortId => port != null ? port.Id : string.Empty;

        public void Bind(string ownerNodeId, UiGraphPort value, int row)
        {
            nodeId = ownerNodeId;
            port = value;
            label.text = UiGraphControlGeometry.PortText(value);
            var input = value.Direction == UiGraphPortDirection.Input;
            TopiaForgeGraphNodeView.SetControlBox(Rect, UiGraphControlGeometry.Port(value.Direction, row));
            label.alignment = input ? TextAlignmentOptions.Left : TextAlignmentOptions.Right;
            TopiaForgeAnchors.Stretch(label.rectTransform,
                input ? UiGraphControlGeometry.PortLabelInset : UiGraphControlGeometry.MarkInset, 0f,
                input ? UiGraphControlGeometry.MarkInset : UiGraphControlGeometry.PortLabelInset, 0f);
            var dotRect = indicator.rectTransform;
            dotRect.anchorMin = new Vector2(input ? 0f : 1f, 0.5f);
            dotRect.anchorMax = dotRect.anchorMin;
            dotRect.pivot = new Vector2(input ? 0f : 1f, 0.5f);
            dotRect.anchoredPosition = new Vector2(input ? UiGraphControlGeometry.MarkInset : -UiGraphControlGeometry.MarkInset, 0f);
            dotRect.sizeDelta = Vector2.one * UiGraphControlGeometry.MarkSize;
        }

        public void SetActive(bool value) => go.SetActive(value);

        public void ApplyTheme(TopiaForgeResolvedTheme theme, bool enabled, UiPaintRole backdrop, bool pending)
        {
            if (!go.activeSelf || port == null) return;
            button.interactable = enabled;
            focus?.SetBackdrop(theme, backdrop);
            fill.SetShape(theme.Descriptor?.Shape.Control);
            TopiaForgeTypography.ApplyControlText(label, theme, TopiaForgeTextStyle.Label, owner.Host.EffectiveUiScale);
            // A pending connection source changes shape as well as fill.
            indicator.sprite = pending ? TopiaForgeSprites.White : TopiaForgeSprites.Circle();
            var role = enabled && pending ? UiPaintRole.Selected : backdrop;
            if (theme.FieldPaint(role, UiTone.Neutral, enabled) is { } paint)
            {
                button.colors = paint.ResolvedStates(button.colors);
                fill.color = paint.Fill;
                label.color = paint.Content;
                indicator.color = paint.Content;
            }
            else
            {
                fill.color = !enabled ? theme.SurfaceSunken : pending ? theme.SelectedTint : theme.Surface;
                label.color = enabled ? theme.Text : theme.TextFaint;
                indicator.color = label.color;
            }
        }

        private void Select() => owner.HandlePortSelected(nodeId, port);
    }
}
