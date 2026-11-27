using System;
using TopiaForge.Mods;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.ModManager
{
    internal static partial class UiGalleryCaptureFixture
    {
        private static UiSurfaceRequest? MatchStatus(UiSurfaceRequest request, UiThemeRef? theme, bool remote)
        {
            switch (request.Title)
            {
                case "Status":
                    var status = StatusGalleryPages.CreateRequest(StatusGalleryPage.Status, request.Kind, 0d, theme);
                    return SameStatusSurface(request, status, remote) && SameNode(request.Content, status.Content) ? status : null;
                case "Countdowns":
                    if (request.Content is not UiColumn { Children.Count: > 0 } column) return null;
                    var last = column.Children[column.Children.Count - 1];
                    var retained = remote && last is UiText;
                    var origin = last is UiCountdown { Id: "timer-elapsed" } timer ? timer.EndsAt : 0d;
                    if (double.IsInfinity(origin) || double.IsNaN(origin) || origin < 0d || origin != Math.Floor(origin) ||
                        (!retained && last is not UiCountdown { Id: "timer-elapsed" })) return null;
                    var clocks = StatusGalleryPages.CreateRequest(StatusGalleryPage.Countdowns, request.Kind, origin, theme);
                    if (!SameStatusSurface(request, clocks, remote) || !(retained ? SameRetainedCountdowns(column, clocks.Content!) :
                        SameNode(column, clocks.Content))) return null;
                    // The exact matched tree carries the bound clock epoch. Use the canonical fixture id for naming its observation.
                    return StatusProjection(clocks, column);
                default:
                    return null;
            }
        }

        internal static UiSurfaceRequest? MatchRetainedStatus(UiSurfaceRequest original, UiNode? admitted,
            UiWireEnvelope first, UiWireEnvelope? latest, ref ulong generation, string clockEpoch)
        {
            if (latest == null || first.OwnerPackageId != RemoteFixtureOwner || latest.OwnerPackageId != first.OwnerPackageId ||
                !latest.PackageDigest.Span.SequenceEqual(first.PackageDigest.Span) || latest.Generation < generation) return null;
            generation = latest.Generation;
            if (latest.ThemeId != first.ThemeId || !latest.DescriptorDigest.Span.SequenceEqual(first.DescriptorDigest.Span) ||
                original.Id != "gallery-countdowns" || admitted is not UiColumn actual || original.Content is not UiColumn timers ||
                timers.Children.Count == 0 || timers.Children[timers.Children.Count - 1] is not UiCountdown anchor ||
                anchor.ClockEpoch == null || anchor.ClockEpoch == clockEpoch || !SameRetainedCountdowns(actual, timers)) return null;
            return StatusProjection(original, actual);
        }

        private static UiSurfaceRequest StatusProjection(UiSurfaceRequest fixture, UiNode content)
        {
            var matched = new UiSurfaceRequest(fixture.Id, fixture.Title, fixture.Body, fixture.Kind, fixture.Width, fixture.Height, content);
            if (fixture.Kind == UiSurfaceKind.Hud)
                matched = matched.WithHudAnchor(fixture.HudAnchor).WithHudVisibility(fixture.HudVisibility);
            return fixture.Theme == null ? matched : matched.WithTheme(fixture.Theme);
        }

        private static bool SameStatusSurface(UiSurfaceRequest actual, UiSurfaceRequest expected, bool remote) =>
            (remote || actual.Id == expected.Id) && actual.Title == expected.Title && actual.Body == expected.Body &&
            actual.Width == expected.Width && actual.Height == expected.Height && actual.HudAnchor == expected.HudAnchor &&
            actual.HudVisibility == expected.HudVisibility;

        private static bool SameRetainedCountdowns(UiColumn actual, UiNode expected) =>
            expected is UiColumn timers && actual.Id == timers.Id && SameFlex(actual.Layout, timers.Layout) &&
            SameEntries(actual.Children, timers.Children, (observed, fixture) => fixture is UiCountdown countdown
                ? observed is UiText zero && zero.Id == countdown.Id && zero.Style == UiTextStyle.Body &&
                    zero.Tone == countdown.Tone &&
                    zero.Text == UiCountdownFormatting.Text(countdown.Label, 0, countdown.Format)
                : SameNode(observed, fixture));

        private static bool SameThresholds(UiThresholds? actual, UiThresholds? expected) =>
            actual == null ? expected == null : expected != null && actual.Warning == expected.Warning &&
            actual.Critical == expected.Critical && actual.Direction == expected.Direction;
    }
}
