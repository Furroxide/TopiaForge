using System;

namespace TopiaForge.Mods
{
    /// <summary>Identifies a named style in the active package-local theme.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public sealed class UiStyleRef : IEquatable<UiStyleRef>
    {
        /// <summary>Creates a reference from the exact id declared in the theme.</summary>
        /// <exception cref="ArgumentException">The id is invalid or reserved by TopiaForge.</exception>
        public UiStyleRef(string id)
        {
            if (!UiThemeIds.IsAuthorId(id))
                throw new ArgumentException("A style id must be 1 to 64 lowercase ASCII letters, digits, dots, underscores or dashes, and start with a letter or digit.", nameof(id));
            Id = id;
        }

        /// <summary>Gets the ordinal style id.</summary>
        public string Id { get; }

        /// <inheritdoc/>
        public bool Equals(UiStyleRef? other) => other != null && StringComparer.Ordinal.Equals(Id, other.Id);

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is UiStyleRef other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Id);
    }
}
