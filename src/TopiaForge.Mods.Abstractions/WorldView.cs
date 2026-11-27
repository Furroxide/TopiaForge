using System;

namespace TopiaForge.Mods
{
    /// <summary>The fog's tone; the provider chooses its colour.</summary>
    [RequiresTopiaForge("0.2.0")]
    public enum FogTint
    {
        /// <summary>Dark, neutral fog.</summary>
        [RequiresTopiaForge("0.2.0")] Dark,
        /// <summary>Pale atmospheric haze.</summary>
        [RequiresTopiaForge("0.2.0")] Haze,
        /// <summary>Neutral grey smoke.</summary>
        [RequiresTopiaForge("0.2.0")] Smoke
    }

    /// <summary>A finite visibility limit between one and 500 metres.</summary>
    [RequiresTopiaForge("0.2.0")]
    public readonly struct VisionFog : IEquatable<VisionFog>
    {
        private VisionFog(float distance, FogTint tint) { VisibleDistance = distance; Tint = tint; }
        /// <summary>Creates a visibility limit; rejects non-finite distances and undefined tones.</summary>
        [RequiresTopiaForge("0.2.0")]
        public static VisionFog Radius(float metres, FogTint tint = FogTint.Dark)
        {
            if (float.IsNaN(metres) || float.IsInfinity(metres) || metres < 1 || metres > 500)
                throw new ArgumentOutOfRangeException(nameof(metres));
            if (tint < FogTint.Dark || tint > FogTint.Smoke) throw new ArgumentOutOfRangeException(nameof(tint));
            return new VisionFog(metres, tint);
        }
        /// <summary>Gets dark fog with a six-metre visibility limit.</summary>
        [RequiresTopiaForge("0.2.0")]
        public static VisionFog Blackout => Radius(6);
        /// <summary>Gets the visibility limit in metres.</summary>
        [RequiresTopiaForge("0.2.0")]
        public float VisibleDistance { get; }
        /// <summary>Gets the requested tone.</summary>
        [RequiresTopiaForge("0.2.0")]
        public FogTint Tint { get; }
        /// <inheritdoc/>
        [RequiresTopiaForge("0.2.0")]
        public bool Equals(VisionFog other) => VisibleDistance.Equals(other.VisibleDistance) && Tint == other.Tint;
        /// <inheritdoc/>
        [RequiresTopiaForge("0.2.0")]
        public override bool Equals(object? obj) => obj is VisionFog other && Equals(other);
        /// <inheritdoc/>
        [RequiresTopiaForge("0.2.0")]
        public override int GetHashCode() => (VisibleDistance.GetHashCode() * 397) ^ (int)Tint;
    }

    /// <summary>A view change; disposal restores the view derived from the remaining leases.</summary>
    [RequiresTopiaForge("0.2.0")]
    public interface IViewLease : IGameplayLease { }

    /// <summary>Changes the addressed view. Participant code is refused, including in single player.</summary>
    [RequiresTopiaForge("0.2.0")]
    public interface IViewControls
    {
        /// <summary>Holds an environment preset; the most recent live lease wins.</summary>
        [RequiresTopiaForge("0.2.0")]
        OperationResult<IViewLease> Environment(string preset);
        /// <summary>Limits visibility; the shortest live distance wins, then Dark, Haze, Smoke on ties.</summary>
        [RequiresTopiaForge("0.2.0")]
        OperationResult<IViewLease> Fog(VisionFog fog);
        /// <summary>Hides this entity's own renderers while any hide lease remains; collisions are unchanged.</summary>
        [RequiresTopiaForge("0.2.0")]
        OperationResult<IViewLease> Hide(IEntity entity);
    }
}
