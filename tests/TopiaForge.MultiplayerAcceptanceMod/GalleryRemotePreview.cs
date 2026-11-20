using System;
using TopiaForge.Mods;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.MultiplayerAcceptance
{
    /// <summary>Shows one fixed development fixture on the host and one admitted guest when explicitly requested.</summary>
    internal sealed class GalleryRemotePreview : IDisposable
    {
        internal const string OptInFile = "gallery-remote-preview.enabled";
        internal const string InputAction = "gallery-remote-preview";
        private const int VariantsPerPage = 6;
        internal const int VariantCount = 2 * VariantsPerPage;
        private readonly IModContext context;
        private readonly IMultiplayerSession session;
        private readonly IDisposable update;
        private readonly IDisposable changed;
        private int next;
        private IUiSurface? localSurface;
        private IUiSurface? remoteSurface;
        private MultiplayerSessionId? activeSession;
        private ParticipantId? activeGuest;

        private GalleryRemotePreview(IModContext context, IMultiplayerSession session, IInputAction action)
        {
            this.context = context;
            this.session = session;
            changed = session.SubscribeChanged(Observe);
            update = context.Events.SubscribeUpdate(_ =>
            {
                if (action.WasPressed) ShowNext();
            });
        }

        internal static void Attach(IModContext context)
        {
            if (!context.TryGetMultiplayer(out var session) || session == null)
            {
                context.Logger.Warn("Gallery preview needs the multiplayer provider.");
                return;
            }
            var registration = context.Input.RegisterAction(new InputActionDefinition(InputAction,
                "Show the next Gallery preview on the listen pair", new[] { InputBinding.Key("F7") },
                suppressWhileUiFocused: false));
            if (!registration.TryGetValue(out var action))
            {
                context.Logger.Warn("Gallery preview input could not be registered (" + registration.ErrorCode + ").");
                return;
            }
            context.Lifetime.Track(new GalleryRemotePreview(context, session, action));
        }

        private void ShowNext()
        {
            var snapshot = session.Snapshot;
            var guestId = FindGuest(snapshot);
            if (!guestId.HasValue || !session.Players.TryGet(guestId.Value, out var guest) || guest == null ||
                !guest.IsPresent || guest.IsLocal)
            {
                context.Logger.Warn("The Gallery preview needs a ready listen host and exactly one connected guest.");
                return;
            }

            CloseSurfaces();
            var request = Request(next);
            var local = context.Ui.CreateSurface(request);
            if (!local.TryGetValue(out var host))
            {
                context.Logger.Warn("The host Gallery preview was refused (" + local.ErrorCode + ").");
                return;
            }
            var remote = guest.Ui.CreateSurface(request);
            if (!remote.TryGetValue(out var reflected))
            {
                host.Dispose();
                context.Logger.Warn("The guest Gallery preview was refused (" + remote.ErrorCode + ").");
                return;
            }

            localSurface = host;
            remoteSurface = reflected;
            activeSession = snapshot.Id;
            activeGuest = guestId;
            next = (next + 1) % VariantCount;
            context.Logger.Info("Published the paired Gallery fixture '" + request.Id + "'.");
        }

        internal static ParticipantId? FindGuest(MultiplayerSessionSnapshot snapshot)
        {
            if (snapshot.State != MultiplayerSessionState.Ready || snapshot.ProcessKind != MultiplayerProcessKind.Interactive ||
                !snapshot.LocalParticipantId.HasValue || (snapshot.ExecutionSides & MultiplayerExecutionSide.Server) == 0)
                return null;

            var local = snapshot.LocalParticipantId.Value;
            ParticipantId? guest = null;
            foreach (var participant in snapshot.Participants)
            {
                if (!participant.IsConnected || participant.Id.Equals(local)) continue;
                if (guest.HasValue) return null;
                guest = participant.Id;
            }
            return guest;
        }

        internal static UiSurfaceRequest Request(int variant)
        {
            if ((uint)variant >= VariantCount) throw new ArgumentOutOfRangeException(nameof(variant));
            var page = variant < VariantsPerPage ? FoundationPage.ThemePacks : FoundationPage.Typography;
            var kind = (variant & 1) == 0 ? UiSurfaceKind.Window : UiSurfaceKind.Hud;
            var theme = (variant % VariantsPerPage / 2) switch
            {
                1 => FoundationPages.TransportTheme,
                2 => FoundationPages.SurvivalTheme,
                _ => null,
            };
            return FoundationPages.CreateRequest(page, kind, theme);
        }

        private void Observe(MultiplayerSessionSnapshot snapshot)
        {
            if (!activeSession.HasValue) return;
            var guest = FindGuest(snapshot);
            if (!activeSession.Value.Equals(snapshot.Id) || !guest.Equals(activeGuest))
                CloseSurfaces();
        }

        public void Dispose()
        {
            update.Dispose();
            changed.Dispose();
            CloseSurfaces();
        }

        private void CloseSurfaces()
        {
            var host = localSurface;
            var guest = remoteSurface;
            localSurface = null;
            remoteSurface = null;
            activeSession = null;
            activeGuest = null;
            try { host?.Dispose(); }
            finally { guest?.Dispose(); }
        }
    }
}
