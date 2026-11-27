using TMPro;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.ModManager
{
    internal sealed partial class UiGalleryNativeTree
    {
        private bool Visit(UiNode fixture, RectTransform native, string path, UiThemeColor? backdrop)
        {
            path = NodePath(path, fixture.Id);
            var childRoot = native;
            switch (fixture)
            {
                case UiCard or UiBox:
                    if (!CapturePanel(fixture, native, path, ref backdrop)) return false;
                    break;
                case UiScroll:
                    var body = native.GetComponent<ScrollRect>()?.content;
                    if (body == null || !body.IsChildOf(native)) return false;
                    childRoot = body;
                    Add(path, fixture.Id, nameof(UiScroll), native, EmptyContent, default);
                    break;
                case UiTabs tabs:
                    return CaptureTabs(tabs, native, path, backdrop);
                case UiTooltip tooltip:
                    return CaptureTooltip(tooltip, native, path, backdrop);
                case UiDivider:
                    var divider = native.Find("Divider")?.GetComponent<UnityUiLayoutDivider>();
                    if (divider == null) return false;
                    Add(path, fixture.Id, nameof(UiDivider), divider.rectTransform, State(UiCaptureNodeFlags.Decorative),
                        new UiCaptureNodePaint(ToRgb(EffectiveColor(divider)), backdrop));
                    return true;
                case UiLayoutNode or UiPadding or UiSizedBox or UiExpanded or UiSpacer or UiPositioned or UiSplitPane:
                    Add(path, fixture.Id, fixture.GetType().Name, native, EmptyContent, default);
                    break;
                case UiIcon or UiImage or UiBadge or UiPips:
                    return native.Find("Artwork") is RectTransform artwork &&
                        CaptureArt(fixture, artwork, path, backdrop);
                default:
                    if (!SingleChild(native.Find("Widget"), out var widget)) return false;
                    var backing = native.Find("Text backing")?.GetComponent<Graphic>();
                    return VisitLeaf(fixture, widget!, path, Over(backing, backdrop));
            }

            var children = UiNodeChildren.Of(fixture);
            // Staging appends the authored children after the panel's own paint layers.
            var start = childRoot.childCount - children.Count;
            if (start < 0) return false;
            for (var index = 0; index < children.Count; index++)
                if (childRoot.GetChild(start + index) is not RectTransform child ||
                    child.name != (children[index].Id ?? children[index].GetType().Name) ||
                    !Visit(children[index], child, ChildPath(path, index), backdrop)) return false;
            return true;
        }

        private bool CaptureTabs(UiTabs tabs, RectTransform native, string path, UiThemeColor? backdrop)
        {
            var count = tabs.Tabs.Count;
            if (native.childCount != count * 2) return false;
            Add(path, tabs.Id, nameof(UiTabs), native, EmptyContent, default);
            var selected = -1;
            for (var index = 0; index < count; index++)
            {
                if (!native.GetChild(index).gameObject.activeSelf) continue;
                if (selected >= 0) return false;
                selected = index;
            }
            if (selected < 0 || native.GetChild(selected) is not RectTransform page ||
                !Visit(tabs.Tabs[selected].Child, page, ChildPath(path, selected), backdrop)) return false;

            for (var index = 0; index < count; index++)
            {
                if (native.GetChild(count + index) is not RectTransform view ||
                    !SingleChild(view.Find("Widget"), out var widget) || widget!.GetComponent<Button>() is not { } button ||
                    view.Find("Tab background")?.GetComponent<Graphic>() is not { } plate ||
                    view.Find("Tab selection mark")?.GetComponent<Graphic>() is not { } mark) return false;
                var marked = mark.isActiveAndEnabled && Bounds(mark.rectTransform).Width > 0f;
                if (marked != (index == selected)) return false;
                var tabPath = ChildPath(path, count + index);
                var fill = Over(button.targetGraphic, marked ? Over(plate, backdrop) : backdrop);
                var flags = Flags(button) | (marked ? UiCaptureNodeFlags.Selected | UiCaptureNodeFlags.RequiresNonColorCue |
                    UiCaptureNodeFlags.HasNonColorCue : UiCaptureNodeFlags.None);
                var start = observations.Count;
                HitAndFocus(tabPath, button, tabs.Tabs[index].Label, backdrop);
                if (!Text(tabPath, null, "Tab", Named<TMP_Text>(widget, "Label"), tabs.Tabs[index].Label, UiTextStyle.Label, fill,
                    flags, marked ? Decoration(view, plate) : default))
                    return false;
                if (marked) Add(tabPath + "/selection", null, "TabSelection", mark.rectTransform,
                    State(UiCaptureNodeFlags.SemanticMark), ArtPaint(mark, fill));
                RegisterGroup(start);
            }
            return true;
        }

        private bool CapturePanel(UiNode fixture, RectTransform native, string path, ref UiThemeColor? backdrop)
        {
            var graphic = native.Find("Fill")?.GetComponent<TopiaForgeRoundedImage>();
            if (graphic == null) return false;
            var bounds = Bounds(graphic.rectTransform);
            var fill = Over(graphic, backdrop);
            var border = native.Find("Border")?.GetComponent<TopiaForgeRoundedImage>();
            var shadow = native.Find("Shadow")?.GetComponent<TopiaForgeRoundedImage>();
            var paintedShadow = shadow != null && shadow.isActiveAndEnabled ? Bounds(shadow.rectTransform) : bounds;
            var pixels = VerticalScale(graphic.rectTransform);
            var style = fixture is UiCard card ? card.Style : ((UiBox)fixture).Style;
            var styleId = !fallback && style != null && Descriptor.Styles.ContainsKey(style.Id) ? style.Id : null;
            var paint = new UiCaptureNodePaint(null, fill, graphic.ResolvedCornerRadius * pixels,
                border != null && border.isActiveAndEnabled ? border.ResolvedBorderWidth * pixels : 0,
                paintedShadow.X - bounds.X, paintedShadow.Y - bounds.Y, styleId);

            var state = UiCaptureNodeFlags.None;
            if (fixture is UiCard { Disabled: true }) state |= UiCaptureNodeFlags.Disabled;
            if (fixture is UiCard { Selected: true })
            {
                state |= UiCaptureNodeFlags.Selected | UiCaptureNodeFlags.RequiresNonColorCue;
                var mark = native.Find("Selection mark")?.GetComponent<Image>();
                if (mark != null && mark.isActiveAndEnabled && Bounds(mark.rectTransform).Width > 0f)
                    state |= UiCaptureNodeFlags.HasNonColorCue;
            }

            var group = observations.Count;
            Add(path, fixture.Id, fixture.GetType().Name, graphic.rectTransform, State(state), paint);
            RegisterGroup(group, protect: false);
            if (fixture is UiCard) backdrop = fill;
            return true;
        }
    }
}
