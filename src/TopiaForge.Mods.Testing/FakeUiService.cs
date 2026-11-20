using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Captures UI surfaces, modals, and toasts without rendering.</summary>
    /// <remarks>Standalone instances do not check thread access. Use <see cref="FakeModContext.CreateWithThreadChecks"/>
    /// to check this service's engine-facing entry points on the context's creating thread.</remarks>
    public sealed partial class FakeUiService : IUiService
    {
        private readonly FakeGameThread gameThread;
        private readonly FakeModLifetime lifetime;
        private readonly List<FakeUiSurface> surfaces = new List<FakeUiSurface>();
        private readonly List<FakeUiModal> modals = new List<FakeUiModal>();
        private readonly List<FakeToast> toasts = new List<FakeToast>();
        private readonly IReadOnlyList<FakeUiSurface> surfaceView;
        private readonly IReadOnlyList<FakeUiModal> modalView;
        private readonly IReadOnlyList<FakeToast> toastView;
        private readonly Dictionary<string, ContentFailure> pendingContentFailures =
            new Dictionary<string, ContentFailure>(StringComparer.Ordinal);
        private UiAccessibilityPreferences accessibility = UiAccessibilityPreferences.Default;
        private FakeSceneService? scenes;
        private ulong nextTargetedSurfaceToken = 1;

        /// <summary>
        /// Creates a fake UI service. Its gameplay HUDs show whenever they are shown, until <see cref="FakeModContext"/>
        /// connects it to its fake scenes, after which they show only in gameplay, as in the game.
        /// </summary>
        public FakeUiService(FakeModLifetime lifetime) : this(lifetime, default(FakeGameThread)) { }

        internal FakeUiService(FakeModLifetime lifetime, FakeGameThread gameThread, FakeUiThemeScope? parentThemeScope = null,
            string? hudPackageDisplayName = null)
        {
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
            this.gameThread = gameThread;
            HudPackageDisplayName = hudPackageDisplayName;
            themeScope = new FakeUiThemeScope(lifetime, gameThread, parentThemeScope);
            surfaceView = surfaces.AsReadOnly();
            modalView = modals.AsReadOnly();
            toastView = toasts.AsReadOnly();
        }

        /// <summary>Gets active fake UI surfaces.</summary>
        public IReadOnlyList<FakeUiSurface> Surfaces => surfaceView;

        /// <summary>Gets open fake UI modals.</summary>
        public IReadOnlyList<FakeUiModal> Modals => modalView;

        /// <summary>Gets every captured toast in display order.</summary>
        public IReadOnlyList<FakeToast> Toasts => toastView;

        /// <summary>
        /// Causes the next content update for a surface id to return a stable failure. An existing surface is armed
        /// immediately; otherwise the failure is queued for the next surface created with that id.
        /// </summary>
        public void FailNextContentUpdate(
            string surfaceId,
            ModErrorCode errorCode = ModErrorCode.External,
            string message = "The fake UI content update was rejected.")
        {
            if (string.IsNullOrWhiteSpace(surfaceId))
            {
                throw new ArgumentException("A surface id is required.", nameof(surfaceId));
            }

            if (errorCode == ModErrorCode.None)
            {
                throw new ArgumentOutOfRangeException(nameof(errorCode));
            }

            foreach (var surface in surfaces)
            {
                if (string.Equals(surface.Id, surfaceId, StringComparison.Ordinal))
                {
                    surface.FailNextContentUpdate(errorCode, message ?? string.Empty);
                    return;
                }
            }

            pendingContentFailures[surfaceId] = new ContentFailure(errorCode, message ?? string.Empty);
        }

        /// <inheritdoc/>
        public UiAccessibilityPreferences Accessibility => accessibility;

        /// <inheritdoc/>
        public OperationResult<UiAccessibilityPreferences> ApplyAccessibility(UiAccessibilityPreferences preferences)
        {
            gameThread.AssertCurrent();
            if (preferences == null) throw new ArgumentNullException(nameof(preferences));
            if (lifetime.IsStopping)
            {
                return OperationResult<UiAccessibilityPreferences>.Failure(
                    ModErrorCode.Cancelled,
                    "The fake mod is stopping and cannot change UI accessibility preferences.");
            }

            var geometryChanged = preferences.UiScale != accessibility.UiScale || preferences.HighContrast != accessibility.HighContrast;
            accessibility = preferences;
            if (geometryChanged) RefreshHudLayoutNotices();
            return OperationResult<UiAccessibilityPreferences>.Success(accessibility);
        }

        /// <inheritdoc/>
        public OperationResult<IUiSurface> CreateSurface(UiSurfaceRequest request) => CreateSurface(request, null);

        internal OperationResult<IUiSurface> CreateTargetedSurface(UiSurfaceRequest request, string player, ulong generation)
        {
            var target = new FakeUiWireTarget(player, generation, nextTargetedSurfaceToken);
            var created = CreateSurface(request, target);
            if (created.Succeeded) nextTargetedSurfaceToken++;
            return created;
        }

        private OperationResult<IUiSurface> CreateSurface(UiSurfaceRequest request, FakeUiWireTarget? wireTarget)
        {
            gameThread.AssertCurrent();
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (lifetime.IsStopping)
            {
                return OperationResult<IUiSurface>.Failure(
                    ModErrorCode.Cancelled,
                    "The fake mod is stopping and cannot create UI surfaces.");
            }

            foreach (var existing in surfaces)
            {
                if (string.Equals(existing.Id, request.Id, StringComparison.Ordinal))
                {
                    return OperationResult<IUiSurface>.Failure(
                        ModErrorCode.Conflict,
                        "A UI surface already uses id '" + request.Id + "'.");
                }
            }

            var themeCheck = themeScope.Package.ValidateSelection(themeScope, request.Theme);
            if (!themeCheck.Succeeded)
                return OperationResult<IUiSurface>.Failure(themeCheck.ErrorCode, themeCheck.ErrorMessage);

            var scenesNow = scenes;
            var surface = new FakeUiSurface(
                request,
                this,
                RemoveSurface,
                scenesNow == null ? (Func<bool>?)null : () => scenesNow.IsGameplay,
                wireTarget);
            surface.SetClock(() => widgetClock(), () => WidgetEpoch());
            var epoch = WidgetEpoch();
            try
            {
                wireTarget?.Measure(this, 1, request, request.Body, visible: true, request.Content, epoch, retained: false);
                FakeUiTreeCapture.VerifyComposition(this, surface, request.Content);
                UiClockBinding.Bind(request.Content, epoch);
            }
            catch (ArgumentException exception) { return OperationResult<IUiSurface>.Failure(ModErrorCode.InvalidArgument, exception.Message); }
            catch (InvalidOperationException) when (wireTarget.HasValue)
            {
                return OperationResult<IUiSurface>.Failure(ModErrorCode.RateLimited, "The remote UI surface exceeds its packet budget.");
            }

            if (pendingContentFailures.TryGetValue(request.Id, out var contentFailure))
            {
                pendingContentFailures.Remove(request.Id);
                surface.FailNextContentUpdate(contentFailure.ErrorCode, contentFailure.Message);
            }

            surfaces.Add(surface);
            try
            {
                surface.AttachLifetimeLease(lifetime.Track(surface));
                themeScope.Package.AddSurface(themeScope, surface);
                if (request.Kind == UiSurfaceKind.Hud) RefreshHudLayoutNotices();
                return OperationResult<IUiSurface>.Success(surface);
            }
            catch (ObjectDisposedException)
            {
                surface.Dispose();
                return OperationResult<IUiSurface>.Failure(
                    ModErrorCode.Cancelled,
                    "The fake mod stopped before the UI surface could be created.");
            }
        }

        /// <inheritdoc/>
        public OperationResult<IUiModal> ShowModal(UiModalRequest request, Action<bool> completed)
        {
            gameThread.AssertCurrent();
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
                    "The fake mod is stopping and cannot show a modal.");
            }

            var modal = new FakeUiModal(request, completed, value => modals.Remove(value));
            modals.Add(modal);
            try
            {
                modal.AttachLifetimeLease(lifetime.Track(modal));
                return OperationResult<IUiModal>.Success(modal);
            }
            catch (ObjectDisposedException)
            {
                modal.Dispose();
                return OperationResult<IUiModal>.Failure(
                    ModErrorCode.Cancelled,
                    "The fake mod stopped before the modal could be shown.");
            }
        }

        /// <inheritdoc/>
        public OperationResult<bool> ShowToast(string message, UiTone tone = UiTone.Neutral)
        {
            gameThread.AssertCurrent();
            if (string.IsNullOrWhiteSpace(message))
            {
                throw new ArgumentException("A toast message is required.", nameof(message));
            }

            if (tone < UiTone.Neutral || tone > UiTone.Danger)
            {
                return OperationResult<bool>.Failure(
                    ModErrorCode.InvalidArgument,
                    "The toast tone is not recognized.");
            }

            if (lifetime.IsStopping)
            {
                return OperationResult<bool>.Failure(
                    ModErrorCode.Cancelled,
                    "The fake mod is stopping and cannot show a toast.");
            }

            toasts.Add(new FakeToast(message, tone));
            return OperationResult<bool>.Success(true);
        }

        /// <summary>Makes gameplay HUDs follow these fake scenes: shown only while they are in gameplay.</summary>
        internal void FollowScenes(FakeSceneService fakeScenes)
        {
            if (fakeScenes == null) throw new ArgumentNullException(nameof(fakeScenes));
            if (scenes != null) scenes.GameplayVisibilityChanged -= RefreshHudLayoutNotices;
            scenes = fakeScenes;
            scenes.GameplayVisibilityChanged += RefreshHudLayoutNotices;
        }

        private void RemoveSurface(FakeUiSurface surface)
        {
            surfaces.Remove(surface);
            themeScope.Package.RemoveSurface(themeScope, surface);
            if (surface.Request.Kind == UiSurfaceKind.Hud) RefreshHudLayoutNotices();
        }

        private readonly struct ContentFailure
        {
            public ContentFailure(ModErrorCode errorCode, string message)
            {
                ErrorCode = errorCode;
                Message = message;
            }

            public ModErrorCode ErrorCode { get; }
            public string Message { get; }
        }
    }
}
