using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal static class UiStingerDelivery
    {
        // The first UI event after toast, audio play and audio stop on the Players channel.
        internal const byte EventKind = 34;
        internal const byte RetireKind = 35;
    }

    internal interface IUiStingerOrigin
    {
        OperationResult<bool> ShowStinger(UiStingerRequest request, out bool acceptedNew);
        OperationResult<bool> ValidateStingerForDelivery(UiStingerRequest request, UiThemeDescriptor? declared, out UiStingerFontFit fontFit);
    }

    internal interface IRemoteUiStingerReceiver
    {
        OperationResult<bool> ShowRemoteStinger(string ownerPackageId, ulong scopeToken, UiStingerRequest request, UiThemeDescriptor? declared,
            AdmittedUiCatalogue? catalogue, bool hasDeclaredTheme, UiStingerFontFit fontFit,
            UiWireEventEnvelope? authenticatedEnvelope = null);
        bool RetireRemoteStingers(string ownerPackageId, ulong scopeToken);
        void ResetRemoteStingers();
    }
}
