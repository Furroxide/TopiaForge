using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static class UiKitControlWireTests
    {
        private const string Owner = "example.controls";
        private const string Epoch = "controls-epoch";
        private static readonly UiWireEnvelope Envelope = new UiWireEnvelope(Owner, new byte[32], 1, null, new byte[32]);
        private static readonly byte[] Prefix = Write(null, Envelope)[..^1];

        internal static void Run()
        {
            LegacyAndControlsKeepTheirExactBytes();
            CallbacksCheckTheirPayloads();
            DefaultsAreVerifiedBeforeWriting();
            NestedControlsKeepClockAndBudgetRules();
            MalformedFieldsAreRefused();
            Console.WriteLine("K2 controls wire: exact tags 46–51, legacy bytes, default bindings, callbacks, clocks and full-frame budgets passed.");
        }

        private static void LegacyAndControlsKeepTheirExactBytes()
        {
            var legacySlider = new UiSlider("s", "S", 0f, 1f, 0.25f, _ => { });
            var legacyText = new UiTextInput("e", "E", "V", _ => { }, "P", 16, enabled: false);
            var legacySliderBytes = new byte[] { 8, 1, (byte)'s', 1, (byte)'S', 0, 0, 0, 0, 0, 0, 128, 63, 0, 0, 128, 62, 1 };
            var legacyTextBytes = new byte[] { 9, 1, (byte)'e', 1, (byte)'E', 1, (byte)'V', 1, (byte)'P', 16, 0 };
            That(Write(legacySlider, null).SequenceEqual(legacySliderBytes) && Write(legacyText, null).SequenceEqual(legacyTextBytes),
                "The original slider and text input keep the exact existing bytes.");
            That(Read(legacySliderBytes, enhanced: false) is UiSlider { Divisions: 0, UsesExplicitFormat: false } &&
                Read(legacyTextBytes, enhanced: false) is UiTextInput { Error: null, UsesExplicitDecoration: false },
                "The original controls keep their authored defaults when read.");
            That(UiWireCodec.RequiredContractVersion(legacySlider) == 1 &&
                UiWireCodec.RequiredContractVersion(new UiColumn(new UiText("Label", UiTextStyle.Label))) == 2,
                "A new runtime still uses the oldest contract for existing node shapes.");

            var text = UiTextInput.WithDecoration("e", "E", "V", _ => { }, new UiInputDecoration("P", "!"), 16, enabled: false);
            var slider = new UiSlider("s", "S", 0f, 1f, 0.25f, _ => { }, 4, UiValueFormat.Multiplier());
            var checkbox = new UiCheckbox("c", "C", true, _ => { }, enabled: false);
            var tabs = Tabs();
            var tooltip = new UiTooltip(new UiText("X"), "m");
            var key = (UiKeyBindingField)UiWireCodec.ProjectInputDefaults(new UiKeyBindingField("k", "a", device: UiInputDevice.Gamepad),
                _ => new[] { InputBinding.Key("F"), InputBinding.GamepadButton(InputGamepadButton.South) })!;
            ByteContract(checkbox, new byte[] { 46, 1, (byte)'c', 1, (byte)'C', 1, 0 });
            ByteContract(tabs, new byte[] { 47, 1, (byte)'t', 1, 1, (byte)'a', 1, 2,
                1, (byte)'a', 1, (byte)'A', 1, 1, (byte)'X', 0, 0, 1, (byte)'b', 1, (byte)'B', 1, 1, (byte)'Y', 0, 0 });
            ByteContract(tooltip, new byte[] { 48, 0, 1, (byte)'m', 1, 1, (byte)'X', 0, 0 });
            ByteContract(text, new byte[] { 49 }.Concat(legacyTextBytes.Skip(1)).Concat(new byte[] { 1, (byte)'!' }).ToArray());
            ByteContract(slider, new byte[] { 50 }.Concat(legacySliderBytes.Skip(1)).Concat(new byte[] { 4, 2, 2 }).ToArray());
            ByteContract(key, new byte[] { 51, 1, (byte)'k', 1, (byte)'a', 0, 1, 1, 2, 0, 1, (byte)'F',
                3, 5, (byte)'S', (byte)'o', (byte)'u', (byte)'t', (byte)'h' });
            var noError = UiTextInput.WithDecoration("e", "E", "V", _ => { }, new UiInputDecoration("P"), 16, enabled: false);
            ByteContract(noError, new byte[] { 49 }.Concat(legacyTextBytes.Skip(1)).Append((byte)0).ToArray());
            That(Read(Write(text, Envelope)) is UiTextInput { Error: "!", Placeholder: "P", Enabled: false } &&
                Read(Write(slider, Envelope)) is UiSlider { Divisions: 4, Format.Kind: UiValueFormatKind.Multiplier, Format.Decimals: 2 } &&
                Read(Write(tabs, Envelope)) is UiTabs { Style: UiTabsStyle.Rail, SelectedTabId: "a", Tabs.Count: 2 },
                "The decoded values match the authored control fields.");
        }

        private static void CallbacksCheckTheirPayloads()
        {
            var replies = new List<string>();
            var tree = new UiColumn(new UiCheckbox("c", "Check", false, value => replies.Add(value ? "checked" : "cleared")),
                Tabs(value => replies.Add(value)), new UiCard(new UiCheckbox("blocked", "Blocked", false, _ => throw new Exception()), disabled: true),
                new UiSlider("s", "Step", 0f, 1f, 0f, value => replies.Add(value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
                    4, UiValueFormat.Percent()), UiTextInput.WithDecoration("e", "Error", "", replies.Add, new UiInputDecoration(error: "!"), 2));
            That(!UiWireCodec.Invoke(tree, "c", "true") && !UiWireCodec.Invoke(tree, "blocked", "1") &&
                !UiWireCodec.Invoke(tree, "t", "other") && !UiWireCodec.Invoke(tree, "s", "0.3") &&
                !UiWireCodec.Invoke(tree, "s", "NaN") && !UiWireCodec.Invoke(tree, "e", "long") && replies.Count == 0,
                "An invalid checkbox, withheld tab, disabled descendant, off-step slider or overlong edit reaches no callback.");
            That(UiWireCodec.Invoke(tree, "c", "1") && UiWireCodec.Invoke(tree, "t", "b") &&
                UiWireCodec.Invoke(tree, "s", "0.25") && UiWireCodec.Invoke(tree, "e", "ok") &&
                replies.SequenceEqual(new[] { "checked", "b", "0.25", "ok" }),
                "Valid control replies preserve exact values and existing host callback ordering.");
            That(!UiWireCodec.Invoke(Tabs(enabled: false), "t", "b"), "A disabled group refuses a tab callback.");

            replies.Clear();
            var decoded = (UiColumn)Read(Write(tree, Envelope), callback: (id, value) => replies.Add(id + "=" + value));
            That(UiWireCodec.Invoke(decoded, "c", "0") && UiWireCodec.Invoke(decoded, "t", "a") &&
                UiWireCodec.Invoke(decoded, "s", "0.5") && UiWireCodec.Invoke(decoded, "e", "OK") &&
                replies.SequenceEqual(new[] { "c=0", "t=a", "s=0.5", "e=OK" }),
                "Received controls use the original targeted reply identities and invariant encodings.");
        }

        private static void DefaultsAreVerifiedBeforeWriting()
        {
            var field = new UiKeyBindingField("k", "a", "Rebind");
            Refused(() => Write(field, Envelope), "A key field cannot encode defaults that have not come from the owning action.");
            Refused(() => UiWireCodec.ProjectInputDefaults(field, _ => null), "Unknown package action fails admission.");
            Refused(() => UiWireCodec.ProjectInputDefaults(field, _ => Enumerable.Repeat(InputBinding.Key("A"), 5).ToArray()),
                "Too many authored default bindings fail before projection.");
            Refused(() => UiWireCodec.ProjectInputDefaults(field, _ => new[] { InputBinding.Key(new string('é', 17)) }),
                "Default control bounds are UTF-8 bytes.");
            var empty = UiWireCodec.ProjectInputDefaults(field, _ => Array.Empty<InputBinding>())!;
            That(Read(Write(empty, Envelope)) is UiKeyBindingField { WireDefaults.Count: 0 }, "The wire allows an empty default set.");
            var bindings = new[] { InputBinding.Key("A"), InputBinding.GamepadAxis(InputGamepadAxis.LeftX, 0.5f) };
            var projected = UiWireCodec.ProjectInputDefaults(field, name => name == "a" ? bindings : null)!;
            bindings[0] = InputBinding.Key("B");
            var receipt = (UiKeyBindingField)Read(Write(projected, Envelope));
            That(!field.HasWireDefaults && receipt.WireDefaults[0].Control == "A" &&
                receipt.WireDefaults[1].Kind == InputBindingKind.GamepadAxis && receipt.WireDefaults[1].Control == "LeftX" &&
                !UiWireCodec.Invoke(receipt, receipt.Id!, "A"), "The projected defaults are immutable, display only and carry no callback.");
        }

        private static void NestedControlsKeepClockAndBudgetRules()
        {
            var timer = new UiCountdown(20d, "Timer", UiTone.Primary, "timer");
            var tab = new UiTabs("t", new[] { new UiTab("a", "A", new UiText("First")),
                new UiTab("b", "B", new UiTooltip(new UiColumn(timer, new UiKeyBindingField("k", "a")), "Timer details")) }, "a");
            var content = (UiTabs)UiWireCodec.ProjectInputDefaults(tab, _ => new[] { InputBinding.Key("F") })!;
            var wrapped = (UiColumn)((UiTooltip)content.Tabs[1].Child).Child;
            That(ReferenceEquals(wrapped.Children[0], timer) && UiWireCodec.RequiredContractVersion(new UiColumn(content)) == 3,
                "A legacy wrapper detects the controls version and hidden children keep their authored clock identity.");
            var measured = UiWireCodec.Measure(content, Epoch, 32 * 1024, envelope: Envelope);
            That(timer.ClockEpoch == null, "Measuring the hidden tab does not bind its clock.");
            var bytes = Write(content, Envelope);
            That(bytes.Length == measured && timer.ClockEpoch == Epoch, "The writer binds clocks through hidden tabs and tooltips.");
            Refused(() => Read(bytes, epoch: "next"), "A new decoder cannot adopt a stale hidden clock.");
            var expired = (UiTabs)Read(Write(content, Envelope, "next", retained: true), epoch: "next");
            That(UiLayoutTree.TryFind(expired, "timer", out var surrogate, out _) && surrogate is UiText { Text: "Timer 0" } &&
                timer.ClockEpoch == Epoch, "A retained stale clock in a hidden tab receives its zero surrogate.");
            Refused(() => new UiSurfaceRequest("hud", "HUD", "", UiSurfaceKind.Hud, content: content),
                "Tabs and tooltip remain interactive in HUD admissions.");

            var large = new UiTabs("full", new[] { new UiTab("a", "A", new UiText("Visible")),
                new UiTab("b", "B", new UiColumn(Enumerable.Range(0, 8).Select(_ => new UiText(new string('x', 4096))).ToArray())) }, "a");
            var request = new UiSurfaceRequest("quota", "Window", "Body");
            try { _ = UiWireCodec.MeasureTargetedSurface(Owner, "peer", 1, 1, request, request.Body, true, large, Epoch, envelope: Envelope); }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("The full 32 KiB frame must count every hidden tab's content.");
        }

        private static void MalformedFieldsAreRefused()
        {
            Refused(() => Read(Frame(new byte[] { 52 })), "An unknown K2 tag is rejected.");
            Refused(() => Read(Frame(new byte[] { 46, 1, (byte)'c', 1, (byte)'C', 2, 1 })), "A malformed checkbox flag is rejected.");
            var invalidTabs = Body(Write(Tabs(), Envelope));
            invalidTabs[3] = 2;
            Refused(() => Read(Frame(invalidTabs)), "An unknown tab style is rejected.");
            invalidTabs[3] = (byte)UiTabsStyle.Rail;
            invalidTabs[7] = 13;
            Refused(() => Read(Frame(invalidTabs)), "A thirteenth tab is rejected before reading children.");
            Refused(() => Read(Frame(new byte[] { 48, 0, 1, (byte)'m', 0 })), "A tooltip requires a child.");
            var excessDefaults = new byte[] { 51, 1, (byte)'k', 1, (byte)'a', 0, 0, 1, 5 }
                .Concat(Enumerable.Repeat(new byte[] { 0, 1, (byte)'F' }, 5).SelectMany(binding => binding)).ToArray();
            Refused(() => Read(Frame(excessDefaults)), "A fifth otherwise valid wire default is rejected.");
            Refused(() => Read(Frame(new byte[] { 51, 1, (byte)'k', 1, (byte)'a', 0, 0, 1, 1, 5 })), "An unknown default kind is rejected.");
            Refused(() => Read(Frame(new byte[] { 51, 1, (byte)'k', 1, (byte)'a', 0, 0, 1, 1, 0, 1, 255 })), "Invalid UTF-8 is rejected.");
            var slider = Body(Write(new UiSlider("s", "S", 0f, 1f, 0.25f, _ => { }, 4, UiValueFormat.Percent()), Envelope));
            slider[^1] = 1;
            Refused(() => Read(Frame(slider)), "Percent has exactly zero encoded decimals.");
        }

        private static UiTabs Tabs(Action<string>? selected = null, bool enabled = true) => new UiTabs("t",
            new[] { new UiTab("a", "A", new UiText("X")), new UiTab("b", "B", new UiText("Y")) },
            "a", selected, UiTabsStyle.Rail, enabled);

        private static void ByteContract(UiNode node, byte[] expected)
        {
            var bytes = Write(node, Envelope);
            That(Body(bytes).SequenceEqual(expected) && UiWireCodec.RequiredContractVersion(new UiColumn(node)) == 3 &&
                UiWireCodec.Measure(node, Epoch, bytes.Length, envelope: Envelope) == bytes.Length,
                "The K2 node writes exactly the contract bytes and is measured by the same encoder for tag " + expected[0] + ".");
            That(UiWireCodec.NeedsEnvelope(node) && Write(Read(bytes), Envelope).SequenceEqual(bytes),
                "The K2 node remains wire-compatible when decoded and re-encoded.");
            Refused(() => Write(node, null), "A K2 node cannot write without the K1 envelope.");
            Refused(() => Read(expected, enhanced: false), "A K2 node is rejected without its root envelope.");
        }

        private static byte[] Write(UiNode? node, UiWireEnvelope? envelope, string epoch = Epoch, bool retained = false)
        {
            var writer = new WorldWireWriter(32 * 1024);
            if (retained) UiWireCodec.WriteRetained(writer, node, epoch, envelope);
            else UiWireCodec.Write(writer, node, epoch, envelope);
            return writer.Segment.AsSpan().ToArray();
        }

        private static byte[] Body(byte[] framed) => framed[Prefix.Length..];
        private static byte[] Frame(byte[] body) => Prefix.Concat(body).ToArray();

        private static UiNode Read(byte[] bytes, bool enhanced = true, string epoch = Epoch, Action<string, string>? callback = null)
        {
            var reader = new WorldWireReader();
            reader.Reset(bytes);
            var reply = callback ?? ((_, _) => { });
            var node = enhanced ? UiNodeWireCodec.Read(reader, reply, epoch, out _) : UiNodeWireCodec.Read(reader, reply, epoch);
            UiNodeWireCodec.End(reader);
            return node ?? throw new InvalidOperationException("The test requires a decoded node.");
        }

        private static void Refused(Action action, string message)
        {
            try { action(); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException(message);
        }

        private static void That(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
