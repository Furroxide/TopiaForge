using System;

namespace TopiaForge.Mods
{
    public partial interface IUiService
    {
        /// <summary>Gets local, owner-scoped world labels. Labels are never broadcast to other players.</summary>
        [RequiresTopiaForge("0.2.0")]
        IUiWorldLabels Labels => UnavailableUiWorldLabels.Instance;
        /// <summary>
        /// Gets unscaled seconds since this UI scope began. A gamemode scope starts a new local session clock;
        /// pause and world slow motion do not stop it. Use this clock for <see cref="UiCountdown"/> deadlines.
        /// </summary>
        [RequiresTopiaForge("0.2.0")]
        double ClockTime => throw new NotSupportedException("This UI host does not provide a session clock. Update its implementation.");
    }

    /// <summary>Creates local labels owned by this mod or gamemode scope, with a bounded pool of 64 labels.</summary>
    [RequiresTopiaForge("0.2.0")]
    public interface IUiWorldLabels
    {
        /// <summary>Creates a label, or returns an actionable failure if the pool is full or the owner is stopping.</summary>
        OperationResult<IUiWorldLabel> Add(UiWorldLabelRequest request);
    }

    /// <summary>An owner-scoped local label. Releasing it returns its view to the pool; stale handles cannot change a reused view.</summary>
    [RequiresTopiaForge("0.2.0")]
    public interface IUiWorldLabel : IDisposable
    {
        /// <summary>Gets whether the label remains owned and its entity, if any, remains alive.</summary>
        bool IsAlive { get; }
        /// <summary>Updates plain-text content with a dirty-checked renderer.</summary>
        void SetText(string text);
        /// <summary>Moves a position label or detaches an entity label to a fixed world position.</summary>
        void SetPosition(Vec3 position);
    }

    /// <summary>Immutable local label placement and visibility. By default geometry hides labels and distance fades them out.</summary>
    [RequiresTopiaForge("0.2.0")]
    public sealed class UiWorldLabelRequest
    {
        private UiWorldLabelRequest(string text, Vec3 position, IEntity? entity, float maximumDistance, bool occlusion, UiTone tone)
        {
            UiWidgetValues.Text(text, nameof(text)); UiWidgetValues.Tone(tone);
            if (text.Length > 32) throw new ArgumentException("A world label holds at most 32 characters.", nameof(text));
            if (!position.IsFinite) throw new ArgumentOutOfRangeException(nameof(position));
            if (float.IsNaN(maximumDistance) || float.IsInfinity(maximumDistance) || maximumDistance < 1 || maximumDistance > 1000)
                throw new ArgumentOutOfRangeException(nameof(maximumDistance), "Label distance must be between 1 and 1000 meters.");
            Text = text; Position = position; Entity = entity; MaximumDistance = maximumDistance; Occlusion = occlusion; Tone = tone;
        }
        /// <summary>Creates a label at a fixed world position; it ends when that world unloads.</summary>
        public static UiWorldLabelRequest At(Vec3 position, string text, float maximumDistance = 30, bool occlusion = true, UiTone tone = UiTone.Neutral) =>
            new UiWorldLabelRequest(text, position, null, maximumDistance, occlusion, tone);
        /// <summary>Creates a label following an entity with a vertical offset; it ends when the entity despawns.</summary>
        public static UiWorldLabelRequest For(IEntity entity, string text, float maximumDistance = 30, bool occlusion = true, UiTone tone = UiTone.Neutral) =>
            new UiWorldLabelRequest(text, Vec3.Zero, entity ?? throw new ArgumentNullException(nameof(entity)), maximumDistance, occlusion, tone);
        /// <summary>Gets the text.</summary>
        public string Text { get; }
        /// <summary>Gets the initial fixed position; ignored while following an entity.</summary>
        public Vec3 Position { get; }
        /// <summary>Gets the followed entity, or null for a fixed position.</summary>
        public IEntity? Entity { get; }
        /// <summary>Gets the distance beyond which the label hides.</summary>
        public float MaximumDistance { get; }
        /// <summary>Gets whether solid geometry hides the label; triggers are ignored.</summary>
        public bool Occlusion { get; }
        /// <summary>Gets the semantic text tone.</summary>
        public UiTone Tone { get; }
    }

    internal sealed class UnavailableUiWorldLabels : IUiWorldLabels
    {
        internal static readonly IUiWorldLabels Instance = new UnavailableUiWorldLabels();
        public OperationResult<IUiWorldLabel> Add(UiWorldLabelRequest request) => OperationResult<IUiWorldLabel>.Failure(
            ModErrorCode.Unavailable, "This UI host does not provide world labels. Update its implementation.");
    }
}
