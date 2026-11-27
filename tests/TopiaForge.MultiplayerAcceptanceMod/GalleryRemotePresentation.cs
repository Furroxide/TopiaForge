using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.MultiplayerAcceptance
{
    /// <summary>Publishes the fixed presentation examples on the admitted visual pair.</summary>
    internal sealed class GalleryRemotePresentation : IDisposable
    {
        internal const string OptInFile = "gallery-remote-preview-presentation-only.enabled";
        internal static int VariantsPerPalette => PresentationPages.OverlayOrder.Count + PresentationPages.StingerRoles.Count + 3;

        private readonly IModContext context;
        private readonly IMultiplayerSession session;
        private readonly UiThemeRef? priorTheme;
        private readonly List<IUiSurface> surfaces = new List<IUiSurface>(8);
        private MultiplayerSessionId? activeSession;
        private IPlayer? activeGuest;
        private int next;
        private bool disposed;

        internal GalleryRemotePresentation(IModContext context, IMultiplayerSession session)
        {
            this.context = context;
            this.session = session;
            priorTheme = context.Ui.Themes.SelectedTheme;
        }

        internal void ShowNext(MultiplayerSessionSnapshot snapshot, IPlayer guest)
        {
            if (disposed) return;
            CloseSurfaces();
            var step = next % VariantsPerPalette;
            var theme = next < VariantsPerPalette ? null : FoundationPages.TransportTheme;
            if (!Accept(context.Ui.Themes.UseTheme(theme), "select the presentation theme")) return;

            activeSession = snapshot.Id;
            activeGuest = guest;
            if (!Show(step, theme, out var id))
            {
                Retire();
                return;
            }

            if (++next == 2 * VariantsPerPalette) next = 0;
            context.Logger.Info("Published the paired Gallery presentation fixture '" + id + "' in " + (theme?.Id ?? "kit") + ".");
        }

        private bool Show(int step, UiThemeRef? theme, out string id)
        {
            var overlayCount = PresentationPages.OverlayOrder.Count;
            if (step < overlayCount)
            {
                var overlay = PresentationPages.OverlayOrder[step];
                id = PresentationPages.OverlayButtonId(overlay);
                return Open(close => PresentationPages.Overlay(overlay, close, theme), out _);
            }
            if (step == overlayCount)
            {
                id = PresentationPages.OverlayStackId;
                return ShowOverlayStack(theme);
            }

            var role = step - overlayCount - 1;
            if (role < PresentationPages.StingerRoles.Count)
            {
                var requested = PresentationPages.StingerRoles[role];
                id = PresentationPages.StingerButtonId(requested);
                return ShowStinger(PresentationPages.Stinger(requested), ModErrorCode.None);
            }
            if (role == PresentationPages.StingerRoles.Count)
            {
                id = PresentationPages.StingerQueueId;
                var queue = PresentationPages.StingerQueue();
                for (var index = 0; index < queue.Count; index++)
                {
                    var expected = index == queue.Count - 1 ? ModErrorCode.RateLimited : ModErrorCode.None;
                    if (!ShowStinger(queue[index], expected)) return false;
                }
                return true;
            }

            id = PresentationPages.ToastStackId;
            if (!Open(_ => PresentationPages.RetainedHud(theme), out _)) return false;
            foreach (var tone in PresentationPages.ToastTones)
            {
                if (!OwnsPair(session.Snapshot)) return false;
                var message = PresentationPages.ToastMessage(tone);
                if (!Accept(context.Ui.ShowToast(message, tone), "show a host toast") ||
                    !OwnsPair(session.Snapshot) ||
                    !Accept(activeGuest!.Ui.ShowToast(message, tone), "show a guest toast")) return false;
            }
            return true;
        }

        private bool ShowOverlayStack(UiThemeRef? theme)
        {
            if (!Open(_ => PresentationPages.RetainedHud(theme), out var hud)) return false;
            // One author scope owns the host and guest handles together. Two pairs use its four overlay slots.
            if (!Open(close => PresentationPages.Overlay(PresentationOverlay.Passive, close, theme), out _) ||
                !Open(close => PresentationPages.Overlay(PresentationOverlay.ButtonOnly, close, theme), out _))
                return false;

            var status = PresentationPages.HudStatus("Supplies loaded.");
            return OwnsPair(session.Snapshot) && Accept(hud.Host.SetContent(status), "update the host HUD") &&
                OwnsPair(session.Snapshot) &&
                Accept(hud.Guest.SetContent(status), "update the guest HUD");
        }

        private bool Open(Func<Action, UiSurfaceRequest> build, out (IUiSurface Host, IUiSurface Guest) pair)
        {
            pair = default;
            if (!OwnsPair(session.Snapshot)) return false;
            var target = activeGuest!;
            var epoch = activeSession!.Value;
            IUiSurface? host = null;
            var hostResult = context.Ui.CreateSurface(build(() => Hide(host, target, epoch)));
            if (!Accept(hostResult, "open the host surface") || !hostResult.TryGetValue(out host)) return false;
            surfaces.Add(host);

            if (!OwnsPair(session.Snapshot)) return false;
            IUiSurface? guest = null;
            var guestResult = target.Ui.CreateSurface(build(() => Hide(guest, target, epoch)));
            if (!Accept(guestResult, "open the guest surface") || !guestResult.TryGetValue(out guest)) return false;
            surfaces.Add(guest);
            pair = (host, guest);
            return true;
        }

        private void Hide(IUiSurface? surface, IPlayer guest, MultiplayerSessionId epoch)
        {
            if (surface?.IsVisible == true && ReferenceEquals(activeGuest, guest) &&
                activeSession is { } current && current.Equals(epoch) &&
                OwnsPair(session.Snapshot))
                surface.Hide();
        }

        private bool ShowStinger(UiStingerRequest request, ModErrorCode expected)
        {
            if (!OwnsPair(session.Snapshot)) return false;
            if (!Accept(context.Ui.ShowStinger(request), "show a host stinger", expected) || !OwnsPair(session.Snapshot))
                return false;
            return Accept(activeGuest!.Ui.ShowStinger(request), "show a guest stinger", expected);
        }

        private bool Accept<T>(OperationResult<T> result, string operation, ModErrorCode expected = ModErrorCode.None)
            where T : notnull
        {
            if (expected == ModErrorCode.None ? result.Succeeded : result.ErrorCode == expected) return true;
            context.Logger.Warn("The paired Gallery fixture could not " + operation + " (" + result.ErrorCode + ").");
            return false;
        }

        internal void Observe(MultiplayerSessionSnapshot snapshot)
        {
            if (activeGuest != null && !OwnsPair(snapshot)) Retire();
        }

        private bool OwnsPair(MultiplayerSessionSnapshot snapshot) => !context.Lifetime.IsStopping && activeGuest is { } guest &&
            !session.CurrentSessionToken.IsCancellationRequested && activeSession is { } currentSession && currentSession.Equals(snapshot.Id) &&
            guest.IsPresent && !guest.IsLocal && GalleryRemotePreview.FindGuest(snapshot) is { } guestId && guestId.Equals(guest.Id) &&
            session.Players.TryGet(guest.Id, out var current) && ReferenceEquals(current, guest);

        private void CloseSurfaces()
        {
            activeGuest = null;
            activeSession = null;
            for (var index = surfaces.Count - 1; index >= 0; index--)
            {
                try { surfaces[index].Dispose(); }
                catch (Exception error) { context.Logger.Warn("A Gallery presentation surface failed to retire (" + error.GetType().Name + ")."); }
            }
            surfaces.Clear();
        }

        private void Retire()
        {
            CloseSurfaces();
            if (!context.Lifetime.IsStopping && !Equals(context.Ui.Themes.SelectedTheme, priorTheme))
                Accept(context.Ui.Themes.UseTheme(priorTheme), "restore the presentation theme");
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Retire();
        }
    }
}
