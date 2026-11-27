using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Worlds
{
    internal sealed class WorldNativeDispatchResult
    {
        private WorldNativeDispatchResult(bool accepted, bool fallback, ModErrorCode code, string message)
        { Accepted = accepted; CanFallback = fallback; ErrorCode = code; ErrorMessage = message; }
        internal bool Accepted { get; }
        internal bool CanFallback { get; }
        internal ModErrorCode ErrorCode { get; }
        internal string ErrorMessage { get; }
        internal static WorldNativeDispatchResult From(OperationResult<IInternalNativeSceneOperation> result) =>
            new WorldNativeDispatchResult(result.Succeeded, !result.Succeeded && result.ErrorCode == ModErrorCode.Unavailable, result.ErrorCode, result.ErrorMessage);
        internal static WorldNativeDispatchResult Refused(ModErrorCode code, string message) =>
            new WorldNativeDispatchResult(false, false, code, message);
    }
}
