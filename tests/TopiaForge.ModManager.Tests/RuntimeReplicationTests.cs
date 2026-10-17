using System;
using System.Linq;
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
    /// The internal replication SPI (WS3.3) as first-party providers use it: it mirrors the execution model's rules,
    /// replica factories are one a kind, canonical registration records at spawn time, spawn requests run here until
    /// authority routing, and a stopping context or one that does not run in the realm is refused.
    /// </summary>
    internal static class RuntimeReplicationTests
    {
        private static readonly ModNetProfile Process = new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Process);

        internal static void Run(string root)
        {
            TheSpiMirrorsTheRuntimesRules();
            RealmRouteAndTimeFollowTheExecutionModel();
            ReplicaFactoriesAreOneAKind();
            CanonicalRegistrationRecordsAtSpawnTime();
            SpawnRequestsRunHereUntilAuthorityRouting();
            StoppingAndAbsentContextsAreRefused();
            RuntimeReplicationServiceTests.Run();
        }

        private static void TheSpiMirrorsTheRuntimesRules()
        {
            Mirror<InternalNetRealm, NetRealm>();
            Mirror<InternalNetworkClass, ModNetworkClass>();
            Mirror<InternalExecutionContext, ModExecutionContext>();
            Mirror<InternalIntent, IntentSource>();
            Mirror<InternalRoute, ExecutionRoute>();
            Mirror<InternalTimeAuthority, TimeControlRoute>();
            Mirror<InternalSpawnSource, MultiplayerSpawnSource>();

            static void Mirror<TSpi, TRuntime>() where TSpi : struct, Enum where TRuntime : struct, Enum
            {
                var spi = Enum.GetValues(typeof(TSpi)).Cast<object>().Select(value => (Name: value.ToString(), Value: Convert.ToInt32(value))).ToArray();
                var runtime = Enum.GetValues(typeof(TRuntime)).Cast<object>().Select(value => (Name: value.ToString(), Value: Convert.ToInt32(value))).ToArray();
                That(spi.SequenceEqual(runtime), typeof(TSpi).Name + " mirrors " + typeof(TRuntime).Name + " name for name and value for value");
            }
        }

        private static void RealmRouteAndTimeFollowTheExecutionModel()
        {
            var (services, control, runtime) = Services();
            var lifetime = new OwnerModLifetime();
            foreach (var (role, realm) in new[]
                     {
                         (InternalSessionRole.Solo, NetRealm.Offline), (InternalSessionRole.ListenHost, NetRealm.ListenHost), (InternalSessionRole.Client, NetRealm.Client)
                     })
            {
                control.Role = role;
                Frame(runtime);
                foreach (var networkClass in Classes)
                {
                    foreach (var context in Contexts)
                    {
                        var profile = new ModNetProfile(networkClass, context);
                        var seam = services.Bind("io.example.rules", profile, lifetime);
                        That(seam.Realm == (InternalNetRealm)(int)realm && seam.NetworkClass == (InternalNetworkClass)(int)networkClass &&
                             seam.ExecutionContext == (InternalExecutionContext)(int)context, profile + " in " + realm + " reports where it runs");
                        foreach (IntentSource intent in Enum.GetValues(typeof(IntentSource)))
                        {
                            That(seam.Route((InternalIntent)(int)intent) == (InternalRoute)(int)ExecutionRouting.Route(realm, profile, intent),
                                profile + " in " + realm + " for " + intent + " routes by the table");
                        }

                        That(seam.TimeAuthority == (InternalTimeAuthority)(int)ExecutionRouting.TimeControl(realm, profile) &&
                             seam.NativePauseFreezesWorld == (realm == NetRealm.Offline), profile + " in " + realm + " has the time rules");
                    }
                }
            }

            // A few answers spelled out, so the table itself is checked too.
            var server = services.Bind("io.example.zombies", new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.GamemodeServer), lifetime);
            var world = services.Bind("io.example.arena", new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.WorldOwner), lifetime);
            var tools = services.Bind("io.example.sandbox", new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Participant), lifetime);
            var hats = services.Bind("io.example.hats", new ModNetProfile(ModNetworkClass.ClientLocal, ModExecutionContext.GamemodeServer), lifetime);
            control.Role = InternalSessionRole.ListenHost;
            Frame(runtime);
            That(server.Route(InternalIntent.None) == InternalRoute.Canonical && server.TimeAuthority == InternalTimeAuthority.Apply, "the host's gamemode server is canonical and controls time");
            That(world.TimeAuthority == InternalTimeAuthority.NotAuthoritative && hats.TimeAuthority == InternalTimeAuthority.NotAuthoritative,
                "online, a world owner and a client-local gamemode server control nobody's time");
            control.Role = InternalSessionRole.Client;
            Frame(runtime);
            That(world.Route(InternalIntent.None) == InternalRoute.Shadow && world.Route(InternalIntent.UiCallback) == InternalRoute.Forwarded &&
                 tools.Route(InternalIntent.None) == InternalRoute.Forwarded && server.Route(InternalIntent.OwnerCommand) == InternalRoute.NotRun &&
                 hats.Route(InternalIntent.None) == InternalRoute.NotRun, "a joined player shadows scripts, forwards intent and never runs a gamemode server");
            Throws<ArgumentOutOfRangeException>(() => world.Route((InternalIntent)17), "an undefined intent is refused");
        }

        private static void ReplicaFactoriesAreOneAKind()
        {
            var (services, _, _) = Services();
            var lifetime = new OwnerModLifetime();
            var creator = services.Bind("io.example.creator", Process, lifetime);
            var rival = services.Bind("io.example.rival", Process, new OwnerModLifetime());
            var factory = new Factory();
            var lease = creator.RegisterReplicaFactory("creator.content", factory);
            That(lease.Succeeded && services.TryGetReplicaFactory("creator.content", out var found) && ReferenceEquals(found, factory),
                "a provider registers how replicas of its kind are built");
            var taken = rival.RegisterReplicaFactory("creator.content", new Factory());
            That(taken.ErrorCode == ModErrorCode.Conflict && services.TryGetReplicaFactory("creator.content", out found) && ReferenceEquals(found, factory),
                "a kind has one factory in the process");
            foreach (var kind in new[] { "Creator", "", "creator content", new string('k', 65) })
            {
                That(creator.RegisterReplicaFactory(kind, new Factory()).ErrorCode == ModErrorCode.InvalidArgument, "'" + kind + "' is not a kind");
            }

            foreach (var kind in new[] { MultiplayerSpawnKinds.AssetPrefab, "asset.mesh" })
            {
                That(creator.RegisterReplicaFactory(kind, new Factory()).ErrorCode == ModErrorCode.InvalidArgument, kind + " is the runtime's own");
            }

            lease.Value!.Dispose();
            lease.Value.Dispose();
            That(!services.TryGetReplicaFactory("creator.content", out _), "the lease removes it, once");
            That(rival.RegisterReplicaFactory("creator.content", new Factory()).Succeeded, "after which another provider may take the kind");
            var second = creator.RegisterReplicaFactory("creator.prop", new Factory());
            lifetime.Dispose();
            That(second.Succeeded && !services.TryGetReplicaFactory("creator.prop", out _), "a factory ends with its context's lifetime");
            That(factory.Calls == 0, "stub: nothing builds a replica until the network world does");
            Throws<ArgumentNullException>(() => rival.RegisterReplicaFactory(null!, new Factory()), "a kind is required");
            Throws<ArgumentNullException>(() => rival.RegisterReplicaFactory("creator.box", null!), "and a factory");
        }

        private static void CanonicalRegistrationRecordsAtSpawnTime()
        {
            var (services, control, runtime) = Services();
            var robots = services.Bind("io.example.robotkit", Process, new OwnerModLifetime());
            var worlds = services.Bind("io.example.worlds", new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.WorldOwner), new OwnerModLifetime());
            var robot = new SeamEntity();
            var platform = new SeamEntity();
            var descriptor = new InternalSpawnDescriptor("robotkit.robot", new TransformState(new Vec3(1, 0, 1), Quat.Identity, new Vec3(1, 1, 1)), new byte[] { 7, 8 });
            That(robots.RegisterCanonical(robot, descriptor).Succeeded, "offline, a provider registers what it spawned");
            var content = new InternalSpawnDescriptor("worlds.arena", TransformState.Identity, null, InternalSpawnSource.WorldContent, "arena/platform#0");
            That(worlds.RegisterCanonical(platform, content).Succeeded, "and a world provider its content");
            var records = runtime.Spawns.Live();
            That(records.Count == 2 && records[0].Entity == robot && records[0].Description.Kind == "robotkit.robot" &&
                 ReferenceEquals(records[0].Description.Descriptor, descriptor) && records[0].Description.OwnerModId == "io.example.robotkit" &&
                 records[0].Description.Transform.Equals(descriptor.Transform) && records[0].Description.Source == MultiplayerSpawnSource.SdkSpawn,
                "the runtime records the spawn at once, with its description");
            That(records[1].Description.Source == MultiplayerSpawnSource.WorldContent && records[1].Description.ContentKey == "arena/platform#0" &&
                 records[1].Description.Profile.Context == ModExecutionContext.WorldOwner, "world content keeps its key");

            // A crate spawned through the asset service and then registered keeps its place, with the provider's description.
            var crate = new SeamEntity();
            var assets = services.Bind("io.example.creator", Process, new OwnerModLifetime());
            assets.RecordAssetSpawn(crate, new AssetSpawnRequest(new SeamPrefab(), TransformState.Identity));
            var sequence = runtime.Spawns.Find(crate)!.Sequence;
            That(assets.RegisterCanonical(crate, new InternalSpawnDescriptor("creator.content", TransformState.Identity)).Succeeded &&
                 runtime.Spawns.Find(crate)!.Sequence == sequence && runtime.Spawns.Find(crate)!.Description.Kind == "creator.content",
                "registering what the asset service recorded describes it in place");

            var dead = new SeamEntity { IsAlive = false };
            That(robots.RegisterCanonical(dead, descriptor).ErrorCode == ModErrorCode.InvalidState && runtime.Spawns.Find(dead) == null, "an entity that is gone is refused");
            That(robots.RegisterCanonical(new SeamEntity(), new InternalSpawnDescriptor(MultiplayerSpawnKinds.AssetPrefab, TransformState.Identity)).ErrorCode ==
                 ModErrorCode.InvalidArgument, "the runtime's own kinds are not a provider's to claim");
            var hats = services.Bind("io.example.hats", new ModNetProfile(ModNetworkClass.ClientLocal, ModExecutionContext.Process), new OwnerModLifetime());
            var hat = new SeamEntity();
            That(hats.RegisterCanonical(hat, new InternalSpawnDescriptor("hats.hat", TransformState.Identity)).Succeeded && runtime.Spawns.Find(hat) == null,
                "a client-local mod's registration is kept local");

            control.Role = InternalSessionRole.ListenHost;
            Frame(runtime);
            var late = new SeamEntity();
            That(robots.RegisterCanonical(late, descriptor).Succeeded && runtime.Spawns.Find(late) != null, "a host records too, until the network world announces");
            robot.IsAlive = false;
            That(!runtime.Spawns.Live().Any(record => record.Entity == robot), "a record ends with its entity");
            Throws<ArgumentNullException>(() => robots.RegisterCanonical(null!, descriptor), "an entity is required");
            Throws<ArgumentNullException>(() => robots.RegisterCanonical(new SeamEntity(), null!), "and a descriptor");
        }

        private static void SpawnRequestsRunHereUntilAuthorityRouting()
        {
            var (services, control, runtime) = Services();
            var robots = services.Bind("io.example.robotkit", Process, new OwnerModLifetime());
            var descriptor = new InternalSpawnDescriptor("robotkit.robot", TransformState.Identity);
            var spawns = 0;
            var robot = new SeamEntity();
            var success = OperationResult<IEntity>.Success(robot);
            var result = robots.RequestSpawn(descriptor, () => { spawns++; return success; });
            That(ReferenceEquals(result, success) && spawns == 1 && runtime.Spawns.Find(robot) != null, "offline, the spawn runs here once and is recorded");
            var refused = OperationResult<IEntity>.Failure(ModErrorCode.RateLimited, "too many robots");
            That(ReferenceEquals(robots.RequestSpawn(descriptor, () => refused), refused) && runtime.Spawns.Live().Count == 1, "a failed spawn comes back as it is");
            var stillborn = new SeamEntity { IsAlive = false };
            robots.RequestSpawn(descriptor, () => OperationResult<IEntity>.Success(stillborn));
            That(runtime.Spawns.Find(stillborn) == null, "an entity that is gone at once is not recorded");
            var failure = new InvalidOperationException("the provider's own failure");
            try
            {
                robots.RequestSpawn(descriptor, () => throw failure);
                That(false, "a spawn that throws throws");
            }
            catch (InvalidOperationException thrown)
            {
                That(ReferenceEquals(thrown, failure), "the provider's own exception comes back unchanged");
            }

            Throws<InvalidOperationException>(() => robots.RequestSpawn(descriptor, () => null!), "a spawn that returns nothing is a fault");
            control.Role = InternalSessionRole.Client;
            Frame(runtime);
            var predicted = new SeamEntity();
            That(robots.RequestSpawn(descriptor, () => OperationResult<IEntity>.Success(predicted)).Succeeded && runtime.Spawns.Find(predicted) != null,
                "stub: a joined player's spawn still runs here, as in single player, until authority routing forwards it");
            var server = services.Bind("io.example.zombies", new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.GamemodeServer), new OwnerModLifetime());
            var ran = false;
            That(server.RequestSpawn(descriptor, () => { ran = true; return success; }).ErrorCode == ModErrorCode.NotAuthoritative && !ran,
                "a gamemode server on a joined player spawns nothing");
            Throws<ArgumentNullException>(() => robots.RequestSpawn(null!, () => success), "a descriptor is required");
            Throws<ArgumentNullException>(() => robots.RequestSpawn(descriptor, null!), "and a spawn");
        }

        private static void StoppingAndAbsentContextsAreRefused()
        {
            var (services, control, runtime) = Services();
            var lifetime = new OwnerModLifetime();
            var seam = services.Bind("io.example.robotkit", Process, lifetime);
            lifetime.BeginStop();
            var spawned = false;
            var descriptor = new InternalSpawnDescriptor("robotkit.robot", TransformState.Identity);
            That(seam.RegisterReplicaFactory("robotkit.robot", new Factory()).ErrorCode == ModErrorCode.Cancelled &&
                 seam.RegisterCanonical(new SeamEntity(), descriptor).ErrorCode == ModErrorCode.Cancelled &&
                 seam.RequestSpawn(descriptor, () => { spawned = true; return OperationResult<IEntity>.Success(new SeamEntity()); }).ErrorCode == ModErrorCode.Cancelled &&
                 seam.RegisterCommand("robotkit.say", _ => OperationResult<bool>.Success(true)).ErrorCode == ModErrorCode.Cancelled &&
                 seam.SubmitCommand("robotkit.say", Array.Empty<byte>()).ErrorCode == ModErrorCode.Cancelled &&
                 seam.AcquireMicrophone(InternalMicrophoneUse.RobotDialogue, "Talking to Sam").ErrorCode == ModErrorCode.Cancelled && !spawned &&
                 runtime.Spawns.Count == 0, "a stopping context is refused everything, and nothing runs");

            var solo = services.Bind("io.example.solo", new ModNetProfile(ModNetworkClass.Standalone, ModExecutionContext.Process), new OwnerModLifetime());
            control.Role = InternalSessionRole.ListenHost;
            Frame(runtime);
            That(solo.RegisterCanonical(new SeamEntity(), descriptor).ErrorCode == ModErrorCode.NotAuthoritative &&
                 solo.SubmitCommand("solo.do", Array.Empty<byte>()).ErrorCode == ModErrorCode.NotAuthoritative &&
                 solo.AcquireMicrophone(InternalMicrophoneUse.RobotDialogue, "Talking").ErrorCode == ModErrorCode.NotAuthoritative,
                "a standalone mod changes nothing online");
            var live = services.Bind("io.example.robotkit", Process, new OwnerModLifetime());
            var offThread = Task.Run(() => live.RegisterCanonical(new SeamEntity(), descriptor));
            Throws<InvalidOperationException>(() => offThread.GetAwaiter().GetResult(), "the seam belongs to the main thread");
        }

        internal static (MultiplayerReplication Services, FakeMultiplayerControl Control, MultiplayerRuntime Runtime) Services()
        {
            var (runtime, control, _) = Runtime(() => SessionPhase.Running);
            runtime.Connect();
            return (new MultiplayerReplication(runtime), control, runtime);
        }

        private sealed class Factory : IInternalReplicaFactory
        {
            internal int Calls { get; private set; }

            public OperationResult<IEntity> CreateReplica(InternalSpawnDescriptor descriptor)
            {
                Calls++;
                return OperationResult<IEntity>.Success(new SeamEntity());
            }
        }
    }
}
