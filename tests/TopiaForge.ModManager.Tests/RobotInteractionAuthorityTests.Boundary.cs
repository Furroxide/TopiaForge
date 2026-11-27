namespace TopiaForge.ModManager.Tests
{
    internal static partial class RobotInteractionAuthorityTests
    {
        private const string Boundary = """
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using UnityEngine;
namespace TopiaForge.RobotKit
{
    // This adapter delegates to the production RobotReplication instance in the enclosing runtime fixture.
    // It only bridges the separately compiled engine boundary; it makes no authority or normalization decisions.
    internal sealed class RobotReplication
    {
        internal Func<IRuntimeReplication, RobotAgentSpawnRequest, Func<bool>, OperationResult<IRobotAgent>> SpawnCore = null!;
        internal OperationResult<IRobotAgent> Spawn(IRuntimeReplication replication, RobotAgentSpawnRequest request,
            Func<bool> canRouteDisposal) => SpawnCore(replication, request, canRouteDisposal);
    }
    internal sealed partial class RobotAgentService
    {
        internal RobotReplication? Replication;
        internal NativeRobot? Last;
        internal IRobotAgentService ForOwner(IModContext context) => new OwnerFacade(this, context);
        internal int NativeReads;
        public bool IsAvailable { get { NativeReads++; return true; } }
        public bool IsNavigationAvailable { get { NativeReads++; return true; } }
        public IReadOnlyList<RobotTypeDescriptor> RobotTypes
        { get { NativeReads++; return Array.Empty<RobotTypeDescriptor>(); } }
        internal IRobotAgent? FindAgentByEntity(IEntity entity)
        { NativeReads++; return entity.IsAlive && Last?.Id == entity.Id ? Last : null; }
        public bool TryGetPlayerEntity(out IEntity? entity) { NativeReads++; entity = null; return false; }
        public OperationResult<IRobotAgent> Spawn(RobotAgentSpawnRequest request) =>
            throw new InvalidOperationException("This fixture must exercise production replication.");
        public Task<OperationResult<ReachableSpawnResult>> FindReachableSpawnAsync(ReachableSpawnRequest request, CancellationToken token)
        {
            NativeReads++;
            return Task.FromResult(OperationResult<ReachableSpawnResult>.Failure(ModErrorCode.Unavailable, "No navigation boundary."));
        }
    }
    internal sealed class NativeRobot : IRobotAgent, IInternalSpeechAgent
    {
        private readonly IRobotAgent inner;
        private readonly GameObject go = new();
        private readonly IModLogger logger = new CapturedModLogger();
        private RobotInteractionBridge? interactionBridge;
        internal readonly RobotAgentSpawnRequest Request;
        internal RobotInteractionOptions Interaction;
        internal int Calls, Disposals, AliveReads;
        internal bool EarlySelectable;
        internal RobotInteractionBridge Bridge => interactionBridge ?? throw new InvalidOperationException("Custom interaction bridge missing.");
        internal readonly Transform Hand = new() { position = new Vector3(1, 0, 0) };
        internal NativeRobot(IRobotAgent inner, RobotAgentSpawnRequest request, string? id)
        {
            this.inner = inner; Id = id ?? inner.Id; Request = request; Interaction = request.Interaction;
            if (request.Interaction.CustomInteraction is { } custom)
            {
                InstallCustomInteractionBridge(custom);
                EarlySelectable = Bridge.CanInteract(Hand);
                Bridge.OnInteractAttempt(Hand, CancellationToken.None);
            }
        }
        /* production installation */
        public string Id { get; }
        public string Name => inner.Name;
        public bool IsAlive { get { AliveReads++; return inner.IsAlive; } }
        public Vec3 Position => inner.Position;
        public Vec3 HeadPosition => inner.HeadPosition;
        public RobotBrainMode BrainMode => inner.BrainMode;
        public bool IsMoving => inner.IsMoving;
        public bool HasReachedTarget => inner.HasReachedTarget;
        public float MoveSpeed => inner.MoveSpeed;
        public float TurnSpeed => inner.TurnSpeed;
        public float StopDistance => inner.StopDistance;
        public RobotGait Gait => inner.Gait;
        public OperationResult<bool> SetBrainMode(RobotBrainMode mode) { Calls++; return inner.SetBrainMode(mode); }
        public OperationResult<bool> ConfigureMovement(RobotMovementSettings settings) { Calls++; return inner.ConfigureMovement(settings); }
        public OperationResult<bool> MoveTo(Vec3 position) { Calls++; return inner.MoveTo(position); }
        public OperationResult<bool> Chase(IEntity target) { Calls++; return inner.Chase(target); }
        public OperationResult<bool> Stop() { Calls++; return inner.Stop(); }
        public OperationResult<bool> SetTint(RobotColor color) { Calls++; return inner.SetTint(color); }
        public OperationResult<bool> SetEmote(string value) { Calls++; return inner.SetEmote(value); }
        public OperationResult<bool> SetName(string value) { Calls++; return inner.SetName(value); }
        public OperationResult<bool> SetScale(float value) { Calls++; return inner.SetScale(value); }
        public OperationResult<bool> SetInteraction(RobotInteractionOptions options)
        {
            Calls++; Interaction = options;
            if (interactionBridge != null) interactionBridge.enabled = false;
            interactionBridge = null;
            if (options.CustomInteraction is { } custom) InstallCustomInteractionBridge(custom);
            return inner.SetInteraction(options);
        }
        public OperationResult<bool> ApplyDamage(float amount, RobotDamageType type, string source)
        { Calls++; return inner.ApplyDamage(amount, type, source); }
        public OperationResult<bool> Kill(RobotDamageType type, string source) { Calls++; return inner.Kill(type, source); }
        public OperationResult<bool> Ragdoll() { Calls++; return inner.Ragdoll(); }
        public OperationResult<bool> Knockback(Vec3 impulse) { Calls++; return inner.Knockback(impulse); }
        public OperationResult<bool> Despawn() { Calls++; return inner.Despawn(); }
        public Task<OperationResult<bool>> SayAsync(string text, CancellationToken token)
        { Calls++; return Task.FromResult(OperationResult<bool>.Success(true)); }
        public void Dispose() { Disposals++; inner.Dispose(); }
    }
}
internal sealed class ProbeReadScope : IInternalNativeEntityReadScope
{
    private readonly int mainThreadId = Thread.CurrentThread.ManagedThreadId;
    internal readonly IModLifetime Lifetime;
    internal bool Current = true;
    internal int Checks;
    internal ProbeReadScope(IModLifetime lifetime) { Lifetime = lifetime; }
    public int SceneInstanceId => 1;
    public string SceneName => "Yard";
    public bool IsCurrent(IModLifetime caller)
    {
        Checks++;
        return Thread.CurrentThread.ManagedThreadId == mainThreadId && Current &&
            ReferenceEquals(caller, Lifetime) && !caller.IsStopping;
    }
    public bool IsCurrentWorld(IInternalNativeEntityReadContext caller) => Current;
}
internal sealed class ProbeContext : IModContext, IInternalReplicationContext, IInternalNativeEntityReadContext, IDisposable
{
    private readonly FakeModContext backing = new();
    internal ProbeReadScope Scope { get; private set; }
    internal int Captures;
    public IRuntimeReplication Replication { get; }
    public IModLifetime Lifetime { get; }
    internal ProbeContext(IModLifetime lifetime, IRuntimeReplication replication)
    { Lifetime = lifetime; Replication = replication; Scope = new ProbeReadScope(lifetime); }
    internal void ReplaceWorld()
    {
        Scope.Current = false;
        Scope = new ProbeReadScope(Lifetime);
    }
    public IInternalNativeEntityReadScope? CaptureNativeEntityReadScope() { Captures++; return Scope; }
    public ModIdentity Identity => backing.Identity;
    public IRuntimeInfo Runtime => backing.Runtime;
    public IModLogger Logger => backing.Logger;
    public IModEvents Events => backing.Events;
    public IModFiles Files => backing.Files;
    public IModConfigService Config => backing.Config;
    public ILocalModStorageService LocalStorage => backing.LocalStorage;
    public IInputService Input => backing.Input;
    public IGameTime Time => backing.Time;
    public IModScheduler Scheduler => backing.Scheduler;
    public ILocalPlayerService LocalPlayer => backing.LocalPlayer;
    public ISceneService Scenes => backing.Scenes;
    public IEntityService Entities => backing.Entities;
    public IPhysicsService Physics => backing.Physics;
    public IInteractionService Interactions => backing.Interactions;
    public IItemService Items => backing.Items;
    public IAssetService Assets => backing.Assets;
    public IAudioService Audio => backing.Audio;
    public IUiService Ui => backing.Ui;
    public ILocalizationService Localization => backing.Localization;
    public ICommandService Commands => backing.Commands;
    public IDiagnosticsService Diagnostics => backing.Diagnostics;
    public IExtensionService Extensions => backing.Extensions;
    public void Dispose() => backing.Dispose();
}
""";
    }
}
