using System;

namespace TopiaForge.Mods
{
    /// <summary>Describes a timed, click-through announcement in the centre of the screen.</summary>
    [RequiresTopiaForge(UiKitRelease.K2)]
    public sealed class UiStingerRequest
    {
        /// <summary>Creates an announcement with a title, optional detail and semantic styling.</summary>
        /// <param name="title">The 1 to 40 character plain-text title in the Banner role.</param>
        /// <param name="subtitle">Optional plain-text detail in the Title role, up to 80 characters.</param>
        /// <param name="glyph">An optional semantic glyph preceding the title.</param>
        /// <param name="role">The semantic fill and foreground pair.</param>
        /// <param name="style">An optional named stinger style from the effective theme.</param>
        /// <param name="durationSeconds">How long the announcement is shown, from 1 to 6 seconds.</param>
        /// <remarks>The UI host checks the rendered two-line limit for each text field when it admits the request.</remarks>
        /// <exception cref="ArgumentNullException">A text field is null.</exception>
        /// <exception cref="ArgumentException">A text field exceeds its character bounds or the title is blank.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The glyph, role or duration is outside its defined range.</exception>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiStingerRequest(string title, string subtitle = "", UiGlyph? glyph = null,
            UiPaintRole role = UiPaintRole.Primary, UiStyleRef? style = null, double durationSeconds = 2.5)
        {
            if (title == null) throw new ArgumentNullException(nameof(title));
            if (string.IsNullOrWhiteSpace(title) || title.Length > 40)
                throw new ArgumentException("A stinger title must contain 1 to 40 characters.", nameof(title));
            if (subtitle == null) throw new ArgumentNullException(nameof(subtitle));
            if (subtitle.Length > 80)
                throw new ArgumentException("A stinger subtitle may contain up to 80 characters.", nameof(subtitle));
            if (glyph is UiGlyph icon && (uint)icon > (uint)UiGlyph.Play)
                throw new ArgumentOutOfRangeException(nameof(glyph));
            UiArtValues.PaintRole(role, nameof(role));
            if (!double.IsFinite(durationSeconds) || durationSeconds < 1d || durationSeconds > 6d)
                throw new ArgumentOutOfRangeException(nameof(durationSeconds), "A stinger must show for 1 to 6 seconds.");

            Title = title;
            Subtitle = subtitle;
            Glyph = glyph;
            Role = role;
            Style = style;
            DurationSeconds = durationSeconds;
        }

        /// <summary>Gets the plain-text title in the Banner role.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public string Title { get; }

        /// <summary>Gets the optional plain-text detail in the Title role.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public string Subtitle { get; }

        /// <summary>Gets the glyph preceding the title, when supplied.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiGlyph? Glyph { get; }

        /// <summary>Gets the semantic fill and foreground pair.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiPaintRole Role { get; }

        /// <summary>Gets the optional named style.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiStyleRef? Style { get; }

        /// <summary>Gets the duration in seconds after the announcement becomes visible.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public double DurationSeconds { get; }
    }

    public partial interface IUiService
    {
        /// <summary>Shows a centre announcement here and, on a gamemode server, to its compatible guests.</summary>
        /// <param name="request">The immutable announcement request.</param>
        /// <returns>Success when admitted or coalesced, or an error describing why it could not be shown.</returns>
        [RequiresTopiaForge(UiKitRelease.K2)]
        OperationResult<bool> ShowStinger(UiStingerRequest request) => UnavailableUiStinger.Result;
    }

    internal static class UnavailableUiStinger
    {
        internal static readonly OperationResult<bool> Result = OperationResult<bool>.Failure(ModErrorCode.Unavailable,
            "This UI host does not provide stingers. Update its implementation.");
    }
}
