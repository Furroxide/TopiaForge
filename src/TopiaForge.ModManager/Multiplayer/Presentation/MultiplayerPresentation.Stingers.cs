using System;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed partial class MultiplayerPresentation
    {
        private bool reportedAddressedStingerQueue;
        private bool reportedBroadcastStingerQueue;

        private bool ReceiveStinger(WorldWireReader reader)
        {
            var audience = UiNodeWireCodec.Text(reader, 128);
            if (audience.Length != 0 && audience != players.Snapshot?.LocalParticipantId?.Value) return false;
            var request = UiWireCodec.ReadStinger(reader, out var envelope, out var scopeToken, out var fontFit);
            if (!players.AuthenticateUiPackage(envelope.OwnerPackageId, envelope.PackageDigest.Span, out var package)) return false;

            var catalogue = package?.Manifest.AdmittedUi;
            var declared = UiPresentationTheme.ViewerTheme(envelope.ThemeId, envelope.DescriptorDigest.Span, catalogue);
            var hasTheme = envelope.ThemeId != null;
            if ((!hasTheme || declared != null) && !UiLayoutDecoration.AdmitsStingerStyle(request, declared ?? UiThemePackParser.Default))
                return false;
            if (Receiver.Ui is not IRemoteUiStingerReceiver sink) return false;
            var result = sink.ShowRemoteStinger(envelope.OwnerPackageId, scopeToken, request, declared, catalogue, hasTheme, fontFit, envelope);
            if (result.ErrorCode == ModErrorCode.RateLimited) ReportFullStingerQueue(audience.Length == 0);
            return result.Succeeded;
        }

        private bool ReceiveStingerRetirement(WorldWireReader reader)
        {
            var audience = UiNodeWireCodec.Text(reader, 128);
            if (audience.Length == 0 || audience != players.Snapshot?.LocalParticipantId?.Value) return false;
            Span<byte> digest = stackalloc byte[32];
            UiWireCodec.ReadStingerRetirement(reader, digest, out var owner, out var scopeToken);
            return players.AuthenticateUiPackage(owner, digest, out _) &&
                receiver?.Ui is IRemoteUiStingerReceiver sink && sink.RetireRemoteStingers(owner, scopeToken);
        }

        private void ReportFullStingerQueue(bool broadcast)
        {
            ref var reported = ref (broadcast ? ref reportedBroadcastStingerQueue : ref reportedAddressedStingerQueue);
            if (reported) return;
            reported = true;
            try { players.ReportFullIncomingStingerQueue(broadcast); }
            catch { }
        }

        private void ResetRemoteStingers()
        {
            reportedAddressedStingerQueue = false;
            reportedBroadcastStingerQueue = false;
            if (receiver?.Ui is IRemoteUiStingerReceiver sink) sink.ResetRemoteStingers();
        }
    }
}
