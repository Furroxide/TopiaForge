using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.PlayersApiTests;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class PresentationRuntimeTests
    {
        internal static void RunK1Delivery()
        {
            LegacyPeerRejectsSharedUpgradesAtomically();
            RetainedFoundationUiGatesAdmissionAndPromotion();
            BaselineGenerationsValidateAgainstTheOldSnapshot();
            TargetedGenerationPreservesTheExactFramedLimit();
            Console.WriteLine("K1 UI delivery tests passed.");
        }

        private static void LegacyPeerRejectsSharedUpgradesAtomically()
        {
            using var rig = new Rig();
            rig.Start();
            var original = new UiText("Ordinary");
            var existing = rig.Host.Ui.CreateSurface(Hud("ordinary", "Legacy HUD", original));
            Assert(existing.Succeeded, "A peer that has no UI capability can still receive a legacy HUD.");
            rig.Frames(10);
            Assert(rig.Guest.ReceivedUi.Surfaces.Count == 1, "The existing peer saw the legacy publication.");

            var next = new UiText("New title", UiTextStyle.Banner);
            var replacement = existing.Value!.SetContent(next);
            var created = rig.Host.Ui.CreateSurface(Hud("new", "New HUD", next));
            Assert(replacement.ErrorCode == ModErrorCode.Unavailable && created.ErrorCode == ModErrorCode.Unavailable &&
                replacement.ErrorMessage == UiPresentationTheme.RequiredRuntime && created.ErrorMessage == UiPresentationTheme.RequiredRuntime,
                "An admitted legacy peer refuses both a K1 replacement and a K1 creation.");
            rig.Frames(10);
            Assert(rig.Host.LocalUi.Surfaces.Count == 1 && ReferenceEquals(rig.Host.LocalUi.Surfaces[0].Content, original),
                "The host keeps its exact retained tree when a peer cannot read the update.");
            Assert(rig.Guest.ReceivedUi.Surfaces.Count == 1 && ((UiText)rig.Guest.ReceivedUi.Surfaces[0].Content!).Text == "Ordinary",
                "The original peer keeps its original canvas and content.");
        }

        private static void RetainedFoundationUiGatesAdmissionAndPromotion()
        {
            using var rig = new Rig();
            var retained = rig.Host.Ui.CreateSurface(Hud("solo", "Solo", new UiText("Status", UiTextStyle.Display)));
            Assert(retained.Succeeded, "A K1 HUD may be authored before the process hosts a session.");
            retained.Value!.Hide();
            var host = rig.Host.Node.Runtime.Host;
            var legacy = new InternalContentOffer(host.GameBuild, MultiplayerModule.ProtocolVersion, MultiplayerModule.PeerVersionRange,
                Array.Empty<InternalContentDescriptor>());
            var rejected = host.EvaluateAdmission(legacy);
            Assert(!rejected.IsAdmitted && rejected.Problems[0] == UiPresentationTheme.RequiredRuntime,
                "Even a hidden retained K1 HUD requires an explicitly capable joining peer.");
            Assert(!host.PrepareSharedUiForHosting().Succeeded && rig.Host.LocalUi.Surfaces.Count == 1 && !retained.Value.IsVisible,
                "An unadmitted K1 origin blocks promotion while its hidden solo handle survives.");
            retained.Value.Dispose();
            Assert(host.PrepareSharedUiForHosting().Succeeded && host.EvaluateAdmission(legacy).IsAdmitted,
                "Retiring the last K1 HUD restores support for a legacy join.");
        }

        private static void BaselineGenerationsValidateAgainstTheOldSnapshot()
        {
            var shared = new UiPresentationGenerations();
            var targeted = new UiPresentationGenerations();
            var ten = Envelope(10);
            var eleven = Envelope(11);
            var twelve = Envelope(12);
            shared.Commit(1, ten);
            Assert(shared.Allows(5, ten), "Another surface can join an active presentation context.");
            Assert(shared.Allows(2, twelve) && shared.Allows(3, eleven) && shared.Allows(3, eleven) && shared.Allows(2, twelve),
                "Every baseline candidate is checked against the pre-message mark in either array order.");
            shared.ClearActive();
            shared.Commit(2, twelve);
            shared.Commit(3, eleven);
            Assert(!shared.Allows(4, ten) && shared.Allows(4, eleven), "A retired generation fails while an active lower one stays valid.");
            shared.Commit(3, null);
            shared.Commit(2, null);
            Assert(!shared.Allows(6, twelve), "An inactive generation equal to the high-water mark is stale.");
            Assert(targeted.Allows(6, twelve), "Independent targeted delivery has its own high-water mark.");
            shared.Reset();
            Assert(shared.Allows(7, ten), "Replacing the authenticated stream clears its presentation marks.");
        }

        private static UiWireEnvelope Envelope(ulong generation) =>
            new UiWireEnvelope("presentation-test", new byte[32], generation, null, new byte[32]);

        private static void TargetedGenerationPreservesTheExactFramedLimit()
        {
            Assert(UiPresentationTheme.TryNextTargetGeneration(0, out var first), "The first generation is available.");
            Assert(UiPresentationTheme.TryNextTargetGeneration(ulong.MaxValue - 1, out var last) && last == ulong.MaxValue &&
                !UiPresentationTheme.TryNextTargetGeneration(last, out var exhausted) && exhausted == 0,
                "Targeted generations refuse exhaustion without wrapping to an earlier value.");

            var request = new UiSurfaceRequest("framed", "Framed limit", "", width: 320, height: 160);
            var children = new UiNode[8];
            for (var index = 0; index < 7; index++) children[index] = new UiText(new string('x', 4096));
            children[7] = new UiText(new string('x', 256));
            var start = FramedSize(request, new UiColumn(children), Envelope(first));
            var tail = 256 + MultiplayerPlayers.MaximumMessageBytes - start;
            children[7] = new UiText(new string('x', tail));
            var tree = new UiColumn(children);
            var stable = false;
            try
            {
                stable = FramedSize(request, tree, Envelope(first)) == MultiplayerPlayers.MaximumMessageBytes &&
                    FramedSize(request, tree, Envelope(first + 127)) == MultiplayerPlayers.MaximumMessageBytes &&
                    FramedSize(request, tree, Envelope(last)) == MultiplayerPlayers.MaximumMessageBytes;
            }
            catch (InvalidOperationException) { }
            Assert(stable, "The same maximum-sized frame fits after another author scope advances its route generation.");

            children[7] = new UiText(new string('x', tail + 1));
            var overLimit = false;
            try { FramedSize(request, new UiColumn(children), Envelope(first)); }
            catch (InvalidOperationException) { overLimit = true; }
            Assert(overLimit, "One additional payload byte exceeds the targeted frame limit.");
        }

        private static int FramedSize(UiSurfaceRequest request, UiNode content, UiWireEnvelope envelope) =>
            UiWireCodec.MeasureTargetedSurface(envelope.OwnerPackageId, "recipient", 1, 1,
                request, request.Body, true, content, "", envelope: envelope);
    }
}
