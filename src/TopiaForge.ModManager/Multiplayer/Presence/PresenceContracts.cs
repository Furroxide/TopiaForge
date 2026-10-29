using System;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>The local player as presence samples it each frame: its feet, its view, its eyes' height and its crouch.</summary>
    internal readonly struct PresenceLocalSample
    {
        public PresenceLocalSample(NetVector3 feet, NetQuaternion view, float eyeHeight, bool crouching)
        {
            Feet = feet;
            View = view;
            EyeHeight = eyeHeight;
            Crouching = crouching;
        }

        /// <summary>Gets where the player's feet are.</summary>
        public NetVector3 Feet { get; }

        /// <summary>Gets the camera's rotation: where the player looks.</summary>
        public NetQuaternion View { get; }

        /// <summary>Gets how high above its feet the player's eyes are, in metres.</summary>
        public float EyeHeight { get; }

        public bool Crouching { get; }
    }

    /// <summary>
    /// What presence needs from the engine (WS3.10): the local player, avatars for the others, and the world's ground and
    /// bounds for the spawn ring and the host's checks. <c>UnityPresenceBackend</c> implements it over the game; the tests'
    /// <c>FakePresenceBackend</c> over a fake. Main thread only; the per-frame members must not allocate.
    /// </summary>
    internal interface IPresenceBackend
    {
        /// <summary>Reads the local player; false at the menu, while a world loads, or whenever there is none.</summary>
        bool TryReadLocalPlayer(out PresenceLocalSample sample);

        /// <summary>Reads the local player's health; false when it cannot be read. Asked a few times a second at most.</summary>
        bool TryReadLocalHealth(out float health, out float maximum);

        /// <summary>Gets what the local player holds by its catalog id, or null; the same instance while it holds the same thing.</summary>
        string? ReadLocalHeldItem();

        /// <summary>Gets the local player's own entity, which stands for this player where others have an avatar, or null.</summary>
        IEntity? LocalPlayerEntity { get; }

        /// <summary>
        /// Moves the local player's feet to <paramref name="feet"/>, as the host's placement or correction asks, facing
        /// <paramref name="yawDegrees"/> unless <paramref name="keepFacing"/>. False when there is no player to move.
        /// </summary>
        bool TeleportLocalPlayer(NetVector3 feet, float yawDegrees, bool keepFacing);

        /// <summary>Makes one remote player's avatar: a root with a kinematic capsule collider, wearing a capsule of <paramref name="tint"/>.</summary>
        IPresenceAvatarObject CreateAvatar(int index, PresenceTint tint);

        /// <summary>Finds the ground straight below <paramref name="from"/>, ignoring players, or null when there is none in reach.</summary>
        NetVector3? FindGround(NetVector3 from);

        /// <summary>Gets whether a standing player fits with its feet at <paramref name="feet"/>, players aside.</summary>
        bool IsClear(NetVector3 feet);

        /// <summary>Gets the box around the loaded world that players stay inside; false when the engine cannot tell.</summary>
        bool TryGetWorldBounds(out PresenceBounds bounds);
    }

    /// <summary>A gameplay host that gives presence its engine side.</summary>
    internal interface IPresenceBackendSource
    {
        IPresenceBackend PresenceBackend { get; }
    }

    /// <summary>One remote player's avatar in the engine. Main thread; nothing per frame allocates.</summary>
    internal interface IPresenceAvatarPresentation
    {
        void Present(AvatarAppearance appearance, bool poweredDown);
    }

    internal interface IPresenceAvatarObject : IDisposable
    {
        /// <summary>Gets the avatar's entity: what physics hits, what a robot's Chase follows, and what a session's TryGetAvatar names.</summary>
        IEntity Entity { get; }

        /// <summary>Gets whether the avatar still exists; one the game destroyed is made again.</summary>
        bool IsAlive { get; }

        /// <summary>Gets whether it wears a body a provider made, rather than its capsule.</summary>
        bool IsWearingBody { get; }

        /// <summary>Gets the point above its head where a nametag goes.</summary>
        NetVector3 HeadPosition { get; }

        /// <summary>Moves the avatar: its feet, and which way its body faces.</summary>
        void Place(NetVector3 feet, float yawDegrees);

        /// <summary>Wears <paramref name="body"/> in place of the capsule; false when it cannot, and the capsule stays.</summary>
        bool Wear(IInternalAvatarBody body);

        /// <summary>Takes the body off and shows the capsule again. The caller disposes the body.</summary>
        void TakeOffBody();

        /// <summary>Shows it moving at <paramref name="speedMetersPerSecond"/> across the ground, crouching or not.</summary>
        void Animate(float speedMetersPerSecond, bool crouching);
    }

    /// <summary>The colour of one player's avatar, the same on every peer: picked by the player's presence index.</summary>
    internal readonly struct PresenceTint : IEquatable<PresenceTint>
    {
        // Twelve saturated colours far enough apart that players tell each other's avatars apart against the world.
        private static readonly float[] Palette =
        {
            0.93f, 0.33f, 0.31f,
            0.27f, 0.62f, 0.95f,
            0.98f, 0.76f, 0.23f,
            0.36f, 0.80f, 0.44f,
            0.72f, 0.42f, 0.93f,
            0.96f, 0.55f, 0.20f,
            0.22f, 0.80f, 0.80f,
            0.94f, 0.40f, 0.70f,
            0.60f, 0.78f, 0.25f,
            0.45f, 0.48f, 0.95f,
            0.85f, 0.62f, 0.45f,
            0.55f, 0.85f, 0.95f
        };

        public PresenceTint(float red, float green, float blue)
        {
            Red = red;
            Green = green;
            Blue = blue;
        }

        /// <summary>Gets the number of distinct colours.</summary>
        public static int Count => Palette.Length / 3;

        public float Red { get; }

        public float Green { get; }

        public float Blue { get; }

        /// <summary>Gets the colour for presence index <paramref name="index"/>, from 1.</summary>
        public static PresenceTint For(int index)
        {
            var slot = ((Math.Max(1, index) - 1) % Count) * 3;
            return new PresenceTint(Palette[slot], Palette[slot + 1], Palette[slot + 2]);
        }

        public InternalAvatarBodyRequest ToBodyRequest() => new InternalAvatarBodyRequest(Red, Green, Blue);

        public bool Equals(PresenceTint other) => Red.Equals(other.Red) && Green.Equals(other.Green) && Blue.Equals(other.Blue);

        public override bool Equals(object? obj) => obj is PresenceTint other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Red, Green, Blue);
    }

    /// <summary>One nametag: a remote player's display name, and where above its avatar to draw it. It carries nothing else.</summary>
    internal readonly struct PresenceNametag
    {
        public PresenceNametag(string displayName, NetVector3 anchor)
        {
            DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
            Anchor = anchor;
        }

        /// <summary>Gets the name the player chose, which the host relays; show it as a name only.</summary>
        public string DisplayName { get; }

        /// <summary>Gets the point above the avatar's head.</summary>
        public NetVector3 Anchor { get; }
    }

    /// <summary>
    /// The seam the nametag layer (WS5b) reads once a frame: every remote player's avatar that is shown, by display name
    /// only, and where its head is. <see cref="NametagVersion"/> moves whenever the set or a name changes. Main thread;
    /// reading allocates nothing.
    /// </summary>
    internal interface IPresenceNametagSource
    {
        int NametagCount { get; }

        int NametagVersion { get; }

        bool TryGetNametag(int slot, out PresenceNametag nametag);
    }
}
