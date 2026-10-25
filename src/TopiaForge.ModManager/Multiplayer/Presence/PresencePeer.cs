using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// One participant as presence knows it on this peer: its name and link from the session, the newest pose this process
    /// holds for it (validated on the host, relayed on a joined player, sampled for the local player), its state, its
    /// avatar and the host's checks of it. Main thread only.
    /// </summary>
    internal sealed class PresencePeer
    {
        internal PresencePeer(ParticipantId id, string displayName, bool isLocal)
        {
            Id = id;
            DisplayName = displayName;
            IsLocal = isLocal;
        }

        internal ParticipantId Id { get; }

        internal string DisplayName { get; set; }

        internal bool IsLocal { get; }

        internal bool IsConnected { get; set; } = true;

        /// <summary>Gets or sets the presence index the host gave the participant, zero until one is known.</summary>
        internal byte Index { get; set; }

        /// <summary>Gets or sets whether the last reconciliation with the session found the participant.</summary>
        internal bool Seen { get; set; }

        internal bool HasPose { get; private set; }

        /// <summary>Gets the pose's time on the host's clock, in microseconds.</summary>
        internal long PoseMicros { get; private set; }

        internal NetQuantizedPosition Position { get; private set; }

        internal uint Rotation { get; private set; } = NetQuantization.IdentityRotationBits;

        internal NetQuantizedVelocity Velocity { get; private set; }

        internal byte EyeCentimeters { get; private set; }

        internal PresenceFlags Flags { get; private set; }

        /// <summary>Gets a number that moves whenever the pose changes, so the host relays only what moved.</summary>
        internal int PoseVersion { get; private set; }

        /// <summary>Gets or sets the pose version the host's last relay tick saw.</summary>
        internal int TickVersion { get; set; }

        /// <summary>Gets or sets whether the pose changed in the last relay tick.</summary>
        internal bool MovedThisTick { get; set; }

        internal bool HasState { get; private set; }

        internal float Health { get; private set; }

        internal float MaximumHealth { get; private set; }

        internal string? HeldItem { get; private set; }

        /// <summary>Gets or sets whether the state changed since the host last relayed states.</summary>
        internal bool StateChanged { get; set; }

        /// <summary>Gets or sets the interpolation buffer the avatar plays this participant's relayed poses back from.</summary>
        internal SnapshotInterpolationBuffer? Buffer { get; set; }

        /// <summary>Gets or sets whether the buffer holds a pose, and the feet and host time of the last one fed to it.</summary>
        internal bool HasFed { get; set; }

        internal NetVector3 FedFeet { get; set; }

        internal long FedMicros { get; set; }

        internal RemotePlayerAvatar? Avatar { get; set; }

        /// <summary>Gets or sets when an avatar that could not be made is tried again.</summary>
        internal long NextAvatarMicros { get; set; }

        // The host's own: its checks of this player, how its clock maps onto the host's, and its place in the host's world.
        internal PresencePlausibility? Plausibility { get; set; }

        internal PresenceHealth? HealthCheck { get; set; }

        internal bool HasClockOffset { get; set; }

        internal long ClockOffsetMicros { get; set; }

        /// <summary>Gets or sets the stream generation the host last caught this player up on: roster and states.</summary>
        internal ulong CaughtUpGeneration { get; set; }

        /// <summary>Gets or sets whether a broadcast skipped the player, so it needs the roster and every state again.</summary>
        internal bool NeedsCatchUp { get; set; }

        /// <summary>Gets or sets the world the host last placed the player in, and its slot there.</summary>
        internal string? PlacedWorld { get; set; }

        internal int Slot { get; set; } = -1;

        /// <summary>Gets or sets whether the host owes the player a correction, sent once its window has room.</summary>
        internal bool CorrectionDue { get; set; }

        internal byte CorrectionNumber { get; set; }

        internal NetVector3 CorrectionTarget { get; set; }

        /// <summary>Gets or sets whether the host owes the player its placement, sent once its window has room.</summary>
        internal bool PlacementDue { get; set; }

        internal NetVector3 PlacementFeet { get; set; }

        internal float PlacementYaw { get; set; }

        /// <summary>Gets or sets whether the host removed the player, so nothing it sends counts any more.</summary>
        internal bool Removed { get; set; }

        /// <summary>Gets or sets the scheduler that picks which poses this player receives each tick (host only).</summary>
        internal ReplicationScheduler? Scheduler { get; set; }

        /// <summary>Takes a pose; returns whether it differs from the one held, which moves <see cref="PoseVersion"/>.</summary>
        internal bool SetPose(long micros, NetQuantizedPosition position, uint rotation, NetQuantizedVelocity velocity, byte eyeCentimeters, PresenceFlags flags)
        {
            var changed = !HasPose || !position.Equals(Position) || rotation != Rotation || !velocity.Equals(Velocity) ||
                          eyeCentimeters != EyeCentimeters || flags != Flags;
            HasPose = true;
            PoseMicros = micros;
            Position = position;
            Rotation = rotation;
            Velocity = velocity;
            EyeCentimeters = eyeCentimeters;
            Flags = flags;
            if (changed) PoseVersion++;
            return changed;
        }

        /// <summary>Takes a state; returns whether it differs from the one held.</summary>
        internal bool SetState(float health, float maximum, string? heldItem)
        {
            var changed = !HasState || !health.Equals(Health) || !maximum.Equals(MaximumHealth) || !string.Equals(heldItem, HeldItem, StringComparison.Ordinal);
            HasState = true;
            Health = health;
            MaximumHealth = maximum;
            HeldItem = heldItem;
            if (changed) StateChanged = true;
            return changed;
        }
    }

    /// <summary>
    /// The presence indices that name participants on the wire. The host gives each participant the lowest free index from
    /// 1 and lets one go when its participant leaves, keeping it out of use for a while so a datagram still on its way never
    /// names the next owner. A joined player keeps the host's roster. Main thread only.
    /// </summary>
    internal sealed class PresenceRoster
    {
        /// <summary>How long a freed index stays out of use: longer than any datagram stays on its way.</summary>
        internal const long QuarantineMicros = 5_000_000;

        private readonly ParticipantId[] byIndex = new ParticipantId[256];
        private readonly long[] freedAt = new long[256];
        private readonly bool[] everFreed = new bool[256];
        private readonly Dictionary<ParticipantId, byte> indices = new Dictionary<ParticipantId, byte>();
        private readonly List<KeyValuePair<byte, ParticipantId>> entries = new List<KeyValuePair<byte, ParticipantId>>();

        /// <summary>Gets a number that moves whenever an index is given, freed or the roster replaced.</summary>
        internal int Version { get; private set; }

        internal int Count => indices.Count;

        internal bool TryGetIndex(ParticipantId participant, out byte index) => indices.TryGetValue(participant, out index);

        internal bool TryGetParticipant(byte index, out ParticipantId participant)
        {
            participant = byIndex[index];
            return index != 0 && participant.IsValid;
        }

        /// <summary>Host: gives <paramref name="participant"/> an index, the one it has if any; false when all 255 are taken or resting.</summary>
        internal bool TryAssign(ParticipantId participant, long nowMicros, out byte index)
        {
            if (indices.TryGetValue(participant, out index)) return true;
            for (var candidate = 1; candidate < 256; candidate++)
            {
                if (byIndex[candidate].IsValid) continue;
                if (everFreed[candidate] && nowMicros - freedAt[candidate] < QuarantineMicros) continue;
                index = (byte)candidate;
                byIndex[candidate] = participant;
                indices.Add(participant, index);
                Version++;
                return true;
            }

            index = 0;
            return false;
        }

        /// <summary>Host: frees the index of a participant that left.</summary>
        internal void Release(ParticipantId participant, long nowMicros)
        {
            if (!indices.TryGetValue(participant, out var index)) return;
            indices.Remove(participant);
            byIndex[index] = default;
            freedAt[index] = nowMicros;
            everFreed[index] = true;
            Version++;
        }

        /// <summary>Joined player: takes the host's roster in place of what it held.</summary>
        internal void Replace(List<KeyValuePair<byte, ParticipantId>> roster)
        {
            Clear();
            foreach (var entry in roster)
            {
                byIndex[entry.Key] = entry.Value;
                indices[entry.Value] = entry.Key;
            }

            Version++;
        }

        /// <summary>Lists the roster in index order, for the host's message; the list is reused.</summary>
        internal IReadOnlyList<KeyValuePair<byte, ParticipantId>> List()
        {
            entries.Clear();
            for (var index = 1; index < 256; index++)
            {
                if (byIndex[index].IsValid) entries.Add(new KeyValuePair<byte, ParticipantId>((byte)index, byIndex[index]));
            }

            return entries;
        }

        internal void Clear()
        {
            Array.Clear(byIndex, 0, byIndex.Length);
            Array.Clear(everFreed, 0, everFreed.Length);
            indices.Clear();
            Version++;
        }
    }
}
