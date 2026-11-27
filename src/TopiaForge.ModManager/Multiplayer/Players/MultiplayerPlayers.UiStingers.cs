using System;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class MultiplayerPlayers
    {
        private static readonly OperationResult<bool> StingerQueueFull = OperationResult<bool>.Failure(ModErrorCode.RateLimited,
            "Three stingers are already waiting for this player in this scope.");
        private static readonly OperationResult<bool> InvalidStingerStyle = OperationResult<bool>.Failure(ModErrorCode.InvalidArgument,
            "A named stinger style must target stinger and preserve its requested role.");

        internal OperationResult<bool> ShowContextStinger(RuntimeReplication context, IUiService local, UiStingerRequest request)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (context.Lifetime.IsStopping)
                return OperationResult<bool>.Failure(ModErrorCode.Cancelled, "The context is stopping.");
            if (Presentation == null || local is not IUiStingerOrigin origin ||
                PresentationRoutingPolicy.Route(Realm, context.Profile, PresentationKind.Toast) != PresentationRoute.BroadcastToEveryone)
                return local.ShowStinger(request);

            var snapshot = Snapshot;
            var compatible = 0;
            var skipped = 0;
            if (snapshot != null)
                foreach (var peer in snapshot.Participants)
                {
                    if (peer.IsLocal || !peer.IsConnected) continue;
                    if (SupportsUiVersion(peer.Id, UiWireCodec.ControlsContractVersion)) compatible++;
                    else skipped++;
                }

            PublishedStingerScope? scope = null;
            byte[]? payload = null;
            double exitSeconds = 0;
            if (compatible != 0)
            {
                var stage = StageStinger(context.Lifetime, context.OwnerModId, local, request, out var envelope, out var declared, out scope);
                if (!stage.Succeeded) return stage;
                var valid = origin.ValidateStingerForDelivery(request, declared, out var fontFit);
                if (!valid.Succeeded) return valid;
                exitSeconds = ReservedStingerExit(declared);
                try
                {
                    var encoded = WriteStingerFrame(string.Empty, envelope!, scope!.Token, fontFit, request);
                    // A local presentation may raise callbacks. Keep this event's exact bytes until its admission.
                    payload = encoded.AsSpan().ToArray();
                }
                catch (ArgumentException error) { return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, error.Message); }
            }

            var shown = origin.ShowStinger(request, out var admitted);
            if (!shown.Succeeded || context.Lifetime.IsStopping) return shown;

            if (skipped != 0 && admitted)
                runtime.Log.Warn("Multiplayer: stinger skipped " + skipped + " guest(s) without UI contract 3.");

            if (payload != null)
                foreach (var peer in snapshot!.Participants)
                {
                    if (peer.IsLocal || !peer.IsConnected || !SupportsUiVersion(peer.Id, UiWireCodec.ControlsContractVersion)) continue;
                    _ = DeliverStinger(scope!, peer.Id, request, exitSeconds, new ArraySegment<byte>(payload));
                }
            return shown;
        }

        internal OperationResult<bool> Stinger(PlayerContext context, ParticipantId player, UiStingerRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var refused = CheckUi<bool>(context, player);
            if (refused != null) return refused;
            if (IsLocal(player)) return context.UiService.ShowStinger(request);
            if (!context.Networked) return Unshared<bool>();
            if (!SupportsUiVersion(player, UiWireCodec.ControlsContractVersion))
                return UiPresentationTheme.UnsupportedVersion(UiWireCodec.ControlsContractVersion);
            if (context.UiService is not IUiStingerOrigin origin) return UnavailableUiStinger.Result;

            var stage = StageStinger(context.Lifetime, context.Mod, context.UiService, request, out var envelope, out var declared, out var scope);
            if (!stage.Succeeded) return stage;
            var valid = origin.ValidateStingerForDelivery(request, declared, out var fontFit);
            if (!valid.Succeeded) return valid;
            try
            {
                var payload = WriteStingerFrame(player.Value, envelope!, scope!.Token, fontFit, request);
                return DeliverStinger(scope!, player, request, ReservedStingerExit(declared), payload);
            }
            catch (ArgumentException error) { return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, error.Message); }
        }

        private static double ReservedStingerExit(UiThemeDescriptor? declared)
        {
            var kit = UiThemePackParser.Default.Motion.SlowMs;
            return Math.Max(kit, declared?.Motion.SlowMs ?? kit) / 1000d;
        }

        internal void ReportFullIncomingStingerQueue(bool broadcast) =>
            runtime.Log.Warn(broadcast
                ? "Multiplayer: a broadcast stinger was dropped because the guest queue was full."
                : "Multiplayer: an addressed stinger was dropped because the guest queue was full.");

        private OperationResult<bool> StageStinger(IModLifetime lifetime, string owner, IUiService local, UiStingerRequest request,
            out UiWireEventEnvelope? envelope, out UiThemeDescriptor? declared, out PublishedStingerScope? scope)
        {
            envelope = null;
            declared = null;
            scope = null;
            if (local is IUiPresentationThemeSource { PresentationThemeScope: { } themeScope } &&
                !themeScope.TryDescribe((UiThemeRef?)null, out declared))
                return UiPresentationTheme.UnknownTheme;
            if (!UiLayoutDecoration.AdmitsStingerStyle(request, declared ?? UiThemePackParser.Default))
                return InvalidStingerStyle;
            if (!TryOutgoingUiDigest(owner, requireSignedRoster: true, out var digest))
                return UiPresentationTheme.UnadmittedPackage;

            envelope = UiPresentationTheme.EventEnvelope(owner, digest!, declared);
            scope = FindOrReserveStingerScope(lifetime, envelope);
            return scope == null ? PlayerDirectory.Unavailable<bool>() : UiPresentationTheme.Allowed;
        }

        private ArraySegment<byte> WriteStingerFrame(string audience, UiWireEventEnvelope envelope, ulong scopeToken,
            UiStingerFontFit fontFit, UiStingerRequest request)
        {
            Begin(UiStingerDelivery.EventKind, string.Empty);
            Writer.WriteString(audience);
            UiWireCodec.WriteStinger(Writer, envelope, scopeToken, fontFit, request);
            return Writer.Segment;
        }
    }
}
