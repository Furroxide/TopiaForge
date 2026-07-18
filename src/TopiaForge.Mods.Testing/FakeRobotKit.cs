using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Complete deterministic fake suite for every public RobotKit service.</summary>
    public sealed class FakeRobotKit
    {
        /// <summary>Creates RobotKit fakes owned by one mod lifetime.</summary>
        public FakeRobotKit(FakeModLifetime lifetime)
        {
            if (lifetime == null)
            {
                throw new ArgumentNullException(nameof(lifetime));
            }

            Agents = new FakeRobotAgentService(lifetime);
            Objectives = new FakeRobotObjectiveService(lifetime);
            BrainQueries = new FakeRobotBrainQueryService(lifetime);
            Conversations = new FakeRobotConversationService(lifetime);
            DialogueInput = new FakePlayerDialogueInputService(lifetime);
        }

        /// <summary>Gets deterministic robot spawning and movement.</summary>
        public FakeRobotAgentService Agents { get; }

        /// <summary>Gets deterministic named targets and objectives.</summary>
        public FakeRobotObjectiveService Objectives { get; }

        /// <summary>Gets controlled structured brain-query completion.</summary>
        public FakeRobotBrainQueryService BrainQueries { get; }

        /// <summary>Gets queued multi-turn conversations.</summary>
        public FakeRobotConversationService Conversations { get; }

        /// <summary>Gets controlled voice-capture transcription.</summary>
        public FakePlayerDialogueInputService DialogueInput { get; }
    }

    /// <summary>Deterministic robot agent service that creates inspectable SDK-native agents.</summary>
    public sealed class FakeRobotAgentService : IRobotAgentService
    {
        private readonly FakeModLifetime lifetime;
        private readonly List<FakeRobotAgent> agents = new List<FakeRobotAgent>();
        private int serial;

        /// <summary>Creates an agent service owned by a fake mod lifetime.</summary>
        public FakeRobotAgentService(FakeModLifetime lifetime)
        {
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        }

        /// <inheritdoc />
        public bool IsAvailable { get; set; } = true;

        /// <inheritdoc />
        public bool IsNavigationAvailable { get; set; } = true;

        /// <inheritdoc />
        public IReadOnlyList<IRobotAgent> ActiveAgents
        {
            get
            {
                Prune();
                return new List<IRobotAgent>(agents).AsReadOnly();
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<RobotTypeDescriptor> RobotTypes { get; set; } =
            new[] { new RobotTypeDescriptor("default", "Default Robot") };

        /// <summary>Gets or sets a stable failure returned by the next spawn, or null for success.</summary>
        public ModErrorCode? NextSpawnFailure { get; set; }

        /// <summary>Gets or sets the next reachable position returned by navigation search.</summary>
        public Vec3 NextReachablePosition { get; set; }

        /// <inheritdoc />
        public OperationResult<IRobotAgent> Spawn(RobotAgentSpawnRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (!IsAvailable)
            {
                return OperationResult<IRobotAgent>.Failure(ModErrorCode.Unavailable, "Fake RobotKit is unavailable.");
            }

            if (NextSpawnFailure.HasValue)
            {
                var failure = NextSpawnFailure.Value;
                NextSpawnFailure = null;
                return OperationResult<IRobotAgent>.Failure(failure, "The configured fake spawn failed.");
            }

            var agent = new FakeRobotAgent(
                "fake-robot-" + (++serial).ToString(System.Globalization.CultureInfo.InvariantCulture),
                request,
                value => agents.Remove(value));
            agents.Add(agent);
            return lifetime.TrackResult<IRobotAgent>(
                agent,
                "The fake mod stopped before the robot could be spawned.");
        }

        /// <inheritdoc />
        public bool TryGetRobot(IEntity entity, out IRobotAgent? agent)
        {
            var fake = entity as FakeRobotAgent;
            agent = fake;
            return fake?.IsAlive == true && agents.Contains(fake);
        }

        /// <inheritdoc />
        public Task<OperationResult<ReachableSpawnResult>> FindReachableSpawnAsync(
            ReachableSpawnRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (cancellationToken.IsCancellationRequested || lifetime.StoppingToken.IsCancellationRequested)
            {
                return Task.FromResult(OperationResult<ReachableSpawnResult>.Failure(
                    ModErrorCode.Cancelled,
                    "The fake reachable-spawn search was cancelled."));
            }

            if (!IsAvailable)
            {
                return Task.FromResult(OperationResult<ReachableSpawnResult>.Failure(
                    ModErrorCode.Unavailable,
                    "Fake RobotKit is unavailable."));
            }

            var position = NextReachablePosition == Vec3.Zero ? request.Origin : NextReachablePosition;
            return Task.FromResult(OperationResult<ReachableSpawnResult>.Success(
                new ReachableSpawnResult(position)));
        }

        private void Prune() => agents.RemoveAll(agent => !agent.IsAlive);
    }

    /// <summary>Inspectable deterministic implementation of one RobotKit agent.</summary>
    public sealed class FakeRobotAgent : IRobotAgent
    {
        private Action<FakeRobotAgent>? release;
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
            Position = position;
            IsMoving = false;
            HasReachedTarget = true;
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
            callback?.Invoke(this);
            IsMoving = false;
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

    /// <summary>Controlled asynchronous structured-query fake.</summary>
    public sealed class FakeRobotBrainQueryService : IRobotBrainQueryService
    {
        private readonly FakeModLifetime lifetime;
        private readonly List<PendingQuery> pending = new List<PendingQuery>();
        private readonly Queue<OperationResult<BrainQueryResult>> queued =
            new Queue<OperationResult<BrainQueryResult>>();

        /// <summary>Creates a fake brain-query service.</summary>
        public FakeRobotBrainQueryService(FakeModLifetime lifetime)
        {
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        }

        /// <inheritdoc />
        public bool IsAvailable { get; set; } = true;

        /// <summary>Gets or sets whether calls complete synchronously from queued/default results.</summary>
        public bool AutoCompleteQueries { get; set; } = true;

        /// <summary>Gets the number of manually controlled pending queries.</summary>
        public int PendingQueryCount => pending.Count;

        /// <summary>Queues a successful structured response.</summary>
        public void EnqueueResult(IReadOnlyDictionary<string, string> values) =>
            queued.Enqueue(OperationResult<BrainQueryResult>.Success(new BrainQueryResult(values)));

        /// <summary>Queues a stable expected failure.</summary>
        public void EnqueueFailure(ModErrorCode errorCode, string message) =>
            queued.Enqueue(OperationResult<BrainQueryResult>.Failure(errorCode, message));

        /// <inheritdoc />
        public Task<OperationResult<BrainQueryResult>> QueryAsync(
            BrainQueryRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (cancellationToken.IsCancellationRequested || lifetime.StoppingToken.IsCancellationRequested)
            {
                return Task.FromResult(OperationResult<BrainQueryResult>.Failure(
                    ModErrorCode.Cancelled,
                    "The fake brain query was cancelled."));
            }

            if (!IsAvailable)
            {
                return Task.FromResult(OperationResult<BrainQueryResult>.Failure(
                    ModErrorCode.Unavailable,
                    "The fake robot brain is unavailable."));
            }

            if (AutoCompleteQueries)
            {
                return Task.FromResult(queued.Count > 0 ? queued.Dequeue() : DefaultResult(request));
            }

            var operation = new PendingQuery();
            pending.Add(operation);
            operation.AttachCancellation(
                cancellationToken,
                lifetime.StoppingToken,
                value => pending.Remove(value));
            return operation.Task;
        }

        /// <summary>Completes the oldest pending query with queued or request-independent values.</summary>
        public bool CompleteNext(IReadOnlyDictionary<string, string> values)
        {
            return TryTake(out var operation) && operation.Complete(
                OperationResult<BrainQueryResult>.Success(new BrainQueryResult(values)));
        }

        /// <summary>Fails the oldest pending query with a stable expected error.</summary>
        public bool FailNext(ModErrorCode errorCode, string message)
        {
            return TryTake(out var operation) && operation.Complete(
                OperationResult<BrainQueryResult>.Failure(errorCode, message));
        }

        private bool TryTake(out PendingQuery operation)
        {
            while (pending.Count > 0)
            {
                operation = pending[0];
                pending.RemoveAt(0);
                if (!operation.Task.IsCompleted)
                {
                    return true;
                }
            }

            operation = null!;
            return false;
        }

        private static OperationResult<BrainQueryResult> DefaultResult(BrainQueryRequest request)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var field in request.Outputs)
            {
                values[field.Name] = field.AllowedStrings != null && field.AllowedStrings.Count > 0
                    ? field.AllowedStrings[0]
                    : string.Empty;
            }

            return OperationResult<BrainQueryResult>.Success(new BrainQueryResult(values));
        }

        private sealed class PendingQuery
        {
            private readonly TaskCompletionSource<OperationResult<BrainQueryResult>> completion =
                new TaskCompletionSource<OperationResult<BrainQueryResult>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            private CancellationTokenRegistration callerCancellation;
            private CancellationTokenRegistration lifetimeCancellation;
            private Action<PendingQuery>? release;

            public void AttachCancellation(
                CancellationToken callerToken,
                CancellationToken lifetimeToken,
                Action<PendingQuery> completed)
            {
                release = completed;
                if (callerToken.CanBeCanceled)
                {
                    callerCancellation = callerToken.Register(Cancel);
                    if (Task.IsCompleted)
                    {
                        callerCancellation.Dispose();
                    }
                }

                if (!Task.IsCompleted && lifetimeToken.CanBeCanceled)
                {
                    lifetimeCancellation = lifetimeToken.Register(Cancel);
                    if (Task.IsCompleted)
                    {
                        lifetimeCancellation.Dispose();
                    }
                }
            }

            public Task<OperationResult<BrainQueryResult>> Task => completion.Task;

            public bool Complete(OperationResult<BrainQueryResult> result)
            {
                var changed = completion.TrySetResult(result);
                if (changed)
                {
                    callerCancellation.Dispose();
                    lifetimeCancellation.Dispose();
                    var completed = release;
                    release = null;
                    completed?.Invoke(this);
                }

                return changed;
            }

            private void Cancel() => Complete(OperationResult<BrainQueryResult>.Failure(
                ModErrorCode.Cancelled,
                "The fake brain query was cancelled."));
        }
    }

    /// <summary>Queued deterministic conversation service.</summary>
    public sealed class FakeRobotConversationService : IRobotConversationService
    {
        private readonly FakeModLifetime lifetime;
        private readonly Queue<OperationResult<RobotConversationTurnResult>> turns =
            new Queue<OperationResult<RobotConversationTurnResult>>();

        /// <summary>Creates a fake conversation service.</summary>
        public FakeRobotConversationService(FakeModLifetime lifetime)
        {
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        }

        /// <inheritdoc />
        public bool IsAvailable { get; set; } = true;

        /// <summary>Gets the number of active conversations.</summary>
        public int ActiveConversationCount { get; private set; }

        /// <summary>Queues one successful turn for the next submit call.</summary>
        public void EnqueueTurn(
            string reply,
            string decision,
            IReadOnlyDictionary<string, string>? values = null) =>
            turns.Enqueue(OperationResult<RobotConversationTurnResult>.Success(
                new RobotConversationTurnResult(
                    reply,
                    decision,
                    values ?? new Dictionary<string, string>())));

        /// <summary>Queues one failed turn for the next submit call.</summary>
        public void EnqueueFailure(ModErrorCode errorCode, string message) =>
            turns.Enqueue(OperationResult<RobotConversationTurnResult>.Failure(errorCode, message));

        /// <inheritdoc />
        public OperationResult<IRobotConversation> BeginConversation(RobotConversationRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (!IsAvailable)
            {
                return OperationResult<IRobotConversation>.Failure(
                    ModErrorCode.Unavailable,
                    "The fake conversation service is unavailable.");
            }

            var conversation = new Conversation(this, request.MaxTurns, () => ActiveConversationCount--);
            ActiveConversationCount++;
            return lifetime.TrackResult<IRobotConversation>(
                conversation,
                "The fake mod stopped before the conversation could begin.");
        }

        private OperationResult<RobotConversationTurnResult> TakeTurn()
        {
            return turns.Count > 0
                ? turns.Dequeue()
                : OperationResult<RobotConversationTurnResult>.Failure(
                    ModErrorCode.NotFound,
                    "No fake conversation turn is queued.");
        }

        private sealed class Conversation : IRobotConversation
        {
            private readonly FakeRobotConversationService service;
            private Action? release;

            public Conversation(FakeRobotConversationService service, int maxTurns, Action release)
            {
                this.service = service;
                MaxTurns = maxTurns;
                this.release = release;
            }

            public bool IsEnded => release == null || TurnCount >= MaxTurns;
            public int TurnCount { get; private set; }
            public int MaxTurns { get; }

            public Task<OperationResult<RobotConversationTurnResult>> SubmitAsync(
                string playerText,
                CancellationToken cancellationToken = default)
            {
                if (cancellationToken.IsCancellationRequested ||
                    service.lifetime.StoppingToken.IsCancellationRequested)
                {
                    return Task.FromResult(OperationResult<RobotConversationTurnResult>.Failure(
                        ModErrorCode.Cancelled,
                        "The fake conversation turn was cancelled."));
                }

                if (IsEnded)
                {
                    return Task.FromResult(OperationResult<RobotConversationTurnResult>.Failure(
                        ModErrorCode.InvalidState,
                        "The fake conversation has ended."));
                }

                var result = service.TakeTurn();
                if (result.Succeeded)
                {
                    TurnCount++;
                    if (TurnCount >= MaxTurns)
                    {
                        Dispose();
                    }
                }

                return Task.FromResult(result);
            }

            public void Dispose()
            {
                var callback = release;
                release = null;
                callback?.Invoke();
            }
        }
    }

    /// <summary>Controlled fake microphone and speech-to-text service.</summary>
    public sealed class FakePlayerDialogueInputService : IPlayerDialogueInputService
    {
        private readonly FakeModLifetime lifetime;

        /// <summary>Creates a fake player dialogue input service.</summary>
        public FakePlayerDialogueInputService(FakeModLifetime lifetime)
        {
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        }

        /// <inheritdoc />
        public bool IsVoiceAvailable { get; set; } = true;

        /// <summary>Gets or sets text returned by the next stopped capture.</summary>
        public string NextTranscript { get; set; } = string.Empty;

        /// <summary>Gets the number of captures still recording.</summary>
        public int ActiveCaptureCount { get; private set; }

        /// <inheritdoc />
        public OperationResult<IVoiceCapture> BeginVoiceCapture()
        {
            if (!IsVoiceAvailable)
            {
                return OperationResult<IVoiceCapture>.Failure(
                    ModErrorCode.Unavailable,
                    "Fake voice capture is unavailable.");
            }

            var capture = new VoiceCapture(
                () => NextTranscript,
                () => ActiveCaptureCount--,
                lifetime.StoppingToken);
            ActiveCaptureCount++;
            return lifetime.TrackResult<IVoiceCapture>(
                capture,
                "The fake mod stopped before voice capture could begin.");
        }

        private sealed class VoiceCapture : IVoiceCapture
        {
            private readonly Func<string> transcript;
            private readonly CancellationToken lifetimeToken;
            private Action? release;

            public VoiceCapture(
                Func<string> transcript,
                Action release,
                CancellationToken lifetimeToken)
            {
                this.transcript = transcript;
                this.release = release;
                this.lifetimeToken = lifetimeToken;
            }

            public bool IsRecording => release != null;

            public Task<OperationResult<VoiceTranscriptResult>> StopAsync(
                CancellationToken cancellationToken = default)
            {
                if (cancellationToken.IsCancellationRequested || lifetimeToken.IsCancellationRequested)
                {
                    Dispose();
                    return Task.FromResult(OperationResult<VoiceTranscriptResult>.Failure(
                        ModErrorCode.Cancelled,
                        "The fake voice capture was cancelled."));
                }

                if (!IsRecording)
                {
                    return Task.FromResult(OperationResult<VoiceTranscriptResult>.Failure(
                        ModErrorCode.InvalidState,
                        "The fake voice capture has stopped."));
                }

                var text = transcript();
                Dispose();
                return Task.FromResult(string.IsNullOrWhiteSpace(text)
                    ? OperationResult<VoiceTranscriptResult>.Failure(
                        ModErrorCode.NotFound,
                        "No fake transcript was configured.")
                    : OperationResult<VoiceTranscriptResult>.Success(
                        new VoiceTranscriptResult(text)));
            }

            public void Dispose()
            {
                var callback = release;
                release = null;
                callback?.Invoke();
            }
        }
    }

    /// <summary>Deterministic named-target and objective registry for RobotKit tests.</summary>
    public sealed class FakeRobotObjectiveService : IRobotObjectiveService
    {
        private readonly FakeModLifetime lifetime;
        private readonly Dictionary<string, TargetRegistration> targets =
            new Dictionary<string, TargetRegistration>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ObjectiveHandle> objectives =
            new Dictionary<string, ObjectiveHandle>(StringComparer.Ordinal);

        /// <summary>Creates a fake objective service owned by a mod lifetime.</summary>
        public FakeRobotObjectiveService(FakeModLifetime lifetime)
        {
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        }

        /// <inheritdoc />
        public bool IsAvailable { get; set; } = true;

        /// <inheritdoc />
        public IReadOnlyList<string> TargetNames
        {
            get
            {
                var names = new List<string>(targets.Keys);
                names.Sort(StringComparer.Ordinal);
                return names.AsReadOnly();
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<RobotTargetInfo> Targets
        {
            get
            {
                var values = new List<RobotTargetInfo>();
                foreach (var target in targets.Values)
                {
                    values.Add(target.Info);
                }

                values.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
                return values.AsReadOnly();
            }
        }

        /// <summary>Gets the number of active target and objective handles.</summary>
        public int ActiveHandleCount => targets.Count + objectives.Count;

        /// <inheritdoc />
        public event Action<RobotProgramDelivery>? ProgramDelivered;

        /// <inheritdoc />
        public OperationResult<IRobotTargetRegistration> RegisterTarget(
            string name,
            RobotTargetKind kind,
            Func<RobotTargetSnapshot?> resolve)
        {
            if (resolve == null)
            {
                throw new ArgumentNullException(nameof(resolve));
            }

            if (!IsAvailable)
            {
                return OperationResult<IRobotTargetRegistration>.Failure(
                    ModErrorCode.Unavailable,
                    "The fake objective service is unavailable.");
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return OperationResult<IRobotTargetRegistration>.Failure(
                    ModErrorCode.InvalidArgument,
                    "A target name is required.");
            }

            var normalized = name.Trim().ToUpperInvariant();
            if (targets.ContainsKey(normalized))
            {
                return OperationResult<IRobotTargetRegistration>.Failure(
                    ModErrorCode.Conflict,
                    "A fake target is already registered as '" + normalized + "'.");
            }

            var registration = new TargetRegistration(
                new RobotTargetInfo(normalized, kind),
                resolve,
                released => targets.Remove(released.Name));
            targets.Add(normalized, registration);
            return lifetime.TrackResult<IRobotTargetRegistration>(
                registration,
                "The fake mod stopped before the robot target could be registered.");
        }

        /// <inheritdoc />
        public bool TryGetTargetInfo(string name, out RobotTargetInfo info)
        {
            if (targets.TryGetValue(name ?? string.Empty, out var registration))
            {
                info = registration.Info;
                return true;
            }

            info = null!;
            return false;
        }

        /// <inheritdoc />
        public bool TryResolveTarget(string name, out RobotTargetSnapshot snapshot)
        {
            if (targets.TryGetValue(name ?? string.Empty, out var registration))
            {
                var value = registration.Resolve();
                if (value.HasValue)
                {
                    snapshot = value.Value;
                    return true;
                }
            }

            snapshot = default;
            return false;
        }

        /// <inheritdoc />
        public OperationResult<IRobotObjectiveHandle> SetObjective(
            IRobotAgent agent,
            RobotObjective objective)
        {
            if (agent == null)
            {
                throw new ArgumentNullException(nameof(agent));
            }

            if (objective == null)
            {
                throw new ArgumentNullException(nameof(objective));
            }

            if (!agent.IsAlive)
            {
                return OperationResult<IRobotObjectiveHandle>.Failure(
                    ModErrorCode.InvalidState,
                    "The fake robot is not alive.");
            }

            if (objectives.TryGetValue(agent.Id, out var previous))
            {
                previous.Dispose();
            }

            var handle = new ObjectiveHandle(
                objective,
                released => objectives.Remove(agent.Id));
            objectives[agent.Id] = handle;
            return lifetime.TrackResult<IRobotObjectiveHandle>(
                handle,
                "The fake mod stopped before the robot objective could be set.");
        }

        /// <inheritdoc />
        public bool TryGetObjective(IRobotAgent agent, out IRobotObjectiveHandle? objective)
        {
            if (agent != null && objectives.TryGetValue(agent.Id, out var value))
            {
                objective = value;
                return true;
            }

            objective = null;
            return false;
        }

        /// <inheritdoc />
        public OperationResult<bool> ClearObjective(IRobotAgent agent)
        {
            if (agent == null)
            {
                throw new ArgumentNullException(nameof(agent));
            }

            if (!objectives.TryGetValue(agent.Id, out var handle))
            {
                return OperationResult<bool>.Success(false);
            }

            handle.Dispose();
            return OperationResult<bool>.Success(true);
        }

        /// <summary>Raises a deterministic program-delivery notification.</summary>
        public void RaiseProgramDelivered(
            IRobotAgent sender,
            IRobotAgent recipient,
            RobotObjective payload) =>
            ProgramDelivered?.Invoke(new RobotProgramDelivery(sender, recipient, payload));

        private sealed class TargetRegistration : IRobotTargetRegistration
        {
            private Action<TargetRegistration>? release;

            public TargetRegistration(
                RobotTargetInfo info,
                Func<RobotTargetSnapshot?> resolve,
                Action<TargetRegistration> release)
            {
                Info = info;
                Resolve = resolve;
                this.release = release;
            }

            public RobotTargetInfo Info { get; }
            public Func<RobotTargetSnapshot?> Resolve { get; }
            public string Name => Info.Name;
            public RobotTargetKind Kind => Info.Kind;
            public bool IsActive => release != null;

            public void Dispose()
            {
                var callback = release;
                release = null;
                callback?.Invoke(this);
            }
        }

        private sealed class ObjectiveHandle : IRobotObjectiveHandle
        {
            private Action<ObjectiveHandle>? release;

            public ObjectiveHandle(RobotObjective objective, Action<ObjectiveHandle> release)
            {
                Objective = objective;
                this.release = release;
                State = objective.Kind == RobotObjectiveKind.Idle
                    ? RobotObjectiveState.Idle
                    : RobotObjectiveState.Seeking;
            }

            public RobotObjective Objective { get; }
            public RobotObjectiveState State { get; private set; }
            public int WaypointIndex { get; set; }
            public bool IsActive => release != null;

            public void Dispose()
            {
                var callback = release;
                release = null;
                State = RobotObjectiveState.Cancelled;
                callback?.Invoke(this);
            }
        }
    }
}
