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

    /// <summary>Where a HUD surface docks on the screen.</summary>
    public enum UiHudAnchor
    {
        /// <summary>
        /// TopiaForge's choice, clear of the game's own HUD: the bottom-left corner for a gameplay HUD, and the middle of the
        /// left edge for a HUD shown everywhere, which also stays clear of the main menu's buttons.
        /// </summary>
        Default = 0,

        /// <summary>The top-left corner. In a world, the game shows its microphone label there.</summary>
        TopLeft = 1,

        /// <summary>The middle of the top edge. Development builds of the game show their version there.</summary>
        Top = 2,

        /// <summary>The top-right corner. The game shows a level's objective there, above every mod's UI.</summary>
        TopRight = 3,

        /// <summary>The middle of the left edge.</summary>
        Left = 4,

        /// <summary>The middle of the right edge.</summary>
        Right = 5,

        /// <summary>The bottom-left corner.</summary>
        BottomLeft = 6,

        /// <summary>The middle of the bottom edge. The game shows dialogue subtitles there.</summary>
        Bottom = 7,

        /// <summary>The bottom-right corner. Toasts stack there too, above every HUD.</summary>
        BottomRight = 8
    }

    /// <summary>When a HUD surface shows.</summary>
    public enum UiHudVisibility
    {
        /// <summary>
        /// In gameplay scenes only. The HUD hides by itself at the main menu and while a scene loads, and shows again when
        /// gameplay resumes, so a HUD made once in <c>OnLoad</c> needs no scene handling.
        /// </summary>
        Gameplay = 0,

        /// <summary>Everywhere, the main menu and scene loads included.</summary>
        Always = 1
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

        private UiSurfaceRequest(UiSurfaceRequest source, UiHudAnchor anchor, UiHudVisibility visibility)
        {
            Id = source.Id;
            Title = source.Title;
            Body = source.Body;
            Kind = source.Kind;
            Width = source.Width;
            Height = source.Height;
            Content = source.Content;
            HudAnchor = anchor;
            HudVisibility = visibility;
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

        /// <summary>
        /// Gets where a HUD surface docks: <see cref="UiHudAnchor.Default"/> unless set with <see cref="WithHudAnchor"/>.
        /// HUDs docked at the same place stack, a small gap apart, so they never overlap.
        /// </summary>
        public UiHudAnchor HudAnchor { get; }

        /// <summary>
        /// Gets when a HUD surface shows: <see cref="UiHudVisibility.Gameplay"/> unless set with
        /// <see cref="WithHudVisibility"/>.
        /// </summary>
        public UiHudVisibility HudVisibility { get; }

        /// <summary>Returns a copy of this HUD request that docks at <paramref name="anchor"/>.</summary>
        /// <exception cref="InvalidOperationException">The request is for a window or a fullscreen tool.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="anchor"/> is not a defined anchor.</exception>
        public UiSurfaceRequest WithHudAnchor(UiHudAnchor anchor)
        {
            RequireHud(nameof(WithHudAnchor), "to dock it");
            if (!Enum.IsDefined(typeof(UiHudAnchor), anchor))
            {
                throw new ArgumentOutOfRangeException(nameof(anchor));
            }

            return new UiSurfaceRequest(this, anchor, HudVisibility);
        }

        /// <summary>
        /// Returns a copy of this HUD request that shows as <paramref name="visibility"/> says, such as
        /// <see cref="UiHudVisibility.Always"/> for a HUD that also shows at the main menu.
        /// </summary>
        /// <exception cref="InvalidOperationException">The request is for a window or a fullscreen tool.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="visibility"/> is not a defined value.</exception>
        public UiSurfaceRequest WithHudVisibility(UiHudVisibility visibility)
        {
            RequireHud(nameof(WithHudVisibility), "to choose when it shows");
            if (!Enum.IsDefined(typeof(UiHudVisibility), visibility))
            {
                throw new ArgumentOutOfRangeException(nameof(visibility));
            }

            return new UiSurfaceRequest(this, HudAnchor, visibility);
        }

        private void RequireHud(string method, string purpose)
        {
            if (Kind != UiSurfaceKind.Hud)
            {
                throw new InvalidOperationException(
                    method + " applies to HUD surfaces only. A window opens centred and keeps where the player moves it, "
                    + "and a window or fullscreen tool shows exactly while the mod shows it; create the surface with "
                    + "UiSurfaceKind.Hud " + purpose + ".");
            }
        }
    }

    /// <summary>
    /// Represents a UI surface that lives as long as the mod, or the scope, that created it: scene loads never end it.
    /// </summary>
    public interface IUiSurface : IDisposable
    {
        /// <summary>Gets the stable surface id.</summary>
        string Id { get; }

        /// <summary>
        /// Gets whether the surface shows on screen now: the mod shows it, TopiaForge is not keeping it hidden (as it keeps a
        /// gameplay HUD hidden at the main menu and during scene loads), and its canvas still exists. A canvas destroyed
        /// outside TopiaForge, by the game or another mod, never counts as visible.
        /// </summary>
        bool IsVisible { get; }

        /// <summary>
        /// Shows the surface. A gameplay HUD shown outside gameplay appears by itself once gameplay resumes.
        /// </summary>
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
        /// Raised once whenever a visible surface becomes hidden, whether through user dismissal,
        /// <see cref="IUiSurface.Hide"/>, or the game or another mod destroying its canvas. It is not raised by disposal,
        /// nor when TopiaForge keeps a gameplay HUD hidden outside gameplay.
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

        /// <summary>
        /// Creates and lifetime-tracks a HUD panel, window or fullscreen tool, shown at once. It lives until this mod, or the
        /// scope that created it, ends or it is disposed: scene loads never end it. A HUD can be created once in
        /// <c>OnLoad</c>; by default it shows only in gameplay scenes (see <see cref="UiHudVisibility"/>).
        /// </summary>
        OperationResult<IUiSurface> CreateSurface(UiSurfaceRequest request);

        /// <summary>
        /// Shows a lifetime-tracked modal and reports whether the user confirmed it. It stays open across scene loads until
        /// it is answered or closed, or the mod or scope that showed it ends, which counts as cancelling.
        /// </summary>
        OperationResult<IUiModal> ShowModal(UiModalRequest request, Action<bool> completed);

        /// <summary>Shows a short TopiaForgeUi toast.</summary>
        OperationResult<bool> ShowToast(string message, UiTone tone = UiTone.Neutral);
    }
}
