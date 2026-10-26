using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TopiaForge.ModManager.Sessions;
using TopiaForge.ModManager.Voice;
using TopiaForge.Mods;
using static TopiaForge.ModManager.Tests.SessionAssert;
using LayerBand = TopiaForge.Mods.UnityUi.TopiaForgeLayerBand;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class VoiceChatTests
    {
        /// <summary>
        /// Esc while push-to-talk waits for its new control cancels the capture, and only the capture: the overlay under it
        /// stays open whichever of the kit's driver and voice chat's frame reads the press first, the binding is kept, and
        /// Esc is neither taken as a control nor refused as one. Every other end of a capture gives Esc back to the overlay,
        /// and the consent, opened over a capture, is answered Not now before the capture is cancelled.
        /// </summary>
        private static void EscapeCancelsTheCaptureAlone(string root)
        {
            var escape = InputBinding.Key("Escape");
            var rig = VoiceRig.On();
            rig.Presenter.OnShown();
            rig.Escape.Press();
            That(rig.Escape.OverlayCloses == 1 && !rig.Escape.Held, "without a capture, Esc is the overlay's");

            // The kit's driver reads the press before voice chat's frame.
            rig.Presenter.CaptureBinding(1);
            rig.Frame();
            That(rig.Presenter.CapturingSlot == 1 && rig.Escape.Held, "a capture holds the top of the kit's Escape stack while it waits");
            rig.Escape.Press();
            rig.Frame();
            CancelledAlone(rig, "the kit's driver first");

            // Voice chat's frame reads it first and leaves it to the kit, which then cancels.
            rig.Presenter.CaptureBinding(1);
            rig.Frame();
            rig.Input.Press(escape);
            rig.Frame();
            That(rig.Presenter.CapturingSlot == 1 && rig.Escape.Held && rig.Presenter.BindingNote == VoiceText.CaptureNote &&
                 rig.Presenter.BindingNoteTone == ScreenTone.Neutral, "voice chat's frame neither takes Esc nor refuses it, and the capture keeps the top");
            rig.Escape.Press();
            rig.Input.Release(escape);
            CancelledAlone(rig, "voice chat's frame first");

            // Every other end of a capture gives the top back.
            rig.Presenter.CaptureBinding(0);
            rig.Presenter.CaptureBinding(0);
            That(!rig.Escape.Held, "asking again gives Esc back");
            rig.Presenter.CaptureBinding(0);
            rig.Frame();
            var g = InputBinding.Key("G");
            rig.Input.Press(g);
            rig.Frame();
            rig.Input.Release(g);
            That(!rig.Escape.Held && rig.Presenter.BindingText(0) == "G", "so does a control that binds");
            rig.Presenter.CaptureBinding(1);
            rig.Presenter.OnHidden();
            That(!rig.Escape.Held, "and leaving VOICE");
            rig.Presenter.CaptureBinding(1);
            rig.Presenter.Stop();
            That(!rig.Escape.Held && rig.Escape.Holds == 6, "and the game closing; each capture took the top once (" + rig.Escape.Holds + ")");
            rig.Escape.Press();
            That(rig.Escape.OverlayCloses == 2, "after which Esc closes the overlay again");

            // The consent, opened over a capture that waits, is a kit modal pushed after it in the same band, so it comes first:
            // Esc answers it Not now, the next Esc cancels the capture, and the one after closes the overlay.
            var asked = new VoiceRig();
            asked.Snapshot = VoiceRig.Session("s1", VoiceRig.Player("p2", "Sam", "key-sam"));
            asked.Start();
            asked.Presenter.OnShown();
            asked.Presenter.CaptureBinding(1);
            asked.Frame();
            asked.Presenter.SetVoiceOn(true);
            var question = asked.Presenter.ConsentQuestion!;
            question.Shown(new RecordingDialog());
            asked.Escape.Show(() => question.Answer(false));
            asked.Escape.Press();
            asked.Frame();
            That(question.IsAnswered && !asked.Presenter.VoiceOn && LastNotice(asked) == VoiceText.StaysOff && asked.Presenter.CapturingSlot == 1 &&
                 asked.Escape.Held && asked.Escape.OverlayCloses == 0, "with the consent open over a capture, the first Esc answers it Not now and the capture waits on");
            asked.Escape.Press();
            That(asked.Presenter.CapturingSlot == -1 && !asked.Escape.Held && asked.Presenter.BindingText(1) == "Mouse Forward" && asked.Escape.OverlayCloses == 0,
                "the second Esc cancels the capture and keeps its binding");
            asked.Escape.Press();
            That(asked.Escape.OverlayCloses == 1, "and the third closes the overlay");

            // The game's side, which no harness can run: the capture sits in the modal band, above the overlay's window band,
            // the kit picking the highest band; it leaves the stack before its cancel runs, and the cancel cannot escape.
            var adapter = TypeIn(Path.Combine(root, "src", "TopiaForge.ModManager", "Multiplayer", "Voice", "Unity", "UnityVoiceEscape.cs"), "UnityVoiceEscape");
            var overlay = TypeIn(Path.Combine(root, "src", "TopiaForge.ModManager", "Overlay", "ManagerOverlay.cs"), "ManagerOverlay");
            That(BandOf(adapter) == "TopiaForgeLayerBand.Modal" && BandOf(overlay) == "TopiaForgeLayerBand.Window" && LayerBand.Modal > LayerBand.Window,
                "a capture's Esc beats the overlay's on the kit's stack (" + BandOf(adapter) + " over " + BandOf(overlay) + ")");
            That(Calls(adapter, "Hold", "TopiaForgeDismissStack.Push", "this", "TopiaForgeInputClaim.Keyboard", "null") &&
                 Calls(adapter, "Release", "TopiaForgeDismissStack.Remove", "this"),
                "holding claims the keyboard on the kit's stack without a separate root, and every release removes it");
            var dismiss = adapter.Members.OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == "Dismiss");
            var steps = dismiss.Body!.Statements;
            That(steps.Count == 3 && steps[1].ToString() == "Release();" && steps[2] is TryStatementSyntax attempt && attempt.Catches.Count == 1,
                "Esc takes the capture off the stack, then cancels it inside a guard");

            // The consent's place on the stack is the kit's and the view's own: the stack dismisses the highest band and,
            // within it, the latest pushed; the consent is a kit modal, in the modal band and pushed as it shows; and closing it
            // any way answers no.
            var kit = Path.Combine(root, "src", "TopiaForge.Mods.UnityUi");
            var stack = TypeIn(Path.Combine(kit, "Runtime", "TopiaForgeDismissStack.cs"), "TopiaForgeDismissStack");
            var picks = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(kit, "Core", "TopiaForgeDismissOrder.cs"))).GetRoot().DescendantNodes()
                .OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == "TopIndex");
            That(Calls(stack, "Top", "Order.Top") &&
                 picks.DescendantNodes().OfType<BinaryExpressionSyntax>().Any(test => test.ToString() == "surfaces[index].Band >= surfaces[top].Band"),
                "the kit's stack dismisses the highest band and, within it, the latest pushed");
            var modal = TypeIn(Path.Combine(kit, "Widgets", "TopiaForgeModal.cs"), "TopiaForgeModalInstance");
            That(BandOf(modal) == "TopiaForgeLayerBand.Modal" &&
                 Calls(modal, "Show", "TopiaForgeDismissStack.Push", "this", "TopiaForgeInputClaim.Navigation", "canvasRoot"),
                "a kit modal joins the stack in the modal band and owns navigation as it shows");
            var consent = TypeIn(Path.Combine(root, "src", "TopiaForge.ModManager", "Overlay", "Multiplayer", "MultiplayerVoiceView.cs"), "MultiplayerVoiceView")
                .Members.OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == "AskConsent").Body!.ToString();
            That(consent.Contains("host.Modal.Custom(", StringComparison.Ordinal) && consent.Contains("modal.Show();", StringComparison.Ordinal) &&
                 consent.Contains("modal.Closed += () => question.Answer(false);", StringComparison.Ordinal), "the consent is such a modal, and closing it any way answers no");
            That(consent.Contains("var decline = row.Button(question.DeclineLabel, () => question.Answer(false));", StringComparison.Ordinal) &&
                 consent.IndexOf("decline.Focus();", StringComparison.Ordinal) > consent.IndexOf("modal.Show();", StringComparison.Ordinal),
                "the consent focuses Not now after showing, so Enter and controller A decline");

            var probe = TypeIn(Path.Combine(root, "src", "TopiaForge.ModManager", "Multiplayer", "Voice", "Unity", "UnityVoiceInput.cs"), "Probe");
            var pressed = probe.Members.OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == "WasPressed");
            That(pressed.Body!.Statements.Last() is ReturnStatementSyntax returns && returns.Expression!.ToString() ==
                 "(button != null && button.wasPressedThisFrame) || (key != Key.None && TopiaForgeInputOwnership.KeyPressedThisFrame(key))",
                "with the Input System alone the capture reads the kit's taken keys too, without treating a mouse button as a key");
        }

        private static void CancelledAlone(VoiceRig rig, string order) =>
            That(rig.Presenter.CapturingSlot == -1 && !rig.Escape.Held && rig.Escape.OverlayCloses == 1 && rig.Presenter.BindingText(1) == "Mouse Forward" &&
                 !rig.Runtime.PushToTalkSuspended && rig.Presenter.BindingNote.Length == 0 && rig.Store.Saves == 0,
                order + ": Esc cancels the capture alone, keeps Mouse Forward, and leaves the overlay open");

        private static ClassDeclarationSyntax TypeIn(string path, string name) =>
            CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Single(type => type.Identifier.ValueText == name);

        private static string BandOf(ClassDeclarationSyntax type) =>
            type.Members.OfType<PropertyDeclarationSyntax>().Single(property => property.Identifier.ValueText == "Band").ExpressionBody?.Expression.ToString() ?? string.Empty;

        private static bool Calls(ClassDeclarationSyntax type, string method, string callee, params string[] arguments) =>
            type.Members.OfType<MethodDeclarationSyntax>().Where(declared => declared.Identifier.ValueText == method)
                .SelectMany(declared => declared.DescendantNodes().OfType<InvocationExpressionSyntax>())
                .Any(call => call.Expression.ToString() == callee &&
                             call.ArgumentList.Arguments.Select(argument => argument.Expression.ToString()).SequenceEqual(arguments));
    }
}
