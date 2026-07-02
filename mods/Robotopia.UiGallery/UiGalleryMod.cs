using Robotopia.Mods;
using Robotopia.Mods.UnityUi;

namespace Robotopia.UiGallery
{
    /// <summary>
    /// Dev-only living catalog of the QwUi kit. F8 toggles the gallery window; every
    /// widget renders in both schemes with live accessibility toggles, making this the
    /// manual-QA surface and the copy-paste reference for mod authors.
    /// </summary>
    public sealed class UiGalleryMod : IRobotopiaMod
    {
        private UiHost? ui;
        private GalleryWindow? gallery;

        public void OnLoad(IModContext context)
        {
            ui = QwUi.For(context);
            ui.Hotkey(QwKey.F8, () =>
            {
                gallery ??= new GalleryWindow(ui);
                gallery.Toggle();
            });
            context.Logger.Info("UI Gallery loaded - press F8 to open.");
        }

        public void OnUnload()
        {
            ui?.Dispose();
            ui = null;
            gallery = null;
        }
    }
}
