using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService : IUiService
    {
        private readonly IModLifetime lifetime;
        private readonly IModLogger logger;
        private readonly UiHost host;
        private readonly HashSet<string> surfaceIds = new HashSet<string>(StringComparer.Ordinal);
        private UiAccessibilityPreferences accessibility = UiAccessibilityPreferences.Default;

        public OwnerUiService(
            string ownerModId,
            string dataPath,
            IModLifetime lifetime,
            IModLogger logger)
        {
            UnityMainThreadGuard.AssertCurrent();
            this.lifetime = lifetime;
            this.logger = logger;
            host = TopiaForgeUi.Create(new TopiaForgeUiOptions
            {
                OwnerId = ownerModId,
                DataDirectory = dataPath,
                LogInfo = logger.Info,
                LogWarn = logger.Warn,
                LogError = logger.Error
            });
            lifetime.Track(host);
        }

        public UiAccessibilityPreferences Accessibility => accessibility;

        public OperationResult<UiAccessibilityPreferences> ApplyAccessibility(UiAccessibilityPreferences preferences)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (preferences == null) throw new ArgumentNullException(nameof(preferences));
            if (lifetime.IsStopping)
            {
                return OperationResult<UiAccessibilityPreferences>.Failure(
                    ModErrorCode.Cancelled,
                    "The mod is stopping and cannot change UI accessibility preferences.");
            }

            try
            {
                host.SetAccessibilityProfile(new TopiaForgeAccessibilityProfile(
                    preferences.HighContrast,
                    preferences.UiScale,
                    preferences.ReducedMotion,
                    preferences.MotionIntensity));
                accessibility = preferences;
                return OperationResult<UiAccessibilityPreferences>.Success(preferences);
            }
            catch (Exception exception)
            {
                return OperationResult<UiAccessibilityPreferences>.Failure(
                    ModErrorCode.External,
                    "TopiaForgeUi could not apply accessibility preferences: " + exception.Message);
            }
        }

        public OperationResult<IUiSurface> CreateSurface(UiSurfaceRequest request)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (lifetime.IsStopping)
            {
                return OperationResult<IUiSurface>.Failure(
                    ModErrorCode.Cancelled,
                    "The mod is stopping and cannot create UI surfaces.");
            }

            if (!surfaceIds.Add(request.Id))
            {
                return OperationResult<IUiSurface>.Failure(
                    ModErrorCode.Conflict,
                    "A UI surface already uses id '" + request.Id + "'.");
            }

            UnityUiSurface? surface = null;
            TopiaForgeWidget? nativeRoot = null;
            try
            {
                if (request.Kind == UiSurfaceKind.Window)
                {
                    var window = host.Window(
                        request.Id,
                        request.Title,
                        request.Width,
                        request.Height,
                        TopiaForgeScheme.Paper);
                    nativeRoot = window;
                    var scroll = window.Content.Scroll(TopiaForgeGap.Sm, TopiaForgeGap.Md);
                    var body = scroll.Content.Label(request.Body, TopiaForgeTextStyle.Body);
                    surface = UnityUiSurface.ForWindow(
                        request.Id,
                        window,
                        body,
                        scroll.Content,
                        lifetime,
                        logger,
                        ReleaseSurfaceId);
                    nativeRoot = null;
                }
                else if (request.Kind == UiSurfaceKind.FullscreenTool)
                {
                    var tool = host.FullscreenTool(request.Id, request.Title, TopiaForgeScheme.Paper);
                    nativeRoot = tool;
                    var scroll = tool.Content.Scroll(TopiaForgeGap.Sm, TopiaForgeGap.None).Flex(1f, 1f);
                    var body = scroll.Content.Label(request.Body, TopiaForgeTextStyle.Body);
                    surface = UnityUiSurface.ForFullscreen(
                        request.Id,
                        tool,
                        body,
                        scroll.Content,
                        lifetime,
                        logger,
                        ReleaseSurfaceId);
                    nativeRoot = null;
                }
                else
                {
                    var layer = host.HudLayer(request.Id);
                    nativeRoot = layer;
                    var panel = layer.Panel(TopiaForgePanelStyle.HudPanel)
                        .Dock(TopiaForgeCorner.TopLeft)
                        .Size(request.Width, request.Height);
                    var column = panel.Column(TopiaForgeGap.Sm, TopiaForgeGap.Md);
                    column.Label(request.Title, TopiaForgeTextStyle.Heading);
                    var scroll = column.Scroll(TopiaForgeGap.Sm, TopiaForgeGap.None);
                    var body = scroll.Content.Label(request.Body, TopiaForgeTextStyle.Body);
                    surface = UnityUiSurface.ForWidget(
                        request.Id,
                        layer,
                        body,
                        scroll.Content,
                        lifetime,
                        logger,
                        ReleaseSurfaceId);
                    nativeRoot = null;
                }

                if (request.Content != null)
                {
                    var contentResult = surface.SetContent(request.Content);
                    if (!contentResult.Succeeded)
                    {
                        AbortSurfaceCreation(surface, nativeRoot, request.Id);
                        return OperationResult<IUiSurface>.Failure(
                            contentResult.ErrorCode,
                            contentResult.ErrorMessage);
                    }
                }

                surface.AttachLifetimeLease(lifetime.Track(surface));
                surface.Show();
                return OperationResult<IUiSurface>.Success(surface);
            }
            catch (Exception exception)
            {
                AbortSurfaceCreation(surface, nativeRoot, request.Id);
                return OperationResult<IUiSurface>.Failure(
                    ModErrorCode.External,
                    "TopiaForgeUi could not create the surface: " + exception.Message);
            }
        }

        public OperationResult<IUiModal> ShowModal(UiModalRequest request, Action<bool> completed)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (completed == null)
            {
                throw new ArgumentNullException(nameof(completed));
            }

            if (lifetime.IsStopping)
            {
                return OperationResult<IUiModal>.Failure(
                    ModErrorCode.Cancelled,
                    "The mod is stopping and cannot show a modal.");
            }

            TopiaForgeModalInstance? modal = null;
            UnityUiModal? state = null;
            try
            {
                // The confirmation presets are fire-and-forget; the safe SDK contract needs a retained handle
                // that can distinguish confirmation, cancellation, native close, and lifetime shutdown.
                modal = host.Modal.Custom(request.Title, TopiaForgeScheme.Paper, 520f);
                state = new UnityUiModal(modal, completed, logger);
                modal.Content.Label(request.Body, TopiaForgeTextStyle.Body);
                var row = modal.Content.Row(TopiaForgeGap.Sm);
                row.Spacer();
                row.Button(request.CancelLabel, state.Cancel, TopiaForgeButtonStyle.Ghost);
                row.Button(
                    request.ConfirmLabel,
                    state.Confirm,
                    request.Destructive ? TopiaForgeButtonStyle.Danger : TopiaForgeButtonStyle.Filled);
                modal.Closed += state.HandleNativeClosed;
                state.AttachLifetimeLease(lifetime.Track(state));
                modal.Show();
                state.ArmCompletion();
                return OperationResult<IUiModal>.Success(state);
            }
            catch (Exception exception)
            {
                try
                {
                    state?.Abort();
                    if (state == null)
                    {
                        modal?.Close();
                    }
                }
                catch (Exception cleanupException)
                {
                    ReportCleanupFailure("modal creation", cleanupException);
                }

                return OperationResult<IUiModal>.Failure(
                    ModErrorCode.External,
                    "TopiaForgeUi could not show the modal: " + exception.Message);
            }
        }

        public OperationResult<bool> ShowToast(string message, UiTone tone = UiTone.Neutral)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (string.IsNullOrWhiteSpace(message))
            {
                return OperationResult<bool>.Failure(
                    ModErrorCode.InvalidArgument,
                    "A toast message is required.");
            }

            if (!Enum.IsDefined(typeof(UiTone), tone))
            {
                return OperationResult<bool>.Failure(
                    ModErrorCode.InvalidArgument,
                    "The toast tone is not recognized.");
            }

            if (lifetime.IsStopping)
            {
                return OperationResult<bool>.Failure(
                    ModErrorCode.Cancelled,
                    "The mod is stopping and cannot show a toast.");
            }

            try
            {
                host.Toast(message, ToNativeTone(tone));
                return OperationResult<bool>.Success(true);
            }
            catch (Exception exception)
            {
                return OperationResult<bool>.Failure(
                    ModErrorCode.External,
                    "TopiaForgeUi could not show the toast: " + exception.Message);
            }
        }

        private static TopiaForgeTone ToNativeTone(UiTone tone)
        {
            switch (tone)
            {
                case UiTone.Success:
                    return TopiaForgeTone.Success;
                case UiTone.Warning:
                    return TopiaForgeTone.Warning;
                case UiTone.Danger:
                    return TopiaForgeTone.Danger;
                default:
                    return TopiaForgeTone.Neutral;
            }
        }

        private void ReleaseSurfaceId(string id) => surfaceIds.Remove(id);

        private void AbortSurfaceCreation(
            UnityUiSurface? surface,
            TopiaForgeWidget? nativeRoot,
            string id)
        {
            try
            {
                if (surface != null)
                {
                    surface.Dispose();
                }
                else
                {
                    nativeRoot?.Destroy();
                }
            }
            catch (Exception exception)
            {
                ReportCleanupFailure("surface creation", exception);
            }
            finally
            {
                surfaceIds.Remove(id);
            }
        }

        private void ReportCleanupFailure(string operation, Exception exception)
        {
            try { logger.Error(exception, "TopiaForgeUi cleanup failed after " + operation + "."); }
            catch { }
        }
    }
}
