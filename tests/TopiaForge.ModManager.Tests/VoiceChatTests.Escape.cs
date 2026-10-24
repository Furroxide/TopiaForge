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
        /// Esc is neither taken as a control nor refused as one. Every other end of a capture gives Esc back to the overlay.
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

            // The game's side, which no harness can run: the capture sits in the modal band, above the overlay's window band,
            // the kit picking the highest band; it leaves the stack before its cancel runs, and the cancel cannot escape.
            var adapter = TypeIn(Path.Combine(root, "src", "TopiaForge.ModManager", "Multiplayer", "Voice", "Unity", "UnityVoiceEscape.cs"), "UnityVoiceEscape");
            var overlay = TypeIn(Path.Combine(root, "src", "TopiaForge.ModManager", "Overlay", "ManagerOverlay.cs"), "ManagerOverlay");
            That(BandOf(adapter) == "TopiaForgeLayerBand.Modal" && BandOf(overlay) == "TopiaForgeLayerBand.Window" && LayerBand.Modal > LayerBand.Window,
                "a capture's Esc beats the overlay's on the kit's stack (" + BandOf(adapter) + " over " + BandOf(overlay) + ")");
            That(Calls(adapter, "Hold", "TopiaForgeDismissStack.Push") && Calls(adapter, "Release", "TopiaForgeDismissStack.Remove"),
                "holding pushes the capture onto the kit's stack, and releasing removes it");
            var dismiss = adapter.Members.OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == "Dismiss");
            var steps = dismiss.Body!.Statements;
            That(steps.Count == 3 && steps[1].ToString() == "Release();" && steps[2] is TryStatementSyntax attempt && attempt.Catches.Count == 1,
                "Esc takes the capture off the stack, then cancels it inside a guard");
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

        private static bool Calls(ClassDeclarationSyntax type, string method, string callee) =>
            type.Members.OfType<MethodDeclarationSyntax>().Where(declared => declared.Identifier.ValueText == method)
                .SelectMany(declared => declared.DescendantNodes().OfType<InvocationExpressionSyntax>())
                .Any(call => call.Expression.ToString() == callee && call.ArgumentList.Arguments.Count == 1 &&
                             call.ArgumentList.Arguments[0].Expression is ThisExpressionSyntax);
    }
}
