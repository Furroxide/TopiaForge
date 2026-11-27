using System;
using System.Reflection;
using TopiaForge.Mods;
using TopiaForge.TestSupport;

namespace TopiaForge.ModManager.Tests
{
    internal static class UiActionContractsTests
    {
        internal static void Run()
        {
            ConstructorsKeepTheirBoundsAndReleaseTags();
            TimeIsRoundedUpAndClamped();
            ADeadlineBindsOnlyOnCommit();
            StatusHonorsPriorityAndExpires();
            RefillsDoNotMutateAuthorState();
            InsetsFollowTheReadableScale();
            PresentationAllocatesNothingAfterWarmup();
            Console.WriteLine("UI action contracts passed: bounds, clock epochs, status, refills, readable scale and allocation.");
        }

        private static void ConstructorsKeepTheirBoundsAndReleaseTags()
        {
            var action = new string('é', 63) + "A";
            var label = new string('B', 24);
            var detail = new string('C', 48);
            var id = new string('D', 128);
            var style = new UiStyleRef("slot.status");
            var image = new UiImageRef("example.mod", "uplink");
            var glyph = new UiKeyGlyph(action, id);
            var slot = new UiActionSlot(action, label, image: image, detail: detail, style: style, id: id);
            Require(glyph.ActionName == action && glyph.Id == id && slot.ActionName == action && slot.Label == label &&
                slot.Detail == detail && ReferenceEquals(slot.Style, style) && ReferenceEquals(slot.Image, image) &&
                slot.Glyph == null && slot.Id == id, "Action names, text and optional fields retain their exact values.");
            Require(new UiActionSlot("Action", "Go", detail: "", unavailableReason: "") is
            { Detail: null, UnavailableReason: null, Id: null, Emphasis: false, Style: null },
                "Wire-empty optional text and omitted fields remain absent.");
            Require(new UiActionSlot("Action", "Go", unavailableReason: " Later! ").UnavailableReason == " Later! ",
                "Plain-text status keeps its spacing and punctuation.");
            var bar = new UiRow(glyph, new UiActionSlot(action, "Go", id: "slot"));
            UiComposition.Validate(bar);
            Require(!UiLayoutTree.ContainsInteractive(bar) &&
                UiLayoutTree.TryFind(bar, id, out var found, out _) && ReferenceEquals(found, glyph),
                "Action nodes support stable lookup without gaining interactive HUD behavior.");
            Reject(() => new UiKeyGlyph(null!), "actionName");
            Reject(() => new UiKeyGlyph(" "), "actionName");
            Reject(() => new UiKeyGlyph(action + "x"), "actionName");
            Reject(() => new UiKeyGlyph("Action", id + "x"), "id");
            Reject(() => new UiActionSlot("", "Go"), "actionName");
            Reject(() => new UiActionSlot("Action", null!), "label");
            Reject(() => new UiActionSlot("Action", label + "x"), "label");
            Reject(() => new UiActionSlot("Action", "Go", detail: detail + "x"), "detail");
            Reject(() => new UiActionSlot("Action", "Go", unavailableReason: detail + "x"), "unavailableReason");
            Reject(() => new UiActionSlot("Action", "Go", (UiGlyph)255), "glyph");
            Reject(() => new UiActionSlot("Action", "Go", UiGlyph.Check, image), "image");

            foreach (var contract in new[] { typeof(UiKeyGlyph), typeof(UiActionSlot), typeof(UiCooldown), typeof(UiCharges) })
            {
                Require(contract.IsSealed && HasReleaseTag(contract), "New action contracts are sealed and release-tagged.");
                foreach (var member in contract.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (member is MethodInfo { IsSpecialName: true }) continue;
                    Require(HasReleaseTag(member) && member is not PropertyInfo { CanWrite: true },
                        "Every authored action member is release-tagged and properties are read-only.");
                }
            }
            Require(HasReleaseTag(typeof(UiPips).GetProperty(nameof(UiPips.Refill))!) &&
                HasReleaseTag(typeof(UiPips).GetMethod(nameof(UiPips.WithRefill), BindingFlags.Public | BindingFlags.Static)!),
                "The new members of the existing K1 type carry the K2 release tag.");
        }

        private static void TimeIsRoundedUpAndClamped()
        {
            var cooldown = new UiCooldown(20, 8);
            Require(cooldown.EndsAt == 20 && cooldown.DurationSeconds == 8 && cooldown.Elapsed(0) == 0 &&
                cooldown.Elapsed(12) == 0 && cooldown.Elapsed(16) == 0.5f,
                "A sweep clamps before its start and uses its authored duration.");
            Require(cooldown.RemainingSeconds(16.25) == 4 && cooldown.RemainingSeconds(19.99) == 1,
                "Fractional seconds must round up until expiry.");
            Require(cooldown.Elapsed(Math.BitDecrement(20)) < 1 && cooldown.Elapsed(20) == 1 &&
                cooldown.RemainingSeconds(20) == 0 && cooldown.RemainingSeconds(40) == 0 && cooldown.Elapsed(40) == 1,
                "The sweep completes at the exact deadline.");
            Require(new UiCooldown(0, double.Epsilon).RemainingSeconds(0) == 0 &&
                new UiCooldown(double.MaxValue, 86_400).RemainingSeconds(0) == int.MaxValue,
                "The finite extremes keep the remaining seconds in range.");
            foreach (var bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1d })
            {
                Reject(() => new UiCooldown(bad, 1), "endsAt");
                Reject(() => new UiCooldown(20, bad), "durationSeconds");
                Reject(() => cooldown.Elapsed(bad), "clockTime");
                Reject(() => cooldown.RemainingSeconds(bad), "clockTime");
            }
            Reject(() => new UiCooldown(20, 0), "durationSeconds");
            Reject(() => new UiCooldown(20, Math.BitIncrement(86_400d)), "durationSeconds");
        }

        private static void ADeadlineBindsOnlyOnCommit()
        {
            var initial = new UiClockBinding.EpochIdentity("initial");
            var sameName = new UiClockBinding.EpochIdentity(new string("initial".ToCharArray()));
            var pending = new UiCooldown(90, 10);
            var slot = new UiActionSlot("Action", "Go", cooldown: pending, charges: new UiCharges(1, 3, pending));
            var pips = UiPips.WithRefill(3, 1, "Cells", pending);
            pending.CheckClock(initial.Value);
            Require(pending.ClockIdentity == null && pending.Sample(85, initial.Value).IsRunning,
                "Preflight can sample a deadline while leaving it unbound.");
            slot.Cooldown!.CommitClock(initial);
            pips.Refill!.CommitClock(sameName);
            Require(ReferenceEquals(pending.ClockIdentity, initial) && ReferenceEquals(slot.Charges!.Refill, pending),
                "A shared deadline binds to its first clock identity once.");

            initial.Adopt("session-A");
            Require(pending.ClockEpoch == "session-A" && pending.Sample(85, "session-A").RemainingSeconds == 5,
                "Initial-epoch adoption keeps a shared deadline on its original timeline.");
            Require(pending.Sample(85, "session-a") is { IsRunning: false, RemainingSeconds: 0, Elapsed: 1 },
                "A retained deadline expires when the ordinal epoch changes.");
            var newDeadline = new UiCooldown(95, 5);
            newDeadline.CheckClock("session-B");
            Reject(() => pending.CheckClock("session-B"), null);
            Reject(() => pending.CommitClock(new UiClockBinding.EpochIdentity("session-B")), null);
            Require(newDeadline.ClockIdentity == null && ReferenceEquals(pending.ClockIdentity, initial),
                "Rejected admission leaves both pending and previously admitted bindings intact.");
        }

        private static void StatusHonorsPriorityAndExpires()
        {
            var clock = new UiClockBinding.EpochIdentity("session");
            var cooldown = new UiCooldown(20, 8);
            var refill = new UiCooldown(24, 10);
            cooldown.CommitClock(clock);
            refill.CommitClock(clock);
            var charges = new UiCharges(1, 3, refill);
            var active = new UiActionSlot("Confirm", "Confirm", detail: "Detail", emphasis: true, cooldown: cooldown, charges: charges);
            var locked = new UiActionSlot("Confirm", "Confirm", unavailableReason: "After the wave",
                detail: "Detail", emphasis: true, cooldown: cooldown, charges: charges);
            var lockedPaint = UiActionSlotPresentation.Resolve(locked, 16, clock.Value);
            Require(lockedPaint is
            {
                StateName: "unavailable", Status: UiActionSlotStatus.Unavailable,
                Text: "After the wave", StatusGlyph: UiGlyph.Lock, Tone: UiTone.Muted, Emphasized: false
            } &&
                lockedPaint.Backdrop(UiPaintRole.Accent) == UiPaintRole.Sunken,
                "Unavailable hides cooldown, charges and emphasis and shows the lock on a Sunken fill.");
            var during = UiActionSlotPresentation.Resolve(active, 16, clock.Value);
            Require(during is
            {
                StateName: "cooldown", Status: UiActionSlotStatus.Cooldown,
                Cooldown.RemainingSeconds: 4, Cooldown.Elapsed: 0.5f, Emphasized: true
            } &&
                during.Backdrop(UiPaintRole.Surface) == UiPaintRole.Raised, "A running cooldown takes status priority over charges.");
            Require(UiActionSlotPresentation.Resolve(active, 20, clock.Value) is
            { StateName: "charging", Status: UiActionSlotStatus.Charges, Charges.Filled: 1, Charges.RemainingSeconds: 4 },
                "Expiry uncovers the active charge refill.");
            Require(UiActionSlotPresentation.Resolve(active, 24, clock.Value) is
            { StateName: "emphasis", Status: UiActionSlotStatus.Charges, Charges.Filled: 2 } &&
                UiActionSlotPresentation.Resolve(active, 16, "new-session") is
                { StateName: "emphasis", Status: UiActionSlotStatus.Charges, Charges.Filled: 2 },
                "Expiry and epoch replacement settle both the slot and the charge count.");
            var ready = UiActionSlotPresentation.Resolve(new UiActionSlot("Confirm", "Confirm", detail: "Detail"), 0, clock.Value);
            Require(ready is { StateName: "ready", Status: UiActionSlotStatus.Detail, Text: "Detail", StatusGlyph: null } &&
                ready.Backdrop(UiPaintRole.Accent) == UiPaintRole.Accent &&
                UiActionSlotPresentation.Resolve(new UiActionSlot("Confirm", "Confirm"), 0, clock.Value) is
                { StateName: "ready", Status: UiActionSlotStatus.None, Text: null },
                "Ready slots keep their own fill and display detail only when supplied.");
        }

        private static void RefillsDoNotMutateAuthorState()
        {
            var refill = new UiCooldown(20, 8);
            var full = new UiPips(1, 1, "Cells");
            var zero = new UiPips(4, 2, "Cells", default);
            var staticFraction = new UiPips(4, 2, "Cells", fraction: 0.5f);
            Require(full is { Fraction: 0f, Refill: null, Presentation: UiPipStyle.Dots, Role: UiPaintRole.Accent } &&
                zero is { Fraction: 0f, Refill: null } && staticFraction is { Fraction: 0.5f, Refill: null } &&
                staticFraction.DisplayLabel == "Cells: 2 / 4 (50% refilling)",
                "K1's omitted, typed default and named fraction forms preserve the authored status.");
            var refilling = UiPips.WithRefill(4, 3, "Cells", refill, UiPipStyle.Segments);
            var charges = new UiCharges(0, 9, refill);
            refill.CommitClock(new UiClockBinding.EpochIdentity("session"));
            Require(ReferenceEquals(refilling.Refill, refill) && ReferenceEquals(charges.Refill, refill),
                "Refills retain the exact authored deadline.");
            Require(UiRefillPresentation.For(refilling, 16, "session") is
            { Filled: 3, Total: 4, Fraction: 0.5f, RemainingSeconds: 4, Percentage: 50, Style: UiPipStyle.Segments } &&
                UiRefillPresentation.For(charges, 10, "session") is
                { Filled: 0, Fraction: 0f, Percentage: 0, IsRefilling: true, Style: UiPipStyle.Dots } &&
                UiRefillPresentation.For(refilling, Math.BitDecrement(20), "session").Percentage == 99 &&
                UiRefillPresentation.For(new UiCharges(10, 10), 16, "session").Style == UiPipStyle.Segments,
                "Refill paint keeps the requested pip style and uses segments for more than nine charges.");
            Require(UiRefillPresentation.For(refilling, 20, "session") is
            { Filled: 4, Fraction: 0f, IsRefilling: false, Percentage: null } &&
                UiRefillPresentation.For(refilling, 16, "new-session").Filled == 4 &&
                UiRefillPresentation.For(charges, 20, "session") is { Filled: 1, Fraction: 0f, IsRefilling: false } &&
                charges.Available == 0 && refilling.Filled == 3 && refilling.Fraction == 0f &&
                refilling.DisplayLabel == "Cells: 3 / 4",
                "Refill expiry advances only the rendered count by one.");
            Require(UiRefillPresentation.For(staticFraction, 60, "session") is
            { Filled: 2, Fraction: 0.5f, IsRefilling: false }, "A K1 fraction stays static across time.");
            Require(new UiCharges(1, 1) is { Available: 1, Maximum: 1, Refill: null } &&
                new UiCharges(0, 32).Maximum == 32, "Capacity endpoints and full counts are valid without a refill.");
            Reject(() => new UiCharges(0, 0), "maximum");
            Reject(() => new UiCharges(0, 33), "maximum");
            Reject(() => new UiCharges(-1, 1), "available");
            Reject(() => new UiCharges(2, 1), "available");
            Reject(() => new UiCharges(1, 1, refill), "refill");
            Reject(() => UiPips.WithRefill(4, 4, "Cells", refill), "filled");
            Reject(() => UiPips.WithRefill(4, 3, "Cells", (UiCooldown)null!), "refill");
        }

        private static void InsetsFollowTheReadableScale()
        {
            var margins = new UiInsets(UiSpace.Sm, UiSpace.Md, UiSpace.Lg, UiSpace.Xs);
            foreach (var scale in new[] { 0.75f, 1f, 1.5f, 2f })
            {
                var size = UiActionSlotGeometry.ContentBounds(scale);
                var inset = UiActionSlotGeometry.ContentBounds(scale, margins);
                var readable = Math.Max(1f, scale);
                Require(Near(size.Width * scale, 128 * readable) && Near(size.Height * scale, 112 * readable) &&
                    Near(inset.Width * scale, 104 * readable) && Near(inset.Height * scale, 96 * readable) &&
                    Near(inset.X * scale, 8 * readable) && Near(inset.Y * scale, 12 * readable),
                    "The slot and its content insets use the same readable scale after canvas scaling.");
            }
            Require(UiActionSlotGeometry.MaximumLabelLines == 2 && UiActionSlotGeometry.EmphasisBorder == 3,
                "The slot's label line limit and visible emphasis geometry are fixed.");
            foreach (var readableScale in new[] { 1f, 2f })
                foreach (var boundary in new[] { 0f, 1f, 2f, 3f, 4f })
                    Require(UiActionSlotGeometry.EmphasisStroke(boundary * readableScale, readableScale) > boundary * readableScale,
                        "Emphasis stays visibly wider than a theme or high-contrast boundary.");
            Require(UiActionSlotGeometry.ContentBounds(1f, new UiInsets(UiSpace.Xxxl)).HasArea,
                "The largest kit insets leave room inside the slot.");
            Reject(() => UiActionSlotGeometry.ContentBounds(float.NaN), "uiScale");
        }

        private static void PresentationAllocatesNothingAfterWarmup()
        {
            const string epoch = "session";
            var cooldown = new UiCooldown(20, 8);
            cooldown.CommitClock(new UiClockBinding.EpochIdentity(epoch));
            var charges = new UiCharges(3, 4, cooldown);
            var active = new UiActionSlot("Confirm", "Confirm", cooldown: cooldown, charges: charges);
            var ready = new UiActionSlot("Confirm", "Confirm");
            var pips = UiPips.WithRefill(4, 3, "Cells", cooldown);
            var now = 16d;
            double observation = 0;
            var bytes = AllocationProbe.Measure("action presentation", () =>
            {
                now = now < 20 ? now + 0.25 : 16;
                observation += UiActionSlotPresentation.Resolve(active, now, epoch).Cooldown.Elapsed +
                    UiActionSlotPresentation.Resolve(ready, now, epoch).Charges.Filled +
                    UiRefillPresentation.For(pips, now, epoch).Fraction +
                    UiActionSlotGeometry.ContentBounds(0.75f).Width;
            }, frames: 4_096, warmups: 4_096);
            Require(bytes == 0 && observation > 0, "Settled and ticking action projections must allocate zero bytes.");
        }

        private static bool HasReleaseTag(MemberInfo member) =>
            member.GetCustomAttribute<RequiresTopiaForgeAttribute>(inherit: false)?.Version == UiKitRelease.K2;

        private static bool Near(double left, double right) => Math.Abs(left - right) < 0.00001;

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Reject(Action operation, string? parameter)
        {
            try { operation(); }
            catch (ArgumentException error) when (error.ParamName == parameter) { return; }
            throw new InvalidOperationException("Expected argument rejection for " + (parameter ?? "a stale clock") + ".");
        }
    }
}
