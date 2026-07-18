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
    public sealed class FakeRobotAgentService : IRobotAgentService, IRobotPlayerEntitySource
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

        /// <summary>
        /// Gets or sets the safe live player returned by <see cref="TryGetPlayerEntity"/>. Set to <c>null</c>, or
        /// use a destroyed entity, to model a scene where the player is unavailable.
        /// </summary>
        public IEntity? PlayerEntity { get; set; } =
            new FakeEntity("fake-player", "Player", Vec3.Zero);

        /// <summary>
        /// Gets or sets whether movement intents teleport agents to their target immediately. Disable this when
        /// testing range, pursuit, or stranded-agent behavior.
        /// </summary>
        public bool AutoCompleteAgentMovement { get; set; } = true;

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
                value => agents.Remove(value))
            {
                AutoCompleteMovement = AutoCompleteAgentMovement
            };
            agents.Add(agent);
            return lifetime.TrackResult<IRobotAgent>(
                agent,
                agent.AttachLifetimeLease,
                "The fake mod stopped before the robot could be spawned.");
        }

        /// <inheritdoc />
        public bool TryGetRobot(IEntity entity, out IRobotAgent? agent)
        {
            if (entity != null && entity.IsAlive)
            {
                foreach (var candidate in agents)
                {
                    if (candidate.IsAlive
                        && (ReferenceEquals(candidate, entity)
                            || string.Equals(candidate.Id, entity.Id, StringComparison.Ordinal)))
                    {
                        agent = candidate;
                        return true;
                    }
                }
            }

            agent = null;
            return false;
        }

        /// <inheritdoc />
        public bool TryGetPlayerEntity(out IEntity? entity)
        {
            var player = PlayerEntity;
            if (!lifetime.IsStopping && player?.IsAlive == true)
            {
                entity = player;
                return true;
            }

            entity = null;
            return false;
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

            var position = NextReachablePosition == Vec3.Zero
                ? request.Origin + new Vec3(Math.Max(0f, request.MinRadius), request.HeightOffset, 0f)
                : NextReachablePosition;
            return Task.FromResult(OperationResult<ReachableSpawnResult>.Success(
                new ReachableSpawnResult(position)));
        }

        private void Prune() => agents.RemoveAll(agent => !agent.IsAlive);
    }
}
