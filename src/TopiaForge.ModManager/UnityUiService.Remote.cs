using System;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService : IRemoteUiReceiver
    {
        public OperationResult<IUiSurface> RemoteSurface(UiSurfaceRequest request)
        {
            var previous = host.PlainText;
            host.PlainText = true;
            try { return CreateSurface(request); }
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
            TopiaForgeToasts.ShowPlain(message, ToNativeTone(tone));
            return OperationResult<bool>.Success(true);
        }
    }
}
