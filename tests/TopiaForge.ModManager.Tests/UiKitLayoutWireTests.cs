using System;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitLayoutWireTests
    {
        private const string Epoch = "layout-current";
        private const string Replacement = "layout-replaced";
        private const string Package = "example.field-layout";
        private static readonly byte[] Digest = Enumerable.Repeat((byte)0x29, 32).ToArray();
        private static readonly UiWireEnvelope Envelope = new UiWireEnvelope(Package, Digest, 4, null, new byte[32]);
        private static readonly UiInsets Insets = new UiInsets(UiSpace.Xs, UiSpace.Sm, UiSpace.Md, UiSpace.Lg);

        internal static void Run()
        {
            LegacyRowsAndColumnsKeepTheirBytes();
            OptionsAndOptionalFieldsUseTheAssignedTags();
            EveryLayoutFieldRoundTrips();
            WrappedClocksCheckAndExpireWithoutRebinding();
            CallbackRoutingCarriesCardDisablement();
            MalformedLayoutFieldsAreRefused();
            DecoderCountsWrappersAndBothCardChildren();
            TargetedFramesCountLayoutBytesAndPreserveClockOnRefusal();
            UiKitControlWireTests.Run();
            UiKitStatusWireTests.Run();
            UiActionContractsTests.Run();
            UiKitActionWireTests.Run();
            Console.WriteLine("K1 layout wire: tags, legacy bytes, bounded decoding, clocks and callbacks passed.");
        }

        private static void LegacyRowsAndColumnsKeepTheirBytes()
        {
            var legacy = new UiColumn(new UiRow(new UiText("A")));
            var expected = new byte[] { 2, 1, 3, 1, 1, 1, (byte)'A', 0, 0 };
            var emitted = Bytes(legacy, envelope: null);
            That(expected.SequenceEqual(emitted) && !UiWireCodec.NeedsEnvelope(legacy), "Existing row and column bytes stay unchanged.");
            var decoded = (UiColumn)Read(emitted, enhanced: false);
            var row = (UiRow)decoded.Children.Single();
            That(decoded.Layout.CrossAxisAlignment == UiCrossAxisAlignment.Stretch && row.Layout.CrossAxisAlignment == UiCrossAxisAlignment.Center,
                "Legacy defaults still stretch a column and center a row.");
            That(Bytes(decoded, envelope: null).SequenceEqual(expected), "A decoded legacy tree also writes the original tags.");
            var legacyWrapper = new UiColumn(new UiPadding(new UiText("A"), default));
            That(UiWireCodec.NeedsEnvelope(legacyWrapper), "An enhanced descendant requires an envelope under a legacy parent tag.");
            That(Body(Bytes(legacyWrapper)).SequenceEqual(new byte[] { 2, 1, 18, 0, 0, 0, 0, 0, 1, 1, (byte)'A', 0, 0 }),
                "The outer legacy tag is independent of descendant version requirements.");
        }

        private static void OptionsAndOptionalFieldsUseTheAssignedTags()
        {
            var layout = new UiFlexLayout(UiSpace.Xxl, Insets, UiMainAxisAlignment.SpaceAround);
            var row = new UiRow(Array.Empty<UiNode>(), layout, "r");
            var bytes = Bytes(row);
            That(Body(bytes).SequenceEqual(new byte[] { 16, 1, (byte)'r', 32, 4, 8, 12, 16, 4, 0, 0 }),
                "Row field order includes a zero cross-alignment presence flag for the authored null.");
            That(((UiRow)Read(bytes)).Layout.CrossAxisAlignment == null, "Null cross alignment round-trips as null.");
            Refused(() => Read(bytes, enhanced: false), "A legacy decoder refuses the version 2 root.");
            Refused(() => Read(new byte[] { 16, 0, 8, 0, 0, 0, 0, 0, 0, 0 }), "An enhanced tag requires its root envelope.");

            var column = new UiColumn(Array.Empty<UiNode>(), new UiFlexLayout(crossAxisAlignment: UiCrossAxisAlignment.Start));
            That(Body(Bytes(column)).SequenceEqual(new byte[] { 17, 0, 8, 0, 0, 0, 0, 0, 1, 0, 0 }),
                "Column writes explicit Start as present with integer zero.");
            That(((UiColumn)Read(Bytes(column))).Layout.CrossAxisAlignment == UiCrossAxisAlignment.Start, "Explicit Start stays authored.");
            var gap = new UiSizedBox(width: 12);
            That(Body(Bytes(gap)).SequenceEqual(new byte[] { 31, 0, 1, 0, 0, 64, 65, 0, 0 }),
                "SizedBox uses independent presence flags for width, height and child.");
            var divider = new UiDivider(UiLineStyle.Dashed, UiSpace.Xs, UiSpace.Md);
            That(Body(Bytes(divider)).SequenceEqual(new byte[] { 25, 0, 1, 4, 12 }), "Divider keeps its single-byte field order.");
        }

        private static void EveryLayoutFieldRoundTrips()
        {
            var timer = new UiCountdown(17.25, "Time", UiTone.Primary, "timer");
            var source = Fixture(timer);
            var bytes = Bytes(source);
            That(UiWireCodec.Measure(source, Epoch, bytes.Length, envelope: Envelope) == bytes.Length, "Measure walks exactly the encoded tree.");
            QuotaRefused(() => UiWireCodec.Measure(source, Epoch, bytes.Length - 1, envelope: Envelope));
            var decoded = (UiColumn)Read(bytes);
            That(decoded.Id == "composition" && decoded.Layout.Padding.Right == UiSpace.Md, "Column id and insets round-trip.");
            var row = Find<UiRow>(decoded, "row");
            That(row.Layout.Spacing == UiSpace.Xxl && row.Layout.MainAxisAlignment == UiMainAxisAlignment.SpaceEvenly &&
                row.Layout.CrossAxisAlignment == UiCrossAxisAlignment.End, "Row overrides round-trip.");
            var padding = Find<UiPadding>(decoded, "padding");
            var expanded = Find<UiExpanded>(decoded, "expanded");
            That(ReferenceEquals(expanded.Child, padding) && expanded.Flex == 3 && padding.Padding.Bottom == UiSpace.Lg &&
                ReferenceEquals(padding.Child, Find<UiCountdown>(decoded, "timer")), "Padding and expanded preserve child, id, tokens and weight.");
            That(Find<UiSpacer>(decoded, "spacer").Flex == 2, "Spacer preserves its id and weight.");

            var wrap = Find<UiWrap>(decoded, "wrap");
            That(wrap.Spacing == UiSpace.Xs && wrap.RunSpacing == UiSpace.Xl && wrap.Padding.Top == UiSpace.Sm &&
                wrap.Alignment == UiMainAxisAlignment.SpaceAround && wrap.Children.Single() is UiDivider
                { Id: "divider", LineStyle: UiLineStyle.Dashed, Before: UiSpace.Md, After: UiSpace.Xs },
                "Wrap gaps, insets, alignment and divider fields round-trip.");
            var grid = Find<UiGrid>(decoded, "grid");
            That(grid.MinimumCellWidth == 148.5f && grid.MaximumColumns == 7 && grid.Spacing == UiSpace.Xs &&
                grid.RunSpacing == UiSpace.Xxl && grid.Padding.Left == UiSpace.Xs, "Grid options round-trip without swapped spacing fields.");
            var card = (UiCard)grid.Children.Single();
            That(card.Id == "card" && card.Selected && card.Disabled && card.Style?.Id == "field-card" &&
                card.Padding is { Bottom: UiSpace.Lg } && card.Header is UiText { Id: "header", Text: "Supplies" },
                "Card preserves both state flags, named style, optional padding and header.");
            That(card.Child is UiBox { Id: "box", Width: 91.5f, Height: 29f, Fill: UiPaintRole.Raised, Style.Id: "supply.box" },
                "Box preserves dimensions, fill, id and strict style id.");
            var stack = Find<UiStack>(decoded, "stack");
            That(stack.Width == 220.25f && stack.Height == 160 && stack.Children.Single() is UiPositioned
            {
                Id: "placed", Left: 24f, Top: 12.5f, Width: null, Height: 52f, Child: UiSizedBox
                { Id: "sized", Width: 80f, Height: null, Child: UiText { Id: "label" } }
            },
                "Stack and positioned retain coordinates and SizedBox's independently null height.");
            That(Bytes(decoded).SequenceEqual(bytes), "Resending decoded nodes is byte-for-byte identical.");
        }

        private static void WrappedClocksCheckAndExpireWithoutRebinding()
        {
            var header = new UiCountdown(35d, "Header", UiTone.Accent, "header-clock");
            var child = new UiCountdown(19d, "Remaining", UiTone.Primary, "body-clock");
            var source = new UiCard(Fixture(child), new UiSizedBox(header, width: 180), id: "clock-card");
            _ = UiWireCodec.Measure(source, Epoch, 32768, envelope: Envelope);
            That(header.ClockEpoch == null && child.ClockEpoch == null, "Measuring a proposed tree does not bind either clock.");
            var writer = new WorldWireWriter(32768);
            That(UiWireCodec.Write(writer, source, Epoch, Envelope) && UiClockBinding.IsCurrent(source, Epoch),
                "Initial writing binds countdowns through the header, grid and other wrappers.");
            That(header.ClockEpoch == Epoch && child.ClockEpoch == Epoch && !UiClockBinding.IsCurrent(source, Replacement),
                "The authored timers keep the exact bound epoch when the presentation clock changes.");
            Refused(() => Read(Copy(writer), epoch: Replacement), "An initial wire payload cannot revive expired clocks.");
            var decodedOriginal = Read(Copy(writer));
            That(Find<UiCountdown>(decodedOriginal, "header-clock").EndsAt == 35d &&
                Find<UiCountdown>(decodedOriginal, "body-clock").EndsAt == 19d, "The decoder binds the unchanged deadline doubles.");

            var fresh = new UiCountdown(80d);
            var rejected = new UiCard(new UiPadding(child, default), new UiSizedBox(fresh, width: 120));
            writer.Reset();
            Refused(() => UiWireCodec.Write(writer, rejected, Replacement, Envelope), "New trees cannot adopt a stale descendant timer.");
            That(writer.Length == 0 && fresh.ClockEpoch == null, "Clock admission validates both card branches before writing or binding.");

            That(!UiWireCodec.WriteRetained(writer, source, Replacement, Envelope), "Expired retained countdowns do not request another clock refresh.");
            var shown = Read(Copy(writer), epoch: Replacement);
            That(Find<UiText>(shown, "header-clock").Text == "Header 0" && Find<UiText>(shown, "body-clock").Text == "Remaining 0" &&
                header.ClockEpoch == Epoch && child.ClockEpoch == Epoch, "Retained expiry writes zero without rebinding the authored tree.");
        }

        private static void CallbackRoutingCarriesCardDisablement()
        {
            var called = string.Empty;
            var wrapped = new UiCard(new UiPadding(new UiRow(new UiExpanded(new UiButton("action", "Go", () => called += "a"))), Insets),
                new UiSizedBox(new UiButton("header-action", "Details", () => called += "h"), height: 42), disabled: true);
            That(UiComposition.ContainsInteractive(wrapped), "Disabled card wrappers still count as interactive HUD content.");
            Refused(() => new UiSurfaceRequest("wrapped", "Controls", "", UiSurfaceKind.Hud, content: wrapped), "HUDs refuse controls inside wrappers.");
            That(!UiWireCodec.Invoke(wrapped, "action", "") && !UiWireCodec.Invoke(wrapped, "header-action", "") && called.Length == 0,
                "Routing refuses disabled card descendants, including header callbacks.");
            var decoded = Read(Bytes(wrapped), callback: (id, _) => called = id);
            That(!UiWireCodec.Invoke(decoded, "header-action", "") && called.Length == 0, "Card disablement survives wire decoding.");

            var enabled = new UiCard(wrapped.Child, wrapped.Header);
            That(UiWireCodec.Invoke(enabled, "action", "") && UiWireCodec.Invoke(enabled, "header-action", "") && called == "ah",
                "Enabled callbacks traverse sized, expanded and padding wrappers.");
            That(!UiWireCodec.Invoke(enabled, "action", "bogus"), "Traversal preserves per-control payload validation.");
        }

        private static UiColumn Fixture(UiCountdown timer) => new UiColumn(new UiNode[]
        {
            new UiRow(new UiNode[] { new UiExpanded(new UiPadding(timer, Insets, "padding"), 3, "expanded"),
                new UiSpacer(2, "spacer") }, new UiFlexLayout(UiSpace.Xxl, Insets, UiMainAxisAlignment.SpaceEvenly, UiCrossAxisAlignment.End), "row"),
            new UiWrap(new UiNode[] { new UiDivider(UiLineStyle.Dashed, UiSpace.Md, UiSpace.Xs, "divider") },
                UiSpace.Xs, UiSpace.Xl, Insets, UiMainAxisAlignment.SpaceAround, "wrap"),
            new UiGrid(new UiNode[] { new UiCard(new UiBox(91.5f, 29f, UiPaintRole.Raised, new UiStyleRef("supply.box"), "box"),
                new UiText("Supplies", id: "header"), new UiStyleRef("field-card"), selected: true, disabled: true, padding: Insets, id: "card") },
                148.5f, 7, UiSpace.Xs, UiSpace.Xxl, Insets, "grid"),
            new UiStack(new UiNode[] { new UiPositioned(new UiSizedBox(new UiText("Filed", id: "label"), width: 80, id: "sized"),
                24, 12.5f, height: 52, id: "placed") }, 220.25f, 160, "stack"),
        }, new UiFlexLayout(UiSpace.Xs, Insets), "composition");

        private static T Find<T>(UiNode root, string id) where T : UiNode =>
            UiLayoutTree.TryFind(root, id, out var node, out _) && node is T typed
                ? typed : throw new InvalidOperationException("The decoded tree lacks " + id + ".");

        private static byte[] Bytes(UiNode node) => Bytes(node, Envelope);

        private static byte[] Bytes(UiNode node, UiWireEnvelope? envelope)
        {
            var writer = new WorldWireWriter(32768);
            _ = UiWireCodec.Write(writer, node, Epoch, envelope);
            return Copy(writer);
        }

        private static byte[] Body(byte[] bytes) => bytes[(Package.Length + 69)..];

        private static byte[] Copy(WorldWireWriter writer) => writer.Segment.AsSpan().ToArray();

        private static UiNode Read(byte[] bytes, bool enhanced = true, string epoch = Epoch, Action<string, string>? callback = null)
        {
            var reader = new WorldWireReader();
            reader.Reset(bytes);
            var replied = callback ?? Ignore;
            var result = enhanced ? UiNodeWireCodec.Read(reader, replied, epoch, out _) : UiNodeWireCodec.Read(reader, replied, epoch);
            UiNodeWireCodec.End(reader);
            return result ?? throw new InvalidOperationException("A non-null layout was expected.");
        }

        private static void Ignore(string id, string value) { }

        private static void Refused(Action action, string reason)
        {
            try { action(); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException("The decoder or sender accepted an invalid layout. " + reason);
        }

        private static void QuotaRefused(Action action)
        {
            try { action(); }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("The layout exceeded the real frame capacity.");
        }

        private static void That(bool value, string requirement)
        {
            if (!value) throw new InvalidOperationException(requirement);
        }
    }
}
