using System;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService : IRemoteUiReceiver
    {
        public OperationResult<IUiSurface> RemoteSurface(UiSurfaceRequest request)
            => RemoteSurface(request, default);

        public OperationResult<IUiSurface> RemoteSurface(UiSurfaceRequest request, UiRemoteThemeSelection theme)
        {
            var previous = host.PlainText;
            host.PlainText = true;
            try { return CreateSurface(request, theme); }
            finally { host.PlainText = previous; }
        }
        public OperationResult<IUiModal> RemoteModal(UiModalRequest request, Action<bool> completed)
        {
            var previous = host.PlainText;
            host.PlainText = true;
            try { return ShowModal(request, completed); }
            finally { host.PlainText = previous; }
        }
        public OperationResult<bool> RemoteToast(string message, UiTone tone)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (lifetime.IsStopping) return OperationResult<bool>.Failure(ModErrorCode.Cancelled, "The mod stopped.");
            if ((uint)tone > (uint)UiTone.Danger)
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "The toast tone is not recognized.");
            TopiaForgeToasts.ShowPlain(message, ToNativeTone(tone));
            return OperationResult<bool>.Success(true);
        }
    }
}
