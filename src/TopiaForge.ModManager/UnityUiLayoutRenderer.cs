using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService
    {
        /// <summary>Retains a native view for each occurrence in a measured declarative tree.</summary>
        private sealed partial class UnityUiLayoutRenderer
        {
            private readonly UiNode content;
            private readonly TopiaForgeContainer stage;
            private readonly Dictionary<UiNode, NativeLeaf> leaves = new Dictionary<UiNode, NativeLeaf>(NodeIdentity.Instance);
            private readonly NativeNode root;

            private UnityUiLayoutRenderer(UiNode content, TopiaForgeContainer stage, UiThemeDescriptor? declared,
                UiCallbackGate callbacks, UiGraphRetentionTransaction graphs)
            {
                this.content = content;
                this.stage = stage;
                root = Create(content, stage, declared, UiPaintRole.Surface, false, callbacks, graphs);
                root.Container.EnsureLayoutElement().ignoreLayout = true;
            }

            /// <summary>Builds and verifies a replacement in the caller's hidden staging container.</summary>
            internal static UnityUiLayoutRenderer Stage(UiNode content, TopiaForgeContainer stage, UiLayoutConstraints limits,
                UiThemeDescriptor? declared, UiCallbackGate callbacks, UiGraphRetentionTransaction graphs)
            {
                UiLayoutTree.Validate(content);
                var renderer = new UnityUiLayoutRenderer(content, stage, declared, callbacks, graphs);
                renderer.Rearrange(limits, declared);
                return renderer;
            }

            /// <summary>Validates a dirty viewport or theme against the retained native leaves before applying geometry.</summary>
            internal void Rearrange(UiLayoutConstraints limits, UiThemeDescriptor? declared)
            {
                var theme = root.Container.ResolvedTheme;
                var effective = theme.Descriptor ?? UiThemePackParser.Default;
                var canvas = stage.Go.GetComponentInParent<Canvas>(includeInactive: true);
                var pixelsPerUnit = canvas != null && canvas.scaleFactor > 0 ? canvas.scaleFactor : 1f;
                var result = UiLayoutEngine.Arrange(content, limits, Measure, pixelsPerUnit, theme.HighContrast);
                var positions = PreparePositions(result, effective, declared, theme.HighContrast);
                var scheme = effective.SelectScheme(theme.Scheme == TopiaForgeScheme.Hud, theme.HighContrast);
                foreach (var position in positions) ApplyPosition(position, scheme, theme);
                stage.Fixed((float)result.Size.Width, (float)result.Size.Height);

                UiLayoutLeafMeasurement Measure(UiNode node, UiLayoutConstraints constraints, bool disabled)
                {
                    if (node is UiCard or UiBox)
                    {
                        var bounds = new UiLayoutExtent(constraints.MaxWidth, constraints.MaxHeight);
                        var decoration = UiLayoutDecoration.Resolve(node, effective, declared, bounds, theme.HighContrast);
                        return new UiLayoutLeafMeasurement(bounds, UiLayoutPaintKind.Painter, decoration.ShadowOutsets);
                    }

                    return leaves[node].Measure(node, constraints, disabled);
                }
            }

            private NativeNode Create(UiNode node, TopiaForgeContainer parent, UiThemeDescriptor? declared, UiPaintRole backdrop,
                bool ancestorDisabled, UiCallbackGate callbacks, UiGraphRetentionTransaction graphs)
            {
                var view = new NativeNode(parent.CreateChild(node.Id ?? node.GetType().Name));
                var childRoot = view.Container;
                switch (node)
                {
                    case UiCard or UiBox:
                        view.Shadow = AddGraphic<TopiaForgeRoundedImage>(view.Container, "Shadow");
                        view.Fill = AddGraphic<TopiaForgeRoundedImage>(view.Container, "Fill");
                        view.Border = AddGraphic<TopiaForgeRoundedImage>(view.Container, "Border");
                        if (node is UiCard card)
                        {
                            var theme = view.Container.ResolvedTheme;
                            var effective = theme.Descriptor ?? UiThemePackParser.Default;
                            backdrop = UiLayoutDecoration.Resolve(node, effective, declared, default, theme.HighContrast).Fill;
                            ancestorDisabled |= card.Disabled;
                            if (card.Selected) view.Mark = AddGraphic<Image>(view.Container, "Selection mark");
                        }
                        break;
                    case UiDivider:
                        view.Divider = AddGraphic<UnityUiLayoutDivider>(view.Container, "Divider");
                        break;
                    case UiStack:
                        view.Container.Go.AddComponent<RectMask2D>();
                        break;
                    case UiScroll:
                        childRoot = CreateScroll(view);
                        break;
                    case UiLayoutNode or UiPadding or UiSizedBox or UiExpanded or UiSpacer or UiPositioned or UiSplitPane:
                        break;
                    default:
                        view.Backplate = AddGraphic<Image>(view.Container, "Text backing");
                        view.Backplate.enabled = false;
                        var leafHost = view.Container.CreateChild("Widget");
                        TopiaForgeAnchors.Stretch(leafHost.Rect);
                        var paint = RenderNode(node, leafHost, callbacks, graphs, ancestorDisabled, backdrop);
                        if (node is UiGraphCanvas) return view;
                        var leaf = new NativeLeaf(leafHost, node, backdrop, paint);
                        view.Leaf = leaf;
                        // An id-less node may occur twice. Its font, content, and sizing inputs are the same per surface.
                        leaves.TryAdd(node, leaf);
                        return view;
                }

                var children = UiNodeChildren.Of(node);
                if (children.Count == 0) return view;
                view.Children = new NativeNode[children.Count];
                for (var index = 0; index < children.Count; index++)
                {
                    view.Children[index] = Create(children[index], childRoot, declared, backdrop, ancestorDisabled, callbacks, graphs);
                }
                return view;
            }

            private NativePlacement[] PreparePositions(UiLayoutResult result, UiThemeDescriptor effective, UiThemeDescriptor? declared,
                bool highContrast)
            {
                var positions = new NativePlacement[result.Placements.Count];
                var indices = new Dictionary<UiLayoutPlacement, int>(positions.Length);
                for (var index = 0; index < positions.Length; index++)
                {
                    var placement = result.Placements[index];
                    var hasParent = placement.Parent != null;
                    var parent = hasParent ? positions[indices[placement.Parent!]] : default;
                    var node = hasParent ? parent.View.Children[placement.ChildIndex] : root;
                    var backdrop = hasParent ? parent.ChildBackdrop : UiPaintRole.Surface;
                    var bounds = placement.Bounds;
                    if (hasParent) bounds = bounds.Translate(-parent.Placement.Bounds.X, -parent.Placement.Bounds.Y);
                    var decoration = placement.Node is UiCard or UiBox
                        ? UiLayoutDecoration.Resolve(placement.Node, effective, declared, new UiLayoutExtent(bounds.Width, bounds.Height), highContrast)
                        : default;
                    positions[index] = new NativePlacement(node, placement, bounds, backdrop, decoration, node.Leaf?.Prepare(placement));
                    indices.Add(placement, index);
                    if (hasParent && placement.Parent!.Node is UiScroll) positions[indices[placement.Parent]].ScrollHeight = bounds.Height;
                }
                return positions;
            }

            private static void ApplyPosition(NativePlacement position, UiThemeScheme scheme, TopiaForgeResolvedTheme theme)
            {
                var view = position.View;
                var bounds = position.Bounds;
                Place(view.Container.Rect, bounds);
                switch (position.Placement.Node)
                {
                    case UiCard or UiBox:
                        PaintDecoration(position, scheme, theme);
                        break;
                    case UiDivider divider:
                        var line = view.Divider!;
                        var thickness = Math.Max(0, Math.Min(theme.HighContrast ? 3 : 2, bounds.Height - (int)divider.Before));
                        Place(line.rectTransform, new UiLayoutRect(0, (int)divider.Before, bounds.Width, thickness));
                        line.SetDashed(divider.LineStyle == UiLineStyle.Dashed);
                        line.color = ColorOf(scheme.ResolveBoundary(position.Backdrop));
                        break;
                    case UiScroll:
                        var contentRect = view.Scroll!.content;
                        var canvasHeight = Math.Max(bounds.Height, position.ScrollHeight);
                        var offset = Math.Clamp(contentRect.anchoredPosition.y, 0, canvasHeight - bounds.Height);
                        Place(contentRect, new UiLayoutRect(0, -offset, bounds.Width, canvasHeight));
                        break;
                    default:
                        if (view.Backplate is { } backing)
                        {
                            position.Leaf?.Apply();
                            backing.enabled = position.Placement.RequiresBackplate;
                            if (backing.enabled)
                            {
                                TopiaForgeAnchors.Stretch(backing.rectTransform);
                                backing.color = ColorOf(scheme.GetPair(position.Backdrop).Fill);
                            }
                            view.Leaf?.ApplyBackdrop(position.Backdrop, theme);
                        }
                        break;
                }
            }

            private static void PaintDecoration(NativePlacement position, UiThemeScheme scheme, TopiaForgeResolvedTheme theme)
            {
                var view = position.View;
                var decoration = position.Decoration;
                var radius = (float)decoration.CornerRadius;
                var border = (float)decoration.BorderWidth;
                var bounds = position.Bounds;
                var pair = scheme.GetPair(decoration.Fill);
                var selected = position.Placement.Node is UiCard { Selected: true };
                var ringInk = selected ? scheme.ResolveFocus(position.Backdrop) : scheme.ResolveBoundary(position.Backdrop);
                var shadow = view.Shadow!;
                shadow.enabled = decoration.ShadowX != 0 || decoration.ShadowY != 0;
                if (shadow.enabled)
                {
                    Place(shadow.rectTransform, new UiLayoutRect(decoration.ShadowX, decoration.ShadowY, bounds.Width, bounds.Height));
                    var ink = ColorOf(scheme.GetPair(position.Backdrop).On);
                    ink.a = theme.Shadow.a;
                    shadow.color = ink;
                    shadow.SetShape(radius);
                }
                TopiaForgeAnchors.Stretch(view.Fill!.rectTransform);
                view.Fill.color = ColorOf(pair.Fill);
                view.Fill.SetShape(radius);
                view.Border!.enabled = border > 0;
                if (view.Border.enabled)
                {
                    TopiaForgeAnchors.Stretch(view.Border.rectTransform);
                    view.Border.color = ColorOf(ringInk);
                    view.Border.SetShape(radius, border);
                }
                if (view.Mark is { } mark && position.Placement.Node is UiCard card)
                {
                    var padding = card.Padding ?? new UiInsets(UiSpace.Lg);
                    var room = Math.Min((int)padding.Top, (int)padding.Right) - 2 * border;
                    var size = Math.Min(12, room);
                    mark.enabled = bounds.Width >= 12 && bounds.Height >= 2 * border;
                    if (!mark.enabled) return;
                    if (size >= 8)
                    {
                        mark.sprite = TopiaForgeSprites.Icon(TopiaForgeIcon.Check);
                        mark.color = ColorOf(pair.On);
                        Place(mark.rectTransform, new UiLayoutRect(bounds.Width - (int)padding.Right + border, border, size, size));
                    }
                    else
                    {
                        // Leave a notch in the selected outline when the caller leaves no room for a check mark.
                        mark.sprite = null;
                        mark.color = ColorOf(pair.Fill);
                        Place(mark.rectTransform, new UiLayoutRect((bounds.Width - 8) / 2, 0, 8, border));
                    }
                }
            }

            private static TopiaForgeContainer CreateScroll(NativeNode view)
            {
                var viewport = view.Container.CreateChild("Viewport");
                TopiaForgeAnchors.Stretch(viewport.Rect);
                viewport.Go.AddComponent<RectMask2D>();
                var hitArea = viewport.Go.AddComponent<Image>();
                hitArea.color = Color.clear;
                hitArea.raycastTarget = true;
                var content = viewport.CreateChild("Scroll content");
                Place(content.Rect, default);
                var scroll = view.Container.Go.AddComponent<ScrollRect>();
                scroll.viewport = viewport.Rect;
                scroll.content = content.Rect;
                scroll.horizontal = false;
                scroll.vertical = true;
                scroll.movementType = ScrollRect.MovementType.Clamped;
                scroll.scrollSensitivity = 28f;
                view.Scroll = scroll;
                return content;
            }

            private static T AddGraphic<T>(TopiaForgeContainer parent, string name) where T : Graphic
            {
                var graphic = parent.CreateChildGameObject(name).AddComponent<T>();
                graphic.raycastTarget = false;
                return graphic;
            }

            private static void Place(RectTransform rect, UiLayoutRect bounds) =>
                TopiaForgeAnchors.Place(rect, (float)bounds.X, (float)bounds.Y, (float)bounds.Width, (float)bounds.Height);

            private static Color ColorOf(UiThemeColor color) => new Color32(color.R, color.G, color.B, byte.MaxValue);

            private sealed class NodeIdentity : IEqualityComparer<UiNode>
            {
                internal static readonly NodeIdentity Instance = new NodeIdentity();

                public bool Equals(UiNode? first, UiNode? second) => ReferenceEquals(first, second);
                public int GetHashCode(UiNode node) => RuntimeHelpers.GetHashCode(node);
            }

            private sealed class NativeNode
            {
                internal NativeNode(TopiaForgeContainer container) => Container = container;

                internal TopiaForgeContainer Container { get; }
                internal NativeNode[] Children { get; set; } = Array.Empty<NativeNode>();
                internal TopiaForgeRoundedImage? Shadow { get; set; }
                internal TopiaForgeRoundedImage? Fill { get; set; }
                internal TopiaForgeRoundedImage? Border { get; set; }
                internal Image? Mark { get; set; }
                internal Image? Backplate { get; set; }
                internal ScrollRect? Scroll { get; set; }
                internal UnityUiLayoutDivider? Divider { get; set; }
                internal NativeLeaf? Leaf { get; set; }
            }

            private struct NativePlacement
            {
                internal NativePlacement(NativeNode view, UiLayoutPlacement placement, UiLayoutRect bounds,
                    UiPaintRole backdrop, UiLayoutDecoration decoration, LeafArrangement? leaf)
                {
                    View = view;
                    Placement = placement;
                    Bounds = bounds;
                    Backdrop = backdrop;
                    Decoration = decoration;
                    Leaf = leaf;
                    ScrollHeight = 0;
                }

                internal NativeNode View { get; }
                internal UiLayoutPlacement Placement { get; }
                internal UiLayoutRect Bounds { get; }
                internal UiPaintRole Backdrop { get; }
                internal UiPaintRole ChildBackdrop => Placement.Node is UiCard ? Decoration.Fill : Backdrop;
                internal UiLayoutDecoration Decoration { get; }
                internal LeafArrangement? Leaf { get; }
                internal double ScrollHeight { get; set; }
            }
        }
    }

    internal sealed class UnityUiLayoutDivider : MaskableGraphic
    {
        private bool dashed;

        internal void SetDashed(bool value)
        {
            if (dashed == value) return;
            dashed = value;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            var rect = GetPixelAdjustedRect();
            if (rect.width <= 0 || rect.height <= 0) return;
            if (!dashed)
            {
                AddDash(mesh, rect.xMin, rect.xMax, rect.yMin, rect.yMax, color);
                return;
            }
            for (var start = rect.xMin; start < rect.xMax; start += 10f)
            {
                AddDash(mesh, start, Mathf.Min(start + 6f, rect.xMax), rect.yMin, rect.yMax, color);
            }
        }

        private static void AddDash(VertexHelper mesh, float left, float right, float bottom, float top, Color32 color)
        {
            var first = mesh.currentVertCount;
            mesh.AddVert(new Vector2(left, bottom), color, Vector2.zero);
            mesh.AddVert(new Vector2(left, top), color, Vector2.zero);
            mesh.AddVert(new Vector2(right, top), color, Vector2.zero);
            mesh.AddVert(new Vector2(right, bottom), color, Vector2.zero);
            mesh.AddTriangle(first, first + 1, first + 2);
            mesh.AddTriangle(first, first + 2, first + 3);
        }
    }
}
