using System;
using System.Collections.Generic;
using TMPro;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    internal sealed class TopiaForgeGraphNodeView
    {
        private readonly TopiaForgeGraphCanvas owner;
        private readonly GameObject go;
        private readonly Image fill;
        private readonly Image ring;
        private readonly TopiaForgeRoundedImage headerFill;
        private readonly TextMeshProUGUI title;
        private readonly TextMeshProUGUI subtitle;
        private readonly Button button;
        private readonly TopiaForgeControlFocus? focus;
        private readonly TopiaForgeGraphNodeDrag drag;
        private readonly List<TopiaForgeGraphPortView> portViews = new List<TopiaForgeGraphPortView>();
        private UiGraphNode node = null!;
        private Vector2 graphPosition;

        public TopiaForgeGraphNodeView(TopiaForgeGraphCanvas owner, RectTransform parent)
        {
            this.owner = owner;
            go = new GameObject("GraphNode", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            Rect = (RectTransform)go.transform;
            Rect.anchorMin = new Vector2(0.5f, 0.5f);
            Rect.anchorMax = new Vector2(0.5f, 0.5f);
            Rect.pivot = new Vector2(0.5f, 0.5f);

            fill = go.AddComponent<Image>();
            fill.sprite = TopiaForgeSprites.Fill(TopiaForgeRadius.Control);
            fill.type = Image.Type.Sliced;
            // The shell still receives node drags across its full area.
            fill.raycastTarget = true;

            ring = CreateImage(go.transform, "Ring", TopiaForgeSprites.Ring(
                TopiaForgeRadius.Control,
                TopiaForgeTokens.BorderStandard));

            var header = new GameObject("Header", typeof(RectTransform));
            header.transform.SetParent(go.transform, false);
            SetControlBox((RectTransform)header.transform, UiGraphControlGeometry.Header);
            headerFill = AddControlFill(header);
            button = header.AddComponent<Button>();
            button.targetGraphic = headerFill;
            button.onClick.AddListener(Select);

            title = CreateLabel(header.transform, "Title", TopiaForgeTextStyle.Label, TextAlignmentOptions.TopLeft);
            SetHeaderLabel(title.rectTransform, UiGraphControlGeometry.TitleTop, UiGraphControlGeometry.TitleHeight);
            subtitle = CreateLabel(header.transform, "Subtitle", TopiaForgeTextStyle.Caption, TextAlignmentOptions.TopLeft);
            SetHeaderLabel(subtitle.rectTransform, UiGraphControlGeometry.SubtitleTop, UiGraphControlGeometry.SubtitleHeight);

            drag = go.AddComponent<TopiaForgeGraphNodeDrag>();
            drag.Bind(owner, string.Empty);
            focus = TopiaForgeControlFocus.Install(owner, button);
        }

        public RectTransform Rect { get; }
        public string NodeId => node != null ? node.Id : string.Empty;
        public bool Enabled => node != null && node.Enabled;
        public bool Active => go.activeSelf;
        public UiGraphNode Node => node;
        public Vector2 GraphPosition => graphPosition;

        public void Bind(UiGraphNode value)
        {
            node = value;
            graphPosition = new Vector2(value.Position.X, value.Position.Y);
            title.text = value.Title;
            subtitle.text = string.IsNullOrEmpty(value.Subtitle) ? value.Type : value.Subtitle;
            Rect.sizeDelta = new Vector2(UiGraphControlGeometry.NodeWidth, UiGraphControlGeometry.NodeHeight(value));
            drag.Bind(owner, value.Id);

            while (portViews.Count < value.Ports.Count)
            {
                portViews.Add(new TopiaForgeGraphPortView(owner, Rect));
            }

            var inputIndex = 0;
            var outputIndex = 0;
            for (var index = 0; index < portViews.Count; index++)
            {
                var active = index < value.Ports.Count;
                var view = portViews[index];
                view.SetActive(active);
                if (!active) continue;
                var port = value.Ports[index];
                var row = port.Direction == UiGraphPortDirection.Input ? inputIndex++ : outputIndex++;
                view.Bind(value.Id, port, row);
            }
        }

        public void SetActive(bool value) => go.SetActive(value);

        public void SetGraphPosition(Vector2 value) => graphPosition = value;

        public void ApplyTheme(
            TopiaForgeResolvedTheme theme,
            bool canvasEnabled,
            bool selected,
            bool pendingNode,
            string? pendingPortId)
        {
            if (!Active || node == null) return;
            var interactive = canvasEnabled && node.Enabled;
            button.interactable = interactive;
            var role = UiControlPaint.GraphNodeBackground(interactive, selected);
            focus?.SetBackdrop(theme, role);
            headerFill.SetShape(theme.Descriptor?.Shape.Control);
            TopiaForgeTypography.ApplyControlText(title, theme, TopiaForgeTextStyle.Label, owner.Host.EffectiveUiScale);
            TopiaForgeTypography.ApplyControlText(subtitle, theme, TopiaForgeTextStyle.Caption, owner.Host.EffectiveUiScale);
            if (theme.FieldPaint(role, node.Tone, interactive) is { } paint)
            {
                button.colors = paint.ResolvedStates(button.colors);
                fill.color = paint.Fill;
                headerFill.color = paint.Fill;
                title.color = paint.Content;
                subtitle.color = paint.Content;
                ring.color = selected ? theme.FocusRing : theme.BoundaryOn(UiPaintRole.Sunken);
            }
            else
            {
                fill.color = interactive ? theme.Surface : theme.Tint;
                headerFill.color = fill.color;
                title.color = interactive ? theme.Text : theme.TextFaint;
                subtitle.color = interactive ? theme.TextMuted : theme.TextFaint;
                ring.color = selected ? theme.FocusRing : owner.ToneColor(theme, node.Tone);
            }

            var border = theme.HighContrast ? 3 : TopiaForgeTokens.BorderStandard;
            if (selected) border++;
            ring.sprite = TopiaForgeSprites.Ring(TopiaForgeRadius.Control, border);
            foreach (var port in portViews)
            {
                port.ApplyTheme(theme, interactive, role,
                    pendingNode && string.Equals(port.PortId, pendingPortId, StringComparison.Ordinal));
            }
        }

        private void Select() => owner.HandleNodeSelected(NodeId);

        private static Image CreateImage(Transform parent, string name, Sprite sprite)
        {
            var child = new GameObject(name, typeof(RectTransform));
            child.transform.SetParent(parent, false);
            var image = child.AddComponent<Image>();
            image.sprite = sprite;
            image.type = Image.Type.Sliced;
            image.raycastTarget = false;
            TopiaForgeAnchors.Stretch((RectTransform)child.transform);
            return image;
        }

        internal static TopiaForgeRoundedImage AddControlFill(GameObject control)
        {
            var image = control.AddComponent<TopiaForgeRoundedImage>();
            image.sprite = TopiaForgeSprites.Fill(TopiaForgeRadius.Control);
            image.type = Image.Type.Sliced;
            image.raycastTarget = true;
            return image;
        }

        internal static TextMeshProUGUI CreateLabel(
            Transform parent,
            string name,
            TopiaForgeTextStyle style,
            TextAlignmentOptions alignment)
        {
            var child = new GameObject(name, typeof(RectTransform));
            child.transform.SetParent(parent, false);
            var label = TopiaForgeTmp.Create(child);
            label.fontSize = TopiaForgeTokens.SizeOf(style);
            label.alignment = alignment;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.raycastTarget = false;
            var font = TopiaForgeFonts.For(style);
            if (font != null) label.font = font;
            if (TopiaForgeTokens.IsBold(style) && TopiaForgeFonts.UseFauxBold) label.fontStyle = FontStyles.Bold;
            return label;
        }

        private static void SetHeaderLabel(RectTransform rect, float top, float height)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -top);
            rect.sizeDelta = new Vector2(-2f * UiGraphControlGeometry.HeaderLabelInset, height);
        }

        internal static void SetControlBox(RectTransform rect, (float X, float Y, float Width, float Height) box)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = rect.anchorMin;
            rect.pivot = rect.anchorMin;
            rect.anchoredPosition = new Vector2(box.X, -box.Y);
            rect.sizeDelta = new Vector2(box.Width, box.Height);
        }
    }

    internal sealed class TopiaForgeGraphEdgeView
    {
        private readonly TopiaForgeGraphCanvas owner;
        private readonly GameObject go;
        private readonly Image hit;
        private readonly Image line;
        private readonly Button button;
        private UiGraphEdge edge = null!;

        public TopiaForgeGraphEdgeView(TopiaForgeGraphCanvas owner, RectTransform parent)
        {
            this.owner = owner;
            go = new GameObject("GraphEdge", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            Rect = (RectTransform)go.transform;
            Rect.anchorMin = new Vector2(0.5f, 0.5f);
            Rect.anchorMax = new Vector2(0.5f, 0.5f);
            hit = go.AddComponent<Image>();
            hit.color = Color.clear;
            hit.raycastTarget = true;
            button = go.AddComponent<Button>();
            button.targetGraphic = hit;
            button.onClick.AddListener(Select);
            var lineGo = new GameObject("Line", typeof(RectTransform));
            lineGo.transform.SetParent(go.transform, false);
            line = lineGo.AddComponent<Image>();
            line.sprite = TopiaForgeSprites.White;
            line.raycastTarget = false;
            TopiaForgeAnchors.Stretch((RectTransform)lineGo.transform, 0f, 3f, 0f, 3f);
        }

        public RectTransform Rect { get; }
        public bool Active => go.activeSelf;
        public string SourceNodeId => edge != null ? edge.SourceNodeId : string.Empty;
        public string TargetNodeId => edge != null ? edge.TargetNodeId : string.Empty;

        public void Bind(UiGraphEdge value) => edge = value;
        public void SetActive(bool value) => go.SetActive(value);

        public void SetGeometry(Vector2 start, Vector2 end)
        {
            var delta = end - start;
            Rect.anchoredPosition = (start + end) * 0.5f;
            Rect.sizeDelta = new Vector2(delta.magnitude, 8f);
            Rect.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
        }

        public void ApplyTheme(TopiaForgeResolvedTheme theme, bool enabled)
        {
            if (!Active || edge == null) return;
            button.interactable = enabled;
            line.color = enabled ? owner.ToneColor(theme, edge.Tone) : theme.TextFaint;
        }

        private void Select()
        {
            if (edge != null) owner.HandleEdgeSelected(edge.Id);
        }
    }

    internal sealed class TopiaForgeGraphNodeDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        private TopiaForgeGraphCanvas? owner;
        private string nodeId = string.Empty;

        public void Bind(TopiaForgeGraphCanvas canvas, string id)
        {
            owner = canvas;
            nodeId = id;
        }

        public void OnBeginDrag(PointerEventData eventData) { }
        public void OnDrag(PointerEventData eventData) => owner?.HandleNodeDrag(nodeId, eventData.delta);
        public void OnEndDrag(PointerEventData eventData) => owner?.HandleNodeDragEnd(nodeId);
    }

    internal sealed class TopiaForgeGraphViewportInput : MonoBehaviour,
        IBeginDragHandler,
        IDragHandler,
        IEndDragHandler,
        IScrollHandler,
        IPointerClickHandler
    {
        private TopiaForgeGraphCanvas? owner;
        private bool panning;

        public void Initialize(TopiaForgeGraphCanvas canvas) => owner = canvas;

        public void OnBeginDrag(PointerEventData eventData)
        {
            panning = eventData.button == PointerEventData.InputButton.Left ||
                      eventData.button == PointerEventData.InputButton.Middle;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (panning) owner?.HandlePan(eventData.delta);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (panning) owner?.HandlePanEnd();
            panning = false;
        }

        public void OnScroll(PointerEventData eventData) => owner?.HandleZoom(eventData.scrollDelta.y);

        public void OnPointerClick(PointerEventData eventData)
        {
            var target = eventData.pointerPressRaycast.gameObject;
            if (!eventData.dragging && (target == null || target.GetComponentInParent<TopiaForgeGraphNodeDrag>() == null))
                owner?.HandleCanvasSelected();
        }
    }
}
