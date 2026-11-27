using System;

namespace TopiaForge.ModManager.Core.Multiplayer
{
    /// <summary>One replicated transform, stamped with the sender's session time.</summary>
    internal readonly struct NetTransformSample
    {
        public NetTransformSample(long serverTimeMicros, NetVector3 position, NetQuaternion rotation)
            : this(serverTimeMicros, position, rotation, false, default)
        {
        }

        public NetTransformSample(long serverTimeMicros, NetVector3 position, NetQuaternion rotation, NetVector3 velocity)
            : this(serverTimeMicros, position, rotation, true, velocity)
        {
        }

        private NetTransformSample(
            long serverTimeMicros,
            NetVector3 position,
            NetQuaternion rotation,
            bool hasVelocity,
            NetVector3 velocity)
        {
            SnapshotInterpolationBuffer.RequireTime(serverTimeMicros, nameof(serverTimeMicros));
            ServerTimeMicros = serverTimeMicros;
            Position = position;
            Rotation = rotation;
            HasVelocity = hasVelocity;
            Velocity = velocity;
        }

        public long ServerTimeMicros { get; }

        public NetVector3 Position { get; }

        public NetQuaternion Rotation { get; }

        public bool HasVelocity { get; }

        public NetVector3 Velocity { get; }
    }

    internal enum SnapshotSampleResult
    {
        Accepted = 0,

        /// <summary>A sample with the same time is already buffered.</summary>
        Duplicate = 1,

        /// <summary>Older than what rendering has consumed, or than everything in a full buffer.</summary>
        Stale = 2,

        /// <summary>The sender's timeline jumped by more than a second; the buffer restarted from this sample.</summary>
        Reset = 3
    }

    internal enum InterpolationMode
    {
        Empty = 0,

        /// <summary>The render time is before the oldest sample, which is shown as it is.</summary>
        Holding = 1,

        Interpolating = 2,

        /// <summary>Past the newest sample, within the 250 ms extrapolation cap.</summary>
        Extrapolating = 3,

        /// <summary>Past the cap: the position stays where 250 ms of extrapolation left it.</summary>
        ExtrapolationCapped = 4
    }

    internal readonly struct InterpolatedTransform
    {
        public InterpolatedTransform(NetVector3 position, NetQuaternion rotation, InterpolationMode mode, long renderTimeMicros)
        {
            Position = position;
            Rotation = rotation;
            Mode = mode;
            RenderTimeMicros = renderTimeMicros;
        }

        public NetVector3 Position { get; }

        public NetQuaternion Rotation { get; }

        public InterpolationMode Mode { get; }

        /// <summary>The sender's session time being shown.</summary>
        public long RenderTimeMicros { get; }
    }
}
