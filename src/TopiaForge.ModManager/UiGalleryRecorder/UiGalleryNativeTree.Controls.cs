using System;
using System.Globalization;
using TMPro;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.ModManager
{
    internal sealed partial class UiGalleryNativeTree
    {
        private bool Control(UiNode fixture, RectTransform rect, string path, UiThemeColor? backdrop)
        {
            switch (fixture)
            {
                case UiButton button:
                    var buttonControl = rect.GetComponent<Button>();
                    if (buttonControl == null) return false;
                    var buttonFill = Over(buttonControl.targetGraphic, backdrop);
                    var buttonFlags = Flags(buttonControl);
                    HitAndFocus(path, buttonControl, button.Label, backdrop);
                    return Text(path, button.Id, nameof(UiButton), Named<TMP_Text>(rect, "Label"), button.Label, UiTextStyle.Label,
                        buttonFill, buttonFlags, Decoration(rect, buttonControl.targetGraphic));
                case UiToggle toggle:
                    return Toggle(toggle, rect, path, backdrop);
                case UiSlider slider:
                    return Slider(slider, rect, path, backdrop);
                case UiTextInput input:
                    return Input(input, rect, path, backdrop);
                case UiDropdown dropdown:
                    return Dropdown(dropdown, rect, path, backdrop);
                case UiVirtualList list:
                    return List(list, rect, path, backdrop);
                case UiGraphCanvas graph:
                    return Graph(graph, rect, path, backdrop);
                default:
                    return false;
            }
        }

        private bool Toggle(UiToggle source, RectTransform rect, string path, UiThemeColor? backdrop)
        {
            var selectable = rect.GetComponent<Toggle>();
            var track = selectable?.targetGraphic;
            var thumb = Named<Image>(rect, "Thumb");
            if (selectable == null || track == null || thumb == null) return false;
            var trackFill = Over(track, backdrop);
            Add(path + "/track", null, "ToggleTrack", track.rectTransform, EmptyContent, new UiCaptureNodePaint(null, trackFill));
            var trackBounds = Bounds(track.rectTransform);
            var thumbBounds = Bounds(thumb.rectTransform);
            var centers = thumbBounds.X + thumbBounds.Width / 2f - (trackBounds.X + trackBounds.Width / 2f);
            var state = selectable.isOn ? UiCaptureNodeFlags.Selected : UiCaptureNodeFlags.None;
            state |= UiCaptureNodeFlags.RequiresNonColorCue | UiCaptureNodeFlags.SemanticMark;
            if (selectable.isOn ? centers > 1f : centers < -1f) state |= UiCaptureNodeFlags.HasNonColorCue;
            Add(path + "/thumb", null, "ToggleThumb", thumb.rectTransform, State(state, source.Label),
                new UiCaptureNodePaint(ToRgb(EffectiveColor(thumb)), trackFill), thumb.isActiveAndEnabled);
            HitAndFocus(path, selectable, source.Label, backdrop);
            return Text(path, source.Id, nameof(UiToggle), Named<TMP_Text>(rect, "Label"), source.Label, UiTextStyle.Body,
                backdrop, Flags(selectable) | (state & UiCaptureNodeFlags.Selected));
        }

        private bool Slider(UiSlider source, RectTransform rect, string path, UiThemeColor? backdrop)
        {
            var selectable = rect.GetComponentInChildren<Slider>(includeInactive: true);
            var track = Named<Image>(rect, "Track");
            var thumb = Named<Image>(rect, "Handle");
            if (selectable == null || track == null || thumb == null) return false;
            var state = State(UiCaptureNodeFlags.SemanticMark | Flags(selectable), source.Label);
            Add(path + "/thumb", null, "SliderThumb", thumb.rectTransform, state,
                new UiCaptureNodePaint(ToRgb(EffectiveColor(thumb)), Over(track, backdrop)), thumb.isActiveAndEnabled);
            HitAndFocus(path, selectable, source.Label, backdrop);
            return Text(path, source.Id, nameof(UiSlider), Named<TMP_Text>(rect, "Label"), source.Label, UiTextStyle.Body,
                       backdrop, Flags(selectable)) &&
                Text(path + "/value", null, "SliderValue", Named<TMP_Text>(rect, "Value"),
                    selectable.value.ToString("0.##", CultureInfo.CurrentCulture), UiTextStyle.Caption, backdrop);
        }

        private bool Input(UiTextInput source, RectTransform rect, string path, UiThemeColor? backdrop)
        {
            var selectable = rect.GetComponentInChildren<TMP_InputField>(includeInactive: true);
            if (selectable == null || selectable.transform is not RectTransform fieldRect) return false;
            var fieldFill = Over(selectable.targetGraphic, backdrop);
            var flags = Flags(selectable);
            HitAndFocus(path, selectable, source.Label, backdrop);
            var nativeValue = selectable.text;
            if (source.Value.Length != 0 && nativeValue == source.Value)
            {
                if (!Text(path + "/value", null, "FieldValue", selectable.textComponent, source.Value,
                        UiTextStyle.Body, fieldFill, flags, nativeInputValue: nativeValue)) return false;
            }
            else
            {
                Add(path + "/value", null, "FieldValue", fieldRect, State(flags, source.Label),
                    new UiCaptureNodePaint(null, fieldFill));
            }

            if (selectable.placeholder is TMP_Text placeholder && placeholder.isActiveAndEnabled &&
                !Text(path + "/placeholder", null, "FieldPlaceholder", placeholder, source.Placeholder, UiTextStyle.Body, fieldFill))
                return false;
            return Text(path, source.Id, nameof(UiTextInput), Named<TMP_Text>(rect, "Label"), source.Label, UiTextStyle.Caption,
                backdrop, flags);
        }

        private bool Dropdown(UiDropdown source, RectTransform rect, string path, UiThemeColor? backdrop)
        {
            var selectable = rect.GetComponentInChildren<TMP_Dropdown>(includeInactive: true);
            if (selectable == null || (uint)selectable.value >= (uint)source.Choices.Count) return false;
            var flags = Flags(selectable);
            HitAndFocus(path, selectable, source.Label, backdrop);
            return Text(path, source.Id, nameof(UiDropdown), Named<TMP_Text>(rect, "Label"), source.Label, UiTextStyle.Caption,
                       backdrop, flags) &&
                Text(path + "/value", null, "FieldValue", selectable.captionText, source.Choices[selectable.value].Label,
                    UiTextStyle.Body, Over(selectable.targetGraphic, backdrop), flags);
        }

        private bool List(UiVirtualList source, RectTransform rect, string path, UiThemeColor? backdrop)
        {
            var scroll = rect.GetComponent<ScrollRect>();
            if (scroll == null || scroll.content == null || !scroll.content.IsChildOf(rect)) return false;
            Add(path, source.Id, nameof(UiVirtualList), rect, EmptyContent, default);
            for (var slot = 0; slot < scroll.content.childCount; slot++)
            {
                if (scroll.content.GetChild(slot) is not RectTransform row || !row.gameObject.activeInHierarchy) continue;
                var index = UiVirtualListGeometry.IndexAtRowTop(-row.anchoredPosition.y);
                if ((uint)index >= (uint)source.Items.Count || row.GetComponent<Button>() is not { } button) return false;
                var item = source.Items[index];
                var itemPath = NodePath(ChildPath(path, index), item.Id);
                var fill = Over(button.targetGraphic, backdrop);
                var flags = Flags(button);
                var ring = row.Find("Ring")?.GetComponent<Image>();
                if (ring != null && ring.isActiveAndEnabled && EffectiveColor(ring).a > 0.001f)
                    flags |= UiCaptureNodeFlags.Selected | UiCaptureNodeFlags.RequiresNonColorCue | UiCaptureNodeFlags.HasNonColorCue;
                HitAndFocus(itemPath, button, item.Title, backdrop);
                if (!Text(itemPath, item.Id, "ListItem", Named<TMP_Text>(row, "RowTitle"), item.Title,
                        UiTextStyle.Body, fill, flags, Decoration(row, button.targetGraphic)) ||
                    (item.Subtitle.Length != 0 && !Text(itemPath + "/subtitle", null, "ListItemSubtitle",
                        Named<TMP_Text>(row, "RowSubtitle"), item.Subtitle, UiTextStyle.Caption, fill, flags))) return false;
            }
            return true;
        }

        private bool Graph(UiGraphCanvas source, RectTransform rect, string path, UiThemeColor? backdrop)
        {
            var graphFill = Over(rect.GetComponent<Image>(), backdrop);
            Add(path, source.Id, nameof(UiGraphCanvas), rect, EmptyContent, new UiCaptureNodePaint(null, graphFill));
            var cards = rect.Find("Nodes");
            if (cards == null) return false;
            var index = 0;
            for (var slot = 0; slot < cards.childCount; slot++)
            {
                var child = cards.GetChild(slot);
                if (!child.gameObject.activeInHierarchy) continue;
                if (child is not RectTransform card || card.name != "GraphNode" || index >= source.Nodes.Count) return false;
                var node = source.Nodes[index];
                if (!GraphNode(node, card, NodePath(ChildPath(path, index), node.Id), graphFill)) return false;
                index++;
            }
            return index == source.Nodes.Count;
        }

        private bool GraphNode(UiGraphNode node, RectTransform card, string path, UiThemeColor? graphFill)
        {
            if (card.Find("Header") is not RectTransform header || header.GetComponent<Button>() is not { } button) return false;
            var shell = Over(card.GetComponent<Image>(), graphFill);
            var fill = Over(button.targetGraphic, shell);
            var flags = Flags(button);
            var ring = card.Find("Ring")?.GetComponent<Image>();
            var selectedWidth = UiControlFocusGeometry.Stroke(theme.HighContrast) + 1f;
            if (ring != null && ring.isActiveAndEnabled &&
                ring.sprite == TopiaForgeSprites.Ring(TopiaForgeRadius.Control, selectedWidth))
                flags |= UiCaptureNodeFlags.Selected | UiCaptureNodeFlags.RequiresNonColorCue | UiCaptureNodeFlags.HasNonColorCue;
            HitAndFocus(path, button, node.Title, shell);
            if (!Text(path, node.Id, "GraphNode", Named<TMP_Text>(header, "Title"), node.Title, UiTextStyle.Label, fill, flags,
                    Decoration(header, button.targetGraphic)) ||
                !Text(path + "/subtitle", null, "GraphNodeSubtitle", Named<TMP_Text>(header, "Subtitle"),
                    node.Subtitle.Length == 0 ? node.Type : node.Subtitle, UiTextStyle.Caption, fill, flags)) return false;

            var portIndex = 0;
            for (var slot = 0; slot < card.childCount; slot++)
            {
                var child = card.GetChild(slot);
                if (child.name != "Port" || !child.gameObject.activeInHierarchy) continue;
                if (child is not RectTransform portRect || portIndex >= node.Ports.Count) return false;
                var port = node.Ports[portIndex];
                var portPath = NodePath(ChildPath(path + "/ports", portIndex), port.Id);
                if (!GraphPort(port, portRect, portPath, shell)) return false;
                portIndex++;
            }
            return portIndex == node.Ports.Count;
        }

        private bool GraphPort(UiGraphPort port, RectTransform rect, string path, UiThemeColor? backdrop)
        {
            if (rect.GetComponent<Button>() is not { } button || Named<Image>(rect, "Indicator") is not { } mark) return false;
            var fill = Over(button.targetGraphic, backdrop);
            var flags = Flags(button);
            if (mark.sprite == TopiaForgeSprites.White)
                flags |= UiCaptureNodeFlags.Selected | UiCaptureNodeFlags.RequiresNonColorCue | UiCaptureNodeFlags.HasNonColorCue;

            if (!Text(path, port.Id, "GraphPort", Named<TMP_Text>(rect, "Label"), UiGraphControlGeometry.PortText(port),
                    UiTextStyle.Label, fill, flags, Decoration(rect, button.targetGraphic))) return false;
            Add(path + "/mark", null, "GraphPortMark", mark.rectTransform, State(flags | UiCaptureNodeFlags.SemanticMark, port.Label),
                new UiCaptureNodePaint(ToRgb(EffectiveColor(mark)), fill), mark.isActiveAndEnabled);
            HitAndFocus(path, button, port.Label, backdrop);
            return true;
        }

        private void HitAndFocus(string path, Selectable selectable, string label, UiThemeColor? backdrop)
        {
            if (selectable.transform is not RectTransform rect) return;
            var graphic = selectable.GetComponent<Graphic>();
            var hit = graphic != null && graphic.raycastTarget ? graphic.rectTransform : rect;
            Add(path + "/hit", null, "HitTarget", hit, State(Flags(selectable) | UiCaptureNodeFlags.HitTarget, label), default);
            var focus = Named<TopiaForgeRoundedImage>(rect, "Focus ring");
            if (focus == null || !focus.isActiveAndEnabled) return;
            var scale = VerticalScale(focus.rectTransform);
            var local = focus.rectTransform.rect;
            var radius = focus.ResolvedCornerRadius is float corner
                ? Mathf.Min(corner, Mathf.Min(local.width, local.height) / 2f) * scale : (float?)null;
            Add(path + "/focus", null, "FocusRing", focus.rectTransform,
                State(UiCaptureNodeFlags.SemanticMark | UiCaptureNodeFlags.FocusIndicator, label),
                new UiCaptureNodePaint(ToRgb(EffectiveColor(focus)), backdrop, radius, focus.ResolvedBorderWidth * scale));
        }

        private UiCaptureNodePaint Decoration(RectTransform rect, Graphic? fill)
        {
            if (fill is not TopiaForgeRoundedImage rounded || rounded.ResolvedCornerRadius is not float radius) return default;
            var scale = VerticalScale(rounded.rectTransform);
            var local = rounded.rectTransform.rect;
            var border = Named<TopiaForgeRoundedImage>(rect, "Ring");
            var shadow = Named<TopiaForgeRoundedImage>(rect, "Shadow");
            var body = Bounds(rounded.rectTransform);
            var hasShadow = shadow != null && shadow.isActiveAndEnabled && EffectiveColor(shadow).a > 0.001f;
            var shadowBounds = hasShadow ? Bounds(shadow!.rectTransform) : body;
            return new UiCaptureNodePaint(null, null, Mathf.Min(radius, Mathf.Min(local.width, local.height) / 2f) * scale,
                border?.ResolvedBorderWidth * scale ?? 0f, shadowBounds.X - body.X, shadowBounds.Y - body.Y);
        }

        private static UiCaptureNodeFlags Flags(Selectable? selectable) => UiCaptureNodeFlags.RequiresLabel |
            (selectable == null || !selectable.IsInteractable() ? UiCaptureNodeFlags.Disabled : UiCaptureNodeFlags.None);
    }
}
