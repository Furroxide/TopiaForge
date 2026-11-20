using System;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods
{
    public partial interface IUiService
    {
        /// <summary>Gets this scope's theme selection capability.</summary>
        /// <remarks>Hosts without this capability return Unavailable from its operations.</remarks>
        [RequiresTopiaForge(UiKitRelease.K1)]
        IUiThemeService Themes => UnavailableUiThemes.Instance;
    }

    /// <summary>Selects and preloads themes declared by the current mod's package.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public interface IUiThemeService
    {
        /// <summary>Gets this scope's explicit selection, or null if the scope inherits its theme.</summary>
        UiThemeRef? SelectedTheme { get; }

        /// <summary>Selects a declared theme throughout this scope or resumes inheritance with null.</summary>
        /// <remarks>
        /// A surface override and a descendant scope's explicit selection take precedence.
        /// Registration starts on demand. Surfaces keep their content and use kit defaults until the pack is ready.
        /// </remarks>
        OperationResult<bool> UseTheme(UiThemeRef? theme);

        /// <summary>Loads a declared theme and retains its resources until the calling scope ends.</summary>
        /// <remarks>Cancelling a call does not cancel other scopes' uses of the same theme.</remarks>
        Task<OperationResult<bool>> PreloadAsync(UiThemeRef theme, CancellationToken cancellationToken = default);
    }

    /// <summary>Identifies a declared theme in the calling mod's own package.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public sealed class UiThemeRef : IEquatable<UiThemeRef>
    {
        /// <summary>Creates a reference from the exact id in the package manifest.</summary>
        /// <exception cref="ArgumentException">The id is invalid or reserved by TopiaForge.</exception>
        public UiThemeRef(string id)
        {
            if (!UiThemeIds.IsAuthorId(id))
                throw new ArgumentException("A theme id must be 1 to 64 lowercase ASCII letters, digits, dots, underscores or dashes, and start with a letter or digit.", nameof(id));
            Id = id;
        }

        /// <summary>Gets the ordinal, package-local theme id.</summary>
        public string Id { get; }

        /// <inheritdoc/>
        public bool Equals(UiThemeRef? other) => other != null && StringComparer.Ordinal.Equals(Id, other.Id);

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is UiThemeRef other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Id);
    }

    public sealed partial class UiSurfaceRequest
    {
        /// <summary>Gets the override for this surface, or null to inherit the scope's theme.</summary>
        [RequiresTopiaForge(UiKitRelease.K1)]
        public UiThemeRef? Theme { get; }

        /// <summary>Returns a copy of this request with its own declared theme, or with null to inherit.</summary>
        [RequiresTopiaForge(UiKitRelease.K1)]
        public UiSurfaceRequest WithTheme(UiThemeRef? theme) => new UiSurfaceRequest(this, HudAnchor, HudVisibility, theme);
    }

    /// <summary>Identifies a semantic fill paired with a readable foreground.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public enum UiPaintRole
    {
        /// <summary>The base surface fill.</summary>
        Surface = 0,
        /// <summary>The fill of raised content.</summary>
        Raised = 1,
        /// <summary>The fill of inset content.</summary>
        Sunken = 2,
        /// <summary>The fill of selected content.</summary>
        Selected = 3,
        /// <summary>The fill of a primary action.</summary>
        Primary = 4,
        /// <summary>The fill of an accent.</summary>
        Accent = 5,
        /// <summary>The fill of a success status.</summary>
        Success = 6,
        /// <summary>The fill of a warning status.</summary>
        Warning = 7,
        /// <summary>The fill of a danger status.</summary>
        Danger = 8
    }

    internal static class UiThemeIds
    {
        internal static bool IsAuthorId(string? value)
        {
            if (string.IsNullOrEmpty(value) || value!.Length > 64 ||
                value.StartsWith("topiaforge.", StringComparison.Ordinal) || !IsLetterOrDigit(value[0]))
                return false;

            for (var index = 1; index < value.Length; index++)
            {
                var character = value[index];
                if (!IsLetterOrDigit(character) && character != '.' && character != '_' && character != '-')
                    return false;
            }

            return true;
        }

        private static bool IsLetterOrDigit(char value) =>
            (value >= 'a' && value <= 'z') || (value >= '0' && value <= '9');
    }

    internal sealed class UnavailableUiThemes : IUiThemeService
    {
        private static readonly OperationResult<bool> Failure = OperationResult<bool>.Failure(
            ModErrorCode.Unavailable, "This UI host does not support theme packs.");
        private static readonly Task<OperationResult<bool>> CompletedFailure = Task.FromResult(Failure);
        internal static readonly IUiThemeService Instance = new UnavailableUiThemes();

        private UnavailableUiThemes() { }

        public UiThemeRef? SelectedTheme => null;

        public OperationResult<bool> UseTheme(UiThemeRef? theme) => Failure;

        public Task<OperationResult<bool>> PreloadAsync(UiThemeRef theme, CancellationToken cancellationToken) =>
            CompletedFailure;
    }
}
