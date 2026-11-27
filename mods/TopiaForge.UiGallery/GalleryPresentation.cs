using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.UiGallery
{
    internal sealed class GalleryPresentation : IDisposable
    {
        private readonly IModContext context;
        private readonly IUiService ui;
        private readonly UiThemeRef? priorTheme;
        private readonly UiAccessibilityPreferences priorPreferences;
        private readonly List<IUiSurface> overlays = new List<IUiSurface>();
        private IUiSurface? launcher;
        private IUiSurface? hud;
        private bool disposed;

        private GalleryPresentation(IModContext context)
        {
            this.context = context;
            ui = context.Ui;
            priorTheme = ui.Themes.SelectedTheme;
            priorPreferences = ui.Accessibility;
        }

        internal static GalleryPresentation? Open(IModContext context, PresentationPage page, UiThemeRef? theme,
            UiAccessibilityPreferences preferences)
        {
            var session = new GalleryPresentation(context);
            // Events take the owner's scope theme. Each surface inherits that same temporary selection.
            if (!session.Accept(session.ui.Themes.UseTheme(theme), "select the presentation theme")) return null;

            try
            {
                if (!session.Accept(session.ui.ApplyAccessibility(preferences), "apply presentation settings")) return null;
                session.launcher = session.Create(page switch
                {
                    PresentationPage.Stingers => PresentationPages.StingerControls(session.ShowStinger, session.ShowStingerQueue),
                    PresentationPage.Overlays => PresentationPages.OverlayControls(session.ShowOverlay, session.ShowOverlayStack),
                    PresentationPage.Toasts => PresentationPages.ToastControls(session.ShowToast, session.ShowToastStack),
                    _ => throw new ArgumentOutOfRangeException(nameof(page)),
                });
                return session.launcher == null ? null : session;
            }
            finally
            {
                if (session.launcher == null) session.Dispose();
            }
        }

        internal bool Tick()
        {
            if (!IsShowing) return false;
            for (var index = overlays.Count - 1; index >= 0; index--)
            {
                if (overlays[index].IsVisible) continue;
                overlays[index].Dispose();
                overlays.RemoveAt(index);
            }
            return true;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                ClearExamples();
                launcher?.Dispose();
            }
            finally
            {
                launcher = null;
                if (!context.Lifetime.IsStopping)
                {
                    Accept(ui.Themes.UseTheme(priorTheme), "restore the Gallery theme");
                    Accept(ui.ApplyAccessibility(priorPreferences), "restore Gallery accessibility");
                }
            }
        }

        private bool IsShowing => !disposed && launcher?.IsVisible == true && !context.Lifetime.IsStopping;

        private void ShowStinger(UiStingerRequest request)
        {
            if (!IsShowing) return;
            launcher!.SetBody("");
            OfferStinger(request);
        }

        private void ShowStingerQueue()
        {
            if (!IsShowing) return;
            launcher!.SetBody("");
            foreach (var request in PresentationPages.StingerQueue()) OfferStinger(request);
        }

        private void OfferStinger(UiStingerRequest request)
        {
            var result = ui.ShowStinger(request);
            if (result.ErrorCode == ModErrorCode.RateLimited)
                launcher!.SetBody("The announcement queue is full.");
            else
                Accept(result, "show a stinger");
        }

        private void ShowToast(UiTone tone)
        {
            if (IsShowing) Accept(ui.ShowToast(PresentationPages.ToastMessage(tone), tone), "show a toast");
        }

        private void ShowToastStack()
        {
            foreach (var tone in PresentationPages.ToastTones) ShowToast(tone);
        }

        private void ShowOverlay(PresentationOverlay variant)
        {
            if (!IsShowing) return;
            ClearExamples();
            CreateOverlay(variant);
        }

        private void ShowOverlayStack()
        {
            if (!IsShowing) return;
            ClearExamples();
            hud = Create(PresentationPages.RetainedHud());
            if (hud == null) return;

            foreach (var variant in PresentationPages.OverlayOrder)
            {
                if (CreateOverlay(variant)) continue;
                ClearExamples();
                return;
            }

            Accept(hud.SetContent(PresentationPages.HudStatus("Supplies loaded.")), "update the retained HUD");
        }

        private bool CreateOverlay(PresentationOverlay variant)
        {
            IUiSurface? overlay = null;
            overlay = Create(PresentationPages.Overlay(variant, () =>
            {
                if (overlay?.IsVisible == true) overlay.Hide();
            }));
            if (overlay == null) return false;
            overlays.Add(overlay);
            return true;
        }

        private IUiSurface? Create(UiSurfaceRequest request)
        {
            var result = ui.CreateSurface(request);
            return Accept(result, "open " + request.Title) ? result.Value : null;
        }

        private void ClearExamples()
        {
            for (var index = overlays.Count - 1; index >= 0; index--) overlays[index].Dispose();
            overlays.Clear();
            hud?.Dispose();
            hud = null;
        }

        private bool Accept<T>(OperationResult<T> result, string action) where T : notnull
        {
            if (result.Succeeded) return true;
            context.Logger.Warn("Gallery could not " + action + " (" + result.ErrorCode + "). " + result.ErrorMessage);
            return false;
        }
    }
}
