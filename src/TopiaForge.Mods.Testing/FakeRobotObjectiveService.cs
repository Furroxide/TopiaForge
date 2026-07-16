using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
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
