using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.UiGallery.Pages
{
    internal enum PresentationPage { Stingers, Overlays, Toasts }

    internal enum PresentationOverlay { Passive, Dismissible, ButtonOnly }

    /// <summary>Safe requests used by the Gallery and its presentation capture fixtures.</summary>
    internal static class PresentationPages
    {
        internal const string StingerQueueId = "gallery-stinger-queue";
        internal const string OverlayStackId = "gallery-overlay-stack";
        internal const string OverlayCloseId = "gallery-overlay-continue";
        internal const string ToastStackId = "gallery-toast-stack";

        internal static readonly IReadOnlyList<UiPaintRole> StingerRoles = Array.AsReadOnly(new[]
        {
            UiPaintRole.Surface, UiPaintRole.Raised, UiPaintRole.Sunken, UiPaintRole.Selected, UiPaintRole.Primary,
            UiPaintRole.Accent, UiPaintRole.Success, UiPaintRole.Warning, UiPaintRole.Danger
        });

        internal static readonly IReadOnlyList<PresentationOverlay> OverlayOrder = Array.AsReadOnly(new[]
        {
            PresentationOverlay.Passive, PresentationOverlay.Dismissible, PresentationOverlay.ButtonOnly
        });

        internal static readonly IReadOnlyList<UiTone> ToastTones = Array.AsReadOnly(new[]
        {
            UiTone.Neutral, UiTone.Success, UiTone.Warning, UiTone.Danger
        });

        internal static UiSurfaceRequest StingerControls(Action<UiStingerRequest> show, Action showQueue, UiThemeRef? theme = null)
        {
            var buttons = new List<UiNode>(StingerRoles.Count);
            foreach (var role in StingerRoles)
            {
                var request = Stinger(role);
                buttons.Add(new UiButton(StingerButtonId(role), role.ToString(), () => show(request), UiButtonStyle.Secondary));
            }

            return Controls("gallery-stingers", "Stingers", theme, new UiColumn(
                new UiGrid(buttons, 156f, maximumColumns: 3),
                new UiButton(StingerQueueId, "Play announcement queue", showQueue)));
        }

        internal static string StingerButtonId(UiPaintRole role) => "gallery-stinger-" + role.ToString().ToLowerInvariant();

        internal static UiStingerRequest Stinger(UiPaintRole role) => role switch
        {
            UiPaintRole.Surface => new UiStingerRequest("Route report", "The eastern trail is clear.", UiGlyph.Info, role),
            UiPaintRole.Raised => new UiStingerRequest("Lookout open", "Follow the stairs to the ridge.", UiGlyph.Eye, role),
            UiPaintRole.Sunken => new UiStingerRequest("Lower trail", "Stay below the tree line.", UiGlyph.Map, role),
            UiPaintRole.Selected => new UiStingerRequest("Route selected", "Head toward the blue marker.", UiGlyph.Flag, role),
            UiPaintRole.Primary => new UiStingerRequest("Depart for the ridge", "Meet at the trailhead.", UiGlyph.Next, role),
            UiPaintRole.Accent => new UiStingerRequest("Relay found", "The tower is east of the bridge.", UiGlyph.Search, role),
            UiPaintRole.Success => new UiStingerRequest("Supplies delivered", "The depot has been restocked.", UiGlyph.Check, role),
            UiPaintRole.Warning => new UiStingerRequest("Storm approaching", "Leave the ridge before the rain.", UiGlyph.Warning, role),
            UiPaintRole.Danger => new UiStingerRequest("Trail closed", "Return to the last marker.", UiGlyph.Cross, role),
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };

        internal static IReadOnlyList<UiStingerRequest> StingerQueue() => Array.AsReadOnly(new[]
        {
            new UiStingerRequest("Depot open", "Collect supplies at the gate.", UiGlyph.Info, durationSeconds: 1d),
            new UiStingerRequest("Water ready", "Fill your flask at the pump.", UiGlyph.Check, durationSeconds: 1d),
            new UiStingerRequest("Weather report", "The ridge will be windy.", UiGlyph.Warning, durationSeconds: 1d),
            new UiStingerRequest("Water ready", "Fill your flask at the pump.", UiGlyph.Check, durationSeconds: 2d),
            new UiStingerRequest("Lanterns lit", "The stairway is marked.", UiGlyph.Flag, durationSeconds: 1d),
            new UiStingerRequest("Gate closing", "Use the lower path.", UiGlyph.Lock, durationSeconds: 1d),
        });

        internal static UiSurfaceRequest OverlayControls(Action<PresentationOverlay> show, Action showStack, UiThemeRef? theme = null) =>
            Controls("gallery-presentation-overlays", "Overlays", theme, new UiColumn(
                new UiButton(OverlayButtonId(PresentationOverlay.Passive), "Show passive notice", () => show(PresentationOverlay.Passive), UiButtonStyle.Secondary),
                new UiButton(OverlayButtonId(PresentationOverlay.Dismissible), "Show Back-dismissible overlay", () => show(PresentationOverlay.Dismissible), UiButtonStyle.Secondary),
                new UiButton(OverlayButtonId(PresentationOverlay.ButtonOnly), "Show button-only overlay", () => show(PresentationOverlay.ButtonOnly), UiButtonStyle.Secondary),
                new UiButton(OverlayStackId, "Stack overlays over HUD", showStack)));

        internal static string OverlayButtonId(PresentationOverlay overlay) => "gallery-overlay-" + overlay.ToString().ToLowerInvariant();

        internal static UiSurfaceRequest Overlay(PresentationOverlay overlay, Action close, UiThemeRef? theme = null)
        {
            var request = overlay switch
            {
                PresentationOverlay.Passive => new UiSurfaceRequest("gallery-overlay-passive", "Route report",
                    "Conditions along the approach are stable.", UiSurfaceKind.Overlay, 640f, 250f,
                    new UiText("Keep to the marked trail.", UiTextStyle.Label)),
                PresentationOverlay.Dismissible => new UiSurfaceRequest("gallery-overlay-dismissible", "Trail markers",
                    "Check the new marker at the fork.", UiSurfaceKind.Overlay, 640f, 250f,
                    new UiButton(OverlayCloseId, "Continue", close)),
                PresentationOverlay.ButtonOnly => new UiSurfaceRequest("gallery-overlay-buttononly", "Route confirmation",
                    "Confirm the updated route to continue.", UiSurfaceKind.Overlay, 640f, 250f,
                    new UiButton(OverlayCloseId, "Confirm route", close)).WithDismissible(false),
                _ => throw new ArgumentOutOfRangeException(nameof(overlay)),
            };
            return Themed(request, theme);
        }

        internal static UiSurfaceRequest RetainedHud(UiThemeRef? theme = null) =>
            Themed(new UiSurfaceRequest("gallery-presentation-hud", "Route watch", "", UiSurfaceKind.Hud, 360f, 132f,
                HudStatus("Signal clear.")).WithHudAnchor(UiHudAnchor.BottomRight).WithHudVisibility(UiHudVisibility.Always), theme);

        internal static UiNode HudStatus(string label) => new UiText(label, UiTextStyle.Label, id: "gallery-presentation-status");

        internal static UiSurfaceRequest ToastControls(Action<UiTone> show, Action showStack, UiThemeRef? theme = null)
        {
            var buttons = new List<UiNode>(ToastTones.Count);
            foreach (var tone in ToastTones)
                buttons.Add(new UiButton("gallery-toast-" + tone.ToString().ToLowerInvariant(), tone.ToString(), () => show(tone), UiButtonStyle.Secondary));

            return Controls("gallery-toasts", "Toasts", theme, new UiColumn(
                new UiGrid(buttons, 156f, maximumColumns: 2),
                new UiButton(ToastStackId, "Show four toasts", showStack)));
        }

        internal static string ToastMessage(UiTone tone) => tone switch
        {
            UiTone.Neutral => "Route marked.",
            UiTone.Success => "Supplies delivered.",
            UiTone.Warning => "Storm near the ridge.",
            UiTone.Danger => "Trail closed.",
            _ => throw new ArgumentOutOfRangeException(nameof(tone)),
        };

        private static UiSurfaceRequest Controls(string id, string title, UiThemeRef? theme, UiNode content) =>
            Themed(new UiSurfaceRequest(id, title, "", UiSurfaceKind.Window, 760f, 410f, content), theme);

        private static UiSurfaceRequest Themed(UiSurfaceRequest request, UiThemeRef? theme) =>
            theme == null ? request : request.WithTheme(theme);
    }
}
