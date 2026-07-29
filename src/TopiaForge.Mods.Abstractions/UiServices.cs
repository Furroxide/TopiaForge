using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods
{
    /// <summary>Identifies a framework UI surface.</summary>
    public enum UiSurfaceKind
    {
        /// <summary>A quiet gameplay HUD panel.</summary>
        Hud = 0,

        /// <summary>An interactive paper-scheme desktop window.</summary>
        Window = 1,

        /// <summary>An immersive paper-scheme tool that fills the safe screen area.</summary>
        FullscreenTool = 2
    }

    /// <summary>Identifies a semantic UI tone.</summary>
    public enum UiTone
    {
        /// <summary>Neutral informational content.</summary>
        Neutral = 0,

        /// <summary>A successful action.</summary>
        Success = 1,

        /// <summary>A warning that may require attention.</summary>
        Warning = 2,

        /// <summary>A failed or destructive action.</summary>
        Danger = 3
    }

    /// <summary>Describes a simple TopiaForgeUi HUD panel or window.</summary>
    public sealed class UiSurfaceRequest
    {
        /// <summary>Creates a UI surface request.</summary>
        public UiSurfaceRequest(
            string id,
            string title,
            string body,
            UiSurfaceKind kind = UiSurfaceKind.Window,
            float width = 460f,
            float height = 320f,
            UiNode? content = null)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("A stable UI surface id is required.", nameof(id));
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                throw new ArgumentException("A UI surface title is required.", nameof(title));
            }

            if (!Enum.IsDefined(typeof(UiSurfaceKind), kind))
            {
                throw new ArgumentOutOfRangeException(nameof(kind));
            }

            if (width <= 0f || height <= 0f || float.IsNaN(width) || float.IsNaN(height))
            {
                throw new ArgumentOutOfRangeException(nameof(width));
            }

            Id = id;
            Title = title;
            Body = body ?? string.Empty;
            Kind = kind;
            Width = width;
            Height = height;
            UiComposition.Validate(content);
            if (kind == UiSurfaceKind.Hud && UiComposition.ContainsInteractive(content))
            {
                throw new ArgumentException(
                    "HUD surfaces are presentation-only; place interactive controls in a window or modal.",
                    nameof(content));
            }

            Content = content;
        }

        /// <summary>Gets the stable id unique inside the current mod.</summary>
        public string Id { get; }

        /// <summary>Gets the surface title.</summary>
        public string Title { get; }

        /// <summary>Gets the initial body text.</summary>
        public string Body { get; }

        /// <summary>Gets the surface kind.</summary>
        public UiSurfaceKind Kind { get; }

        /// <summary>Gets the requested width in scaled UI units.</summary>
        public float Width { get; }

        /// <summary>Gets the requested height in scaled UI units.</summary>
        public float Height { get; }

        /// <summary>Gets optional immutable interactive composition rendered below the dirty-checked body text.</summary>
        public UiNode? Content { get; }
    }

    /// <summary>Represents a lifetime-owned UI surface.</summary>
    public interface IUiSurface : IDisposable
    {
        /// <summary>Gets the stable surface id.</summary>
        string Id { get; }

        /// <summary>Gets whether the surface is currently visible.</summary>
        bool IsVisible { get; }

        /// <summary>Shows the surface.</summary>
        void Show();

        /// <summary>Hides the surface without releasing it.</summary>
        void Hide();

        /// <summary>Updates body text using the UI kit's dirty-checked setter.</summary>
        void SetBody(string body);

        /// <summary>Atomically replaces the immutable interactive composition below the body text.</summary>
        OperationResult<bool> SetContent(UiNode content);
    }

    /// <summary>
    /// Optional capability implemented by UI surfaces that report when a visible surface is dismissed.
    /// Consumers should feature-detect this interface so alternate surface implementations remain compatible.
    /// </summary>
    public interface IUiSurfaceDismissalSource
    {
        /// <summary>
        /// Raised once whenever a visible surface becomes hidden, whether through user dismissal or
        /// <see cref="IUiSurface.Hide"/>. It is not raised by disposal.
        /// </summary>
        event Action? Dismissed;
    }

    /// <summary>Describes a confirmation modal.</summary>
    public sealed class UiModalRequest
    {
        /// <summary>Creates a modal request.</summary>
        public UiModalRequest(
            string title,
            string body,
            string confirmLabel = "CONFIRM",
            string cancelLabel = "CANCEL",
            bool destructive = false)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                throw new ArgumentException("A modal title is required.", nameof(title));
            }

            if (string.IsNullOrWhiteSpace(confirmLabel) || string.IsNullOrWhiteSpace(cancelLabel))
            {
                throw new ArgumentException("Modal action labels are required.");
            }

            Title = title;
            Body = body ?? string.Empty;
            ConfirmLabel = confirmLabel;
            CancelLabel = cancelLabel;
            Destructive = destructive;
        }

        /// <summary>Gets the modal title.</summary>
        public string Title { get; }

        /// <summary>Gets the modal body.</summary>
        public string Body { get; }

        /// <summary>Gets the positive action label.</summary>
        public string ConfirmLabel { get; }

        /// <summary>Gets the cancellation label.</summary>
        public string CancelLabel { get; }

        /// <summary>Gets whether the positive action is destructive.</summary>
        public bool Destructive { get; }
    }

    /// <summary>Represents an open confirmation modal.</summary>
    public interface IUiModal : IDisposable
    {
        /// <summary>Gets whether the modal remains open.</summary>
        bool IsOpen { get; }

        /// <summary>Closes the modal as a cancellation.</summary>
        void Close();
    }

    /// <summary>Immutable accessibility preferences applied to one mod's TopiaForgeUi host.</summary>
    public sealed class UiAccessibilityPreferences
    {
        /// <summary>Creates UI accessibility preferences.</summary>
        /// <param name="highContrast">Whether the host uses its high-contrast semantic palette.</param>
        /// <param name="uiScale">Host-relative UI scale in the inclusive 0.75-to-1.5 range.</param>
        /// <param name="reducedMotion">Whether transitions, pulses, and punches resolve immediately.</param>
        /// <param name="motionIntensity">Host-relative motion intensity in the inclusive zero-to-two range.</param>
        public UiAccessibilityPreferences(
            bool highContrast = false,
            float uiScale = 1f,
            bool reducedMotion = false,
            float motionIntensity = 1f)
        {
            if (uiScale < 0.75f || uiScale > 1.5f || float.IsNaN(uiScale) || float.IsInfinity(uiScale))
            {
                throw new ArgumentOutOfRangeException(nameof(uiScale));
            }

            if (motionIntensity < 0f || motionIntensity > 2f
                || float.IsNaN(motionIntensity) || float.IsInfinity(motionIntensity))
            {
                throw new ArgumentOutOfRangeException(nameof(motionIntensity));
            }

            HighContrast = highContrast;
            UiScale = uiScale;
            ReducedMotion = reducedMotion;
            MotionIntensity = motionIntensity;
        }

        /// <summary>Gets the default host preferences.</summary>
        public static UiAccessibilityPreferences Default { get; } = new UiAccessibilityPreferences();

        /// <summary>Gets whether the host uses its high-contrast semantic palette.</summary>
        public bool HighContrast { get; }

        /// <summary>Gets the host-relative UI scale.</summary>
        public float UiScale { get; }

        /// <summary>Gets whether nonessential motion is disabled.</summary>
        public bool ReducedMotion { get; }

        /// <summary>Gets the host-relative motion intensity.</summary>
        public float MotionIntensity { get; }
    }

    /// <summary>Creates owner-scoped TopiaForgeUi surfaces, modals, and toasts.</summary>
    public interface IUiService
    {
        /// <summary>Gets the current accessibility preferences for this mod's UI host.</summary>
        UiAccessibilityPreferences Accessibility { get; }

        /// <summary>Applies accessibility preferences to existing and future UI owned by this mod.</summary>
        OperationResult<UiAccessibilityPreferences> ApplyAccessibility(UiAccessibilityPreferences preferences);

        /// <summary>Creates and lifetime-tracks a HUD panel or window.</summary>
        OperationResult<IUiSurface> CreateSurface(UiSurfaceRequest request);

        /// <summary>Shows a lifetime-tracked modal and reports whether the user confirmed it.</summary>
        OperationResult<IUiModal> ShowModal(UiModalRequest request, Action<bool> completed);

        /// <summary>Shows a short TopiaForgeUi toast.</summary>
        OperationResult<bool> ShowToast(string message, UiTone tone = UiTone.Neutral);
    }
}
