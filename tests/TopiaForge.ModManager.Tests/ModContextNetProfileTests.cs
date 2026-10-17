using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.Seam;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The factory seam (WS3.3): every mod context carries a network profile, its mod's network class from the effective
    /// manifest and its execution context; the gameplay host receives it; scopes take their session's execution context;
    /// and every context's entity, asset, item and UI services pass through the network decorators bound to its seam.
    /// </summary>
    internal static class ModContextNetProfileTests
    {
        internal static void Run(string root)
        {
            Directory.CreateDirectory(root);
            PackageContextsCarryTheirModsNetworkClass(Path.Combine(root, "packages"));
            ScopesTakeTheirSessionsExecutionContext(Path.Combine(root, "scopes"));
            SessionScopesRunWhereTheSessionSays(Path.Combine(root, "session"));
            ContextsShareTheRuntimesSeam(Path.Combine(root, "shared"));
        }

        private static void PackageContextsCarryTheirModsNetworkClass(string root)
        {
            var unsupported = Manifest("io.example.legacy");
            unsupported.SchemaVersion = ModManifest.ManifestV5SchemaVersion;
            foreach (var (manifest, expected) in new (ModManifest, ModNetworkClass)[]
                     {
                         (Manifest("io.example.plain"), ModNetworkClass.Shared),
                         (Manifest("io.example.lobby", ModMultiplayerMetadata.SessionMode), ModNetworkClass.Shared),
                         (Manifest("io.example.hats", ModMultiplayerMetadata.ClientLocalMode), ModNetworkClass.ClientLocal),
                         (Manifest("io.example.rules", ModMultiplayerMetadata.ServerOnlyMode), ModNetworkClass.ServerOnly),
                         (Manifest("io.example.solo", ModMultiplayerMetadata.StandaloneMode), ModNetworkClass.Standalone),
                         (Manifest("io.example.future", "peer-to-peer"), ModNetworkClass.Standalone),
                         (unsupported, ModNetworkClass.Standalone)
                     })
            {
                var factory = new ProfileFactory();
                var context = Context(Path.Combine(root, manifest.Id), manifest, factory);
                var profile = new ModNetProfile(expected, ModExecutionContext.Process);
                That(context.NetProfile.Equals(profile), manifest.Id + " runs as " + profile + " (" + context.NetProfile + ")");
                That(factory.Made.Single().Profile.Equals(profile) && factory.Made.Single().Owner == manifest.Id && !factory.Made.Single().Scoped,
                    manifest.Id + "'s gameplay host receives the same profile");
                var replication = ((IInternalReplicationContext)context).Replication;
                That(replication.NetworkClass == (InternalNetworkClass)(int)expected && replication.ExecutionContext == InternalExecutionContext.Process &&
                     ReferenceEquals(replication, context.NetReplication), manifest.Id + "'s replication seam reports it to first-party providers");
                ExpectDecorated(context, factory);
                context.DisposeLifetime();
            }
        }

        private static void ScopesTakeTheirSessionsExecutionContext(string root)
        {
            using var host = new HostDispatcher();
            var factory = new ProfileFactory();
            var parent = Context(root, Manifest("io.example.sandbox"), factory);
            foreach (var context in Contexts)
            {
                var scope = Scope(parent, "session-" + context, host, context);
                var expected = new ModNetProfile(ModNetworkClass.Shared, context);
                That(scope.Context.NetProfile.Equals(expected) && factory.Made[^1].Profile.Equals(expected) && factory.Made[^1].Scoped,
                    "a scope made as " + context + " runs as " + expected + ", and its gameplay host is told so");
                That(((IInternalReplicationContext)scope.Context).Replication.ExecutionContext == (InternalExecutionContext)(int)context,
                    "its replication seam reports " + context);
                ExpectDecorated(scope.Context, factory);
                That(ReferenceEquals(((ProbeAssets)((NetAssetService)scope.Context.Assets).Inner).Parent, ((NetAssetService)parent.Assets).Inner),
                    "its asset service still takes its package's own as parent");
                scope.BeginStop();
                scope.Dispose();
            }

            // A content or discovery scope runs where its caller does, a scope of a scope included.
            var inherited = Scope(parent, "discovery", host, null);
            That(inherited.Context.NetProfile.Context == ModExecutionContext.Process, "a scope without a context runs where its parent does");
            var server = Scope(parent, "server", host, ModExecutionContext.GamemodeServer);
            var content = Scope(server.Context, "content", host, null);
            That(content.Context.NetProfile.Equals(new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.GamemodeServer)),
                "a content scope of a gamemode server is a gamemode server");
            var before = parent.ActiveChildScopeCount;
            Throws<ArgumentOutOfRangeException>(() => Scope(parent, "undefined", host, (ModExecutionContext)9), "an undefined context is refused");
            That(parent.ActiveChildScopeCount == before, "before any scope is made");
            foreach (var scope in new[] { content, server, inherited })
            {
                scope.BeginStop();
                scope.Dispose();
            }

            // A client-local mod's scopes stay client-local.
            var cosmetic = Context(root + "-hats", Manifest("io.example.hats", ModMultiplayerMetadata.ClientLocalMode), new ProfileFactory());
            var hatScope = Scope(cosmetic, "hats", host, ModExecutionContext.WorldOwner);
            That(hatScope.Context.NetProfile.Equals(new ModNetProfile(ModNetworkClass.ClientLocal, ModExecutionContext.WorldOwner)), "the class is the mod's");
            hatScope.BeginStop();
            hatScope.Dispose();
            cosmetic.DisposeLifetime();
            parent.DisposeLifetime();
        }

        private static void SessionScopesRunWhereTheSessionSays(string root)
        {
            That(GamemodeSessionOrchestrator.ScopeContextFor("io.example.zombies", "io.example.zombies") == ModExecutionContext.GamemodeServer,
                "the gamemode's package runs the factory's server");
            That(GamemodeSessionOrchestrator.ScopeContextFor("IO.Example.Sandbox", "io.example.sandbox") == ModExecutionContext.GamemodeServer,
                "whatever the case of its id");
            That(GamemodeSessionOrchestrator.ScopeContextFor("io.example.arena", "io.example.zombies") == ModExecutionContext.WorldOwner,
                "a world from another package is its world owner");
            That(GamemodeSessionOrchestrator.ScopeContextFor("io.example.pack", "io.example.zombies") == ModExecutionContext.WorldOwner,
                "and so is a launch target's owner");

            // A real launch: the fixture's one package provides the world and the gamemode, so its scope is the gamemode server.
            using var fixture = new SessionFixture(root);
            fixture.Launch();
            var expected = new ModNetProfile(fixture.Parent.NetProfile.NetworkClass, ModExecutionContext.GamemodeServer);
            That(fixture.ScopeProfiles.Count == 1 && fixture.ScopeProfiles[0].Equals(expected),
                "the launched session's scope is made as " + expected + " (" + string.Join(", ", fixture.ScopeProfiles) + ")");
            var session = SessionFixture.ModeSession!;
            That(session.Context is IInternalReplicationContext seam && seam.Replication.ExecutionContext == InternalExecutionContext.GamemodeServer,
                "and the gamemode's controller sees it through its context");
        }

        private static void ContextsShareTheRuntimesSeam(string root)
        {
            var log = new RecordingRuntimeLog();
            var control = new FakeMultiplayerControl();
            var runtime = new MultiplayerRuntime(() => control, new MultiplayerRuntimeHost("2545", "0.2.0", log), log);
            runtime.Connect();
            var services = new MultiplayerReplication(runtime);
            var factory = new ProfileFactory();
            var paths = new ManagerPaths(root);
            paths.EnsureCreated();
            var context = new ModContext(Manifest("io.example.blocks"), paths, Path.Combine(root, "package"), new OwnerFacadeStoppingTests.Logger(),
                new ModServiceRegistry(), null, factory, null, services);
            That(ReferenceEquals(context.NetReplication.Services, services), "the context binds to the services it was given");
            var spawned = context.Assets.Spawn(new AssetSpawnRequest(new SeamPrefab(), TransformState.Identity));
            var records = runtime.Spawns.Live();
            That(spawned.Succeeded && records.Count == 1 && records[0].Entity == spawned.Value && records[0].Description.OwnerModId == "io.example.blocks",
                "a mod's spawn through its context is recorded in the runtime's own ledger (" + records.Count + " records)");
            var replication = ((IInternalReplicationContext)context).Replication;
            control.Role = InternalSessionRole.ListenHost;
            Frame(runtime);
            That(replication.Realm == InternalNetRealm.ListenHost, "and the context sees the runtime's realm");
            control.Role = InternalSessionRole.Solo;
            Frame(runtime);
            That(replication.Realm == InternalNetRealm.Offline, "as it changes");
            context.DisposeLifetime();
        }

        private static void ExpectDecorated(ModContext context, ProfileFactory factory)
        {
            var made = factory.Made[^1].Services;
            That(context.Assets is NetAssetService assets && ReferenceEquals(assets.Inner, made.Assets) && ReferenceEquals(assets.Replication, context.NetReplication),
                "the asset service is the decorator around the host's, bound to the context's seam");
            That(context.Items is NetItemService items && ReferenceEquals(items.Inner, made.Items) && ReferenceEquals(items.Replication, context.NetReplication),
                "so is the item service");
            That(context.Ui is NetUiService ui && ReferenceEquals(ui.Inner, made.Ui) && ReferenceEquals(ui.Replication, context.NetReplication), "and the UI service");
            var entities = (ProbeEntities)made.Entities;
            var calls = entities.Queries;
            context.Entities.Query(new EntityQuery(maximumResults: 1));
            That(entities.Queries == calls + 1, "and the entity service reaches the host's through its decorator");
        }

        private static ModContext Context(string root, ModManifest manifest, ProfileFactory factory)
        {
            var paths = new ManagerPaths(root);
            paths.EnsureCreated();
            return new ModContext(manifest, paths, Path.Combine(root, "package"), new OwnerFacadeStoppingTests.Logger(), new ModServiceRegistry(), null, factory);
        }

        private static ModContextScope Scope(ModContext parent, string session, HostDispatcher host, ModExecutionContext? context)
        {
            var creation = parent.CreateChildScopeAsync(session, CancellationToken.None, () => { },
                new NativeTransitionAccessSlot(session + ":" + parent.Identity.Id, session, () => !parent.Lifetime.IsStopping), host, context);
            HostDispatcherTests.Pump(host, creation);
            return creation.GetAwaiter().GetResult();
        }

        /// <summary>A gameplay host that records the profile each context was made with.</summary>
        private sealed class ProfileFactory : IGameplayContextFactory
        {
            internal List<(string Owner, ModNetProfile Profile, bool Scoped, GameplayContextServices Services)> Made { get; } =
                new List<(string, ModNetProfile, bool, GameplayContextServices)>();

            public GameplayContextServices Create(string ownerModId, string packagePath, string dataPath, IModLifetime lifetime, IModLogger logger,
                ModNetProfile profile, NativeTransitionAccessSlot? transitionAccess = null)
            {
                var other = GameplayContextServices.Unavailable(lifetime);
                var services = new GameplayContextServices(other.Input, other.LocalPlayer, new ProbeEntities(), other.Physics, other.Time, other.Scheduler,
                    other.Scenes, other.Interactions, other.Items, new ProbeAssets(), other.Audio, other.Ui, other.UnityInterop, other.SceneTransitions);
                Made.Add((ownerModId, profile, transitionAccess != null, services));
                return services;
            }
        }

        private sealed class ProbeEntities : IEntityService
        {
            internal int Queries { get; private set; }

            public bool TryGetTransform(IEntity entity, out TransformState transform)
            {
                transform = TransformState.Identity;
                return false;
            }

            public OperationResult<TransformState> SetTransform(IEntity entity, TransformState transform) =>
                OperationResult<TransformState>.Failure(ModErrorCode.Unavailable, "probe");

            public IReadOnlyList<IEntity> Query(EntityQuery query)
            {
                Queries++;
                return Array.Empty<IEntity>();
            }

            public OperationResult<bool> Destroy(IEntity entity) => OperationResult<bool>.Failure(ModErrorCode.Unavailable, "probe");

            public OperationResult<IEntityMotion> AcquireMotion(IEntity entity) => OperationResult<IEntityMotion>.Failure(ModErrorCode.Unavailable, "probe");
        }

        private sealed class ProbeAssets : IAssetService, IParentAssetScope
        {
            internal IAssetService? Parent { get; private set; }

            public void AttachParent(IAssetService parent) => Parent = parent;

            public Task<OperationResult<IAssetBundle>> LoadBundleAsync(string relativePath, CancellationToken cancellationToken = default) =>
                Task.FromResult(OperationResult<IAssetBundle>.Failure(ModErrorCode.Unavailable, "probe"));

            public Task<OperationResult<IPrefabAsset>> LoadPrefabAsync(IAssetBundle bundle, string assetName, CancellationToken cancellationToken = default) =>
                Task.FromResult(OperationResult<IPrefabAsset>.Failure(ModErrorCode.Unavailable, "probe"));

            public OperationResult<ISpawnedEntity> Spawn(AssetSpawnRequest request) => OperationResult<ISpawnedEntity>.Success(new SeamEntity());
        }
    }
}
