using System;
using TopiaForge.Mods;

namespace TopiaForge.UiGallery.Pages
{
    internal enum StatusGalleryPage { Status, Countdowns }

    internal static class StatusGalleryPages
    {
        internal static UiSurfaceRequest CreateRequest(StatusGalleryPage page, UiSurfaceKind kind, double clockTime,
            UiThemeRef? theme = null)
        {
            var (id, title, height, content) = page switch
            {
                StatusGalleryPage.Status => ("gallery-status", "Status", 500f, Status()),
                StatusGalleryPage.Countdowns => ("gallery-countdowns", "Countdowns", 460f, Countdowns(clockTime)),
                _ => throw new ArgumentOutOfRangeException(nameof(page)),
            };
            var request = new UiSurfaceRequest(id, title, string.Empty, kind, 840f, height, content);
            if (kind == UiSurfaceKind.Hud)
            {
                var anchor = page == StatusGalleryPage.Status ? UiHudAnchor.TopLeft : UiHudAnchor.Left;
                request = request.WithHudAnchor(anchor).WithHudVisibility(UiHudVisibility.Always);
            }
            return theme == null ? request : request.WithTheme(theme);
        }

        private static UiNode Status()
        {
            var pressure = new UiThresholds(0.85, 0.95, UiThresholdDirection.AtOrAbove);
            var charge = new UiThresholds(0.40, 0.20);
            return new UiColumn(
                new UiGrid(new UiNode[]
                {
                    new UiColumn(
                        new UiText("Pressure", UiTextStyle.Heading),
                        UiProgress.WithThresholds(0.60f, "Pressure", pressure, id: "pressure-normal"),
                        UiProgress.WithThresholds(0.85f, "Pressure", pressure, id: "pressure-warning"),
                        UiProgress.WithThresholds(0.95f, "Pressure", pressure, id: "pressure-critical")),
                    new UiColumn(
                        new UiText("Charge", UiTextStyle.Heading),
                        UiProgress.WithThresholds(0.60f, "Charge", charge, id: "charge-normal"),
                        UiProgress.WithThresholds(0.40f, "Charge", charge, id: "charge-warning"),
                        UiProgress.WithThresholds(0.20f, "Charge", charge, id: "charge-critical")),
                }, minimumCellWidth: 300, maximumColumns: 2, spacing: UiSpace.Lg),
                new UiDivider(UiLineStyle.Solid),
                new UiText("Readings", UiTextStyle.Heading),
                new UiGrid(new UiNode[]
                {
                    new UiStatBar("Integrity", 54, 100, new UiThresholds(60, 45), id: "stat-value"),
                    new UiStatBar("Reserve", 48, 100, new UiThresholds(60, 48), UiStatFormat.ValueOfMaximum, id: "stat-maximum"),
                    new UiStatBar("Storage", 51, 100, new UiThresholds(45, 25), UiStatFormat.Percent, id: "stat-percent"),
                    new UiProgress(0.65f, "Signal reserve", UiTone.Success, id: "legacy-signal"),
                }, minimumCellWidth: 300, maximumColumns: 2, spacing: UiSpace.Lg));
        }

        private static UiNode Countdowns(double clockTime)
        {
            var start = Math.Floor(clockTime);
            var thresholds = new UiThresholds(10, 5);
            return new UiColumn(
                new UiText("Display formats", UiTextStyle.Heading),
                UiCountdown.WithFormat(start + 24, "Seconds", UiCountdownFormat.Seconds, thresholds, id: "timer-seconds"),
                UiCountdown.WithFormat(start + 7500, "Minutes and seconds", UiCountdownFormat.MinutesSeconds, id: "timer-minutes"),
                UiCountdown.WithFormat(start + 3723, "Hours, minutes and seconds", UiCountdownFormat.HoursMinutesSeconds, id: "timer-hours"),
                new UiDivider(UiLineStyle.Solid),
                new UiText("Deadline and thresholds", UiTextStyle.Heading),
                UiCountdown.WithFormat(start + 12, "Next transfer", UiCountdownFormat.MinutesSeconds, thresholds, id: "timer-transfer"),
                UiCountdown.WithFormat(start, "Elapsed", UiCountdownFormat.MinutesSeconds, thresholds, id: "timer-elapsed"));
        }
    }
}
