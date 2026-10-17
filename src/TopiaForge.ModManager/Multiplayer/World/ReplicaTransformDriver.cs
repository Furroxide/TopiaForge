using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Moves a joined player's replicas to where the host has them, slightly in the past. Each replica's host samples go
    /// into its own replication-model <see cref="SnapshotInterpolationBuffer"/>, stamped with the host's time and the
    /// local time the sample was taken in; once a frame, before the mods' LateUpdate, every replica with samples is
    /// written the buffer's pose for the local time now. A replica still being built keeps its samples and moves once it
    /// exists. It allocates nothing in steady state: a replica's buffer is made with its first sample. Main thread only.
    /// </summary>
    internal sealed class ReplicaTransformDriver
    {
        private readonly List<ClientWorldEntity> driven = new List<ClientWorldEntity>();

        /// <summary>Gets how many replicas the driver keeps samples for.</summary>
        internal int Count => driven.Count;

        /// <summary>Gets how many replicas the last frame moved.</summary>
        internal int MovedLastFrame { get; private set; }

        internal void Track(ClientWorldEntity replica)
        {
            if (replica == null) throw new ArgumentNullException(nameof(replica));
            if (replica.DriverIndex >= 0) return;
            replica.DriverIndex = driven.Count;
            driven.Add(replica);
        }

        internal void Untrack(ClientWorldEntity replica)
        {
            if (replica == null) throw new ArgumentNullException(nameof(replica));
            var index = replica.DriverIndex;
            if (index < 0 || index >= driven.Count || !ReferenceEquals(driven[index], replica)) return;
            var last = driven.Count - 1;
            if (index != last)
            {
                driven[index] = driven[last];
                driven[index].DriverIndex = index;
            }

            driven.RemoveAt(last);
            replica.DriverIndex = -1;
            replica.Buffer?.Reset();
        }

        /// <summary>
        /// Offers one host sample, taken at <paramref name="hostTimeMicros"/> on the host's clock and received at
        /// <paramref name="arrivalMicros"/> on this one.
        /// </summary>
        internal SnapshotSampleResult Offer(ClientWorldEntity replica, long hostTimeMicros, in NetPose pose, long arrivalMicros)
        {
            if (replica == null) throw new ArgumentNullException(nameof(replica));
            var buffer = replica.Buffer ??= new SnapshotInterpolationBuffer(NetworkWorldHost.DownstreamIntervalMicros);
            var sample = pose.HasVelocity
                ? new NetTransformSample(hostTimeMicros, pose.Position, pose.Rotation, pose.Velocity)
                : new NetTransformSample(hostTimeMicros, pose.Position, pose.Rotation);
            return buffer.Add(sample, arrivalMicros);
        }

        /// <summary>Writes every replica that has samples and an object the pose for <paramref name="localNowMicros"/>.</summary>
        internal void Drive(long localNowMicros)
        {
            var moved = 0;
            for (var index = 0; index < driven.Count; index++)
            {
                var replica = driven[index];
                var target = replica.Object;
                var buffer = replica.Buffer;
                if (target == null || buffer == null || buffer.Count == 0 || !target.IsAlive) continue;
                if (!buffer.TrySample(localNowMicros, out var rendered)) continue;
                target.WritePose(new NetPose(rendered.Position, rendered.Rotation));
                moved++;
            }

            MovedLastFrame = moved;
        }

        /// <summary>Forgets every replica.</summary>
        internal void Clear()
        {
            foreach (var replica in driven)
            {
                replica.DriverIndex = -1;
                replica.Buffer?.Reset();
            }

            driven.Clear();
            MovedLastFrame = 0;
        }
    }
}
