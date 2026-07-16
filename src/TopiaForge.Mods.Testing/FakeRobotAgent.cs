using System;
using System.Threading;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Inspectable deterministic implementation of one RobotKit agent.</summary>
    public sealed class FakeRobotAgent : IRobotAgent
    {
        private Action<FakeRobotAgent>? release;
        private IDisposable? lifetimeLease;
        private RobotInteractionOptions interaction;

        internal FakeRobotAgent(
            string id,
            RobotAgentSpawnRequest request,
            Action<FakeRobotAgent> release)
        {
            Id = id;
            Name = request.Name ?? id;
            Position = request.Position;
            BrainMode = request.BrainMode;
            Gait = request.Gait;
            MoveSpeed = request.MoveSpeed;
            TurnSpeed = request.TurnSpeed;
            StopDistance = request.StopDistance;
            Tint = request.Tint;
            Scale = request.Scale;
            interaction = request.Interaction;
            this.release = release;
        }

        internal void AttachLifetimeLease(IDisposable lease)
        {
            lifetimeLease = lease ?? throw new ArgumentNullException(nameof(lease));
        }

        /// <inheritdoc />
        public string Id { get; }

        /// <inheritdoc />
        public string Name { get; private set; }

        /// <inheritdoc />
        public bool IsAlive => release != null;

        /// <inheritdoc />
        public Vec3 Position { get; private set; }

        /// <inheritdoc />
        public Vec3 HeadPosition => Position + new Vec3(0f, 1.8f * Scale, 0f);

        /// <inheritdoc />
        public RobotBrainMode BrainMode { get; private set; }

        /// <inheritdoc />
        public bool IsMoving { get; private set; }

        /// <inheritdoc />
        public bool HasReachedTarget { get; private set; }

        /// <inheritdoc />
        public float MoveSpeed { get; private set; }

        /// <inheritdoc />
        public float TurnSpeed { get; private set; }

        /// <inheritdoc />
        public float StopDistance { get; private set; }

        /// <inheritdoc />
        public RobotGait Gait { get; private set; }

        /// <summary>Gets the most recently assigned tint.</summary>
        public RobotColor? Tint { get; private set; }

        /// <summary>Gets the most recently assigned emote shortcode.</summary>
        public string Emote { get; private set; } = string.Empty;

        /// <summary>Gets the current uniform scale.</summary>
        public float Scale { get; private set; }

        /// <summary>Gets accumulated deterministic damage.</summary>
        public float DamageTaken { get; private set; }

        /// <summary>Gets the most recent movement target.</summary>
        public Vec3? MovementTarget { get; private set; }

        /// <summary>Gets or sets whether movement intents complete by teleporting in this deterministic fake.</summary>
        public bool AutoCompleteMovement { get; set; } = true;

        /// <inheritdoc />
        public OperationResult<bool> SetBrainMode(RobotBrainMode mode) => Mutate(() => BrainMode = mode);

        /// <inheritdoc />
        public OperationResult<bool> ConfigureMovement(RobotMovementSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            return Mutate(() =>
            {
                Gait = settings.Gait;
                MoveSpeed = settings.MoveSpeed;
                TurnSpeed = settings.TurnSpeed;
                StopDistance = settings.StopDistance;
            });
        }

        /// <inheritdoc />
        public OperationResult<bool> MoveTo(Vec3 position) => Mutate(() =>
        {
            MovementTarget = position;
            if (AutoCompleteMovement)
            {
                Position = position;
                IsMoving = false;
                HasReachedTarget = true;
            }
            else
            {
                IsMoving = true;
                HasReachedTarget = false;
            }
        });

        /// <inheritdoc />
        public OperationResult<bool> Chase(IEntity target)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            return MoveTo(target.Position);
        }

        /// <inheritdoc />
        public OperationResult<bool> Stop() => Mutate(() =>
        {
            IsMoving = false;
            MovementTarget = null;
        });

        /// <inheritdoc />
        public OperationResult<bool> SetTint(RobotColor color) => Mutate(() => Tint = color);

        /// <inheritdoc />
        public OperationResult<bool> SetEmote(string emojiShortcode) =>
            Mutate(() => Emote = emojiShortcode ?? string.Empty);

        /// <inheritdoc />
        public OperationResult<bool> SetName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "A robot name is required.");
            }

            return Mutate(() => Name = name);
        }

        /// <inheritdoc />
        public OperationResult<bool> SetScale(float scale)
        {
            if (scale <= 0f || float.IsNaN(scale) || float.IsInfinity(scale))
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Robot scale must be finite and positive.");
            }

            return Mutate(() => Scale = scale);
        }

        /// <inheritdoc />
        public OperationResult<bool> SetInteraction(RobotInteractionOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            return Mutate(() => interaction = options);
        }

        /// <inheritdoc />
        public OperationResult<bool> ApplyDamage(float amount, RobotDamageType type, string source)
        {
            if (amount <= 0f || float.IsNaN(amount) || float.IsInfinity(amount))
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Damage must be finite and positive.");
            }

            return Mutate(() => DamageTaken += amount);
        }

        /// <inheritdoc />
        public OperationResult<bool> Kill(RobotDamageType type, string source)
        {
            if (!IsAlive)
            {
                return OperationResult<bool>.Success(false);
            }

            Dispose();
            return OperationResult<bool>.Success(true);
        }

        /// <inheritdoc />
        public OperationResult<bool> Ragdoll() => AliveResult();

        /// <inheritdoc />
        public OperationResult<bool> Knockback(Vec3 impulse) => AliveResult();

        /// <inheritdoc />
        public OperationResult<bool> Despawn()
        {
            var changed = IsAlive;
            Dispose();
            return OperationResult<bool>.Success(changed);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            var callback = release;
            release = null;
            try
            {
                callback?.Invoke(this);
            }
            finally
            {
                IsMoving = false;
                Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
            }
        }

        private OperationResult<bool> Mutate(Action action)
        {
            if (!IsAlive)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The fake robot is not alive.");
            }

            action();
            return OperationResult<bool>.Success(true);
        }

        private OperationResult<bool> AliveResult() => IsAlive
            ? OperationResult<bool>.Success(true)
            : OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The fake robot is not alive.");
    }
}
