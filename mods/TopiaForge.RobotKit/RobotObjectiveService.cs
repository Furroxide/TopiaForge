using System;
using System.Collections.Generic;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.RobotKit
{
    // Publishes IRobotObjectiveService: persistent robot programs (go-to / follow / patrol / wander / flee /
    // reprogram-courier / idle) executed frame-to-frame on top of IRobotAgent movement intents, plus the
    // session-scoped named-target registry that both the runners and LLM payloads draw from. Unity-free â€” it only
    // touches the SDK contracts and ObjectiveRunner â€” so the whole flow unit-tests on net8.0 with a fake agent.
    // Ticked after the agent service (agents step first, then objectives react to fresh reached/moving state).
    // Never throws.
    internal sealed class RobotObjectiveService : IRobotObjectiveService,
        IOwnerBoundExtensionFactory, IDisposable
    {
        private const string FrameworkOwnerId = "io.github.furroxide.topiaforge.robotkit";
        private readonly IModLogger logger;
        private readonly string defaultOwnerId;
        private readonly Func<float> now;
        private readonly Func<float> random01;
        private readonly Func<IEntity, IRobotAgent?>? resolveAgent; // live entity -> agent, for Reprogram couriers
        private readonly Dictionary<string, ObjectiveRunner> runners = new Dictionary<string, ObjectiveRunner>();
        private readonly Dictionary<string, string> runnerOwners =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<ObjectiveRunner> stepBuffer = new List<ObjectiveRunner>();
        private readonly Dictionary<string, Dictionary<string, TargetEntry>> targetsByOwner =
            new Dictionary<string, Dictionary<string, TargetEntry>>(StringComparer.OrdinalIgnoreCase);

        private float clock;
        private bool disposed;

        public RobotObjectiveService(
            IModLogger logger,
            Func<float>? now = null,
            Func<IEntity, IRobotAgent?>? resolveAgent = null,
            Func<float>? random01 = null,
            string ownerModId = FrameworkOwnerId)
        {
            this.logger = logger;
            defaultOwnerId = string.IsNullOrWhiteSpace(ownerModId) ? FrameworkOwnerId : ownerModId;
            this.now = now ?? (() => clock);
            this.resolveAgent = resolveAgent;
            if (random01 != null)
            {
                this.random01 = random01;
            }
            else
            {
                var rng = new Random();
                this.random01 = () => (float)rng.NextDouble();
            }
        }

        public bool IsAvailable => !disposed;

        public event Action<RobotProgramDelivery>? ProgramDelivered;
        private event Action<OwnedDelivery>? ProgramDeliveredOwned;

        public IReadOnlyList<string> TargetNames => GetTargetNames(defaultOwnerId);

        public IReadOnlyList<RobotTargetInfo> Targets => GetTargets(defaultOwnerId);

        public bool TryGetTargetInfo(string name, out RobotTargetInfo info)
        {
            return TryGetTargetInfo(defaultOwnerId, name, out info);
        }

        internal bool TryGetTargetInfo(string ownerModId, string name, out RobotTargetInfo info)
        {
            info = null!;
            if (string.IsNullOrWhiteSpace(name)
                || !TryGetOwnerTargets(ownerModId, out var targets)
                || !targets.TryGetValue(Normalize(name), out var entry))
            {
                return false;
            }

            info = entry.Info;
            return true;
        }

        public void RegisterTarget(
            string name,
            RobotTargetKind kind,
            Func<RobotTargetSnapshot?> resolve)
        {
            RegisterTarget(defaultOwnerId, name, kind, resolve);
        }

        OperationResult<IRobotTargetRegistration> IRobotObjectiveService.RegisterTarget(
            string name,
            RobotTargetKind kind,
            Func<RobotTargetSnapshot?> resolve) =>
            RegisterTarget(defaultOwnerId, name, kind, resolve);

        public void UnregisterTarget(string name)
        {
            if (string.IsNullOrWhiteSpace(name)
                || !TryGetOwnerTargets(defaultOwnerId, out var targets))
            {
                return;
            }

            var normalized = Normalize(name);
            if (targets.TryGetValue(normalized, out var entry))
            {
                ReleaseTarget(defaultOwnerId, normalized, entry);
            }
        }

        internal OperationResult<IRobotTargetRegistration> RegisterTarget(
            string ownerModId,
            string name,
            RobotTargetKind kind,
            Func<RobotTargetSnapshot?> resolve)
        {
            if (resolve == null)
            {
                throw new ArgumentNullException(nameof(resolve));
            }

            if (disposed)
            {
                return OperationResult<IRobotTargetRegistration>.Failure(
                    ModErrorCode.Unavailable,
                    "RobotKit objective service is unavailable.");
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return OperationResult<IRobotTargetRegistration>.Failure(
                    ModErrorCode.InvalidArgument,
                    "A target name is required.");
            }

            var normalized = Normalize(name);
            var targets = GetOwnerTargets(ownerModId, create: true)!;
            if (targets.ContainsKey(normalized))
            {
                return OperationResult<IRobotTargetRegistration>.Failure(
                    ModErrorCode.Conflict,
                    "A target is already registered as '" + normalized + "'.");
            }

            var entry = new TargetEntry(new RobotTargetInfo(normalized, kind), resolve);
            targets[normalized] = entry;
            return OperationResult<IRobotTargetRegistration>.Success(
                new TargetRegistration(this, ownerModId, normalized, entry));
        }

        public bool TryResolveTarget(string name, out RobotTargetSnapshot snapshot)
        {
            return TryResolveTarget(defaultOwnerId, name, out snapshot);
        }

        internal bool TryResolveTarget(string ownerModId, string name, out RobotTargetSnapshot snapshot)
        {
            snapshot = default;
            if (string.IsNullOrWhiteSpace(name)
                || !TryGetOwnerTargets(ownerModId, out var targets)
                || !targets.TryGetValue(Normalize(name), out var entry))
            {
                return false;
            }

            RobotTargetSnapshot? resolved;
            try
            {
                resolved = entry.Resolve();
            }
            catch (Exception exception)
            {
                logger.Warn("Objective target '" + name + "' resolver threw: " + exception.Message);
                return false;
            }

            if (resolved == null)
            {
                return false;
            }

            snapshot = resolved.Value;
            return true;
        }

        object IOwnerBoundExtensionFactory.CreateOwnerFacade(
            Type contractType,
            string ownerModId,
            IModLifetime lifetime)
        {
            if (contractType != typeof(IRobotObjectiveService))
            {
                throw new ArgumentException("Unsupported RobotKit objective extension contract.", nameof(contractType));
            }

            return new OwnerFacade(this, ownerModId, lifetime);
        }

        public IRobotObjectiveHandle SetObjective(IRobotAgent agent, RobotObjective objective)
        {
            if (agent == null)
            {
                return new CancelledObjectiveHandle(objective ?? RobotObjective.Idle());
            }

            var result = SetObjective(defaultOwnerId, agent, objective);
            return result.TryGetValue(out var handle)
                ? handle
                : new CancelledObjectiveHandle(objective ?? RobotObjective.Idle());
        }

        OperationResult<IRobotObjectiveHandle> IRobotObjectiveService.SetObjective(
            IRobotAgent agent,
            RobotObjective objective) =>
            SetObjective(defaultOwnerId, agent, objective);

        internal OperationResult<IRobotObjectiveHandle> SetObjective(
            string ownerModId,
            IRobotAgent agent,
            RobotObjective objective)
        {
            if (agent == null)
            {
                throw new ArgumentNullException(nameof(agent));
            }

            var program = objective ?? throw new ArgumentNullException(nameof(objective));
            if (disposed)
            {
                return OperationResult<IRobotObjectiveHandle>.Failure(
                    ModErrorCode.Unavailable,
                    "RobotKit objective service is unavailable.");
            }

            if (!TryGetAgentId(agent, out var agentId))
            {
                return OperationResult<IRobotObjectiveHandle>.Failure(
                    ModErrorCode.InvalidArgument,
                    "The agent has no stable identifier.");
            }

            var runner = new ObjectiveRunner(
                agent,
                program,
                name => TryResolveTarget(ownerModId, name, out var snapshot) ? snapshot : (RobotTargetSnapshot?)null,
                now,
                random01,
                resolveAgent,
                (recipient, payload) => DeliverProgram(ownerModId, agent, recipient, payload),
                exception => logger.Warn("Robot objective cancellation cleanup failed: " + exception.Message));

            if (runners.TryGetValue(agentId, out var previous))
            {
                TryCancel(previous, "replacing an objective");
                runnerOwners.Remove(agentId);
            }

            if (disposed || !TryIsAlive(agent, out var alive) || !alive)
            {
                TryCancel(runner, "rejecting an objective for an unavailable agent");
                return OperationResult<IRobotObjectiveHandle>.Failure(
                    ModErrorCode.InvalidState,
                    "The robot agent is not alive.");
            }

            runners[agentId] = runner;
            runnerOwners[agentId] = ownerModId;
            return OperationResult<IRobotObjectiveHandle>.Success(runner);
        }

        public IRobotObjectiveHandle? GetObjective(IRobotAgent agent)
        {
            return TryGetObjective(defaultOwnerId, agent, out var objective) ? objective : null;
        }

        bool IRobotObjectiveService.TryGetObjective(
            IRobotAgent agent,
            out IRobotObjectiveHandle? objective) =>
            TryGetObjective(defaultOwnerId, agent, out objective);

        internal bool TryGetObjective(
            string ownerModId,
            IRobotAgent agent,
            out IRobotObjectiveHandle? objective)
        {
            objective = null;
            if (agent == null || !TryGetAgentId(agent, out var agentId)
                || !runners.TryGetValue(agentId, out var runner)
                || !runnerOwners.TryGetValue(agentId, out var owner)
                || !string.Equals(owner, ownerModId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            objective = runner;
            return true;
        }

        public void ClearObjective(IRobotAgent agent)
        {
            ClearObjective(defaultOwnerId, agent);
        }

        OperationResult<bool> IRobotObjectiveService.ClearObjective(IRobotAgent agent) =>
            ClearObjective(defaultOwnerId, agent);

        internal OperationResult<bool> ClearObjective(string ownerModId, IRobotAgent agent)
        {
            if (agent == null)
            {
                throw new ArgumentNullException(nameof(agent));
            }

            if (agent == null || !TryGetAgentId(agent, out var agentId)
                || !runners.TryGetValue(agentId, out var runner)
                || !runnerOwners.TryGetValue(agentId, out var owner)
                || !string.Equals(owner, ownerModId, StringComparison.OrdinalIgnoreCase))
            {
                return OperationResult<bool>.Success(false);
            }

            TryCancel(runner, "clearing an objective");
            runners.Remove(agentId);
            runnerOwners.Remove(agentId);
            return OperationResult<bool>.Success(true);
        }

        // Advance every live objective and drop those whose robot is gone or that were cancelled elsewhere. Call
        // after the agent service's Tick so runners see this frame's reached/moving state.
        public void Tick(float deltaTime)
        {
            if (disposed)
            {
                return;
            }

            clock += deltaTime;

            // Step from a copied buffer: a Reprogram delivery calls SetObjective(recipient, payload) mid-step,
            // which mutates the runner map. A runner replaced mid-tick may still sit in the buffer â€” harmless,
            // cancelled runners return from Step immediately.
            stepBuffer.Clear();
            foreach (var runner in runners.Values)
            {
                stepBuffer.Add(runner);
            }

            foreach (var runner in stepBuffer)
            {
                try
                {
                    if (!runner.IsCancelled && runner.AgentAlive)
                    {
                        runner.Step();
                    }
                }
                catch (Exception exception)
                {
                    logger.Warn("Robot objective tick failed; the faulty objective was cancelled: " + exception.Message);
                    TryCancel(runner, "isolating a failed objective");
                }
            }

            // Prune in a second pass over the live map (cancelled/replaced runners and dead robots).
            List<string>? dead = null;
            foreach (var pair in runners)
            {
                var remove = pair.Value.IsCancelled;
                if (!remove)
                {
                    try
                    {
                        remove = !pair.Value.AgentAlive;
                    }
                    catch (Exception exception)
                    {
                        logger.Warn("Robot objective liveness check failed; the faulty objective was removed: "
                            + exception.Message);
                        remove = true;
                    }
                }

                if (remove)
                {
                    (dead ??= new List<string>()).Add(pair.Key);
                }
            }

            if (dead != null)
            {
                foreach (var id in dead)
                {
                    runners.Remove(id);
                    runnerOwners.Remove(id);
                }
            }
        }

        // Applies a courier's payload to the recipient and announces the hand-over. Runs inside the courier
        // runner's Step (via its delivery callback) â€” hence Tick stepping from a buffer, not the live map.
        private void DeliverProgram(
            string ownerModId,
            IRobotAgent sender,
            IRobotAgent recipient,
            RobotObjective payload)
        {
            var applied = SetObjective(ownerModId, recipient, payload);
            if (!applied.Succeeded)
            {
                logger.Warn(
                    "Could not deliver robot program objective " + payload.Kind + ": " + applied.ErrorMessage);
                return;
            }

            var delivery = new RobotProgramDelivery(sender, recipient, payload);
            SafeEvent.Invoke(
                ProgramDelivered,
                delivery,
                exception => logger.Warn("A ProgramDelivered subscriber threw: " + exception.Message));
            SafeEvent.Invoke(
                ProgramDeliveredOwned,
                new OwnedDelivery(ownerModId, delivery),
                exception => logger.Warn("An owner-bound ProgramDelivered subscriber threw: " + exception.Message));
        }

        public void OnSceneChanged()
        {
            // The robots and the things targets pointed at are gone; objectives are session-only by design.
            foreach (var runner in runners.Values)
            {
                TryCancel(runner, "clearing objectives for a scene change");
            }

            runners.Clear();
            runnerOwners.Clear();
            ClearTargets();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            foreach (var runner in runners.Values)
            {
                TryCancel(runner, "disposing the objective service");
            }

            runners.Clear();
            runnerOwners.Clear();
            ClearTargets();
        }

        private IReadOnlyList<string> GetTargetNames(string ownerModId)
        {
            if (!TryGetOwnerTargets(ownerModId, out var targets))
            {
                return Array.Empty<string>();
            }

            var names = new List<string>(targets.Keys);
            names.Sort(StringComparer.Ordinal);
            return names;
        }

        private IReadOnlyList<RobotTargetInfo> GetTargets(string ownerModId)
        {
            if (!TryGetOwnerTargets(ownerModId, out var targets))
            {
                return Array.Empty<RobotTargetInfo>();
            }

            var infos = new List<RobotTargetInfo>(targets.Count);
            foreach (var entry in targets.Values)
            {
                infos.Add(entry.Info);
            }

            infos.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
            return infos;
        }

        private Dictionary<string, TargetEntry>? GetOwnerTargets(string ownerModId, bool create)
        {
            if (targetsByOwner.TryGetValue(ownerModId, out var targets))
            {
                return targets;
            }

            if (!create)
            {
                return null;
            }

            targets = new Dictionary<string, TargetEntry>(StringComparer.OrdinalIgnoreCase);
            targetsByOwner[ownerModId] = targets;
            return targets;
        }

        private bool TryGetOwnerTargets(
            string ownerModId,
            out Dictionary<string, TargetEntry> targets)
        {
            targets = null!;
            return !string.IsNullOrWhiteSpace(ownerModId)
                && targetsByOwner.TryGetValue(ownerModId, out targets!);
        }

        private void ReleaseTarget(
            string ownerModId,
            string normalizedName,
            TargetEntry expected)
        {
            if (!TryGetOwnerTargets(ownerModId, out var targets)
                || !targets.TryGetValue(normalizedName, out var current)
                || !ReferenceEquals(current, expected))
            {
                expected.Deactivate();
                return;
            }

            targets.Remove(normalizedName);
            expected.Deactivate();
            RemoveEmptyOwnerTargets(ownerModId, targets);
        }

        private void RemoveEmptyOwnerTargets(
            string ownerModId,
            Dictionary<string, TargetEntry> targets)
        {
            if (targets.Count == 0)
            {
                targetsByOwner.Remove(ownerModId);
            }
        }

        private void ClearTargets()
        {
            foreach (var targets in targetsByOwner.Values)
            {
                foreach (var entry in targets.Values)
                {
                    entry.Deactivate();
                }
            }

            targetsByOwner.Clear();
        }

        private static string Normalize(string name)
        {
            return name.Trim().ToUpperInvariant();
        }

        private bool TryGetAgentId(IRobotAgent agent, out string agentId)
        {
            agentId = string.Empty;
            try
            {
                agentId = agent.Id;
            }
            catch (Exception exception)
            {
                logger.Warn("Robot objective rejected an agent whose id getter failed: " + exception.Message);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(agentId))
            {
                return true;
            }

            logger.Warn("Robot objective rejected an agent with a blank id.");
            return false;
        }

        private bool TryIsAlive(IRobotAgent agent, out bool alive)
        {
            alive = false;
            try
            {
                alive = agent.IsAlive;
                return true;
            }
            catch (Exception exception)
            {
                logger.Warn("Robot objective rejected an agent whose liveness getter failed: " + exception.Message);
                return false;
            }
        }

        private void TryCancel(ObjectiveRunner runner, string operation)
        {
            try
            {
                runner.Dispose();
            }
            catch (Exception exception)
            {
                logger.Warn("Robot objective failed while " + operation + ": " + exception.Message);
            }
        }

        private sealed class CancelledObjectiveHandle : IRobotObjectiveHandle
        {
            public CancelledObjectiveHandle(RobotObjective objective)
            {
                Objective = objective;
            }

            public RobotObjective Objective { get; }
            public RobotObjectiveState State => RobotObjectiveState.Cancelled;
            public int WaypointIndex => 0;
            public bool IsActive => false;
            public void Dispose() { }
        }

        private sealed class TargetEntry
        {
            public TargetEntry(RobotTargetInfo info, Func<RobotTargetSnapshot?> resolve)
            {
                Info = info;
                Resolve = resolve;
            }

            public RobotTargetInfo Info { get; }
            public Func<RobotTargetSnapshot?> Resolve { get; }
            public bool IsActive { get; private set; } = true;
            public void Deactivate() => IsActive = false;
        }

        private sealed class TargetRegistration : IRobotTargetRegistration
        {
            private RobotObjectiveService? service;
            private readonly string ownerModId;
            private readonly string normalizedName;
            private readonly TargetEntry entry;

            public TargetRegistration(
                RobotObjectiveService service,
                string ownerModId,
                string normalizedName,
                TargetEntry entry)
            {
                this.service = service;
                this.ownerModId = ownerModId;
                this.normalizedName = normalizedName;
                this.entry = entry;
            }

            public string Name => normalizedName;
            public RobotTargetKind Kind => entry.Info.Kind;
            public bool IsActive => service != null && entry.IsActive;

            public void Dispose()
            {
                Interlocked.Exchange(ref service, null)?.ReleaseTarget(ownerModId, normalizedName, entry);
            }
        }

        private sealed class OwnedDelivery
        {
            public OwnedDelivery(string ownerModId, RobotProgramDelivery delivery)
            {
                OwnerModId = ownerModId;
                Delivery = delivery;
            }

            public string OwnerModId { get; }
            public RobotProgramDelivery Delivery { get; }
        }

        private sealed class OwnerFacade : IRobotObjectiveService
        {
            private readonly RobotObjectiveService service;
            private readonly string ownerModId;
            private readonly IModLifetime lifetime;
            private readonly object eventSync = new object();
            private readonly List<DeliverySubscription> subscriptions = new List<DeliverySubscription>();

            public OwnerFacade(RobotObjectiveService service, string ownerModId, IModLifetime lifetime)
            {
                this.service = service;
                this.ownerModId = ownerModId;
                this.lifetime = lifetime;
            }

            public bool IsAvailable => !lifetime.IsStopping && service.IsAvailable;
            public IReadOnlyList<string> TargetNames => service.GetTargetNames(ownerModId);
            public IReadOnlyList<RobotTargetInfo> Targets => service.GetTargets(ownerModId);

            public event Action<RobotProgramDelivery>? ProgramDelivered
            {
                add
                {
                    if (value == null || lifetime.IsStopping)
                    {
                        return;
                    }

                    var subscription = new DeliverySubscription(service, ownerModId, lifetime, value);
                    lock (eventSync)
                    {
                        subscriptions.Add(subscription);
                    }

                    service.ProgramDeliveredOwned += subscription.Wrapper;
                    try
                    {
                        lifetime.Track(subscription);
                    }
                    catch (ObjectDisposedException)
                    {
                        lock (eventSync)
                        {
                            subscriptions.Remove(subscription);
                        }
                    }
                }
                remove
                {
                    if (value == null)
                    {
                        return;
                    }

                    DeliverySubscription? subscription = null;
                    lock (eventSync)
                    {
                        for (var index = subscriptions.Count - 1; index >= 0; index--)
                        {
                            if (subscriptions[index].Matches(value))
                            {
                                subscription = subscriptions[index];
                                subscriptions.RemoveAt(index);
                                break;
                            }
                        }
                    }

                    subscription?.Dispose();
                }
            }

            public OperationResult<IRobotTargetRegistration> RegisterTarget(
                string name,
                RobotTargetKind kind,
                Func<RobotTargetSnapshot?> resolve)
            {
                if (lifetime.IsStopping)
                    return OperationResult<IRobotTargetRegistration>.Failure(
                        ModErrorCode.InvalidState, "The mod lifetime is stopping.");
                var result = service.RegisterTarget(ownerModId, name, kind, resolve);
                if (!result.TryGetValue(out var registration))
                {
                    return result;
                }

                try
                {
                    return OperationResult<IRobotTargetRegistration>.Success(
                        new OwnerTargetRegistration(registration, lifetime.Track(registration)));
                }
                catch (ObjectDisposedException)
                {
                    return OperationResult<IRobotTargetRegistration>.Failure(
                        ModErrorCode.InvalidState,
                        "The mod lifetime is stopping.");
                }
            }

            public bool TryGetTargetInfo(string name, out RobotTargetInfo info) =>
                service.TryGetTargetInfo(ownerModId, name, out info);
            public bool TryResolveTarget(string name, out RobotTargetSnapshot snapshot) =>
                service.TryResolveTarget(ownerModId, name, out snapshot);

            public OperationResult<IRobotObjectiveHandle> SetObjective(
                IRobotAgent agent,
                RobotObjective objective)
            {
                if (lifetime.IsStopping)
                    return OperationResult<IRobotObjectiveHandle>.Failure(
                        ModErrorCode.InvalidState, "The mod lifetime is stopping.");
                var result = service.SetObjective(ownerModId, agent, objective);
                if (!result.TryGetValue(out var handle))
                {
                    return result;
                }

                try
                {
                    return OperationResult<IRobotObjectiveHandle>.Success(
                        new OwnerObjectiveHandle(handle, lifetime.Track(handle)));
                }
                catch (ObjectDisposedException)
                {
                    return OperationResult<IRobotObjectiveHandle>.Failure(
                        ModErrorCode.InvalidState,
                        "The mod lifetime is stopping.");
                }
            }

            public bool TryGetObjective(IRobotAgent agent, out IRobotObjectiveHandle? objective) =>
                service.TryGetObjective(ownerModId, agent, out objective);

            public OperationResult<bool> ClearObjective(IRobotAgent agent) => lifetime.IsStopping
                ? OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The mod lifetime is stopping.")
                : service.ClearObjective(ownerModId, agent);

            private sealed class OwnerTargetRegistration : IRobotTargetRegistration
            {
                private readonly IRobotTargetRegistration registration;
                private IDisposable? lifetimeLease;

                public OwnerTargetRegistration(
                    IRobotTargetRegistration registration,
                    IDisposable lifetimeLease)
                {
                    this.registration = registration;
                    this.lifetimeLease = lifetimeLease;
                }

                public string Name => registration.Name;
                public RobotTargetKind Kind => registration.Kind;
                public bool IsActive => lifetimeLease != null && registration.IsActive;

                public void Dispose()
                {
                    Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
                }
            }

            private sealed class OwnerObjectiveHandle : IRobotObjectiveHandle
            {
                private readonly IRobotObjectiveHandle handle;
                private IDisposable? lifetimeLease;

                public OwnerObjectiveHandle(IRobotObjectiveHandle handle, IDisposable lifetimeLease)
                {
                    this.handle = handle;
                    this.lifetimeLease = lifetimeLease;
                }

                public RobotObjective Objective => handle.Objective;
                public RobotObjectiveState State => handle.State;
                public int WaypointIndex => handle.WaypointIndex;
                public bool IsActive => lifetimeLease != null && handle.IsActive;

                public void Dispose()
                {
                    Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
                }
            }

            private sealed class DeliverySubscription : IDisposable
            {
                private RobotObjectiveService? service;
                private readonly string ownerModId;
                private readonly Action<RobotProgramDelivery> handler;
                private readonly IModLifetime lifetime;

                public DeliverySubscription(
                    RobotObjectiveService service,
                    string ownerModId,
                    IModLifetime lifetime,
                    Action<RobotProgramDelivery> handler)
                {
                    this.service = service;
                    this.ownerModId = ownerModId;
                    this.handler = handler;
                    this.lifetime = lifetime;
                    Wrapper = OnDelivery;
                }

                public Action<OwnedDelivery> Wrapper { get; }
                public bool Matches(Action<RobotProgramDelivery> candidate) => handler == candidate;

                public void Dispose()
                {
                    var current = Interlocked.Exchange(ref service, null);
                    if (current != null)
                    {
                        current.ProgramDeliveredOwned -= Wrapper;
                    }
                }

                private void OnDelivery(OwnedDelivery delivery)
                {
                    if (service != null && !lifetime.IsStopping
                        && string.Equals(delivery.OwnerModId, ownerModId, StringComparison.OrdinalIgnoreCase))
                    {
                        handler(delivery.Delivery);
                    }
                }
            }
        }
    }
}
