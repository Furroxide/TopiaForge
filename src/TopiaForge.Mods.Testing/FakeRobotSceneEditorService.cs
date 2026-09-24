using System;
using System.Collections.Generic;
using System.Linq;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Deterministic robot-edit leases with separately inspectable scene state; no native-game claims.</summary>
    public sealed class FakeRobotSceneEditorService : IRobotSceneEditorService
    {
        private readonly FakeModLifetime lifetime;
        private readonly List<FakeRobotEditTarget> targets = new List<FakeRobotEditTarget>();
        private readonly HashSet<FakeRobotEditTarget> edited = new HashSet<FakeRobotEditTarget>();

        /// <summary>Creates an editor owned by the supplied fake lifetime.</summary>
        public FakeRobotSceneEditorService(FakeModLifetime lifetime) =>
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        /// <inheritdoc/>
        public bool IsAvailable { get; set; } = true;
        /// <inheritdoc/>
        public IReadOnlyList<IRobotEditTarget> Targets => targets.Where(target => target.IsAlive).ToArray();
        /// <summary>Gets the number of exclusive edit leases currently held.</summary>
        public int ActiveLeaseCount => edited.Count;
        /// <summary>Gets the number of live temporary personality instances.</summary>
        public int ActiveTemporaryPersonalityCount { get; private set; }
        /// <summary>Adds a fixture target. Duplicate ids and shared target objects are rejected.</summary>
        public void AddTarget(FakeRobotEditTarget target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (targets.Any(existing => existing.Id == target.Id)) throw new ArgumentException("Duplicate robot target id.", nameof(target));
            targets.Add(target);
        }
        /// <inheritdoc/>
        public bool TryResolve(IRobotAgent agent, out IRobotEditTarget? target)
        {
            target = targets.FirstOrDefault(candidate => candidate.IsAlive && ReferenceEquals(candidate.Agent, agent));
            return target != null;
        }
        /// <inheritdoc/>
        public OperationResult<IRobotEditLease> BeginTemporaryEdit(IRobotEditTarget target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (!IsAvailable || lifetime.IsStopping)
                return OperationResult<IRobotEditLease>.Failure(ModErrorCode.Unavailable, "Fake robot editor unavailable.");
            if (!(target is FakeRobotEditTarget fixture) || !targets.Contains(fixture) || !fixture.IsAlive)
                return OperationResult<IRobotEditLease>.Failure(ModErrorCode.NotFound, "Robot target is not live in this editor.");
            if (!edited.Add(fixture))
                return OperationResult<IRobotEditLease>.Failure(ModErrorCode.Conflict, "Robot already has an exclusive edit lease.");
            var lease = new Lease(this, fixture);
            return lifetime.TrackResult<IRobotEditLease>(lease, lease.Attach, "Robot editor lifetime stopped.");
        }

        private sealed class Lease : IRobotEditLease
        {
            private readonly FakeRobotSceneEditorService owner;
            private readonly FakeRobotEditTarget target;
            private readonly TransformState originalTransform;
            private readonly RobotBrainMode originalBrain;
            private readonly RobotPersonalityDraft? originalPersonality;
            private TransformState? lastTransform;
            private RobotBrainMode? lastBrain;
            private RobotPersonalityDraft? temporaryPersonality;
            private IDisposable? lifetimeLease;
            private bool active = true;
            private bool disposeCalled;

            public Lease(FakeRobotSceneEditorService owner, FakeRobotEditTarget target)
            {
                this.owner = owner;
                this.target = target;
                originalTransform = target.Transform;
                originalBrain = target.BrainMode;
                originalPersonality = target.Personality;
            }
            public void Attach(IDisposable lease) => lifetimeLease = lease;
            public IRobotEditTarget Target => target;
            public bool IsActive => active && target.IsAlive;
            private OperationResult<T>? Failure<T>() where T : notnull => !active
                ? OperationResult<T>.Failure(ModErrorCode.InvalidState, "Robot edit lease ended.")
                : !target.IsAlive ? OperationResult<T>.Failure(ModErrorCode.NotFound, "Robot target disappeared.")
                : target.PreviewErrorCode != ModErrorCode.None
                    ? OperationResult<T>.Failure(target.PreviewErrorCode, "Injected robot preview failure.") : null;
            public OperationResult<TransformState> PreviewTransform(TransformState transform)
            {
                var failure = Failure<TransformState>();
                if (failure != null) return failure;
                lastTransform = target.Transform = transform;
                return OperationResult<TransformState>.Success(transform);
            }
            public OperationResult<bool> PreviewBrainMode(RobotBrainMode mode)
            {
                if (!Enum.IsDefined(typeof(RobotBrainMode), mode))
                    return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Unknown robot brain mode.");
                var failure = Failure<bool>();
                if (failure != null) return failure;
                lastBrain = target.BrainMode = mode;
                return OperationResult<bool>.Success(true);
            }
            public OperationResult<bool> PreviewPersonality(RobotPersonalityDraft personality)
            {
                if (personality == null) throw new ArgumentNullException(nameof(personality));
                var failure = Failure<bool>();
                if (failure != null) return failure;
                if (temporaryPersonality == null) owner.ActiveTemporaryPersonalityCount++;
                temporaryPersonality = new RobotPersonalityDraft(personality.DisplayName, personality.Instructions, personality.Temperature);
                target.Personality = temporaryPersonality;
                return OperationResult<bool>.Success(true);
            }
            public OperationResult<bool> Restore()
            {
                if (!active) return OperationResult<bool>.Success(false);
                active = false;
                var conflict = false;
                if (target.IsAlive)
                {
                    if (temporaryPersonality != null)
                    {
                        if (ReferenceEquals(target.Personality, temporaryPersonality)) target.Personality = originalPersonality;
                        else conflict = true;
                    }
                    if (lastBrain.HasValue)
                    {
                        if (target.BrainMode == lastBrain.Value) target.BrainMode = originalBrain;
                        else conflict = true;
                    }
                    if (lastTransform.HasValue)
                    {
                        var current = target.Transform;
                        var last = lastTransform.Value;
                        var position = current.Position == last.Position;
                        var rotation = current.Rotation == last.Rotation;
                        var scale = current.Scale == last.Scale;
                        target.Transform = new TransformState(position ? originalTransform.Position : current.Position,
                            rotation ? originalTransform.Rotation : current.Rotation, scale ? originalTransform.Scale : current.Scale);
                        conflict |= !position || !rotation || !scale;
                    }
                }
                if (temporaryPersonality != null) owner.ActiveTemporaryPersonalityCount--;
                temporaryPersonality = null;
                owner.edited.Remove(target);
                var registration = lifetimeLease;
                lifetimeLease = null;
                registration?.Dispose();
                if (target.ThrowOnRestore) throw new InvalidOperationException("Injected robot restore exception.");
                return conflict ? OperationResult<bool>.Failure(ModErrorCode.Conflict, "Robot changed outside Creator Tools; conflicting properties were not overwritten.")
                    : OperationResult<bool>.Success(true);
            }
            public void Dispose()
            {
                if (disposeCalled) return;
                disposeCalled = true;
                Restore();
                if (target.ThrowOnDispose) throw new InvalidOperationException("Injected robot dispose exception.");
            }
        }
    }

    /// <summary>Mutable fixture state observed independently from workbench commands and lease results.</summary>
    public sealed class FakeRobotEditTarget : IRobotEditTarget
    {
        /// <summary>Creates a scene target, optionally associated with a managed agent.</summary>
        public FakeRobotEditTarget(string id, TransformState transform, IRobotAgent? agent = null)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A robot id is required.", nameof(id));
            Id = id;
            DisplayName = id;
            Transform = transform;
            Agent = agent;
            IsNativeSceneObject = agent == null;
        }
        /// <inheritdoc/>
        public string Id { get; }
        /// <inheritdoc/>
        public string DisplayName { get; set; }
        /// <inheritdoc/>
        public string SceneName { get; set; } = "RobotopiaCity";
        /// <inheritdoc/>
        public bool IsAlive { get; set; } = true;
        /// <inheritdoc/>
        public bool IsNativeSceneObject { get; set; }
        /// <summary>Gets the optional managed agent whose identity resolves to this target.</summary>
        public IRobotAgent? Agent { get; }
        /// <summary>Gets or sets scene transform, including simulated external writes.</summary>
        public TransformState Transform { get; set; }
        /// <summary>Gets or sets the simulated brain mode.</summary>
        public RobotBrainMode BrainMode { get; set; } = RobotBrainMode.Autonomous;
        /// <summary>Gets or sets the personality object identity and immutable fields.</summary>
        public RobotPersonalityDraft? Personality { get; set; }
        /// <summary>Gets or sets the error returned before any preview writes.</summary>
        public ModErrorCode PreviewErrorCode { get; set; }
        /// <summary>Gets or sets whether restoration throws after releasing its owned state.</summary>
        public bool ThrowOnRestore { get; set; }
        /// <summary>Gets or sets whether disposal throws after releasing its owned state.</summary>
        public bool ThrowOnDispose { get; set; }
        /// <inheritdoc/>
        public bool TryGetTransform(out TransformState transform)
        {
            transform = Transform;
            return IsAlive;
        }
    }
}
