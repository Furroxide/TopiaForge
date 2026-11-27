using System;
using TopiaForge.Mods;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.ModManager
{
    internal static partial class UiGalleryCaptureFixture
    {
        internal const string PresentationHudId = "gallery-presentation-hud";
        internal const string ToastControlsId = "gallery-toasts";
        private const string StingerControlsId = "gallery-stingers";

        internal static bool MatchesCommittedBody(UiSurfaceRequest original, string? update, UiSurfaceRequest selected) =>
            (update ?? original.Body) == selected.Body;

        private static UiSurfaceRequest? MatchPresentation(string owner, UiWireEnvelope? envelope, UiSurfaceRequest request,
            UiThemeDescriptor? declared)
        {
            var remote = envelope != null;
            var themeId = remote ? envelope!.ThemeId : DeclaredThemeId(declared);
            if (!PresentationTheme(owner, themeId, envelope?.DescriptorDigest, declared, out var theme)) return null;
            if (remote ? request.Theme != null || !IsTransportId(request.Id, "remote-") :
                request.Theme != null && request.Theme.Id != themeId) return null;

            if (request.Kind == UiSurfaceKind.Overlay)
            {
                foreach (var mode in PresentationPages.OverlayOrder)
                {
                    var expected = PresentationPages.Overlay(mode, static () => { }, theme);
                    if (Matches(expected)) return expected;
                }
            }
            else if (request.Kind == UiSurfaceKind.Hud)
            {
                var expected = PresentationPages.RetainedHud(theme);
                if (Matches(expected)) return expected;
            }
            else if (request.Kind == UiSurfaceKind.Window && owner == Owner && !remote)
            {
                var expected = request.Id switch
                {
                    StingerControlsId => PresentationPages.StingerControls(static _ => { }, static () => { }, theme),
                    "gallery-presentation-overlays" => PresentationPages.OverlayControls(static _ => { }, static () => { }, theme),
                    ToastControlsId => PresentationPages.ToastControls(static _ => { }, static () => { }, theme),
                    _ => null,
                };
                if (expected != null && Matches(expected)) return expected;
            }
            return null;

            bool Matches(UiSurfaceRequest expected) => (remote || expected.Id == request.Id) &&
                request.Kind == expected.Kind && request.Dismissible == expected.Dismissible &&
                request.Title == expected.Title && request.Body == expected.Body && request.Width == expected.Width &&
                request.Height == expected.Height && request.HudAnchor == expected.HudAnchor &&
                request.HudVisibility == expected.HudVisibility && SameNode(request.Content, expected.Content);
        }

        internal static UiSurfaceRequest? MatchRetainedPresentation(UiSurfaceRequest fixture, UiNode? content,
            UiThemeDescriptor? originalTheme, UiThemeDescriptor? currentTheme, UiWireEnvelope? original,
            UiWireEnvelope? latest, ref ulong generation)
        {
            if (fixture.Id != PresentationHudId || !MatchesPresentationTheme(originalTheme, currentTheme, original) ||
                (original == null) != (latest == null)) return null;
            if (original != null && (latest!.Generation < generation || original.OwnerPackageId != latest.OwnerPackageId ||
                original.ThemeId != latest.ThemeId || !original.PackageDigest.Span.SequenceEqual(latest.PackageDigest.Span) ||
                !original.DescriptorDigest.Span.SequenceEqual(latest.DescriptorDigest.Span))) return null;
            if (latest != null) generation = latest.Generation;

            var initial = PresentationPages.RetainedHud(fixture.Theme);
            if (!SameNode(content, initial.Content))
            {
                var updated = PresentationPages.HudStatus("Supplies loaded.");
                if (!SameNode(content, updated)) return null;
                initial = StatusProjection(initial, updated);
            }
            return initial;
        }

        internal static bool MatchesPresentationTheme(UiThemeDescriptor? original, UiThemeDescriptor? current,
            UiWireEnvelope? envelope) => envelope == null ? SameDescriptor(original, current) :
                current == null || DeclaredThemeId(current) == envelope.ThemeId &&
                (envelope.ThemeId == null || current.Digest.Span.SequenceEqual(envelope.DescriptorDigest.Span));

        internal static bool SameDescriptor(UiThemeDescriptor? first, UiThemeDescriptor? second) =>
            ReferenceEquals(first, second) || (first != null && second != null && first.Id == second.Id &&
            first.Digest.Span.SequenceEqual(second.Digest.Span));

        internal static string? MatchStinger(string owner, UiWireEventEnvelope? envelope, UiStingerRequest request,
            UiThemeDescriptor? declared)
        {
            if (!MatchesOwner(owner, envelope != null, envelope?.OwnerPackageId) ||
                !PresentationTheme(owner, envelope == null ? DeclaredThemeId(declared) : envelope.ThemeId,
                    envelope?.DescriptorDigest, declared, out _)) return null;

            foreach (var role in PresentationPages.StingerRoles)
                if (SameStinger(request, PresentationPages.Stinger(role))) return PresentationPages.StingerButtonId(role);
            foreach (var offer in PresentationPages.StingerQueue())
                if (SameStinger(request, offer)) return PresentationPages.StingerQueueId;
            return null;
        }

        internal static bool MatchesToast(string message, UiTone tone) =>
            (uint)tone <= (uint)UiTone.Danger && message == PresentationPages.ToastMessage(tone);

        private static bool SameStinger(UiStingerRequest first, UiStingerRequest second) =>
            first.Title == second.Title && first.Subtitle == second.Subtitle && first.Role == second.Role &&
            first.Glyph == second.Glyph && first.Style?.Id == second.Style?.Id && first.DurationSeconds == second.DurationSeconds;

        private static string? DeclaredThemeId(UiThemeDescriptor? descriptor) =>
            ReferenceEquals(descriptor, UiThemePackParser.Default) ? null : descriptor?.Id;

        private static bool PresentationTheme(string owner, string? id, ReadOnlyMemory<byte>? wireDigest,
            UiThemeDescriptor? declared, out UiThemeRef? theme)
        {
            theme = null;
            var declaredId = DeclaredThemeId(declared);
            if (id == null) return declaredId == null;
            if (declaredId != null && declaredId != id) return false;
            if (owner == Owner)
            {
                if (id != FoundationPages.SurvivalTheme.Id || declaredId != id) return false;
                theme = FoundationPages.SurvivalTheme;
                return true;
            }

            if (id != FoundationPages.TransportTheme.Id || (wireDigest == null && declaredId == null) ||
                wireDigest.HasValue && !wireDigest.Value.Span.SequenceEqual(Transport.Value.Digest.Span) ||
                declaredId != null && !SameDescriptor(declared, Transport.Value)) return false;
            theme = FoundationPages.TransportTheme;
            return true;
        }
    }
}
