using System;
using TopiaForge.ModManager.Core.Multiplayer;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// The local player's own presence: it samples the player every frame and says when a pose is due, 20 a second while
    /// anything changes and one a second at rest. It flags a jump the speed limit cannot explain as a teleport; the host
    /// validates its own placements and corrections at the landing. It counts the player away after two minutes without
    /// moving or turning, and reads health and the held item a few times a second, saying when they change. It owns the
    /// pose: the host only checks it. Nothing per frame allocates. Main thread only.
    /// </summary>
    internal sealed class PresenceLocalPlayer
    {
        /// <summary>The interval between poses while the player moves: 20 a second.</summary>
        internal const long SendIntervalMicros = 50_000;

        /// <summary>The interval between poses at rest, so the host knows the player is still there.</summary>
        internal const long HeartbeatMicros = 1_000_000;

        /// <summary>How long a player who neither moves nor turns takes to count as away.</summary>
        internal const long AwayAfterMicros = 120_000_000;

        /// <summary>How often health and the held item are read.</summary>
        internal const long StateReadIntervalMicros = 250_000;

        /// <summary>The shortest interval between two state reports.</summary>
        internal const long StateSendIntervalMicros = 100_000;

        // A player moves faster than this only when its game moves it, so a bigger step between two frames is a teleport.
        private const long MaximumUnitsPerSecond = 30L * NetQuantization.PositionUnitsPerMeter;
        private const long JumpSlackUnits = NetQuantization.PositionUnitsPerMeter;

        // Moving less than about two centimetres, without turning, is standing still.
        private const long StillUnits = 10;

        private readonly IPresenceBackend backend;
        private bool hasPrevious;
        private NetQuantizedPosition previousPosition;
        private long previousMicros;
        private bool hasSent;
        private NetQuantizedPosition sentPosition;
        private uint sentRotation;
        private PresenceFlags sentFlags;
        private byte sentEye;
        private NetQuantizedVelocity sentVelocity;
        private long sentMicros;
        private long poseDueMicros;
        private bool hasActivity;
        private NetQuantizedPosition activityPosition;
        private uint activityRotation;
        private long activityMicros;
        private bool teleportPending;
        private bool hasStateRead;
        private long stateReadMicros;
        private long stateSentMicros;
        private bool stateDirty;
        private bool stateSent;

        internal PresenceLocalPlayer(IPresenceBackend backend)
        {
            this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        /// <summary>Gets whether the last sample found a player.</summary>
        internal bool HasPlayer { get; private set; }

        internal NetQuantizedPosition Position { get; private set; }

        internal uint Rotation { get; private set; } = NetQuantization.IdentityRotationBits;

        internal byte EyeCentimeters { get; private set; }

        /// <summary>Gets the flags as of the last sample, a pending teleport aside.</summary>
        internal PresenceFlags Flags { get; private set; }

        /// <summary>Gets the velocity the last pose taken carried.</summary>
        internal NetQuantizedVelocity Velocity => sentVelocity;

        /// <summary>Gets or sets whether the player is typing in chat; the chat overlay (WS5b) sets it.</summary>
        internal bool Typing { get; set; }

        internal bool HasHealth { get; private set; }

        internal float Health { get; private set; }

        internal float MaximumHealth { get; private set; }

        internal string? HeldItem { get; private set; }

        /// <summary>Starts afresh, as at a change of realm: nothing has been sent yet.</summary>
        internal void Restart()
        {
            hasPrevious = false;
            hasSent = false;
            hasActivity = false;
            teleportPending = false;
            hasStateRead = false;
            stateDirty = false;
            stateSent = false;
            sentVelocity = default;
        }

        /// <summary>Samples the player for this frame; <paramref name="talking"/> comes from the voice service.</summary>
        internal void Sample(long nowMicros, bool talking)
        {
            if (!backend.TryReadLocalPlayer(out var sample) || !sample.Feet.IsFinite)
            {
                HasPlayer = false;
                hasPrevious = false;
                return;
            }

            var position = NetQuantization.QuantizePosition(sample.Feet);
            var rotation = NetQuantization.QuantizeRotation(sample.View);
            if (hasPrevious)
            {
                var elapsed = Math.Max(0, nowMicros - previousMicros);
                var allowed = MaximumUnitsPerSecond * Math.Min(elapsed, 1_000_000) / 1_000_000 + JumpSlackUnits;
                if (position.DistanceSquaredTo(previousPosition) > allowed * allowed)
                {
                    teleportPending = true;
                }
            }

            hasPrevious = true;
            previousPosition = position;
            previousMicros = nowMicros;
            if (!hasActivity || position.DistanceSquaredTo(activityPosition) > StillUnits * StillUnits || rotation != activityRotation)
            {
                hasActivity = true;
                activityPosition = position;
                activityRotation = rotation;
                activityMicros = nowMicros;
            }

            var flags = PresenceFlags.None;
            if (sample.Crouching) flags |= PresenceFlags.Crouching;
            if (talking) flags |= PresenceFlags.Talking;
            if (Typing) flags |= PresenceFlags.Typing;
            if (nowMicros - activityMicros >= AwayAfterMicros) flags |= PresenceFlags.Away;
            HasPlayer = true;
            Position = position;
            Rotation = rotation;
            EyeCentimeters = EyeOf(sample.EyeHeight);
            Flags = flags;
        }

        /// <summary>
        /// Takes the pose that is due now, numbered with the host's latest <paramref name="correction"/>: false while none is
        /// due, or with no player.
        /// </summary>
        internal bool TryTakePose(long nowMicros, byte correction, out PresencePoseRecord pose)
        {
            pose = default;
            if (!HasPlayer) return false;
            if (hasSent && nowMicros < poseDueMicros) return false;
            var elapsed = nowMicros - sentMicros;
            var changed = !hasSent || teleportPending || !Position.Equals(sentPosition) || Rotation != sentRotation ||
                          Flags != sentFlags || EyeCentimeters != sentEye || !sentVelocity.Equals(default(NetQuantizedVelocity));
            if (!changed && elapsed < HeartbeatMicros) return false;
            var velocity = !hasSent || teleportPending || elapsed <= 0
                ? default
                : VelocityOf(sentPosition, Position, elapsed);
            pose.Millis = unchecked((uint)(nowMicros / 1000));
            pose.Correction = correction;
            pose.Flags = teleportPending ? Flags | PresenceFlags.Teleported : Flags;
            pose.EyeCentimeters = EyeCentimeters;
            pose.Position = Position;
            pose.Rotation = Rotation;
            pose.Velocity = velocity;
            // Poses keep to a 50 ms beat whatever the frame rate, so a game at 60 frames a second still sends 20; one that fell
            // a whole beat behind, at rest or stalled, starts the beat again from now rather than catching up in a burst.
            poseDueMicros = !hasSent || nowMicros - poseDueMicros >= SendIntervalMicros ? nowMicros + SendIntervalMicros : poseDueMicros + SendIntervalMicros;
            hasSent = true;
            sentPosition = Position;
            sentRotation = Rotation;
            sentFlags = Flags;
            sentEye = EyeCentimeters;
            sentVelocity = velocity;
            sentMicros = nowMicros;
            teleportPending = false;
            return true;
        }

        /// <summary>
        /// Reads health and the held item when a read is due, and says whether a state report is due now: on a change, at
        /// most ten a second.
        /// </summary>
        internal bool TryTakeState(long nowMicros)
        {
            if (!HasPlayer) return false;
            if (!hasStateRead || nowMicros - stateReadMicros >= StateReadIntervalMicros)
            {
                hasStateRead = true;
                stateReadMicros = nowMicros;
                var read = backend.TryReadLocalHealth(out var health, out var maximum) && !float.IsNaN(health) && !float.IsNaN(maximum) &&
                           maximum > 0f && !float.IsInfinity(maximum);
                var held = backend.ReadLocalHeldItem();
                if (held != null && !PresenceWire.IsHeldItem(held)) held = null;
                if (read) health = Math.Max(0f, Math.Min(health, maximum));
                if (read != HasHealth || (read && (!health.Equals(Health) || !maximum.Equals(MaximumHealth))) || !string.Equals(held, HeldItem, StringComparison.Ordinal))
                {
                    stateDirty = true;
                }

                HasHealth = read;
                Health = read ? health : 0f;
                MaximumHealth = read ? maximum : 0f;
                HeldItem = held;
            }

            if (!stateDirty || (stateSent && nowMicros - stateSentMicros < StateSendIntervalMicros)) return false;
            stateDirty = false;
            stateSent = true;
            stateSentMicros = nowMicros;
            return true;
        }

        private static byte EyeOf(float eyeHeight)
        {
            if (float.IsNaN(eyeHeight) || eyeHeight <= 0f) return 0;
            var centimetres = NetQuantization.RoundHalfAwayFromZero(eyeHeight * 100.0);
            return centimetres >= 255 ? (byte)255 : (byte)centimetres;
        }

        private static NetQuantizedVelocity VelocityOf(NetQuantizedPosition from, NetQuantizedPosition to, long elapsedMicros)
        {
            var scale = 1_000_000.0 / (NetQuantization.PositionUnitsPerMeter * (double)elapsedMicros);
            return NetQuantization.QuantizeVelocity(new NetVector3(
                (float)((to.X - (double)from.X) * scale),
                (float)((to.Y - (double)from.Y) * scale),
                (float)((to.Z - (double)from.Z) * scale)));
        }
    }
}
