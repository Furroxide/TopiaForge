using System;
using System.Linq;
using TopiaForge.Mods;
using TopiaForge.MultiplayerAcceptance;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiGalleryNativeStatusTests
    {
        private const string FirstClock = "status-recording-first";
        private const string NextClock = "status-recording-next";
        private const string Owner = UiGalleryCaptureFixture.Owner;
        private const string Publisher = UiGalleryCaptureFixture.RemoteFixtureOwner;
        private const string Receiver = UiPresentationTheme.ReceiverOwner;
        private static readonly byte[] Package = Enumerable.Repeat((byte)0x58, 32).ToArray();

        internal static void Run()
        {
            ReadOnlyCanonicalTimerText();
            UseObservedTrackProjection();
            MatchExactStatusFixtures();
            FollowOnlyAuthenticatedRetainedCountdowns();
            CaptureNativeStatusChildren();
            Console.WriteLine("Native status recorder source checks passed for exact fixtures, observed timer text and paint geometry, and retained ownership.");
        }

        private static void ReadOnlyCanonicalTimerText()
        {
            foreach (var (text, caption, format, expected) in new[]
            {
                ("Relay 24", "Relay", UiCountdownFormat.Seconds, 24),
                ("RELAY 59", "Relay", UiCountdownFormat.Seconds, 59),
                ("0:00", "", UiCountdownFormat.MinutesSeconds, 0),
                ("1:00", "", UiCountdownFormat.MinutesSeconds, 60),
                ("125:00", "", UiCountdownFormat.MinutesSeconds, 7500),
                ("1:02:03", "", UiCountdownFormat.HoursMinutesSeconds, 3723),
                ("596523:14:07", "", UiCountdownFormat.HoursMinutesSeconds, int.MaxValue),
            })
                Check(UiGalleryStatusEvidence.TryReadCountdown(text, caption, format, true, out var actual) && actual == expected,
                    "The recorder accepts only the actual canonical numeric value and its approved caption.");

            foreach (var (text, caption, format) in new (string?, string, UiCountdownFormat)[]
            {
                (null, "", UiCountdownFormat.Seconds), ("", "", UiCountdownFormat.Seconds),
                ("Alert 24", "Relay", UiCountdownFormat.Seconds), ("Relay  24", "Relay", UiCountdownFormat.Seconds),
                ("Relay -24", "Relay", UiCountdownFormat.Seconds), ("024", "", UiCountdownFormat.Seconds),
                ("24\u200B", "", UiCountdownFormat.Seconds), ("0:5", "", UiCountdownFormat.MinutesSeconds),
                ("00:05", "", UiCountdownFormat.MinutesSeconds), ("0:60", "", UiCountdownFormat.MinutesSeconds),
                ("1:60:00", "", UiCountdownFormat.HoursMinutesSeconds), ("596523:14:08", "", UiCountdownFormat.HoursMinutesSeconds),
                ("1:02:03:00", "", UiCountdownFormat.HoursMinutesSeconds),
            })
                Check(!UiGalleryStatusEvidence.TryReadCountdown(text, caption, format, false, out _),
                    "Untrusted or noncanonical renderer text must fail recording.");
        }

        private static void UseObservedTrackProjection()
        {
            var track = new UiLayoutRect(50, 70, 400, 64);
            var fill = new UiLayoutRect(50, 70, 199.75, 64);
            var empty = new UiLayoutRect(249.75, 70, 200.25, 64);
            Check(UiGalleryStatusEvidence.TryMeasureSplit(track, fill, empty, .5f, out var fraction) &&
                Math.Abs(fraction - .499375f) < .00001f,
                "The recorder returns the observed split fraction from real screen frames.");
            Check(UiGalleryStatusEvidence.TryMeasureSplit(track, track, new UiLayoutRect(450, 70, 0, 64), 1f, out fraction) && fraction == 1f &&
                UiGalleryStatusEvidence.TryMeasureSplit(track, new UiLayoutRect(50, 70, 0, 64), track, 0f, out fraction) && fraction == 0f,
                "Zero-width inactive clips preserve the correct full and empty endpoints.");
            Check(!UiGalleryStatusEvidence.TryMeasureSplit(track, fill, empty.Translate(2, 0), .5f, out _) &&
                !UiGalleryStatusEvidence.TryMeasureSplit(track, fill, empty.Translate(-2, 0), .5f, out _) &&
                !UiGalleryStatusEvidence.TryMeasureSplit(track, fill, empty.Translate(0, 2), .5f, out _) &&
                !UiGalleryStatusEvidence.SamePaintBounds(track, fill),
                "Gapped, overlapping, moved masks or a cropped fill hull fail native paint geometry.");
            Check(!UiGalleryStatusEvidence.TryMeasureFill(track, new UiLayoutRect(50, 70, 50, 64), .5f, out _) &&
                !UiGalleryStatusEvidence.TryMeasureFill(track, new UiLayoutRect(50, 70, double.NaN, 64), .5f, out _) &&
                !UiGalleryStatusEvidence.TryMeasureFill(track, new UiLayoutRect(50, 70, -1, 64), 0f, out _),
                "A stale pre-layout legacy fill width or invalid measurement cannot be recorded as the authored fraction.");
        }

        private static void MatchExactStatusFixtures()
        {
            var descriptor = new AcceptanceGalleryPackage().Descriptor;
            for (var index = 0; index < GalleryRemotePreview.StatusVariantCount; index++)
            {
                var request = GalleryRemotePreview.Request(GalleryRemotePreview.StatusStart + index, 120.7d);
                var expectedId = index < 4 ? "gallery-status" : "gallery-countdowns";
                Check(request.Id == expectedId && request.Kind == ((index & 1) == 0 ? UiSurfaceKind.Window : UiSurfaceKind.Hud) &&
                    request.Theme?.Id == ((index & 2) == 0 ? null : FoundationPages.TransportTheme.Id),
                    "The eight status routes are independent of the preceding K1 route count.");
                var palette = request.Theme == null ? null : descriptor;
                Check(UiGalleryCaptureFixture.Match(Publisher, false, null, request, palette)?.Id == expectedId &&
                    (palette == null || UiGalleryCaptureFixture.Match(Publisher, false, null, request) == null),
                    "The local acceptance process may record the exact opt-in fixture with its admitted palette.");
                var envelope = Envelope(10, palette);
                var decoded = RoundTrip(request.Content!, envelope, FirstClock, retained: false, out var received);
                var routed = Reframe(request, decoded);
                var matched = UiGalleryCaptureFixture.Match(Receiver, true, received, routed, palette);
                Check(matched?.Id == expectedId && UiGalleryCaptureFixture.Match(Owner, true, received, routed) == null &&
                    UiGalleryCaptureFixture.Match(Receiver, true, null, routed) == null &&
                    UiGalleryCaptureFixture.Match(Receiver, true, Envelope(10, null, owner: "io.example.publisher"), routed) == null,
                    "Native recording requires the exact loader-owned authenticated route.");
                if (index < 4) continue;
                Check(ReferenceEquals(matched!.Content, decoded) && Last(decoded) is UiCountdown { EndsAt: 120, ClockEpoch: FirstClock },
                    "The approved timer fixture keeps its admitted clock identity without exporting a deadline.");
            }

            var local = StatusGalleryPages.CreateRequest(StatusGalleryPage.Status, UiSurfaceKind.Window, 0d, FoundationPages.SurvivalTheme);
            Check(UiGalleryCaptureFixture.Match(Owner, false, null, local)?.Id == local.Id, "The ordinary Gallery accepts its own Survival fixture.");
            var legacy = (UiProgress)((UiGrid)Last(local.Content!)).Children[^1];
            Check(legacy is { Id: "legacy-signal", Thresholds: null } &&
                UiGalleryCaptureFixture.Match(Owner, false, null, ReplaceLast(local, new UiProgress(.64f, legacy.Label, legacy.Tone,
                    id: legacy.Id))) == null,
                "An edited legacy fill does not inherit the trusted fixture id.");

            var timers = GalleryRemotePreview.Request(GalleryRemotePreview.StatusStart + 4, 120d);
            Check(UiGalleryCaptureFixture.Match(Publisher, false, null, ReplaceLast(timers,
                UiCountdown.WithFormat(120.25d, "Elapsed", UiCountdownFormat.MinutesSeconds, new UiThresholds(10, 5), id: "timer-elapsed"))) == null &&
                UiGalleryCaptureFixture.Match(Publisher, false, null, ReplaceLast(timers,
                UiCountdown.WithFormat(120d, "Private text", UiCountdownFormat.MinutesSeconds, new UiThresholds(10, 5), id: "timer-elapsed"))) == null,
                "A noncanonical timer origin or edited caption cannot opt into export.");
        }

        private static void FollowOnlyAuthenticatedRetainedCountdowns()
        {
            var request = GalleryRemotePreview.Request(GalleryRemotePreview.StatusStart + 4, 120d);
            var first = Envelope(10);
            var live = RoundTrip(request.Content!, first, FirstClock, retained: false, out var incoming);
            var initial = UiGalleryCaptureFixture.Match(Receiver, true, incoming, Reframe(request, live))!;
            var latest = Envelope(12);
            var zero = RoundTrip(live, latest, NextClock, retained: true, out var retained);
            var high = first.Generation;
            Check(UiGalleryCaptureFixture.MatchRetainedStatus(initial, zero, first, retained, ref high, FirstClock) == null,
                "The bound live epoch cannot be replaced for a zero-only capture.");
            Check(UiGalleryCaptureFixture.MatchRetainedStatus(initial, zero, first, retained, ref high, NextClock)?.Id == initial.Id &&
                high == latest.Generation && Last(zero) is UiText { Text: "Elapsed 0:00", Style: UiTextStyle.Body, Tone: UiTone.Danger },
                "The committed next epoch and latest same-package envelope admit only the real codec's zero projection.");
            Check(UiGalleryCaptureFixture.MatchRetainedStatus(initial, zero, first, Envelope(11), ref high, NextClock) == null &&
                UiGalleryCaptureFixture.MatchRetainedStatus(initial, zero, first, Envelope(13, package: new byte[32]), ref high, NextClock) == null,
                "A stale generation or changed package hash cannot re-arm the verified capture.");
            var changed = ReplaceLast(Reframe(request, zero), new UiText("Player input", UiTextStyle.Body, UiTone.Danger, "timer-elapsed")).Content;
            Check(UiGalleryCaptureFixture.MatchRetainedStatus(initial, changed, first, Envelope(13), ref high, NextClock) == null &&
                UiGalleryCaptureFixture.MatchRetainedStatus(initial, zero, first, retained, ref high, NextClock) == null,
                "A newer edited tree closes the capture route to all earlier generations.");
            Check(UiGalleryCaptureFixture.Match(Receiver, true, retained, Reframe(request, zero))?.Id == request.Id &&
                UiGalleryCaptureFixture.Match(Publisher, false, null, Reframe(request, zero, localId: true)) == null,
                "An authenticated fresh receiver may observe the exact retained surrogate, and a local authored counterfeit is refused.");
        }

        private static UiWireEnvelope Envelope(ulong generation, UiThemeDescriptor? palette = null,
            string owner = Publisher, byte[]? package = null) => new UiWireEnvelope(owner, package ?? Package, generation,
                palette?.Id, palette == null ? new byte[32] : palette.Digest.Span);

        private static UiNode RoundTrip(UiNode tree, UiWireEnvelope sent, string clock, bool retained, out UiWireEnvelope? observed)
        {
            var writer = new WorldWireWriter(32 * 1024);
            if (retained)
            {
                Check(!UiWireCodec.WriteRetained(writer, tree, clock, sent), "The stale native fixture carries no rebinding deadline.");
            }
            else UiWireCodec.Write(writer, tree, clock, sent);
            var reader = new WorldWireReader();
            reader.Reset(writer.Segment.ToArray());
            var decoded = UiWireCodec.ReadWithEnvelope(reader, (_, _) => { }, clock, out observed);
            UiWireCodec.End(reader);
            return decoded!;
        }

        private static UiNode Last(UiNode root) => ((UiColumn)root).Children[^1];

        private static UiSurfaceRequest ReplaceLast(UiSurfaceRequest request, UiNode last)
        {
            var children = ((UiColumn)request.Content!).Children.ToArray();
            if (children[^1] is UiGrid readings)
            {
                var cells = readings.Children.ToArray();
                cells[^1] = last;
                children[^1] = new UiGrid(cells, readings.MinimumCellWidth, readings.MaximumColumns,
                    readings.Spacing, readings.RunSpacing, readings.Padding, readings.Id);
            }
            else children[^1] = last;
            return Reframe(request, new UiColumn(children), localId: !request.Id.StartsWith("remote-", StringComparison.Ordinal),
                preserveTheme: true);
        }

        private static UiSurfaceRequest Reframe(UiSurfaceRequest request, UiNode content, bool localId = false, bool preserveTheme = false)
        {
            var framed = new UiSurfaceRequest(localId ? request.Id : "remote-24", request.Title, request.Body, request.Kind,
                request.Width, request.Height, content);
            if (request.Kind == UiSurfaceKind.Hud) framed = framed.WithHudAnchor(request.HudAnchor).WithHudVisibility(request.HudVisibility);
            return preserveTheme && request.Theme != null ? framed.WithTheme(request.Theme) : framed;
        }

        private static void Check(bool condition, string detail)
        {
            if (!condition) throw new InvalidOperationException(detail);
        }
    }
}
