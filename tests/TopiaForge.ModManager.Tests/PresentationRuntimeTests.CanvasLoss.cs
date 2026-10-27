using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using static TopiaForge.ModManager.Tests.PlayersApiTests;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class PresentationRuntimeTests
    {
        private static void HostCanvasLossRetiresSharedState()
        {
            using var rig = new Rig(); rig.Start();
            using var lifetime = new FakeModLifetime();
            var local = new LossUiService(lifetime);
            var context = rig.Host.Node.Replication.Bind("canvas-loss", new ModNetProfile(ModNetworkClass.Shared,
                ModExecutionContext.GamemodeServer), lifetime);
            var ui = new NetUiService(local, context);
            foreach (var visible in new[] { true, false })
            {
                var surface = ui.CreateSurface(Hud("lost", "old")).Value!;
                if (!visible) surface.Hide();
                rig.Frames(10);
                local.Created[local.Created.Count - 1].Lose(); rig.Frames(10);
                Assert(rig.Guest.ReceivedUi.Surfaces.Count == 0,
                    "a host canvas lost while " + (visible ? "visible" : "hidden") + " retires its shared slot");
                Assert(lifetime.TrackedResourceCount == 0, "lost host surfaces release their tracking leases");
            }
        }

        private static void GuestCanvasLossRecoversLatestState()
        {
            using var rig = new Rig();
            using var lifetime = new FakeModLifetime();
            var local = new LossUiService(lifetime);
            rig.Guest.Players.Presentation = new MultiplayerPresentation(rig.Guest.Players,
                () => new PresentationSink(local, rig.Guest.ReceivedAudio));
            rig.Start();
            var hud = rig.Host.Ui.CreateSurface(Hud("remote", "current", new UiText("latest"))).Value!;
            rig.Frames(10);
            foreach (var visible in new[] { true, false })
            {
                if (!visible) { hud.Hide(); rig.Frames(10); }
                var lost = local.Created[local.Created.Count - 1];
                var created = local.Created.Count;
                lost.Lose(); rig.Frames(70);
                Assert(local.Created.Count == created + 1,
                    "a lost guest canvas is recreated from retained state without another host write");
                var replacement = local.Created[local.Created.Count - 1];
                Assert(replacement.IsAlive && replacement.IsVisible == visible && replacement.Inner.Body == "current" &&
                    ((UiText)replacement.Inner.Content!).Text == "latest", "canvas recovery preserves the latest content and visibility");
            }
            hud.Show(); rig.Frames(10);
            Assert(local.Created[local.Created.Count - 1].IsVisible, "recovered hidden surfaces can be shown later");
        }

        // Uses the native surface's presence state machine, including silent loss while hidden.
        private sealed class LossUiService : IUiService
        {
            private readonly FakeUiService inner;
            internal readonly List<LossSurface> Created = new List<LossSurface>();
            internal LossUiService(FakeModLifetime lifetime) { inner = new FakeUiService(lifetime); }
            public UiAccessibilityPreferences Accessibility => inner.Accessibility;
            public OperationResult<UiAccessibilityPreferences> ApplyAccessibility(UiAccessibilityPreferences value) => inner.ApplyAccessibility(value);
            public OperationResult<IUiModal> ShowModal(UiModalRequest request, Action<bool> completed) => inner.ShowModal(request, completed);
            public OperationResult<bool> ShowToast(string message, UiTone tone = UiTone.Neutral) => inner.ShowToast(message, tone);
            public OperationResult<IUiSurface> CreateSurface(UiSurfaceRequest request)
            {
                var result = inner.CreateSurface(request);
                if (!result.TryGetValue(out var value)) return result;
                var surface = new LossSurface((FakeUiSurface)value); Created.Add(surface);
                return OperationResult<IUiSurface>.Success(surface);
            }
        }

        private sealed class LossSurface : IUiSurface, IUiSurfaceDismissalSource, IPresentationSurfaceState
        {
            internal readonly FakeUiSurface Inner;
            private readonly UiSurfacePresence presence = new UiSurfacePresence(false, true);
            internal LossSurface(FakeUiSurface inner) { Inner = inner; }
            public string Id => Inner.Id;
            public bool IsVisible => presence.IsVisible;
            public bool IsAlive => presence.IsLive;
            public event Action? Dismissed;
            public void Show() { presence.Show(); if (IsAlive) Inner.Show(); }
            public void Hide()
            {
                var effects = presence.Hide();
                if (IsAlive) Inner.Hide();
                if ((effects & UiPresenceEffects.Dismissed) != 0) Dismissed?.Invoke();
            }
            public void SetBody(string body) { if (IsAlive) Inner.SetBody(body); }
            public OperationResult<bool> SetContent(UiNode node) => IsAlive ? Inner.SetContent(node) :
                OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The canvas was lost.");
            public void Dispose() { presence.Dispose(); Inner.Dispose(); Dismissed = null; }
            internal void Lose()
            {
                var effects = presence.Lose(); Inner.Dispose();
                if ((effects & UiPresenceEffects.Dismissed) != 0) Dismissed?.Invoke();
            }
        }
    }
}
