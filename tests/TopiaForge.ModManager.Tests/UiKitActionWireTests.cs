using System;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static class UiKitActionWireTests
    {
        private const string Epoch = "session:5:0";
        private const string NextEpoch = "session:5:1";
        private static readonly UiWireEnvelope Envelope = new UiWireEnvelope(
            "example.mod", Enumerable.Repeat((byte)19, 32).ToArray(), 7, "surface-theme", Enumerable.Repeat((byte)23, 32).ToArray());
        private static readonly byte[] ImageDigest = Enumerable.Range(1, 32).Select(number => (byte)number).ToArray();
        private static readonly byte[] Header = Encode(null)[..^1];
        private static readonly InputBinding[] Defaults =
        {
            InputBinding.Key("Q"),
            InputBinding.GamepadButton(InputGamepadButton.LeftShoulder),
        };

        internal static void Run()
        {
            Tag40UsesTheOwnedDefinition();
            Tags41And42HaveTheirExactFields();
            MalformedActionFramesAreRefused();
            ClockChecksFollowTraversalAndCommit();
            RetainedFramesEncodeExpiredSurrogates();
            MeasureMatchesTheBoundedFrame();
            CompleteTargetedFrameUsesTheSameCap();
            Console.WriteLine("UI action tags 40–42, exact payloads, package defaults and retained clocks passed.");
        }

        private static void Tag40UsesTheOwnedDefinition()
        {
            var glyph = new UiKeyGlyph("confirm", "key");
            var projected = (UiKeyGlyph)Project(glyph);
            var encoding = Encode(projected);
            Require(Body(encoding).SequenceEqual(KeyPayload()) && UiWireCodec.RequiredContractVersion(new UiColumn(glyph)) == 3 &&
                Decode(encoding) is UiKeyGlyph { Id: "key", ActionName: "confirm", HasWireDefaults: true } decoded &&
                decoded.WireDefaults.Select(value => value.Control).SequenceEqual(new[] { "Q", "LeftShoulder" }),
                "Tag 40 keeps the exact id, case-sensitive action and ordered display defaults.");
            Require(!glyph.HasWireDefaults && glyph.WireDefaults.Count == 0 &&
                !UiComposition.ContainsInteractive(projected) && !UiWireCodec.Invoke(projected, "key", ""),
                "Projecting defaults leaves the author's glyph untouched and adds no callback.");
            Refuse(() => UiWireCodec.ProjectInputDefaults(new UiKeyGlyph("Confirm", "bad-key"), GetDefaults), "bad-key");
            Refuse(() => UiWireCodec.ProjectInputDefaults(glyph, _ => Enumerable.Repeat(InputBinding.Key("F"), 5).ToArray()), "four");
            Refuse(() => Encode(glyph), "owning action");
            Refuse(() => UiWireCodec.Write(new WorldWireWriter(), projected), "ownership envelope");
            Refuse(() => UiWireCodec.Read(Reader(encoding), Ignore, Epoch));
            Refuse(() => Decode(Body(encoding)));
            Refuse(() => Decode(Frame(new byte[] { 52, 0 })));

            var empty = (UiKeyGlyph)UiWireCodec.ProjectInputDefaults(glyph, _ => Array.Empty<InputBinding>())!;
            Require(Decode(Encode(empty)) is UiKeyGlyph { HasWireDefaults: true, WireDefaults.Count: 0 },
                "A present empty defaults list has a valid wire shape.");
        }

        private static void Tags41And42HaveTheirExactFields()
        {
            var cooldown = new UiCooldown(20, 8);
            var refill = new UiCooldown(30, 10);
            var style = new UiStyleRef("action.edge");
            var slot = new UiActionSlot("confirm", "Confirm", UiGlyph.Check, detail: "Ready", emphasis: true,
                cooldown: cooldown, charges: new UiCharges(2, 3, refill), unavailableReason: "Wait", style: style, id: "slot");
            var normalized = (UiActionSlot)Project(slot);
            var frame = Encode(normalized);
            Require(Body(frame).SequenceEqual(SlotPayload()) && UiWireCodec.RequiredContractVersion(slot) == 3 &&
                Decode(frame) is UiActionSlot
                {
                    Id: "slot", Glyph: UiGlyph.Check, Image: null, Label: "Confirm", Detail: "Ready",
                    Style.Id: "action.edge", Emphasis: true, UnavailableReason: "Wait", Cooldown.EndsAt: 20,
                    Cooldown.DurationSeconds: 8, Cooldown.ClockEpoch: Epoch, Charges.Available: 2, Charges.Maximum: 3,
                    Charges.Refill.DurationSeconds: 10, Charges.Refill.ClockEpoch: Epoch
                } decoded &&
                decoded.WireDefaults.Select(value => value.Control).SequenceEqual(new[] { "Q", "LeftShoulder" }),
                "Tag 41 encodes every slot field and its current epoch in contract order.");
            Require(ReferenceEquals(normalized.Cooldown, cooldown) && ReferenceEquals(normalized.Charges?.Refill, refill) &&
                !slot.HasWireDefaults && slot.PackDescriptorDigest.IsEmpty,
                "Normalizing a slot preserves the original deadline identities.");

            var image = new UiImageRef("icons", "emblem");
            var imageSlot = new UiActionSlot("confirm", "Confirm", image: image, detail: "Ready", emphasis: true,
                cooldown: cooldown, charges: slot.Charges, unavailableReason: "Wait", style: style, id: slot.Id);
            Refuse(() => Encode(Project(imageSlot)), "package-local descriptor");
            Refuse(() => imageSlot.WithWireImageDigest(new byte[31]), "SHA-256");
            Refuse(() => slot.WithWireImageDigest(ImageDigest), "SHA-256");
            var digest = ImageDigest.ToArray();
            var signed = imageSlot.WithWireImageDigest(digest);
            digest[0] = 0;
            frame = Encode(Project(signed));
            Require(Body(frame).SequenceEqual(SlotPayload(glyphPresent: 0, imagePresent: 1)) &&
                Decode(frame) is UiActionSlot { Glyph: null, Image.PackId: "icons", Image.ImageId: "emblem" } imported &&
                imported.PackDescriptorDigest.Span.SequenceEqual(ImageDigest) &&
                !imported.PackDescriptorDigest.Span.SequenceEqual(Envelope.DescriptorDigest.Span) && !imageSlot.HasWireDefaults,
                "Tag 41 carries the icon pack's digest independently of the surface theme.");

            var pips = UiPips.WithRefill(4, 3, "Cells", refill, UiPipStyle.Segments, UiPaintRole.Warning, new UiStyleRef("pips.edge"), "pips");
            frame = Encode(pips);
            Require(Body(frame).SequenceEqual(PipPayload()) && UiWireCodec.RequiredContractVersion(pips) == 3 &&
                Decode(frame) is UiPips
                {
                    Id: "pips", Total: 4, Filled: 3, Label: "Cells", Fraction: 0,
                    Presentation: UiPipStyle.Segments, Role: UiPaintRole.Warning, Style.Id: "pips.edge", Refill.EndsAt: 30,
                    Refill.DurationSeconds: 10, Refill.ClockEpoch: Epoch
                },
                "Tag 42 preserves the whole count, appearance and refill deadline.");

            var staticPips = new UiPips(4, 3, "Cells", 0.5f, UiPipStyle.Segments, UiPaintRole.Warning, new UiStyleRef("pips.edge"), "pips");
            Require(Body(Encode(staticPips)).SequenceEqual(StaticPipPayload(3, .5f)) &&
                UiWireCodec.RequiredContractVersion(staticPips) == 2 && Decode(Encode(staticPips)) is UiPips { Refill: null, Fraction: .5f },
                "Static pips retain K1's exact bytes, tag and version.");
        }

        private static void MalformedActionFramesAreRefused()
        {
            Malformed(KeyPayload(action: ""));
            Malformed(KeyPayload(action: new string('A', 65)));
            Malformed(KeyPayload(count: 5));
            Malformed(KeyPayload(kind: 5));
            Malformed(KeyPayload(control: new string('é', 17)));
            Malformed(KeyPayload(control: "indeterminate", kind: (byte)InputBindingKind.GamepadButton));
            Malformed(FramePayload(w =>
            {
                w.WriteByte(40); w.WriteByte(0); w.WriteByte(1); w.WriteByte(0xff); w.WriteByte(0);
            }));
            Malformed(SlotPayload(glyphPresent: 2));
            Malformed(SlotPayload(glyph: 255));
            Malformed(SlotPayload(label: new string('x', 25)));
            Malformed(SlotPayload(detail: new string('x', 49)));
            Malformed(SlotPayload(reason: new string('x', 49)));
            Malformed(SlotPayload(imagePresent: 1));
            Malformed(SlotPayload(glyphPresent: 0, imagePresent: 1, pack: "INVALID"));
            Malformed(SlotPayload(glyphPresent: 0, imagePresent: 1, image: "topiaforge.reserved"));
            var imagePayload = SlotPayload(glyphPresent: 0, imagePresent: 1);
            var digestOffset = imagePayload.AsSpan().IndexOf(ImageDigest);
            Require(digestOffset >= 0, "The image test fixture contains its digest.");
            Malformed(imagePayload[..(digestOffset + 20)]);
            Malformed(SlotPayload(emphasis: 2));
            Malformed(SlotPayload(cooldownFlag: 2));
            Malformed(SlotPayload(chargesFlag: 3));
            Malformed(SlotPayload(refillFlag: 2));
            Malformed(SlotPayload(available: 3));
            Malformed(SlotPayload(maximum: 0));
            Malformed(SlotPayload(deadline: double.NaN));
            Malformed(SlotPayload(deadline: -1));
            Malformed(SlotPayload(duration: 0));
            Malformed(SlotPayload(duration: double.PositiveInfinity));
            Malformed(SlotPayload(style: "ACTION.EDGE"));
            Malformed(SlotPayload(epoch: NextEpoch));
            Malformed(SlotPayload(epoch: new string('e', 257)));
            Malformed(PipPayload(4));
            Malformed(PipPayload(presentation: 2));
            Malformed(PipPayload(role: 9));
            Malformed(PipPayload(duration: 86_401));
            Malformed(PipPayload(epoch: NextEpoch));
            Malformed(PipPayload().Concat(new byte[] { 0 }).ToArray());
        }

        private static void ClockChecksFollowTraversalAndCommit()
        {
            var active = new UiCooldown(20, 8);
            var charge = new UiCooldown(30, 10);
            var pip = new UiCooldown(40, 12);
            var node = new UiTabs("tabs", new[] { new UiTab("first", "First", new UiText("Idle")),
                new UiTab("second", "Second", new UiPadding(new UiColumn(
                    new UiActionSlot("confirm", "Confirm", cooldown: active, charges: new UiCharges(2, 3, charge)),
                    UiPips.WithRefill(4, 3, "Cells", pip), UiPips.WithRefill(5, 4, "Backup", active)), new UiInsets(UiSpace.Sm))) }, "first");
            var projected = Project(node);
            Require(!UiClockBinding.IsCurrent(projected, Epoch) &&
                UiWireCodec.Measure(projected, Epoch, 32768, envelope: Envelope) > 0 &&
                active.ClockIdentity == null && charge.ClockIdentity == null && pip.ClockIdentity == null,
                "Measuring through hidden wrappers never admits a pending clock.");
            var staged = new WorldWireWriter(32768);
            Require(UiWireCodec.Prepare(staged, projected, Epoch, false, Envelope) && active.ClockIdentity == null &&
                charge.ClockIdentity == null && pip.ClockIdentity == null, "Staging the full frame does not bind clocks.");
            Encode(projected);
            Require(UiClockBinding.IsCurrent(projected, Epoch) && !UiClockBinding.IsCurrent(projected, NextEpoch) &&
                active.ClockEpoch == Epoch && ReferenceEquals(active.ClockIdentity, charge.ClockIdentity) &&
                ReferenceEquals(active.ClockIdentity, pip.ClockIdentity), "Both slot clocks and hidden pips bind to the same admitted identity.");

            var pending = new UiCooldown(50, 10);
            var clock = active.ClockIdentity;
            var invalid = Project(new UiColumn(UiPips.WithRefill(4, 3, "Fresh", pending), new UiActionSlot("confirm", "Confirm",
                charges: new UiCharges(2, 3, charge))));
            var writer = new WorldWireWriter();
            Refuse(() => UiWireCodec.Write(writer, invalid, NextEpoch, Envelope), "ended clock epoch");
            Require(writer.Length == 0 && pending.ClockIdentity == null && ReferenceEquals(active.ClockIdentity, clock),
                "The stale refill rejects the mixed tree before any bytes or binding changes.");
            Refuse(() => UiClockBinding.Bind(new UiColumn(UiPips.WithRefill(4, 3, "Fresh", pending), UiPips.WithRefill(4, 3, "Old", pip)), NextEpoch),
                "ended clock epoch");
            Require(pending.ClockIdentity == null, "Local mixed admission leaves a fresh pip unbound when a later pip is stale.");
        }

        private static void RetainedFramesEncodeExpiredSurrogates()
        {
            var active = new UiCooldown(20, 8);
            var refill = new UiCooldown(30, 10);
            var slot = (UiActionSlot)Project(new UiActionSlot("confirm", "Confirm", UiGlyph.Check, detail: "Ready", emphasis: true,
                cooldown: active, charges: new UiCharges(2, 3, refill), unavailableReason: "Wait", style: new UiStyleRef("action.edge"), id: "slot"));
            var pips = UiPips.WithRefill(4, 3, "Cells", refill, UiPipStyle.Segments, UiPaintRole.Warning, new UiStyleRef("pips.edge"), "pips");
            Encode(slot);
            Encode(pips);
            var writer = new WorldWireWriter(32768);
            Require(UiWireCodec.WriteRetained(writer, slot, NextEpoch, Envelope), "Even an untimed retained tag 41 requires the new epoch field.");
            var arrived = Copy(writer);
            Require(Body(arrived).SequenceEqual(SlotPayload(cooldownFlag: 0, available: 3, refillFlag: 0, epoch: NextEpoch)) &&
                Decode(arrived, NextEpoch) is UiActionSlot { Cooldown: null, Charges.Available: 3, Charges.Refill: null } &&
                slot.Charges?.Available == 2 && active.ClockEpoch == Epoch && refill.ClockEpoch == Epoch,
                "The retained slot omits stale deadlines and advances its encoded charge once.");
            writer.Reset();
            Require(!UiWireCodec.WriteRetained(writer, pips, NextEpoch, Envelope) &&
                Body(Copy(writer)).SequenceEqual(StaticPipPayload(4, 0f)) &&
                Decode(Copy(writer), NextEpoch) is UiPips { Filled: 4, Fraction: 0, Refill: null } && pips.Filled == 3,
                "A retained stale refill uses the K1 pip encoding of its completed count.");
            writer.Reset();
            var ready = Project(new UiActionSlot("confirm", "Confirm"));
            Require(UiWireCodec.Prepare(writer, ready, Epoch, false, Envelope), "An untimed tag 41 still schedules clock-epoch refresh.");
            Refuse(() => Decode(Copy(writer), NextEpoch), "stale");
        }

        private static void MeasureMatchesTheBoundedFrame()
        {
            var active = new UiCooldown(20, 8);
            var action = Project(new UiColumn(new UiActionSlot("confirm", "Confirm", cooldown: active), UiPips.WithRefill(4, 3, "Cells", active)));
            var measure = UiWireCodec.Measure(action, Epoch, 32768, envelope: Envelope);
            Require(measure > 0 && active.ClockIdentity == null, "Measuring an action frame leaves it pending.");
            try
            {
                UiWireCodec.Measure(action, Epoch, measure - 1, envelope: Envelope);
                throw new Exception("An undersized action frame was accepted.");
            }
            catch (InvalidOperationException)
            {
                Require(active.ClockIdentity == null, "A capacity failure changes no deadline.");
            }
            var writer = new WorldWireWriter(measure);
            UiWireCodec.Write(writer, action, Epoch, Envelope);
            Require(writer.Length == measure && active.ClockEpoch == Epoch, "An exact-size frame commits only after encoding.");
            Refuse(() => UiWireCodec.Measure(action, new string('e', 257), 32768, retained: true, envelope: Envelope), "256");
        }

        private static void CompleteTargetedFrameUsesTheSameCap()
        {
            const int cap = 32 * 1024;
            const string target = "recipient";
            var request = new UiSurfaceRequest("frame", "Action frame", "Actions");
            var deadline = new UiCooldown(50, 12);
            var filler = Enumerable.Range(0, 7).Select(_ => (UiNode)new UiText(new string('x', 4096))).ToArray();
            UiNode Tree(int tail) => Project(new UiColumn(filler.Concat(new UiNode[]
            {
                new UiKeyGlyph("confirm"), new UiActionSlot("confirm", "Confirm", cooldown: deadline),
                UiPips.WithRefill(4, 3, "Cells", deadline), new UiText(new string('x', tail)),
            }).ToArray()));
            int Measure(UiNode candidate) => UiWireCodec.MeasureTargetedSurface(Envelope.OwnerPackageId, target, 1, 1,
                request, request.Body, true, candidate, Epoch, envelope: Envelope);
            int Write(UiNode candidate)
            {
                var writer = new WorldWireWriter(cap);
                writer.WriteByte(1); writer.WriteByte(10); writer.WriteString(Envelope.OwnerPackageId); writer.WriteString(target);
                writer.WriteVarUInt(1); writer.WriteVarUInt(1);
                UiWireCodec.WriteTargetedSurfaceBody(writer, request, request.Body, true, candidate, Epoch, false, Envelope);
                return writer.Length;
            }
            var tail = 256 + cap - Measure(Tree(256));
            var exact = Tree(tail);
            Require(Measure(exact) == cap && Write(exact) == cap, "All action tags fit at the exact full-frame cap.");
            var tooLarge = Tree(tail + 1);
            Require(ExceedsCapacity(() => Measure(tooLarge)) && ExceedsCapacity(() => Write(tooLarge)) && deadline.ClockIdentity == null,
                "One extra action frame byte is rejected without binding its pending deadlines.");
        }

        private static bool ExceedsCapacity(Action send)
        {
            try { send(); }
            catch (InvalidOperationException) { return true; }
            return false;
        }

        private static byte[] KeyPayload(string action = "confirm", byte count = 2, byte kind = 0, string control = "Q") =>
            FramePayload(w =>
            {
                w.WriteByte(40); w.WriteString("key"); w.WriteString(action); w.WriteByte(count);
                w.WriteByte(kind); w.WriteString(control); w.WriteByte(3); w.WriteString("LeftShoulder");
                for (var index = 2; index < count; index++) { w.WriteByte(0); w.WriteString("R"); }
            });

        private static byte[] SlotPayload(byte glyphPresent = 1, byte glyph = 0, byte imagePresent = 0,
            string pack = "icons", string image = "emblem", string label = "Confirm",
            string detail = "Ready", byte emphasis = 1, double deadline = 20, double duration = 8, byte cooldownFlag = 1,
            byte chargesFlag = 1, byte available = 2, byte maximum = 3, byte refillFlag = 1, string reason = "Wait",
            string style = "action.edge", string epoch = Epoch) => FramePayload(w =>
        {
            w.WriteByte(41); w.WriteString("slot"); w.WriteString("confirm"); w.WriteByte(2);
            w.WriteByte(0); w.WriteString("Q"); w.WriteByte(3); w.WriteString("LeftShoulder");
            w.WriteString(label); w.WriteByte(glyphPresent);
            if (glyphPresent == 1) w.WriteByte(glyph);
            w.WriteByte(imagePresent);
            if (imagePresent == 1)
            {
                w.WriteString(pack);
                foreach (var value in ImageDigest) w.WriteByte(value);
                w.WriteString(image);
            }
            w.WriteString(detail); w.WriteByte(emphasis);
            w.WriteByte(cooldownFlag);
            if (cooldownFlag == 1) { Double(w, deadline); Double(w, duration); }
            w.WriteByte(chargesFlag);
            if (chargesFlag == 1)
            {
                w.WriteByte(available); w.WriteByte(maximum); w.WriteByte(refillFlag);
                if (refillFlag == 1) { Double(w, 30); Double(w, 10); }
            }
            w.WriteString(reason); w.WriteString(style); w.WriteString(epoch);
        });

        private static byte[] PipPayload(byte filled = 3, byte presentation = 1, byte role = 7,
            double duration = 10, string epoch = Epoch) => FramePayload(w =>
        {
            w.WriteByte(42); w.WriteString("pips"); w.WriteByte(4); w.WriteByte(filled); w.WriteString("Cells");
            w.WriteByte(presentation); w.WriteByte(role); w.WriteString("pips.edge"); Double(w, 30); Double(w, duration); w.WriteString(epoch);
        });

        private static byte[] StaticPipPayload(byte filled, float fraction) => FramePayload(w =>
        {
            w.WriteByte(30); w.WriteString("pips"); w.WriteByte(4); w.WriteByte(filled); w.WriteString("Cells");
            w.WriteSingle(fraction); w.WriteByte(1); w.WriteByte(7); w.WriteString("pips.edge");
        });

        private static InputBinding[]? GetDefaults(string name) => name == "confirm" ? Defaults : null;
        private static UiNode Project(UiNode node) => UiWireCodec.ProjectInputDefaults(node, GetDefaults)!;
        private static byte[] Encode(UiNode? node) => FramePayload(w => UiWireCodec.Write(w, node, Epoch, Envelope));
        private static void Double(WorldWireWriter w, double value) => w.WriteUInt64(unchecked((ulong)BitConverter.DoubleToInt64Bits(value)));
        private static byte[] Body(byte[] packet) => packet[Header.Length..];
        private static byte[] Frame(byte[] payload) => Header.Concat(payload).ToArray();
        private static void Malformed(byte[] payload) => Refuse(() => Decode(Frame(payload)));
        private static byte[] FramePayload(Action<WorldWireWriter> write)
        {
            var writer = new WorldWireWriter(32768);
            write(writer);
            return Copy(writer);
        }
        private static WorldWireReader Reader(byte[] bytes)
        {
            var reader = new WorldWireReader();
            reader.Reset(bytes);
            return reader;
        }
        private static byte[] Copy(WorldWireWriter writer) => writer.Segment.ToArray();
        private static void Ignore(string id, string value) => throw new InvalidOperationException("Action nodes must not send replies.");
        private static UiNode? Decode(byte[] bytes, string epoch = Epoch)
        {
            var reader = Reader(bytes);
            var node = UiWireCodec.ReadWithEnvelope(reader, Ignore, epoch, out _);
            UiWireCodec.End(reader);
            return node;
        }

        private static void Refuse(Action action, string? message = null)
        {
            try { action(); }
            catch (ArgumentException error) when (message == null || error.Message.Contains(message, StringComparison.Ordinal)) { return; }
            throw new InvalidOperationException("An invalid UI action frame was accepted or returned the wrong diagnostic.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
