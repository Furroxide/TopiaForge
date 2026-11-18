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
            var thumb = selectable?.targetGraphic;
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
            Add(path + "/value", null, "FieldValue", fieldRect, State(flags, source.Label),
                new UiCaptureNodePaint(null, fieldFill));
            HitAndFocus(path, selectable, source.Label, backdrop);
            // The value may be player text. Only the authored placeholder and external caption qualify.
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
            foreach (var label in scroll.content.GetComponentsInChildren<TMP_Text>())
            {
                if (label.transform.parent is not RectTransform row || row.name != "ListRow") continue;
                for (var index = 0; index < source.Items.Count; index++)
                {
                    var item = source.Items[index];
                    if (label.text != item.Title) continue;
                    var itemPath = NodePath(ChildPath(path, index), item.Id);
                    var fill = Over(row.GetComponent<Image>(), backdrop);
                    var flags = Flags(row.GetComponent<Button>());
                    var ring = row.Find("Ring")?.GetComponent<Image>();
                    if (ring != null && ring.isActiveAndEnabled && EffectiveColor(ring).a > 0.001f)
                        flags |= UiCaptureNodeFlags.Selected | UiCaptureNodeFlags.RequiresNonColorCue | UiCaptureNodeFlags.HasNonColorCue;
                    if (!Text(itemPath, item.Id, "ListItem", label, item.Title, UiTextStyle.Body, fill, flags)) return false;
                    foreach (var sibling in row.GetComponentsInChildren<TMP_Text>())
                    {
                        if (sibling == label || item.Subtitle.Length == 0 || sibling.text != item.Subtitle) continue;
                        if (!Text(itemPath + "/subtitle", null, "ListItemSubtitle", sibling, item.Subtitle, UiTextStyle.Caption,
                            fill, flags)) return false;
                    }
                    break;
                }
            }
            return true;
        }

        private bool Graph(UiGraphCanvas source, RectTransform rect, string path, UiThemeColor? backdrop)
        {
            Add(path, source.Id, nameof(UiGraphCanvas), rect, EmptyContent, new UiCaptureNodePaint(null, backdrop));
            foreach (var label in rect.GetComponentsInChildren<TMP_Text>())
            {
                if (label.name != "Title" || label.transform.parent is not RectTransform card || card.name != "GraphNode") continue;
                for (var index = 0; index < source.Nodes.Count; index++)
                {
                    var node = source.Nodes[index];
                    if (label.text != node.Title) continue;
                    var itemPath = NodePath(ChildPath(path, index), node.Id);
                    var fill = Over(card.GetComponent<Image>(), backdrop);
                    var flags = Flags(card.GetComponent<Button>());
                    var ring = card.Find("Ring")?.GetComponent<Image>();
                    var selectedWidth = (theme.HighContrast ? 3 : TopiaForgeTokens.BorderStandard) + 1;
                    if (ring != null && ring.isActiveAndEnabled &&
                        ring.sprite == TopiaForgeSprites.Ring(TopiaForgeRadius.Control, selectedWidth))
                        flags |= UiCaptureNodeFlags.Selected | UiCaptureNodeFlags.RequiresNonColorCue | UiCaptureNodeFlags.HasNonColorCue;
                    if (!Text(itemPath, node.Id, "GraphNode", label, node.Title, UiTextStyle.Heading, fill, flags)) return false;
                    break;
                }
            }
            return true;
        }

        private void HitAndFocus(string path, Selectable selectable, string label, UiThemeColor? backdrop)
        {
            if (selectable.transform is not RectTransform rect) return;
            Add(path + "/hit", null, "HitTarget", rect, State(Flags(selectable) | UiCaptureNodeFlags.HitTarget, label), default);
            var focus = Named<Image>(rect, "Focus ring");
            if (focus == null || !focus.isActiveAndEnabled) return;
            Add(path + "/focus", null, "FocusRing", focus.rectTransform, State(UiCaptureNodeFlags.SemanticMark, label),
                new UiCaptureNodePaint(ToRgb(EffectiveColor(focus)), backdrop));
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
