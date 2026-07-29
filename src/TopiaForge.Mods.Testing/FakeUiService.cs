using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Captures UI surfaces, modals, and toasts without rendering.</summary>
    public sealed class FakeUiService : IUiService
    {
        private readonly FakeModLifetime lifetime;
        private readonly List<FakeUiSurface> surfaces = new List<FakeUiSurface>();
        private readonly List<FakeUiModal> modals = new List<FakeUiModal>();
        private readonly List<FakeToast> toasts = new List<FakeToast>();
        private readonly Dictionary<string, ContentFailure> pendingContentFailures =
            new Dictionary<string, ContentFailure>(StringComparer.Ordinal);
        private UiAccessibilityPreferences accessibility = UiAccessibilityPreferences.Default;

        /// <summary>Creates a fake UI service.</summary>
        public FakeUiService(FakeModLifetime lifetime)
        {
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        }

        /// <summary>Gets active fake UI surfaces.</summary>
        public IReadOnlyList<FakeUiSurface> Surfaces => surfaces.AsReadOnly();

        /// <summary>Gets open fake UI modals.</summary>
        public IReadOnlyList<FakeUiModal> Modals => modals.AsReadOnly();

        /// <summary>Gets every captured toast in display order.</summary>
        public IReadOnlyList<FakeToast> Toasts => toasts.AsReadOnly();

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
            if (preferences == null) throw new ArgumentNullException(nameof(preferences));
            if (lifetime.IsStopping)
            {
                return OperationResult<UiAccessibilityPreferences>.Failure(
                    ModErrorCode.Cancelled,
                    "The fake mod is stopping and cannot change UI accessibility preferences.");
            }

            accessibility = preferences;
            return OperationResult<UiAccessibilityPreferences>.Success(accessibility);
        }

        /// <inheritdoc/>
        public OperationResult<IUiSurface> CreateSurface(UiSurfaceRequest request)
        {
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

            var surface = new FakeUiSurface(request, value => surfaces.Remove(value));
            if (pendingContentFailures.TryGetValue(request.Id, out var contentFailure))
            {
                pendingContentFailures.Remove(request.Id);
                surface.FailNextContentUpdate(contentFailure.ErrorCode, contentFailure.Message);
            }

            surfaces.Add(surface);
            try
            {
                surface.AttachLifetimeLease(lifetime.Track(surface));
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
            if (string.IsNullOrWhiteSpace(message))
            {
                throw new ArgumentException("A toast message is required.", nameof(message));
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
                    "The fake mod is stopping and cannot show a toast.");
            }

            toasts.Add(new FakeToast(message, tone));
            return OperationResult<bool>.Success(true);
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
