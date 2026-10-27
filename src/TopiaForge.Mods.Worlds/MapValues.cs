using System;
using System.Linq;

namespace TopiaForge.Mods
{
    /// <summary>The content identity of the exact imported project bytes.</summary>
    [RequiresTopiaForge("0.2.0")]
    public sealed class MapRevision
    {
        /// <summary>Creates a bounded SHA-256 identity with the packed size and authored modified time.</summary>
        public MapRevision(string sha256, long size, string modifiedTime)
        {
            if (sha256 == null || sha256.Length != 64 || !sha256.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))
                throw new ArgumentException("A lowercase SHA-256 digest is required.", nameof(sha256));
            if (size <= 0 || size > 8 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(size));
            if (modifiedTime == null || modifiedTime.Length > 128 || modifiedTime.Any(char.IsControl))
                throw new ArgumentException("The modified time must be bounded plain text.", nameof(modifiedTime));
            Sha256 = sha256; Size = size; ModifiedTime = modifiedTime;
        }
        /// <summary>Gets the lowercase SHA-256 digest of the packed project.</summary>
        public string Sha256 { get; }
        /// <summary>Gets the packed project size in bytes.</summary>
        public long Size { get; }
        /// <summary>Gets the authored modified time, or an empty value when absent.</summary>
        public string ModifiedTime { get; }
    }

    /// <summary>An editor transform preserving whether rotation was Euler degrees or a quaternion.</summary>
    [RequiresTopiaForge("0.2.0")]
    public readonly struct MapTransform
    {
        /// <summary>Creates a finite editor transform using Euler degrees.</summary>
        public MapTransform(Vec3 position, Vec3 eulerDegrees, Vec3 scale)
        {
            if (!position.IsFinite || !eulerDegrees.IsFinite || !scale.IsFinite)
                throw new ArgumentException("Editor transforms must be finite.");
            Position = position; EulerDegrees = eulerDegrees; QuaternionRotation = null; Scale = scale;
        }
        /// <summary>Creates a finite editor transform preserving the authored quaternion.</summary>
        public MapTransform(Vec3 position, Quat rotation, Vec3 scale)
        {
            if (!position.IsFinite || !rotation.IsFinite || rotation.LengthSquared <= 0.000001f || !scale.IsFinite)
                throw new ArgumentException("Editor transforms need finite values and a nonzero quaternion.");
            Position = position; EulerDegrees = null; QuaternionRotation = rotation; Scale = scale;
        }
        /// <summary>Gets the position in editor axes.</summary>
        public Vec3 Position { get; }
        /// <summary>Gets Euler degrees, or null when a quaternion was authored.</summary>
        public Vec3? EulerDegrees { get; }
        /// <summary>Gets the authored quaternion, or null for Euler degrees.</summary>
        public Quat? QuaternionRotation { get; }
        /// <summary>Gets the scale exactly as authored.</summary>
        public Vec3 Scale { get; }
        /// <summary>Gets the identity editor transform.</summary>
        public static MapTransform Identity => new MapTransform(Vec3.Zero, Vec3.Zero, new Vec3(1, 1, 1));
    }

    /// <summary>Components described by the map editor, independent of native prefab expansion.</summary>
    [Flags, RequiresTopiaForge("0.2.0")]
    public enum AuthoredComponents
    {
        /// <summary>No recognized component.</summary>
        None = 0,
        /// <summary>An authored model renderer.</summary>
        ModelRenderer = 1,
        /// <summary>An authored prefab instance.</summary>
        PrefabInstance = 2,
        /// <summary>A player spawn marker.</summary>
        SpawnLocation = 4,
        /// <summary>A point of interest.</summary>
        Poi = 8,
        /// <summary>An area of interest.</summary>
        Aoi = 16,
        /// <summary>An authored agent.</summary>
        Agent = 32
    }
}
