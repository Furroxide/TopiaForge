using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    // Deliberately retain retired callbacks/handles to emulate an already queued service completion.
    // These are independent offline observations, not proof of native rendering, sound or device input.
    internal sealed class SandboxGraphTestAudio : IAudioService
    {
        public List<Playback> Handles { get; } = new List<Playback>();
        public int ActiveCount => Handles.Count(handle => !handle.Disposed);
        public string FaultCue { get; set; } = string.Empty;
        public OperationResult<IAudioPlayback> Play(AudioPlayRequest request)
        {
            if (request.CueId == FaultCue) throw new InvalidOperationException("Seeded graph audio failure.");
            var playback = new Playback(request.CueId);
            Handles.Add(playback);
            return OperationResult<IAudioPlayback>.Success(playback);
        }

        internal sealed class Playback : IAudioPlayback
        {
            public Playback(string cue) { Cue = cue; }
            public string Cue { get; }
            public bool Disposed { get; private set; }
            public bool Completed { get; private set; }
            public bool ThrowOnStop { get; set; }
            public bool ThrowOnDispose { get; set; }
            public Action? OnDispose { get; set; }
            public int StopCalls { get; private set; }
            public int DisposeCalls { get; private set; }
            public bool IsPlaying => !Disposed && !Completed;
            public void Complete() { Completed = true; }
            public void Stop()
            {
                StopCalls++;
                if (ThrowOnStop) throw new InvalidOperationException("Seeded playback stop failure.");
                Completed = true;
            }
            public void Dispose()
            {
                DisposeCalls++;
                Disposed = true;
                var callback = OnDispose;
                OnDispose = null;
                callback?.Invoke();
                if (ThrowOnDispose) throw new InvalidOperationException("Seeded playback dispose failure.");
            }
        }
    }

    internal sealed class SandboxGraphTestInteractions : IInteractionService
    {
        public List<Registration> Handles { get; } = new List<Registration>();
        public int ActiveCount => Handles.Count(handle => handle.IsActive);
        public OperationResult<IInteractableRegistration> Register(
            IEntity entity, InteractableDefinition definition, Action<InteractionEvent> handler)
        {
            var handle = new Registration(entity, handler);
            Handles.Add(handle);
            return OperationResult<IInteractableRegistration>.Success(handle);
        }
        public bool TryGetFocused(out IInteractableRegistration? interaction)
        {
            interaction = null;
            return false;
        }
        internal sealed class Registration : IInteractableRegistration
        {
            private readonly Action<InteractionEvent> callback;
            public Registration(IEntity entity, Action<InteractionEvent> callback)
            {
                Entity = entity;
                this.callback = callback;
            }
            public IEntity Entity { get; }
            public bool IsActive => !Disposed && Entity.IsAlive;
            public bool Disposed { get; private set; }
            public bool ThrowOnDispose { get; set; }
            public int DisposeCalls { get; private set; }
            public Action? OnDispose { get; set; }
            public void DeliverQueuedCallback() => callback(new InteractionEvent(
                Entity, new PlayerSnapshot(Vec3.Zero, new Ray(Vec3.Zero, new Vec3(0f, 0f, 1f)))));
            public void Dispose()
            {
                DisposeCalls++;
                Disposed = true;
                var callback = OnDispose;
                OnDispose = null;
                callback?.Invoke();
                if (ThrowOnDispose) throw new InvalidOperationException("Seeded interaction dispose failure.");
            }
        }
    }

    internal sealed class SandboxGraphTestConversations : IRobotConversationService
    {
        public List<Conversation> Handles { get; } = new List<Conversation>();
        public bool IsAvailable => true;
        public int ActiveCount => Handles.Count(handle => !handle.IsEnded);
        public OperationResult<IRobotConversation> BeginConversation(RobotConversationRequest request)
        {
            var conversation = new Conversation(request.MaxTurns);
            Handles.Add(conversation);
            return OperationResult<IRobotConversation>.Success(conversation);
        }
        internal sealed class Conversation : IRobotConversation
        {
            public Conversation(int maxTurns) { MaxTurns = maxTurns; }
            public TaskCompletionSource<OperationResult<RobotConversationTurnResult>> Completion { get; } =
                new TaskCompletionSource<OperationResult<RobotConversationTurnResult>>();
            public bool IsEnded { get; private set; }
            public int TurnCount => Completion.Task.IsCompletedSuccessfully ? 1 : 0;
            public int MaxTurns { get; }
            public int SubmitCalls { get; private set; }
            public int DisposeCalls { get; private set; }
            public bool ThrowOnDispose { get; set; }
            public Action? OnDispose { get; set; }
            public Task<OperationResult<RobotConversationTurnResult>> SubmitAsync(
                string playerText, CancellationToken cancellationToken = default)
            {
                SubmitCalls++;
                return Completion.Task;
            }
            public void Complete(string decision = "CHAT") => Completion.TrySetResult(
                OperationResult<RobotConversationTurnResult>.Success(new RobotConversationTurnResult(
                    "Synthetic offline response", decision, new Dictionary<string, string>())));
            public void Dispose()
            {
                DisposeCalls++;
                IsEnded = true;
                var callback = OnDispose;
                OnDispose = null;
                callback?.Invoke();
                if (ThrowOnDispose) throw new InvalidOperationException("Seeded conversation dispose failure.");
            }
        }
    }

    internal sealed class RobotFixtureEntities : IEntityService
    {
        private readonly IEntityService inner;
        public RobotFixtureEntities(IEntityService inner) { this.inner = inner; }
        public bool TryGetTransform(IEntity entity, out TransformState transform)
        {
            if (entity is FakeRobotAgent robot)
            {
                transform = new TransformState(robot.Position, Quat.Identity, new Vec3(robot.Scale, robot.Scale, robot.Scale));
                return robot.IsAlive;
            }
            return inner.TryGetTransform(entity, out transform);
        }
        public OperationResult<TransformState> SetTransform(IEntity entity, TransformState transform)
        {
            if (!(entity is FakeRobotAgent)) return inner.SetTransform(entity, transform);
            // The graph fixture spawns at this exact transform. No simulated transform mutation is claimed here.
            return TryGetTransform(entity, out var observed) && observed.Equals(transform)
                ? OperationResult<TransformState>.Success(observed)
                : OperationResult<TransformState>.Failure(ModErrorCode.Unavailable,
                    "This fixture admits only a robot's existing identity-rotation transform.");
        }
        public IReadOnlyList<IEntity> Query(EntityQuery query) => inner.Query(query);
        public OperationResult<bool> Destroy(IEntity entity) => inner.Destroy(entity);
        public OperationResult<IEntityMotion> AcquireMotion(IEntity entity) => inner.AcquireMotion(entity);
    }

    internal sealed class SandboxGraphTestContext : IModContext
    {
        public SandboxGraphTestContext(FakeModContext inner, IAudioService audio, IInteractionService interactions)
        {
            this.inner = inner;
            Audio = audio;
            Interactions = interactions;
            Entities = new RobotFixtureEntities(inner.Entities);
        }
        private readonly IModContext inner;
        public IAudioService Audio { get; }
        public IInteractionService Interactions { get; }
        public ModIdentity Identity => inner.Identity;
        public IRuntimeInfo Runtime => inner.Runtime;
        public IModLogger Logger => inner.Logger;
        public IModLifetime Lifetime => inner.Lifetime;
        public IModEvents Events => inner.Events;
        public IModFiles Files => inner.Files;
        public IModConfigService Config => inner.Config;
        public ILocalModStorageService LocalStorage => inner.LocalStorage;
        public IInputService Input => inner.Input;
        public IGameTime Time => inner.Time;
        public IModScheduler Scheduler => inner.Scheduler;
        public ILocalPlayerService LocalPlayer => inner.LocalPlayer;
        public ISceneService Scenes => inner.Scenes;
        public IEntityService Entities { get; }
        public IPhysicsService Physics => inner.Physics;
        public IItemService Items => inner.Items;
        public IAssetService Assets => inner.Assets;
        public IUiService Ui => inner.Ui;
        public ILocalizationService Localization => inner.Localization;
        public ICommandService Commands => inner.Commands;
        public IDiagnosticsService Diagnostics => inner.Diagnostics;
        public IExtensionService Extensions => inner.Extensions;
    }
}
