using System;

namespace TopiaForge.Mods
{
    internal readonly struct UiLayoutDecoration
    {
        private UiLayoutDecoration(string? styleId, UiPaintRole fill, double radius, double border, double shadowX, double shadowY)
        {
            StyleId = styleId;
            Fill = fill;
            CornerRadius = radius;
            BorderWidth = border;
            ShadowX = shadowX;
            ShadowY = shadowY;
        }

        internal string? StyleId { get; }
        internal UiPaintRole Fill { get; }
        internal double CornerRadius { get; }
        internal double BorderWidth { get; }
        internal double ShadowX { get; }
        internal double ShadowY { get; }
        internal UiLayoutOutsets ShadowOutsets => new UiLayoutOutsets(
            Math.Max(0, -ShadowX), Math.Max(0, -ShadowY), Math.Max(0, ShadowX), Math.Max(0, ShadowY));

        internal static bool AdmitsNamedStyles(UiNode? node, UiThemeDescriptor descriptor)
        {
            if (node == null) return true;
            (UiStyleRef? Style, UiThemeTarget Target, UiPaintRole? FixedRole) named = node switch
            {
                UiCard card => (card.Style, UiThemeTarget.Card, null),
                UiBox box => (box.Style, UiThemeTarget.Box, null),
                UiBadge badge => (badge.Style, UiThemeTarget.Badge, badge.Role),
                UiPips pips => (pips.Style, UiThemeTarget.Pips, pips.Role),
                _ => (null, default, null),
            };
            var (style, target, fixedRole) = named;
            if (style != null && (!descriptor.Styles.TryGetValue(style.Id, out var definition) || definition.Target != target ||
                fixedRole.HasValue && definition.Fill.HasValue && definition.Fill.Value != fixedRole.Value)) return false;
            var children = UiNodeChildren.Of(node);
            for (var index = 0; index < children.Count; index++)
            {
                if (!AdmitsNamedStyles(children[index], descriptor)) return false;
            }
            return true;
        }

        /// <summary>Resolves component paint after validating the named style against its admitted descriptor.</summary>
        /// <remarks>The declared descriptor can be absent for an authenticated remote fallback. A local kit-default
        /// surface supplies the kit descriptor for both arguments.</remarks>
        internal static UiLayoutDecoration Resolve(UiNode node, UiThemeDescriptor effective, UiThemeDescriptor? declared,
            UiLayoutExtent size, bool highContrast)
        {
            var (target, style, fill, selected, fixedRole) = node switch
            {
                UiCard card => (UiThemeTarget.Card, card.Style, UiPaintRole.Surface, card.Selected, false),
                UiBox box => (UiThemeTarget.Box, box.Style, box.Fill, false, false),
                UiBadge badge => (UiThemeTarget.Badge, badge.Style, badge.Role, false, true),
                UiPips pips => (UiThemeTarget.Pips, pips.Style, pips.Role, false, true),
                _ => throw new ArgumentException("The node has no theme component decoration.", nameof(node)),
            };

            string? styleId = null;
            var component = effective.Components[target];
            if (style != null && declared != null)
            {
                if (!declared.Styles.TryGetValue(style.Id, out var definition) || definition.Target != target)
                    throw new ArgumentException("The named style must be declared for this component.", nameof(node));
                if (fixedRole && definition.Fill.HasValue && definition.Fill.Value != fill)
                    throw new ArgumentException("The named status style must preserve the requested paint role.", nameof(node));

                if (effective.Digest.Span.SequenceEqual(declared.Digest.Span) &&
                    effective.Styles.TryGetValue(style.Id, out var applied) && applied.Target == target)
                {
                    styleId = style.Id;
                    component = applied.Component;
                    if (!fixedRole) fill = applied.Fill ?? UiPaintRole.Surface;
                }
            }

            var radius = effective.Shape.Resolve(component.Radius, (float)size.Width, (float)size.Height);
            var halfShortEdge = Math.Min(size.Width, size.Height) / 2d;
            var border = (double)component.Border;
            if (selected) border = Math.Max(2d, border);
            if (highContrast && border > 0) border = Math.Max(3d, border);
            var shadow = highContrast ? UiThemeShadow.None : component.Shadow;
            var (shadowX, shadowY) = shadow switch
            {
                UiThemeShadow.Small => (-3d, 4d),
                UiThemeShadow.Card => (-4d, 8d),
                _ => (0d, 0d),
            };
            return new UiLayoutDecoration(styleId, fill, Math.Min(radius, halfShortEdge), border, shadowX, shadowY);
        }
    }
}
