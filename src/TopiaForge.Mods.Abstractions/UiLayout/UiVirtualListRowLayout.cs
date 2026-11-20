using System;

namespace TopiaForge.Mods
{
    /// <summary>Fits the text of a fixed-height virtual row without changing its focus gutters.</summary>
    internal readonly struct UiVirtualListRowLayout
    {
        internal const UiPaintRole BadgeRole = UiPaintRole.Raised;
        private const double Inset = 8d;
        private const double TitleInset = 14d;
        private const double VerticalInset = 4d;
        private const double Gap = 8d;
        private const double BadgeHorizontalInset = 9d;
        private const double BadgeVerticalInset = 2d;
        private const double MinimumBadgeHeight = 22d;
        private const double FitTolerance = 0.001d;
        private const double MinimumRowWidth = 40d;

        private readonly UiLayoutExtent title;
        private readonly UiLayoutExtent? subtitle;
        private readonly UiLayoutExtent? badge;

        internal UiVirtualListRowLayout(UiLayoutExtent title, UiLayoutExtent? subtitle, UiLayoutExtent? badge)
        {
            this.title = title;
            this.subtitle = subtitle;
            this.badge = badge;
        }

        internal double RequiredWidth => Math.Max(MinimumRowWidth, TitleInset + Inset + title.Width +
            (subtitle.HasValue ? Gap + subtitle.Value.Width : 0d) +
            (badge.HasValue ? Gap + badge.Value.Width + 2d * BadgeHorizontalInset : 0d));

        internal void RequireFits(double viewportWidth, int index)
        {
            var height = UiVirtualListGeometry.RowHeight - 2d * VerticalInset;
            if (!Fits(title, height) || (subtitle.HasValue && !Fits(subtitle.Value, height)) ||
                (badge.HasValue && !Fits(badge.Value, height - 2d * BadgeVerticalInset)) ||
                RequiredWidth + 2d * UiVirtualListGeometry.Gutter > viewportWidth + FitTolerance)
                throw new ArgumentException("Virtual-list item at index " + index + " cannot fit its title, subtitle and badge.");
        }

        private static bool Fits(UiLayoutExtent extent, double height) =>
            double.IsFinite(extent.Width) && extent.Width >= 0d && extent.Height >= 0d && extent.Height <= height + FitTolerance;

        internal (UiLayoutRect Title, UiLayoutRect Subtitle, UiLayoutRect Badge, UiLayoutRect BadgeLabel) Place(double rowWidth)
        {
            var right = rowWidth - Inset;
            var badgeBox = default(UiLayoutRect);
            var badgeLabel = default(UiLayoutRect);
            if (badge is { } label)
            {
                var width = label.Width + 2d * BadgeHorizontalInset;
                var height = Math.Max(MinimumBadgeHeight, label.Height + 2d * BadgeVerticalInset);
                badgeBox = Centered(right - width, width, height);
                badgeLabel = Centered(badgeBox.X + BadgeHorizontalInset, label.Width, label.Height);
                right -= width + Gap;
            }

            var subtitleBox = default(UiLayoutRect);
            if (subtitle is { } secondary)
            {
                subtitleBox = Centered(right - secondary.Width, secondary.Width, secondary.Height);
                right -= secondary.Width + Gap;
            }

            return (Centered(TitleInset, Math.Max(0d, right - TitleInset), title.Height), subtitleBox, badgeBox, badgeLabel);
        }

        private static UiLayoutRect Centered(double x, double width, double height) =>
            new UiLayoutRect(x, (UiVirtualListGeometry.RowHeight - height) / 2d, width, height);
    }
}
