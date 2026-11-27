using System;
using System.Globalization;
using TopiaForge.Mods;
using TopiaForge.TestSupport;

namespace TopiaForge.ModManager.Tests
{
    internal static class UiArtContractTests
    {
        private static readonly UiImageRef Crate = new UiImageRef("survival", "crate");

        internal static void Run()
        {
            ReferencesAndLabels();
            MaskAndImageSizing();
            ImagePlacement();
            BadgeStatusGlyphs();
            PartialPipsStayWithinOneMark();
            SettledGeometryDoesNotAllocate();
            Console.WriteLine("UiArtContractTests passed.");
        }

        private static void ReferencesAndLabels()
        {
            var same = new UiImageRef("survival", "crate");
            Require(Crate.Equals(same) && Crate.GetHashCode() == same.GetHashCode(), "Equal image references have equal hashes.");
            Require(!Crate.Equals(new UiImageRef("another", "crate")) && !Crate.Equals(new UiImageRef("survival", "atlas")),
                "Both ids distinguish an image reference.");
            Require(new UiImageRef(new string('p', 64), "a0._-").ImageId == "a0._-", "The full safe id alphabet is supported.");
            Throws<ArgumentException>(() => new UiImageRef("Survival", "crate"));
            Throws<ArgumentException>(() => new UiImageRef("survival", "Crate"));
            Throws<ArgumentException>(() => new UiImageRef("topiaforge.pack", "crate"));
            Throws<ArgumentException>(() => new UiImageRef("survival", "topiaforge.image"));
            Throws<ArgumentException>(() => new UiImageRef("survival", "../crate"));
            Throws<ArgumentException>(() => new UiImageRef(new string('p', 65), "crate"));

            var expected = new[]
            {
                "check", "cross", "down", "next", "search", "warning", "info", "use",
                "microphone", "microphone off", "talk", "vote", "timer", "lock", "map", "notes",
                "eye", "bell", "flag", "star", "health", "shield", "energy", "settings",
                "player", "players", "host", "signal", "volume", "leave", "play"
            };
            Require(Enum.GetValues(typeof(UiGlyph)).Length == expected.Length, "The K1 glyph contract contains exactly 31 glyphs.");
            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
                for (var index = 0; index < expected.Length; index++)
                {
                    var icon = new UiIcon((UiGlyph)index);
                    Require(icon.SemanticLabel == expected[index], "The glyph wire value resolves its invariant semantic label.");
                    Require(icon.Size == 24f && icon.Tone == UiTone.Neutral && !icon.Decorative && icon.Id == null,
                        "Omitted icon options use the approved defaults.");
                }
                var custom = new UiIcon(UiGlyph.Info, "<b>Read</b>", 18, UiTone.Accent, id: "help");
                Require(custom.SemanticLabel == "<b>Read</b>" && custom.Id == "help", "Custom labels are kept as typed.");
                Require(new UiPips(5, 2, "Charges", 0.5f).DisplayLabel == "Charges: 2 / 5 (50% refilling)",
                    "The visible count and percentage use invariant formatting.");
            }
            finally
            {
                CultureInfo.CurrentCulture = culture;
            }

            Require(new UiIcon(UiGlyph.Info, decorative: true).SemanticLabel == string.Empty,
                "Decorative glyphs carry no invented label.");
            Require(new UiIcon(UiGlyph.Info, string.Empty, decorative: true).Decorative, "An explicitly empty decorative label is valid.");
            Throws<ArgumentException>(() => new UiIcon(UiGlyph.Info, string.Empty));
            Throws<ArgumentException>(() => new UiIcon(UiGlyph.Info, "   "));
            Throws<ArgumentException>(() => new UiIcon(UiGlyph.Info, "Hidden but labelled", decorative: true));
            Require(new UiIcon(UiGlyph.Next, new string('x', 256), 128).Size == 128, "The size and label upper bounds are inclusive.");
            Throws<ArgumentException>(() => new UiIcon(UiGlyph.Info, new string('x', 257)));
            Throws<ArgumentOutOfRangeException>(() => new UiIcon((UiGlyph)(-1)));
            Throws<ArgumentOutOfRangeException>(() => new UiIcon((UiGlyph)31));
            Throws<ArgumentOutOfRangeException>(() => new UiIcon(UiGlyph.Lock, size: 17.99f));
            Throws<ArgumentOutOfRangeException>(() => new UiIcon(UiGlyph.Lock, size: float.PositiveInfinity));
            Throws<ArgumentOutOfRangeException>(() => new UiIcon(UiGlyph.Lock, size: float.NaN));
            Throws<ArgumentOutOfRangeException>(() => new UiIcon(UiGlyph.Lock, tone: (UiTone)99));
        }

        private static void MaskAndImageSizing()
        {
            var natural = new UiImage(Crate, "A crate");
            var frame = UiImageFrame.Resolve(natural, 500, 250, isMask: false);
            Require(frame.Width == 500 && frame.Height == 250 && natural.Width == null && natural.Height == null,
                "Verified dimensions resolve without mutating the authored node.");

            var explicitWidth = new UiImage(Crate, "A crate", width: 200, tint: UiTone.Muted);
            var widthOnly = UiImageFrame.Resolve(explicitWidth, 500, 250, isMask: true);
            Require(widthOnly.Width == 200 && widthOnly.Height == 250 && explicitWidth.Height == null,
                "An omitted height follows verified pixels independently of a supplied width.");
            var heightOnly = UiImageFrame.Resolve(new UiImage(Crate, "A crate", height: 96), 500, 250, isMask: false);
            Require(heightOnly.Width == 500 && heightOnly.Height == 96, "An omitted width follows verified pixels independently.");

            Throws<ArgumentException>(() => UiImageFrame.Resolve(explicitWidth, 500, 250, isMask: false));
            Throws<ArgumentOutOfRangeException>(() => UiImageFrame.Resolve(natural, 2048, 250, isMask: false));
            Require(UiImageFrame.Resolve(explicitWidth, 2048, 250, isMask: true).Width == 200,
                "An oversized sprite axis is valid only when explicitly bounded.");
            Throws<ArgumentException>(() => UiImageFrame.Resolve(natural, 0, 250, isMask: false));
            Throws<ArgumentException>(() => UiImageFrame.Resolve(natural, 500, 2049, isMask: false));
            Throws<ArgumentNullException>(() => new UiImage(null!, "Image"));
            Throws<ArgumentNullException>(() => new UiImage(Crate, null!, decorative: true));
            Throws<ArgumentException>(() => new UiImage(Crate, string.Empty));
            Throws<ArgumentException>(() => new UiImage(Crate, "Labeled decoration", decorative: true));
            Require(new UiImage(Crate, string.Empty, 1, 1024, decorative: true).Decorative,
                "A decorative image supports the full size range.");
            Throws<ArgumentOutOfRangeException>(() => new UiImage(Crate, "A crate", width: 1025));
            Throws<ArgumentOutOfRangeException>(() => new UiImage(Crate, "A crate", height: float.NegativeInfinity));
            Throws<ArgumentOutOfRangeException>(() => new UiImage(Crate, "A crate", fit: (UiImageFit)2));
            Throws<ArgumentOutOfRangeException>(() => new UiImage(Crate, "A crate", tint: (UiTone)(-1)));
        }

        private static void ImagePlacement()
        {
            var contained = UiImageFrame.Resolve(new UiImage(Crate, "Box"), 500, 250, isMask: false).Place(250, 250);
            Require(contained.X == 0 && contained.Y == 62.5f && contained.Width == 250 && contained.Height == 125,
                "Contain centers the uncut image inside the box.");
            Require(contained.SourceLeft == 0 && contained.SourceTop == 0
                && contained.SourceWidth == 1 && contained.SourceHeight == 1, "Contain samples the whole sprite.");

            var covered = UiImageFrame.Resolve(new UiImage(Crate, "Box", fit: UiImageFit.Cover), 500, 250, isMask: false)
                .Place(250, 250);
            Require(covered.X == 0 && covered.Y == 0 && covered.Width == 250 && covered.Height == 250,
                "Cover fills the destination box without a larger draw rectangle.");
            Require(covered.SourceLeft == 0.25f && covered.SourceTop == 0
                && covered.SourceWidth == 0.5f && covered.SourceHeight == 1, "Cover crops centrally across the long axis.");

            var tall = UiImageFrame.Resolve(new UiImage(Crate, "Tall box", height: 1024, fit: UiImageFit.Cover),
                1, 2048, isMask: false);
            var cropped = tall.Place(1024, 1024);
            Require(cropped.Width == 1024 && cropped.Height == 1024 && cropped.SourceWidth == 1
                && cropped.SourceHeight == (1f / 2048) && cropped.SourceTop > 0,
                "A tall sprite also stays bounded and crops along the correct axis.");
            Throws<ArgumentOutOfRangeException>(() => tall.Place(0, 200));
        }

        private static void BadgeStatusGlyphs()
        {
            var named = new UiStyleRef("status.ring");
            var badge = new UiBadge("Ready", UiPaintRole.Success, UiBadgeStyle.Outlined, UiGlyph.Star, named, "ready");
            Require(badge.Text == "Ready" && badge.Role == UiPaintRole.Success && badge.Presentation == UiBadgeStyle.Outlined
                && badge.Style == named && badge.Id == "ready", "Badge options remain authored values.");
            Require(UiBadgePresentation.Glyph(badge, highContrast: false) == UiGlyph.Star, "Ordinary badges use the requested glyph.");
            Require(UiBadgePresentation.Glyph(badge, highContrast: true) == UiGlyph.Check && badge.Icon == UiGlyph.Star,
                "A high-contrast success badge substitutes its required glyph without mutating the node.");
            Require(UiBadgePresentation.Glyph(new UiBadge("Warning", UiPaintRole.Warning), true) == UiGlyph.Warning,
                "A high-contrast warning badge adds its required glyph when omitted.");
            Require(UiBadgePresentation.Glyph(new UiBadge("Blocked", UiPaintRole.Danger, icon: UiGlyph.Info), true) == UiGlyph.Cross,
                "A high-contrast danger badge replaces an incompatible glyph.");
            Require(UiBadgePresentation.Glyph(new UiBadge("Map", UiPaintRole.Accent, icon: UiGlyph.Map), true) == UiGlyph.Map,
                "High contrast keeps a requested glyph for non-status roles.");
            Throws<ArgumentException>(() => new UiBadge(string.Empty));
            Throws<ArgumentException>(() => new UiBadge(new string('x', 257)));
            Throws<ArgumentOutOfRangeException>(() => new UiBadge("Status", role: (UiPaintRole)9));
            Throws<ArgumentOutOfRangeException>(() => new UiBadge("Status", presentation: (UiBadgeStyle)2));
            Throws<ArgumentOutOfRangeException>(() => new UiBadge("Status", icon: (UiGlyph)31));
        }

        private static void PartialPipsStayWithinOneMark()
        {
            var pips = new UiPips(5, 2, "Charges", 0.5f);
            var dots = new UiPipTrack(pips, 61);
            Require(dots.Width == 40 && dots.Height == 62, "Dots stay readable and wrap in source order.");
            var complete = dots.Mark(0).Fill(2f, 9f);
            Require(complete.VisibleBounds is { X: 2, Y: 2, Width: 14, Height: 14 } && complete.Radius == 7f &&
                dots.Mark(1).Fill(2f, 9f).VisibleBounds.Width == 14, "Earlier dots fill the full rounded inset.");
            Require(dots.Mark(2).Fill(2f, 9f).VisibleBounds is { X: 2, Y: 24, Width: 7, Height: 14 },
                "Only the next dot fills a fraction of the drawable interior.");
            Require(!dots.Mark(3).Fill(2f, 9f).VisibleBounds.HasArea && !dots.Mark(4).Fill(2f, 9f).VisibleBounds.HasArea,
                "Later dots remain empty.");

            var segments = new UiPipTrack(new UiPips(3, 1, "Charges", 0.75f, UiPipStyle.Segments), 80);
            Require(segments.Width == 80 && segments.Height == 8 && segments.Mark(2).X == 56,
                "Segments grow to fill the row while keeping space between marks.");
            Require(segments.Mark(1).Width == 24 && segments.Mark(1).Fill(2f, 0f).VisibleBounds is { Width: 15, Height: 4 } &&
                !segments.Mark(2).Fill(2f, 0f).VisibleBounds.HasArea,
                "A partially filled segment uses the same fraction and leaves later marks empty.");

            var sliver = new UiPipTrack(new UiPips(1, 0, "Charge", 0.1f, UiPipStyle.Segments), 18).Mark(0).Fill(3f, 4f);
            Require(sliver.Interior is { X: 3, Y: 3, Width: 12, Height: 2 } && sliver.Radius == 1f &&
                sliver.VisibleBounds is { X: 3, Y: 3, Height: 2 } && Math.Abs(sliver.VisibleBounds.Width - 1.2) < 0.0001,
                "A ten-percent fill remains visible within a minimum-width high-contrast segment.");

            var full = new UiPips(1, 1, "Charge");
            Require(full.DisplayLabel == "Charge: 1 / 1" && new UiPipTrack(full, 18).Mark(0).Fill(2f, 0f).VisibleBounds.Width == 14,
                "A complete track shows its count and no refill caption.");
            Require(new UiPips(32, 0, "Charge", 0.99999994f).DisplayLabel == "Charge: 0 / 32 (99% refilling)",
                "An incomplete refill never displays completion.");
            Throws<ArgumentOutOfRangeException>(() => new UiPips(0, 0, "Charge"));
            Throws<ArgumentOutOfRangeException>(() => new UiPips(33, 0, "Charge"));
            Throws<ArgumentOutOfRangeException>(() => new UiPips(2, 3, "Charge"));
            Throws<ArgumentOutOfRangeException>(() => new UiPips(1, 1, "Charge", 0.1f));
            Throws<ArgumentOutOfRangeException>(() => new UiPips(1, 0, "Charge", 1f));
            Throws<ArgumentOutOfRangeException>(() => new UiPips(1, 0, "Charge", float.NaN));
            Throws<ArgumentException>(() => new UiPips(1, 0, ""));
            Throws<ArgumentOutOfRangeException>(() => new UiPips(1, 0, "Charge", presentation: (UiPipStyle)2));
            Throws<ArgumentOutOfRangeException>(() => new UiPipTrack(full, 17));
            Throws<ArgumentOutOfRangeException>(() => dots.Mark(5));
        }

        private static void SettledGeometryDoesNotAllocate()
        {
            var frame = UiImageFrame.Resolve(new UiImage(Crate, "A crate"), 500, 250, isMask: false);
            var pips = new UiPips(5, 2, "Charges", 0.5f);
            var track = new UiPipTrack(pips, 60);
            var sum = 0d;
            var bytes = AllocationProbe.Measure("resolved art and status reads",
                () => sum += frame.Place(250, 250).Height + track.Mark(2).Fill(2f, 4f).VisibleBounds.Width + pips.DisplayLabel.Length,
                4096, warmups: 64);
            Require(bytes == 0 && sum > 0f, "Repeated resolved art and status reads allocate no managed bytes.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Throws<TException>(Action action) where TException : ArgumentException
        {
            try { action(); }
            catch (TException) { return; }
            throw new InvalidOperationException("Expected " + typeof(TException).Name + ".");
        }
    }
}
