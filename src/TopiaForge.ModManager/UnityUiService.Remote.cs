using System;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService : IRemoteUiReceiver, IRemoteTargetedToastReceiver
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
        public OperationResult<bool> RemoteToast(string message, UiTone tone) => RemoteToast(message, tone, galleryTargeted: false);

        OperationResult<bool> IRemoteTargetedToastReceiver.RemoteTargetedToast(string ownerPackageId, string message, UiTone tone) =>
            RemoteToast(message, tone, galleryTargeted: ownerPackageId == UiGalleryCaptureFixture.RemoteFixtureOwner);

        private OperationResult<bool> RemoteToast(string message, UiTone tone, bool galleryTargeted)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (lifetime.IsStopping) return OperationResult<bool>.Failure(ModErrorCode.Cancelled, "The mod stopped.");
            if ((uint)tone > (uint)UiTone.Danger)
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "The toast tone is not recognized.");
            if (galleryTargeted) PresentGalleryToast(message, tone, legacyRemote: true);
            else TopiaForgeToasts.ShowPlain(message, ToNativeTone(tone), host);
            return OperationResult<bool>.Success(true);
        }
    }
}
