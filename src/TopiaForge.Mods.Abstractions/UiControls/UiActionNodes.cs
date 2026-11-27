using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace TopiaForge.Mods
{
    /// <summary>Draws the viewer's current binding for an action registered by the owning package.</summary>
    /// <remarks>
    /// The binding uses the last device the viewer used, then the first binding of the other device.
    /// An action without bindings shows an informational glyph and the text <c>Unbound</c>.
    /// The host validates the action name at admission. This node is presentation-only and can appear in a HUD.
    /// </remarks>
    [RequiresTopiaForge(UiKitRelease.K2)]
    public sealed class UiKeyGlyph : UiNode
    {
        private readonly ReadOnlyCollection<UiInputBindingHint>? wireDefaults;

        /// <summary>Creates a glyph that tracks a named local input action.</summary>
        /// <param name="actionName">The action's ordinal registration name, from 1 to 64 characters.</param>
        /// <param name="id">The optional stable presentation id.</param>
        /// <exception cref="ArgumentNullException"><paramref name="actionName"/> is null.</exception>
        /// <exception cref="ArgumentException">The name or supplied id is blank or exceeds its allowed length.</exception>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiKeyGlyph(string actionName, string? id = null) : base(id)
        {
            UiControlText.Require(actionName, 64, nameof(actionName));
            ActionName = actionName;
        }

        internal UiKeyGlyph(string actionName, string? id, ReadOnlyCollection<UiInputBindingHint> defaults)
            : this(actionName, id)
        {
            wireDefaults = defaults;
        }

        internal IReadOnlyList<UiInputBindingHint> WireDefaults =>
            (IReadOnlyList<UiInputBindingHint>?)wireDefaults ?? Array.Empty<UiInputBindingHint>();
        internal bool HasWireDefaults => wireDefaults != null;

        internal UiKeyGlyph WithWireDefaults(ReadOnlyCollection<UiInputBindingHint> defaults) =>
            new UiKeyGlyph(ActionName, Id, defaults);

        /// <summary>Gets the ordinal action name registered by the package that owns the surface.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public string ActionName { get; }
    }

    /// <summary>Shows an action's key, label, optional art and current status in a presentation-only slot.</summary>
    /// <remarks>
    /// A slot uses 128 by 112 reference units at the viewer's readable scale and can appear in a HUD.
    /// Admission checks the owning package's action and assets and whether the label fits two lines.
    /// The status line takes the unavailable reason, running cooldown, charges, or detail, in that order.
    /// </remarks>
    [RequiresTopiaForge(UiKitRelease.K2)]
    public sealed class UiActionSlot : UiNode
    {
        private readonly ReadOnlyCollection<UiInputBindingHint>? wireDefaults;

        /// <summary>Creates a read-only action slot whose binding and deadlines follow the viewing process.</summary>
        /// <param name="actionName">The action's ordinal registration name, from 1 to 64 characters.</param>
        /// <param name="label">The visible plain-text label, from 1 to 24 characters.</param>
        /// <param name="glyph">An optional kit glyph.</param>
        /// <param name="image">An optional verified image from the package that owns the surface. A mask uses the content color.</param>
        /// <param name="detail">Optional plain text of at most 48 characters shown when no higher-priority status applies.</param>
        /// <param name="emphasis">Whether to give the slot the selected treatment when available.</param>
        /// <param name="cooldown">An optional deadline for the slot's cooldown sweep and remaining seconds.</param>
        /// <param name="charges">An optional count and deadline for refilling a charge.</param>
        /// <param name="unavailableReason">Optional plain text of at most 48 characters shown with a lock while unavailable.</param>
        /// <param name="style">An optional named style declared for the action slot component.</param>
        /// <param name="id">The optional stable presentation id.</param>
        /// <remarks>Empty optional text is omitted. Supply either a glyph or an image when the slot needs art.</remarks>
        /// <exception cref="ArgumentNullException"><paramref name="actionName"/> or <paramref name="label"/> is null.</exception>
        /// <exception cref="ArgumentException">Required text or the supplied id is invalid, optional text is too long, or both kinds of art are supplied.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="glyph"/> is outside the defined glyphs.</exception>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiActionSlot(string actionName, string label, UiGlyph? glyph = null, UiImageRef? image = null,
            string? detail = null, bool emphasis = false, UiCooldown? cooldown = null, UiCharges? charges = null,
            string? unavailableReason = null, UiStyleRef? style = null, string? id = null) : base(id)
        {
            UiControlText.Require(actionName, 64, nameof(actionName));
            UiControlText.Require(label, 24, nameof(label));
            if (glyph.HasValue && image != null)
                throw new ArgumentException("An action slot accepts either a glyph or an image.", nameof(image));
            if (glyph is UiGlyph symbol) UiGlyphLabels.Validate(symbol, nameof(glyph));

            ActionName = actionName;
            Label = label;
            Glyph = glyph;
            Image = image;
            Detail = OptionalStatus(detail, nameof(detail));
            Emphasis = emphasis;
            Cooldown = cooldown;
            Charges = charges;
            UnavailableReason = OptionalStatus(unavailableReason, nameof(unavailableReason));
            Style = style;
        }

        private UiActionSlot(UiActionSlot source, ReadOnlyCollection<UiInputBindingHint>? defaults,
            ReadOnlyMemory<byte> packDescriptorDigest)
            : this(source.ActionName, source.Label, source.Glyph, source.Image, source.Detail, source.Emphasis,
                source.Cooldown, source.Charges, source.UnavailableReason, source.Style, source.Id)
        {
            wireDefaults = defaults;
            PackDescriptorDigest = packDescriptorDigest;
        }

        internal IReadOnlyList<UiInputBindingHint> WireDefaults =>
            (IReadOnlyList<UiInputBindingHint>?)wireDefaults ?? Array.Empty<UiInputBindingHint>();
        internal bool HasWireDefaults => wireDefaults != null;
        internal ReadOnlyMemory<byte> PackDescriptorDigest { get; }

        internal UiActionSlot WithWireDefaults(ReadOnlyCollection<UiInputBindingHint> defaults) =>
            new UiActionSlot(this, defaults, PackDescriptorDigest);

        internal UiActionSlot WithWireImageDigest(ReadOnlySpan<byte> digest) =>
            new UiActionSlot(this, wireDefaults, CopyImageDigest(digest));

        internal UiActionSlot WithWireMetadata(ReadOnlyCollection<UiInputBindingHint> defaults, ReadOnlySpan<byte> digest) =>
            new UiActionSlot(this, defaults, Image == null && digest.IsEmpty ? default : CopyImageDigest(digest));

        private ReadOnlyMemory<byte> CopyImageDigest(ReadOnlySpan<byte> digest)
        {
            if (Image == null || digest.Length != 32)
                throw new ArgumentException("A remote action slot image requires its package-local theme descriptor's SHA-256 digest.", nameof(digest));
            return digest.ToArray();
        }

        /// <summary>Gets the ordinal action name registered by the package that owns the surface.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public string ActionName { get; }

        /// <summary>Gets the plain-text label that identifies the action.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public string Label { get; }

        /// <summary>Gets the optional kit glyph.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiGlyph? Glyph { get; }

        /// <summary>Gets the optional package-local image.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiImageRef? Image { get; }

        /// <summary>Gets the optional plain-text detail.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public string? Detail { get; }

        /// <summary>Gets whether the available slot uses the selected treatment.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public bool Emphasis { get; }

        /// <summary>Gets the optional deadline for the slot's cooldown.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiCooldown? Cooldown { get; }

        /// <summary>Gets the optional authored charge count and refill.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiCharges? Charges { get; }

        /// <summary>Gets the reason shown instead of other status when the action is unavailable.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public string? UnavailableReason { get; }

        /// <summary>Gets the optional named style.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiStyleRef? Style { get; }

        private static string? OptionalStatus(string? value, string parameter)
        {
            if (value is { Length: > 48 })
                throw new ArgumentException("Action slot status text can contain at most 48 characters.", parameter);
            return string.IsNullOrEmpty(value) ? null : value;
        }
    }
}
