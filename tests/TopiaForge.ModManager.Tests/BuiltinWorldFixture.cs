using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal sealed class BuiltinWorldFixture : IWorldLoadContext, IModContext, IInternalWorldRuntimeContext, IInternalSceneTransitionContext, IInternalWorldSessionContext, IInternalSessionContentContext, IDisposable, IWorldDiscoveryContext, IInternalMapStorageContext
    {
        internal readonly FakeModContext Fake = new FakeModContext();
        internal readonly NativeFixture Native = new NativeFixture();
        public string SessionId => "session-built-in";
        public string TargetId => "example.worlds.target";
        public string WorldId { get; set; } = "example.worlds.world";
        public string? WorldFamilyId { get; set; }
        public string FamilyId { get; set; } = "io.github.furroxide.topiaforge.worlds.map";
        public int MaximumResults { get; set; } = 4096;
        public string MapCacheDirectory { get; set; } = string.Empty;
        public string PublishedMapLibraryPath { get; set; } = string.Empty;
        public WorldLoadTransition Transition { get; set; } = WorldLoadTransition.SceneReplacement;
        public WorldSpawnPolicy SpawnPolicy { get; set; } = new WorldSpawnPolicy(WorldSpawnKind.AuthoredMarker, "Spawn");
        public IModContext Context => this;
        public IInternalWorldRuntimeService WorldRuntime => Native;
        public IInternalSceneTransitionService SceneTransitions { get; set; } = null!;
        public IWorldSessionService Sessions => throw new NotSupportedException();
        public IInternalWorldSessionOperations ContentOperations { get; set; } = null!;
        public WorldReadiness World => new WorldReadiness(Native.Scene, TransformState.Identity);
        public ModIdentity Identity => Fake.Identity;
        public IRuntimeInfo Runtime => Fake.Runtime;
        internal IModLifetime? LifetimeOverride;
        public IModLifetime Lifetime => LifetimeOverride ?? Fake.Lifetime;
        public IModEvents Events => Fake.Events;
        public IModFiles Files => Fake.Files;
        public IModConfigService Config => Fake.Config;
        public ILocalModStorageService LocalStorage => Fake.LocalStorage;
        public IInputService Input => Fake.Input;
        public ILocalPlayerService LocalPlayer => Fake.LocalPlayer;
        public ISceneService Scenes => Fake.Scenes;
        public IEntityService Entities => Fake.Entities;
        public IPhysicsService Physics => Fake.Physics;
        public IInteractionService Interactions => Fake.Interactions;
        public IItemService Items => Fake.Items;
        public IAssetService Assets => Fake.Assets;
        public IAudioService Audio => Fake.Audio;
        public IUiService Ui => Fake.Ui;
        public ILocalizationService Localization => Fake.Localization;
        public ICommandService Commands => Fake.Commands;
        public IDiagnosticsService Diagnostics => Fake.Diagnostics;
        public IExtensionService Extensions => Fake.Extensions;
        public IGameTime Time => Fake.Time;
        public IModScheduler Scheduler => Fake.Scheduler;
        public IModLogger Logger => Fake.Logger;
        public void Dispose() => Fake.Dispose();

        internal sealed class NativeFixture : IInternalWorldRuntimeService, IInternalWorldPreparation
        {
            internal readonly TaskCompletionSource<OperationResult<WorldReadiness>> Completion = new TaskCompletionSource<OperationResult<WorldReadiness>>();
            internal NativeWorldLoadRequest? Request;
            internal IEntity? Content;
            internal WorldSpawnPolicy? Policy;
            internal int DisposeCount;
            internal bool ThrowOnDispose;
            internal CancellationToken PrepareToken;
            internal CancellationToken FinishToken;
            internal TransformState? ProviderSpawn;
            internal Action? OnPrepare;
            public WorldSceneIdentity Scene { get; } = new WorldSceneIdentity(42, "ActualLoadedScene");
            public TransformState? NativeDefaultSpawn => TransformState.Identity;
            public Task<OperationResult<IReadOnlyList<NativeWorldEntry>>> DiscoverAsync(NativeWorldSource source, int maximumResults, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Built-in worlds never run discovery.");
            public Task<OperationResult<IInternalWorldPreparation>> PrepareAsync(NativeWorldLoadRequest request, CancellationToken cancellationToken)
            {
                Request = request; PrepareToken = cancellationToken;
                OnPrepare?.Invoke();
                return Task.FromResult(OperationResult<IInternalWorldPreparation>.Success(this));
            }
            public Task<OperationResult<WorldReadiness>> FinishAsync(WorldSpawnPolicy policy, IEntity? contentRoot,
                TransformState? providerSpawn, CancellationToken cancellationToken)
            {
                Content = contentRoot; Policy = policy; FinishToken = cancellationToken; ProviderSpawn = providerSpawn;
                return Completion.Task;
            }
            internal void Succeed() => Completion.SetResult(OperationResult<WorldReadiness>.Success(new WorldReadiness(Scene,
                new TransformState(new Vec3(3, 4, 5), Quat.Identity, new Vec3(1, 1, 1)))));
            public void Dispose()
            {
                DisposeCount++;
                if (ThrowOnDispose) throw new InvalidOperationException("native cleanup failed");
            }
        }
    }
}
