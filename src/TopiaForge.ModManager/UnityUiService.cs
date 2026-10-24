using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// One mod's (or one scope's) <see cref="IUiService"/>. Every surface and modal it makes lives as long as that mod or
    /// scope: their canvases survive scene loads, and the lifetime ends them. A gameplay HUD hides by itself outside
    /// gameplay; a surface whose canvas the game or another mod destroys reports itself hidden for good.
    /// </summary>
    internal sealed partial class OwnerUiService : IUiService
    {
        private readonly IModLifetime lifetime;
        private readonly IModLogger logger;
        private readonly UiHost host;
        private readonly UiGameplayGate gameplay;
        private readonly UnityUiHudPlacement hudPlacement;
        private readonly HashSet<string> surfaceIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<UnityUiSurface> surfaces = new List<UnityUiSurface>();
        private UiAccessibilityPreferences accessibility = UiAccessibilityPreferences.Default;

        public OwnerUiService(
            string ownerModId,
            string dataPath,
            IModLifetime lifetime,
            IModLogger logger,
            UiGameplayGate gameplay,
            UnityUiHudPlacement hudPlacement)
        {
            UnityMainThreadGuard.AssertCurrent();
            this.lifetime = lifetime;
            this.logger = logger;
            this.gameplay = gameplay ?? throw new ArgumentNullException(nameof(gameplay));
            this.hudPlacement = hudPlacement ?? throw new ArgumentNullException(nameof(hudPlacement));
            host = TopiaForgeUi.Create(new TopiaForgeUiOptions
            {
                OwnerId = ownerModId,
                DataDirectory = dataPath,
                LogInfo = logger.Info,
                LogWarn = logger.Warn,
                LogError = logger.Error
            });
            lifetime.Track(host);
            host.LayerLost += OnLayerLost;
            lifetime.Track(gameplay.Subscribe(OnGameplayChanged));
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
                hudPlacement.Refresh();
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
                // Every surface's canvas is persistent: it lives as long as this mod or scope, never as long as a scene.
                if (request.Kind == UiSurfaceKind.Window)
                {
                    var window = host.Window(
                        request.Id,
                        request.Title,
                        request.Width,
                        request.Height,
                        TopiaForgeScheme.Paper,
                        persistent: true);
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
                        ReleaseSurface);
                    nativeRoot = null;
                }
                else if (request.Kind == UiSurfaceKind.FullscreenTool)
                {
                    var tool = host.FullscreenTool(request.Id, request.Title, TopiaForgeScheme.Paper, persistent: true);
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
                        ReleaseSurface);
                    nativeRoot = null;
                }
                else
                {
                    surface = CreateHud(request, ref nativeRoot);
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
                surfaces.Add(surface);
                surface.Present();
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
                // Persistent, as surfaces are: the question stays open across scene loads until it is answered or closed,
                // or the mod or scope that asked it ends.
                modal = host.Modal.Custom(request.Title, TopiaForgeScheme.Paper, 520f, persistent: true);
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

        private void ReleaseSurface(UnityUiSurface surface)
        {
            surfaceIds.Remove(surface.Id);
            surfaces.Remove(surface);
        }

        private void OnGameplayChanged(bool isGameplay)
        {
            for (var index = surfaces.Count - 1; index >= 0; index--)
            {
                if (index >= surfaces.Count || !surfaces[index].FollowsGameplay)
                {
                    continue;
                }

                try
                {
                    surfaces[index].ApplyGameplay(isGameplay);
                }
                catch (Exception exception)
                {
                    ReportCleanupFailure(isGameplay ? "showing a gameplay HUD" : "hiding a gameplay HUD", exception);
                }
            }
        }

        private void OnLayerLost(GameObject root)
        {
            foreach (var surface in surfaces.ToArray())
            {
                if (ReferenceEquals(surface.CanvasRoot, root))
                {
                    surface.HandleLost();
                }
            }
        }

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
