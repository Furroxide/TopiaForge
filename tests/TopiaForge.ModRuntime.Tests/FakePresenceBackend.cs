using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.TestDoubles
{
    /// <summary>
    /// Presence's engine side over a fake: a local player a test moves by hand, avatars that record every placement, a
    /// ground the test describes, and bounds the test sets. The ModRuntime and ModManager harnesses both compile it.
    /// </summary>
    internal sealed class FakePresenceBackend : IPresenceBackend
    {
        private readonly FakeEntity playerEntity = new FakeEntity("player");

        /// <summary>Gets or sets whether there is a local player in a world.</summary>
        internal bool HasPlayer { get; set; } = true;

        internal NetVector3 Feet { get; set; }

        internal NetQuaternion View { get; set; } = NetQuaternion.Identity;

        internal float EyeHeight { get; set; } = 1.6f;

        internal bool Crouching { get; set; }

        internal bool HasHealth { get; set; } = true;

        internal float Health { get; set; } = 100f;

        internal float MaximumHealth { get; set; } = 100f;

        internal string? HeldItem { get; set; }

        /// <summary>Gets or sets where the ground is under a point, or null for none: flat at height zero by default.</summary>
        internal Func<NetVector3, NetVector3?> Ground { get; set; } = from => new NetVector3(from.X, 0f, from.Z);

        /// <summary>Gets or sets whether a standing player fits at a point: everywhere by default.</summary>
        internal Func<NetVector3, bool> Clear { get; set; } = _ => true;

        /// <summary>Gets or sets the world's bounds, or null when the engine cannot tell.</summary>
        internal PresenceBounds? Bounds { get; set; }

        /// <summary>Gets or sets whether making an avatar fails, as a broken engine would.</summary>
        internal bool FailAvatars { get; set; }

        /// <summary>Gets or sets whether avatars keep every placement in their trails; off, they allocate nothing per frame.</summary>
        internal bool RecordTrails { get; set; } = true;

        /// <summary>Gets every teleport presence asked for, in order.</summary>
        internal List<(NetVector3 Feet, float Yaw, bool KeepFacing)> Teleports { get; } = new List<(NetVector3, float, bool)>();

        /// <summary>Gets every avatar made, alive or not.</summary>
        internal List<FakeAvatar> Avatars { get; } = new List<FakeAvatar>();

        internal int GroundProbes { get; private set; }

        public IEntity? LocalPlayerEntity => HasPlayer ? playerEntity : null;

        public bool TryReadLocalPlayer(out PresenceLocalSample sample)
        {
            sample = HasPlayer ? new PresenceLocalSample(Feet, View, EyeHeight, Crouching) : default;
            return HasPlayer;
        }

        public bool TryReadLocalHealth(out float health, out float maximum)
        {
            health = Health;
            maximum = MaximumHealth;
            return HasPlayer && HasHealth;
        }

        public string? ReadLocalHeldItem() => HasPlayer ? HeldItem : null;

        public bool TeleportLocalPlayer(NetVector3 feet, float yawDegrees, bool keepFacing)
        {
            if (!HasPlayer) return false;
            Teleports.Add((feet, yawDegrees, keepFacing));
            Feet = feet;
            return true;
        }

        public IPresenceAvatarObject CreateAvatar(int index, PresenceTint tint)
        {
            if (FailAvatars) throw new InvalidOperationException("The fake engine makes no avatars now.");
            var avatar = new FakeAvatar(this, index, tint);
            Avatars.Add(avatar);
            return avatar;
        }

        public NetVector3? FindGround(NetVector3 from)
        {
            GroundProbes++;
            return Ground(from);
        }

        public bool IsClear(NetVector3 feet) => Clear(feet);

        public bool TryGetWorldBounds(out PresenceBounds bounds)
        {
            bounds = Bounds ?? PresenceBounds.Everywhere;
            return Bounds.HasValue;
        }

        /// <summary>Gets the avatars alive now.</summary>
        internal List<FakeAvatar> Alive()
        {
            var alive = new List<FakeAvatar>();
            foreach (var avatar in Avatars)
            {
                if (avatar.IsAlive) alive.Add(avatar);
            }

            return alive;
        }
    }

    /// <summary>One avatar in the fake engine: where it was placed, how it animated and what body it wore.</summary>
    internal sealed class FakeAvatar : IPresenceAvatarObject
    {
        private readonly FakePresenceBackend engine;

        internal FakeAvatar(FakePresenceBackend engine, int index, PresenceTint tint)
        {
            this.engine = engine;
            Index = index;
            Tint = tint;
            Entity = new FakeEntity("avatar-" + index, () => Feet);
        }

        internal int Index { get; }

        internal PresenceTint Tint { get; }

        internal NetVector3 Feet { get; private set; }

        internal float Yaw { get; private set; }

        internal float Speed { get; private set; }

        internal bool Crouching { get; private set; }

        internal int Placements { get; private set; }

        internal IInternalAvatarBody? Body { get; private set; }

        /// <summary>Gets or sets whether the fake engine destroyed it, as a scene change might.</summary>
        internal bool Destroyed { get; set; }

        /// <summary>Gets every placement, in order.</summary>
        internal List<NetVector3> Trail { get; } = new List<NetVector3>();

        public IEntity Entity { get; }

        public bool IsAlive => !Destroyed;

        public bool IsWearingBody => Body != null;

        public NetVector3 HeadPosition => new NetVector3(Feet.X, Feet.Y + 2.25f, Feet.Z);

        public void Place(NetVector3 feet, float yawDegrees)
        {
            Feet = feet;
            Yaw = yawDegrees;
            Placements++;
            if (engine.RecordTrails) Trail.Add(feet);
        }

        public bool Wear(IInternalAvatarBody body)
        {
            if (Body != null || Destroyed) return false;
            Body = body;
            return true;
        }

        public void TakeOffBody() => Body = null;

        public void Animate(float speedMetersPerSecond, bool crouching)
        {
            Speed = speedMetersPerSecond;
            Crouching = crouching;
        }

        public void Dispose() => Destroyed = true;
    }

    /// <summary>A fake engine's entity.</summary>
    internal sealed class FakeEntity : IEntity, IInternalNativeObjectEntity
    {
        private readonly Func<NetVector3>? position;

        internal FakeEntity(string id, Func<NetVector3>? position = null)
        {
            Id = id;
            this.position = position;
        }

        public string Id { get; }

        public string Name => Id;

        public bool IsAlive { get; set; } = true;

        public Vec3 Position
        {
            get
            {
                var at = position?.Invoke() ?? NetVector3.Zero;
                return new Vec3(at.X, at.Y, at.Z);
            }
        }

        public object? NativeObject => IsAlive ? this : null;
    }

    /// <summary>A body provider in the fake engine: it makes bodies, refuses or throws as a test asks, and records them.</summary>
    internal sealed class FakeBodyFactory : IInternalAvatarBodyFactory
    {
        internal List<FakeBody> Made { get; } = new List<FakeBody>();

        /// <summary>Gets or sets whether a body can be made now, as a scene with a robot to copy allows.</summary>
        internal bool Available { get; set; } = true;

        internal bool Throws { get; set; }

        internal int Requests { get; private set; }

        public OperationResult<IInternalAvatarBody> CreateBody(InternalAvatarBodyRequest request)
        {
            Requests++;
            if (Throws) throw new InvalidOperationException("The fake provider failed.");
            if (!Available) return OperationResult<IInternalAvatarBody>.Failure(ModErrorCode.Unavailable, "No robot to copy here.");
            var body = new FakeBody(request);
            Made.Add(body);
            return OperationResult<IInternalAvatarBody>.Success(body);
        }
    }

    /// <summary>
    /// A voice service in the fake provider: whether this player transmits, and whom the host's relay heard lately. Only
    /// what presence reads is real; the rest is unused.
    /// </summary>
    internal sealed class FakeVoice : IInternalVoiceService
    {
        internal InternalVoiceTransmitState State { get; set; }

        /// <summary>Gets the participants the relay heard lately.</summary>
        internal HashSet<ParticipantId> Speaking { get; } = new HashSet<ParticipantId>();

        public bool Enabled { get; set; } = true;

        public InternalVoiceTransmitMode Mode { get; set; }

        public float ActivationThresholdDb { get; set; } = -40f;

        public float InputLevelDb => -96f;

        public InternalVoiceTransmitState TransmitState => State;

        public void SubmitCapture(float[] samples, int count, int sampleRate) => throw new NotSupportedException();

        public void SetTransmitting(bool transmitting) => throw new NotSupportedException();

        public IVoicePlaybackStream GetPlaybackStream(ParticipantId speaker) => throw new NotSupportedException();

        public bool TryGetPlaybackStream(ParticipantId speaker, out IVoicePlaybackStream? stream) => throw new NotSupportedException();

        public bool IsSpeaking(ParticipantId speaker) => Speaking.Contains(speaker);

        public void SetListenerMutes(IReadOnlyCollection<ParticipantId> muted, bool muteAll = false) => throw new NotSupportedException();

        public bool GetListenerMutes(ICollection<ParticipantId> into) => throw new NotSupportedException();

        public InternalVoiceStatistics GetStatistics() => throw new NotSupportedException();
    }

    /// <summary>One fake body: its tint, its last animation, and whether it was disposed.</summary>
    internal sealed class FakeBody : IInternalAvatarBody
    {
        internal FakeBody(InternalAvatarBodyRequest request)
        {
            Request = request;
            Root = new FakeEntity("body");
        }

        internal InternalAvatarBodyRequest Request { get; }

        internal float Speed { get; private set; }

        internal bool Disposed { get; private set; }

        public IEntity Root { get; }

        public bool IsAlive => !Disposed && ((FakeEntity)Root).IsAlive;

        public void Animate(float speedMetersPerSecond, bool crouching) => Speed = speedMetersPerSecond;

        public void Dispose() => Disposed = true;
    }
}
