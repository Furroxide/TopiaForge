using System;
using TopiaForge.Mods;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.MultiplayerAcceptance
{
    /// <summary>Shows one fixed development fixture on the host and one admitted guest when explicitly requested.</summary>
    internal sealed class GalleryRemotePreview : IDisposable
    {
        internal const string OptInFile = "gallery-remote-preview.enabled";
        internal const string StatusOnlyOptInFile = "gallery-remote-preview-status-only.enabled";
        internal const string RetainedCountdownOptInFile = "gallery-remote-preview-retained-countdown.enabled";
        private const string SkipExampleBindings = "gallery-remote-preview-no-bindings.enabled";
        internal const string InputAction = "gallery-remote-preview";
        private static readonly ControlGalleryPage[] ControlPages = (ControlGalleryPage[])Enum.GetValues(typeof(ControlGalleryPage));
        private const int VariantsPerPage = 6;
        internal const int StatusVariantCount = 8;
        internal const int ActionVariantCount = 8;
        private static int FoundationVariantCount => FoundationPages.TransportPages.Count * VariantsPerPage;
        internal static int K1VariantCount => FoundationVariantCount + 3;
        internal static int StatusStart => K1VariantCount + ControlPages.Length * 2;
        internal static int ActionStart => StatusStart + StatusVariantCount;
        internal static int VariantCount => ActionStart + ActionVariantCount;
        private readonly IModContext context;
        private readonly IMultiplayerSession session;
        private readonly IDisposable update;
        private readonly IDisposable changed;
        private readonly int first;
        private readonly int limit;
        private readonly bool retainCountdown;
        private int next;
        private IUiSurface? localSurface;
        private IUiSurface? remoteSurface;
        private MultiplayerSessionId? activeSession;
        private ParticipantId? activeGuest;
        private IPlayer? retainedTarget;
        private bool replaced;

        private GalleryRemotePreview(IModContext context, IMultiplayerSession session, IInputAction action)
        {
            this.context = context;
            this.session = session;
            retainCountdown = context.Files.DataFileExists(RetainedCountdownOptInFile);
            var statusOnly = retainCountdown || context.Files.DataFileExists(StatusOnlyOptInFile);
            first = retainCountdown ? ActionStart - 1 : statusOnly ? StatusStart : 0;
            limit = statusOnly ? ActionStart : VariantCount;
            next = first;
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
            if (!context.Files.DataFileExists(SkipExampleBindings))
            {
                var expedition = context.Input.RegisterAction(ControlGalleryPages.Expedition());
                var ledger = context.Input.RegisterAction(ControlGalleryPages.Ledger());
                var actions = ActionGalleryPages.RegisterActions(context.Input);
                if (!expedition.Succeeded || !ledger.Succeeded || !actions)
                    context.Logger.Warn("One or more Gallery example bindings could not be registered or cleared.");
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
            var request = Request(next, context.Ui.ClockTime);
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
            if (retainCountdown) retainedTarget = guest;
            if (++next == limit) next = first;
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

        internal static UiSurfaceRequest Request(int variant, double clockTime)
        {
            if ((uint)variant >= (uint)VariantCount) throw new ArgumentOutOfRangeException(nameof(variant));
            if (variant >= ActionStart)
            {
                var action = variant - ActionStart;
                var actionPage = (ActionGalleryPage)(action / 4);
                var surface = (action & 1) == 0 ? UiSurfaceKind.Window : UiSurfaceKind.Hud;
                return ActionGalleryPages.CreateRequest(actionPage, surface, clockTime,
                    (action & 2) == 0 ? null : FoundationPages.TransportTheme, transport: true);
            }
            if (variant >= StatusStart)
            {
                var status = variant - StatusStart;
                var statusPage = (StatusGalleryPage)(status / 4);
                var statusKind = (status & 1) == 0 ? UiSurfaceKind.Window : UiSurfaceKind.Hud;
                return StatusGalleryPages.CreateRequest(statusPage, statusKind, clockTime, (status & 2) == 0
                    ? null : FoundationPages.TransportTheme);
            }
            if (variant >= K1VariantCount)
            {
                var controlVariant = variant - K1VariantCount;
                return ControlGalleryPages.Create(ControlPages[controlVariant / 2], (controlVariant & 1) == 0
                    ? null : FoundationPages.TransportTheme);
            }
            if (variant >= FoundationVariantCount)
                return FoundationLayoutPages.LayoutNotice(Palette(variant - FoundationVariantCount));
            var page = FoundationPages.TransportPages[variant / VariantsPerPage];
            var kind = (variant & 1) == 0 ? UiSurfaceKind.Window : UiSurfaceKind.Hud;
            return FoundationPages.CreateTransportRequest(page, kind, Palette(variant % VariantsPerPage / 2));

            static UiThemeRef? Palette(int index) => index switch
            {
                1 => FoundationPages.TransportTheme,
                2 => FoundationPages.SurvivalTheme,
                _ => null,
            };
        }

        private void Observe(MultiplayerSessionSnapshot snapshot)
        {
            if (!activeSession.HasValue) return;
            var guest = FindGuest(snapshot);
            if (!guest.Equals(activeGuest) || (retainedTarget != null && !OwnsRetainedTarget(retainedTarget)))
            {
                CloseSurfaces();
                return;
            }
            if (activeSession.Value.Equals(snapshot.Id)) return;

            // A map replacement can keep the original player and publication alive while the stream resynchronizes.
            if (retainedTarget != null && !replaced && remoteSurface?.IsVisible == true && localSurface?.IsVisible == true)
            {
                activeSession = snapshot.Id;
                replaced = true;
                context.Logger.Info("Kept the Countdown fixture on its existing player route through a session replacement.");
                return;
            }
            CloseSurfaces();
        }

        private bool OwnsRetainedTarget(IPlayer expected) => expected.IsPresent &&
            session.Players.TryGet(expected.Id, out var current) && ReferenceEquals(current, expected);

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
            retainedTarget = null;
            replaced = false;
            try { host?.Dispose(); }
            finally { guest?.Dispose(); }
        }
    }
}
