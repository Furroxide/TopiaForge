using System;
using System.Collections.Generic;
using TMPro;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService
    {
        private sealed partial class UnityUiLayoutRenderer
        {
            private const float TargetSize = 40f;

            private sealed class NativeLeaf
            {
                private readonly RectTransform widget;
                private readonly TopiaForgeListViewRuntime? virtualList;
                private readonly NativePart part;
                private readonly TopiaForgeContainer host;
                private readonly bool focusRing;
                private readonly Action<UiPaintRole>? paint;
                private UiPaintRole lastBackdrop;
                private TopiaForgeResolvedTheme lastTheme;

                internal NativeLeaf(TopiaForgeContainer host, UiNode node, UiPaintRole backdrop, Action<UiPaintRole>? paint)
                {
                    this.host = host;
                    this.paint = paint;
                    lastBackdrop = backdrop;
                    lastTheme = host.ResolvedTheme;
                    if (host.Rect.childCount != 1 || host.Rect.GetChild(0) is not RectTransform rect)
                        throw new InvalidOperationException("A declarative leaf must render as one native widget.");
                    widget = rect;
                    if (node is UiVirtualList) virtualList = rect.GetComponent<TopiaForgeListViewRuntime>();
                    focusRing = rect.GetComponentInChildren<TopiaForgeControlFocusEvents>(includeInactive: true) != null;
                    var target = node switch
                    {
                        UiToggle or UiCheckbox => rect.GetComponent<Toggle>() as Selectable,
                        UiSlider => rect.GetComponentInChildren<Slider>(includeInactive: true),
                        _ => null,
                    };
                    if (target != null && target.GetComponent<Image>() == null)
                    {
                        var hitArea = target.gameObject.AddComponent<Image>();
                        hitArea.color = Color.clear;
                        hitArea.raycastTarget = true;
                    }
                    // List rows have their own scrolling and clipping.
                    part = new NativePart(rect, target?.transform, node, inspectChildren: node is not UiVirtualList);
                }

                internal UiLayoutLeafMeasurement Measure(UiNode node, UiLayoutConstraints constraints, bool disabled)
                {
                    var size = part.Measure(constraints.MaxWidth);
                    var interactive = UiLayoutTree.IsInteractive(node);
                    var width = Math.Max(interactive ? Math.Max(TargetSize, size.Width) : size.Width,
                        virtualList?.RequireRowsFit(constraints.MaxWidth) ?? 0d);
                    var height = interactive ? Math.Max(TargetSize, size.Height) : size.Height;
                    if (double.IsFinite(constraints.MaxWidth) && node is UiVirtualList or UiProgress)
                        width = Math.Max(width, constraints.MaxWidth);
                    var focus = focusRing && !disabled ? (host.ResolvedTheme.HighContrast ? 5d : 4d) : 0d;
                    var paint = node is UiProgress { Label.Length: 0 } ? UiLayoutPaintKind.Painter : UiLayoutPaintKind.None;
                    return new UiLayoutLeafMeasurement(new UiLayoutExtent(width, height), paint,
                        focusOutsets: new UiLayoutOutsets(focus, focus, focus, focus),
                        scrollLineHeight: node is UiText ? part.MeasureLineHeight() : null);
                }

                internal LeafArrangement Prepare(UiLayoutPlacement placement)
                {
                    var bounds = placement.Bounds;
                    virtualList?.RequireRowsFit(bounds.Width);
                    var parts = new List<NativePartPlacement>();
                    part.Arrange(bounds.Width, bounds.Height, parts);
                    return new LeafArrangement(widget, part.Group, part.Fitter, parts, virtualList, bounds.Width);
                }

                internal void ApplyBackdrop(UiPaintRole backdrop, TopiaForgeResolvedTheme theme)
                {
                    if (backdrop == lastBackdrop && ReferenceEquals(lastTheme, theme)) return;
                    paint?.Invoke(backdrop);
                    lastBackdrop = backdrop;
                    lastTheme = theme;
                }
            }

            private sealed class LeafArrangement
            {
                private readonly RectTransform widget;
                private readonly HorizontalOrVerticalLayoutGroup? group;
                private readonly ContentSizeFitter? fitter;
                private readonly List<NativePartPlacement> parts;
                private readonly TopiaForgeListViewRuntime? virtualList;
                private readonly double width;

                internal LeafArrangement(RectTransform widget, HorizontalOrVerticalLayoutGroup? group,
                    ContentSizeFitter? fitter, List<NativePartPlacement> parts, TopiaForgeListViewRuntime? virtualList, double width)
                {
                    this.widget = widget;
                    this.group = group;
                    this.fitter = fitter;
                    this.parts = parts;
                    this.virtualList = virtualList;
                    this.width = width;
                }

                internal void Apply()
                {
                    if (group != null) group.enabled = false;
                    if (fitter != null) fitter.enabled = false;
                    TopiaForgeAnchors.Stretch(widget);
                    virtualList?.PlaceRows(width);
                    foreach (var part in parts)
                    {
                        if (part.Item.Group != null) part.Item.Group.enabled = false;
                        if (part.Item.Fitter != null) part.Item.Fitter.enabled = false;
                        Place(part.Item.Rect, part.Bounds);
                    }
                }
            }

            // Measures the actual text and layout metadata created by the retained widget factories.
            // Unity's layout rebuild skips inactive widgets, including a window's first hidden composition.
            private sealed class NativePart
            {
                private readonly TMP_Text? text;
                private readonly LayoutElement? layout;
                private readonly NativePart[] children;
                private readonly bool target;
                private readonly bool editable;
                private readonly bool hasText;
                private readonly IReadOnlyList<UiChoice>? choices;
                private readonly string? countdownPrefix;

                internal NativePart(RectTransform rect, Transform? hitTarget, UiNode source, bool inspectChildren = true,
                    bool insideEditor = false)
                {
                    Rect = rect;
                    text = rect.GetComponent<TMP_Text>();
                    layout = rect.GetComponent<LayoutElement>();
                    Group = rect.GetComponent<HorizontalOrVerticalLayoutGroup>();
                    Fitter = rect.GetComponent<ContentSizeFitter>();
                    target = rect == hitTarget;
                    editable = insideEditor || rect.GetComponent<TMP_InputField>() != null;
                    if (text != null)
                    {
                        if (source is UiCountdown countdown)
                            countdownPrefix = countdown.Label.Length == 0 ? string.Empty : countdown.Label + " ";
                        if (source is UiDropdown dropdown &&
                            rect.GetComponentInParent<TMP_Dropdown>(includeInactive: true)?.captionText == text)
                            choices = dropdown.Choices;
                    }
                    if (!inspectChildren || rect.childCount == 0)
                    {
                        children = Array.Empty<NativePart>();
                        hasText = text != null;
                        return;
                    }

                    var parts = new List<NativePart>();
                    for (var index = 0; index < rect.childCount; index++)
                    {
                        if (rect.GetChild(index) is not RectTransform child || !child.gameObject.activeSelf) continue;
                        var part = new NativePart(child, hitTarget, source, insideEditor: editable);
                        if (Group != null ? part.layout?.ignoreLayout == true : !part.hasText) continue;
                        parts.Add(part);
                        hasText |= part.hasText;
                    }
                    hasText |= text != null;
                    children = parts.ToArray();
                }

                internal RectTransform Rect { get; }
                internal HorizontalOrVerticalLayoutGroup? Group { get; }
                internal ContentSizeFitter? Fitter { get; }

                internal double? MeasureLineHeight()
                {
                    var height = text?.GetPreferredValues("Mg").y;
                    return height is > 0f ? height.Value : null;
                }

                internal UiLayoutExtent Measure(double available)
                {
                    var width = target ? TargetSize : 0d;
                    var height = width;
                    if (layout != null)
                    {
                        width = Math.Max(width, Math.Max(layout.minWidth, layout.preferredWidth));
                        height = Math.Max(height, Math.Max(layout.minHeight, layout.preferredHeight));
                    }
                    var content = text != null ? MeasureCaption(text, available)
                        : Group != null ? MeasureGroup(Group, available) : MeasureAnchors(available);
                    return new UiLayoutExtent(Math.Max(content.Width, width), Math.Max(content.Height, height));
                }

                private UiLayoutExtent MeasureGroup(HorizontalOrVerticalLayoutGroup group, double available)
                {
                    var innerWidth = Math.Max(0, available - group.padding.horizontal);
                    var horizontal = group is HorizontalLayoutGroup;
                    var gap = Math.Max(0, children.Length - 1) * group.spacing;
                    var width = horizontal ? gap : 0d;
                    var height = horizontal ? 0d : gap;
                    foreach (var child in children)
                    {
                        var size = child.Measure(horizontal ? double.PositiveInfinity : innerWidth);
                        width = horizontal ? width + size.Width : Math.Max(width, size.Width);
                        height = horizontal ? Math.Max(height, size.Height) : height + size.Height;
                    }
                    return new UiLayoutExtent(width + group.padding.horizontal, height + group.padding.vertical);
                }

                private UiLayoutExtent MeasureAnchors(double available)
                {
                    var width = 0d;
                    var height = 0d;
                    foreach (var child in children)
                    {
                        var rect = child.Rect;
                        var horizontal = rect.anchorMin.x == 0f && rect.anchorMax.x == 1f
                            ? Math.Max(0, rect.offsetMin.x - rect.offsetMax.x) : 0;
                        var vertical = rect.anchorMin.y == 0f && rect.anchorMax.y == 1f
                            ? Math.Max(0, rect.offsetMin.y - rect.offsetMax.y) : 0;
                        var size = child.Measure(Math.Max(0, available - horizontal));
                        width = Math.Max(width, size.Width + horizontal);
                        height = Math.Max(height, size.Height + vertical);
                    }
                    return new UiLayoutExtent(width, height);
                }

                internal void Arrange(double width, double height, List<NativePartPlacement> placements)
                {
                    if (Group is not { } group) return;
                    var horizontal = group is HorizontalLayoutGroup;
                    var contentWidth = Math.Max(0, width - group.padding.horizontal);
                    var contentHeight = Math.Max(0, height - group.padding.vertical);
                    var x = (double)group.padding.left;
                    var y = (double)group.padding.top;
                    var weights = 0d;
                    var remaining = contentWidth;
                    if (horizontal)
                    {
                        remaining -= Math.Max(0, children.Length - 1) * group.spacing;
                        foreach (var child in children)
                        {
                            remaining -= child.Measure(double.PositiveInfinity).Width;
                            weights += Math.Max(0, child.layout?.flexibleWidth ?? 0);
                        }
                        remaining = Math.Max(0, remaining);
                    }

                    foreach (var child in children)
                    {
                        var desired = child.Measure(horizontal ? double.PositiveInfinity : contentWidth);
                        var flexible = Math.Max(0, child.layout?.flexibleWidth ?? 0);
                        var fullWidth = group.childForceExpandWidth || flexible > 0;
                        var childWidth = horizontal ? desired.Width + (weights > 0 ? remaining * flexible / weights : 0)
                            : fullWidth ? contentWidth : desired.Width;
                        var childHeight = desired.Height;
                        var alignY = horizontal ? Math.Max(0, contentHeight - childHeight) / 2d : 0d;
                        placements.Add(new NativePartPlacement(child, new UiLayoutRect(x, y + alignY, childWidth, childHeight)));
                        child.Arrange(childWidth, childHeight, placements);
                        if (horizontal) x += childWidth + group.spacing;
                        else y += childHeight + group.spacing;
                    }
                }

                private UiLayoutExtent MeasureCaption(TMP_Text text, double width)
                {
                    var value = text.text;
                    var size = MeasureText(text, value, width);
                    if (choices != null)
                    {
                        foreach (var choice in choices) size = Enclose(size, MeasureText(text, choice.Label, width));
                    }
                    if (countdownPrefix != null && value != null)
                    {
                        // The countdown can change glyphs without going through layout again. Reserve the same
                        // number of places for the widest digit in the actual face.
                        var digits = Math.Max(1, value.Length - countdownPrefix.Length);
                        for (var digit = '0'; digit <= '9'; digit++)
                            size = Enclose(size, MeasureText(text, countdownPrefix + new string(digit, digits), width));
                    }
                    return size;
                }

                private UiLayoutExtent MeasureText(TMP_Text text, string? value, double width)
                {
                    if (string.IsNullOrEmpty(value)) return default;
                    var natural = text.GetPreferredValues(value);
                    if (editable) return new UiLayoutExtent(Math.Min(width, natural.x), natural.y);
                    if (width <= 0 || natural.x <= width || text.textWrappingMode == TextWrappingModes.NoWrap ||
                        text.textWrappingMode == TextWrappingModes.PreserveWhitespaceNoWrap)
                        return new UiLayoutExtent(natural.x, natural.y);
                    var wrapped = text.GetPreferredValues(value, (float)width, 0f);
                    // Keep the width used to decide line breaks even when no line fills its allocation.
                    return new UiLayoutExtent(Math.Max(width, wrapped.x), wrapped.y);
                }

                private static UiLayoutExtent Enclose(UiLayoutExtent first, UiLayoutExtent second) =>
                    new UiLayoutExtent(Math.Max(first.Width, second.Width), Math.Max(first.Height, second.Height));
            }

            private readonly struct NativePartPlacement
            {
                internal NativePartPlacement(NativePart item, UiLayoutRect bounds)
                {
                    Item = item;
                    Bounds = bounds;
                }

                internal NativePart Item { get; }
                internal UiLayoutRect Bounds { get; }
            }
        }
    }
}
