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
        private readonly TextMeshProUGUI title;
        private readonly TextMeshProUGUI subtitle;
        private readonly Button button;
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
            fill.raycastTarget = true;
            button = go.AddComponent<Button>();
            button.targetGraphic = fill;
            button.onClick.AddListener(Select);

            ring = CreateImage(go.transform, "Ring", TopiaForgeSprites.Ring(
                TopiaForgeRadius.Control,
                TopiaForgeTokens.BorderStandard));

            title = CreateLabel(go.transform, "Title", TopiaForgeTextStyle.Heading, TextAlignmentOptions.TopLeft);
            SetRect(title.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0f, -8f), new Vector2(-20f, 24f));
            title.margin = new Vector4(10f, 0f, 10f, 0f);

            subtitle = CreateLabel(go.transform, "Subtitle", TopiaForgeTextStyle.Caption, TextAlignmentOptions.TopLeft);
            SetRect(subtitle.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0f, -32f), new Vector2(-20f, 20f));
            subtitle.margin = new Vector4(10f, 0f, 10f, 0f);

            drag = go.AddComponent<TopiaForgeGraphNodeDrag>();
            drag.Bind(owner, string.Empty);
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
            Rect.sizeDelta = new Vector2(TopiaForgeGraphCanvas.NodeWidth, TopiaForgeGraphCanvas.HeightFor(value));
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
            fill.color = interactive ? theme.Surface : theme.Tint;
            ring.color = selected ? theme.FocusRing : owner.ToneColor(theme, node.Tone);
            title.color = interactive ? theme.Text : theme.TextFaint;
            subtitle.color = interactive ? theme.TextMuted : theme.TextFaint;
            foreach (var port in portViews)
            {
                port.ApplyTheme(theme, interactive, pendingNode && string.Equals(port.PortId, pendingPortId, StringComparison.Ordinal));
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

        private static TextMeshProUGUI CreateLabel(
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
            label.raycastTarget = false;
            var font = TopiaForgeFonts.For(style);
            if (font != null) label.font = font;
            if (TopiaForgeTokens.IsBold(style) && TopiaForgeFonts.UseFauxBold) label.fontStyle = FontStyles.Bold;
            return label;
        }

        private static void SetRect(
            RectTransform rect,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 pivot,
            Vector2 position,
            Vector2 size)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }
    }

    internal sealed class TopiaForgeGraphPortView
    {
        private readonly TopiaForgeGraphCanvas owner;
        private readonly GameObject go;
        private readonly Image hit;
        private readonly Image indicator;
        private readonly TextMeshProUGUI label;
        private readonly Button button;
        private string nodeId = string.Empty;
        private UiGraphPort port = null!;

        public TopiaForgeGraphPortView(TopiaForgeGraphCanvas owner, RectTransform parent)
        {
            this.owner = owner;
            go = new GameObject("Port", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            Rect = (RectTransform)go.transform;
            hit = go.AddComponent<Image>();
            hit.color = Color.clear;
            hit.raycastTarget = true;
            button = go.AddComponent<Button>();
            button.targetGraphic = hit;
            button.onClick.AddListener(Select);

            var indicatorGo = new GameObject("Indicator", typeof(RectTransform));
            indicatorGo.transform.SetParent(go.transform, false);
            indicator = indicatorGo.AddComponent<Image>();
            indicator.sprite = TopiaForgeSprites.Circle();
            indicator.raycastTarget = false;

            var labelGo = new GameObject("Label", typeof(RectTransform));
            labelGo.transform.SetParent(go.transform, false);
            label = TopiaForgeTmp.Create(labelGo);
            label.fontSize = TopiaForgeTokens.CaptionSize;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.raycastTarget = false;
        }

        public RectTransform Rect { get; }
        public string PortId => port != null ? port.Id : string.Empty;

        public void Bind(string ownerNodeId, UiGraphPort value, int row)
        {
            nodeId = ownerNodeId;
            port = value;
            label.text = value.Required ? value.Label + " *" : value.Label;
            var input = value.Direction == UiGraphPortDirection.Input;
            Rect.anchorMin = new Vector2(input ? 0f : 1f, 1f);
            Rect.anchorMax = Rect.anchorMin;
            Rect.pivot = new Vector2(input ? 0f : 1f, 1f);
            Rect.anchoredPosition = new Vector2(input ? 8f : -8f, -58f - row * TopiaForgeGraphCanvas.PortRowHeight);
            Rect.sizeDelta = new Vector2(96f, TopiaForgeGraphCanvas.PortRowHeight);
            label.alignment = input ? TextAlignmentOptions.Left : TextAlignmentOptions.Right;
            TopiaForgeAnchors.Stretch(label.rectTransform, input ? 14f : 0f, 0f, input ? 0f : 14f, 0f);
            var dotRect = indicator.rectTransform;
            dotRect.anchorMin = new Vector2(input ? 0f : 1f, 0.5f);
            dotRect.anchorMax = dotRect.anchorMin;
            dotRect.pivot = new Vector2(input ? 0f : 1f, 0.5f);
            dotRect.anchoredPosition = Vector2.zero;
            dotRect.sizeDelta = new Vector2(9f, 9f);
        }

        public void SetActive(bool value) => go.SetActive(value);

        public void ApplyTheme(TopiaForgeResolvedTheme theme, bool enabled, bool pending)
        {
            if (!go.activeSelf || port == null) return;
            button.interactable = enabled;
            label.color = enabled ? theme.TextMuted : theme.TextFaint;
            indicator.color = pending ? theme.FocusRing : enabled ? theme.Primary : theme.TextFaint;
        }

        private void Select() => owner.HandlePortSelected(nodeId, port);
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
            if (!eventData.dragging) owner?.HandleCanvasSelected();
        }
    }
}
