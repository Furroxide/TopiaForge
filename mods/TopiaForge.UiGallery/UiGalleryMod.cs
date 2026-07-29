using System;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.UiGallery
{
    /// <summary>
    /// Dev-only living catalog of the TopiaForgeUi kit. F8 toggles the gallery window; every
    /// widget renders in both schemes with live accessibility toggles, making this the
    /// manual-QA surface and the copy-paste reference for mod authors.
    /// </summary>
    public sealed class UiGalleryMod : TopiaForgeMod
    {
        private UiHost? ui;
        private GalleryWindow? gallery;
        private IInputAction? toggleAction;
        private IDisposable? galleryLifetime;

        /// <inheritdoc />
        protected override void OnLoad()
        {
            ui = TopiaForgeUi.For(Context);
            Context.Lifetime.Track(ui);
            var registration = Context.Input.RegisterAction(new InputActionDefinition(
                "toggle-gallery",
                "Toggle TopiaForge UI Gallery",
                new[] { InputBinding.Key("F8") }));
            if (!registration.TryGetValue(out toggleAction))
            {
                Context.Logger.Error(
                    "UI Gallery input registration failed (" + registration.ErrorCode + "): " +
                    registration.ErrorMessage);
                return;
            }

            Context.Events.SubscribeUpdate(deltaTime =>
            {
                if (toggleAction?.WasPressed != true || ui == null)
                {
                    return;
                }

                if (gallery == null)
                {
                    gallery = new GalleryWindow(ui);
                    galleryLifetime = Context.Lifetime.Track(gallery);
                }

                gallery.Toggle();
            });
            Context.Logger.Info("UI Gallery loaded - press F8 to open.");
        }

        /// <inheritdoc />
        protected override void OnUnload()
        {
            // SDK input/events, the gallery, and the direct kit host are released in reverse order by Lifetime.
            galleryLifetime = null;
            gallery = null;
            toggleAction = null;
            ui = null;
        }
    }
}
