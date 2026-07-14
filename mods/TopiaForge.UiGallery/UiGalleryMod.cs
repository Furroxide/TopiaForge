using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.UiGallery
{
    /// <summary>
    /// Dev-only living catalog of the TopiaForgeUi kit. F8 toggles the gallery window; every
    /// widget renders in both schemes with live accessibility toggles, making this the
    /// manual-QA surface and the copy-paste reference for mod authors.
    /// </summary>
    public sealed class UiGalleryMod : ITopiaForgeMod
    {
        private UiHost? ui;
        private GalleryWindow? gallery;

        public void OnLoad(IModContext context)
        {
            ui = TopiaForgeUi.For(context);
            ui.Hotkey(TopiaForgeKey.F8, () =>
            {
                gallery ??= new GalleryWindow(ui);
                gallery.Toggle();
            });
            context.Logger.Info("UI Gallery loaded - press F8 to open.");
        }

        public void OnUnload()
        {
            gallery?.Dispose();
            gallery = null;
            ui?.Dispose();
            ui = null;
        }
    }
}
