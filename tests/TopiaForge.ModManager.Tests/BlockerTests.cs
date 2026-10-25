using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static class BlockerTests
    {
        private static int assertions;
        private static readonly Vec3 Size = new Vec3(2, 3, .5f);
        internal static void Run()
        {
            assertions = 0;
            GeometryAndFakes();
            HostileDescriptions();
            RoutingAndLifetime();
            ReplicasAndShadows();
            UnityConstruction();
            Console.WriteLine("Blocker tests passed: " + assertions + " assertions.");
        }

        private static void GeometryAndFakes()
        {
            foreach (var value in new[] { float.NaN, float.NegativeInfinity, float.PositiveInfinity, -.1f, 0f, .049f, 1001f })
                foreach (var size in new[] { new Vec3(value, 1, 1), new Vec3(1, value, 1), new Vec3(1, 1, value) })
                    Throws(() => BlockerRequest.Box(Vec3.Zero, size), "invalid side");
            foreach (var value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                Throws(() => BlockerRequest.Box(new Vec3(value, 0, 0), Size), "invalid position");
                Throws(() => BlockerRequest.Box(Vec3.Zero, Size, value), "invalid yaw");
            }
            var request = BlockerRequest.Box(new TransformState(new Vec3(1, 2, 3), Quat.Identity, new Vec3(8, 4, 2)), new Vec3(.05f, 1000, 1));
            Check(request.Placement.Scale == new Vec3(1, 1, 1) && request.BlocksSight, "inclusive bounds and ignored placement scale");
            using var context = new FakeModContext();
            var blocker = context.Physics.AddBlocker(BlockerRequest.Box(Vec3.Zero, new Vec3(8, 1, 1), 90)).Value!;
            Check(context.Physics.Blockers.Count == 1 && blocker.Size == new Vec3(8, 1, 1), "fake records size");
            Check(context.Physics.Overlap(new Bounds(new Vec3(0, 0, 3), new Vec3(1, 1, 1))).Contains(blocker), "overlap sees box edge away from centre");
            Check(!context.Physics.Overlap(new Bounds(new Vec3(3, 0, 0), new Vec3(1, 1, 1))).Contains(blocker), "overlap respects rotation");
            Check(context.Entities.SetTransform(blocker, new TransformState(new Vec3(20, 0, 0), Quat.Identity, new Vec3(1, 1, 1))).Succeeded,
                "fake blocker moves through entity service");
            Check(context.Physics.Overlap(new Bounds(new Vec3(23, 0, 0), new Vec3(1, 1, 1)), 1).Single() == blocker, "moved blocker overlaps at new position");
            var transparent = context.Physics.AddBlocker(BlockerRequest.Box(Vec3.Zero, Size, blocksSight: false)).Value!;
            Check(!context.Physics.Overlap(new Bounds(Vec3.Zero, Size)).Contains(transparent), "Ignore Raycast blocker stays outside ordinary queries");
            context.Dispose();
            Check(!blocker.IsAlive && !transparent.IsAlive, "fake context owns blockers");
            Check(context.Physics.AddBlocker(request).ErrorCode == ModErrorCode.Cancelled, "fake stopping scope refuses");
        }

        private static void HostileDescriptions()
        {
            var request = BlockerRequest.Box(Vec3.Zero, Size, blocksSight: false);
            var payload = BlockerDescriptor.Encode(request);
            Check(payload.Length == 14 && payload[0] == 1 && payload[13] == 0, "14-byte box envelope");
            Check(BlockerDescriptor.TryRead(payload, out var size, out var sight) && size == Size && !sight, "descriptor round trip");
            var scene = new FakeWorldScene();
            var factory = new BlockerReplicaFactory(scene);
            foreach (var length in new[] { 0, 1, 13, 15, 100 }) Reject(new byte[length]);
            var bad = (byte[])payload.Clone(); bad[0] = 0; Reject(bad);
            bad = (byte[])payload.Clone(); bad[13] = 2; Reject(bad);
            foreach (var offset in new[] { 1, 5, 9 })
                foreach (var value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0f, .049f, 1001f })
                {
                    bad = (byte[])payload.Clone();
                    BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(offset), BitConverter.SingleToInt32Bits(value));
                    Reject(bad);
                }
            Check(scene.Blockers.Count == 0, "invalid bytes never reach engine creation");
            var made = factory.CreateReplica(new InternalSpawnDescriptor(BlockerDescriptor.Kind, request.Placement, payload));
            Check(made.Value is IBlocker box && box.Size == Size && box.IsAlive, "factory builds requested collider geometry");
            factory.ReleaseReplica(made.Value!);
            Check(!made.Value!.IsAlive, "factory releases replica");

            void Reject(byte[] bytes)
            {
                Check(!BlockerDescriptor.TryRead(bytes, out _, out _), "hostile descriptor rejected");
                Check(!factory.CreateReplica(new InternalSpawnDescriptor(BlockerDescriptor.Kind, TransformState.Identity, bytes)).Succeeded,
                    "factory refuses hostile descriptor");
                var writer = new WorldWireWriter(1024);
                WorldEntryCodec.WriteSpawn(writer, new NetworkEntityId(1), BlockerDescriptor.Kind, WorldPeer.ModId, 0, 0,
                    new NetVector3(1, 1, 1), new NetPose(default, NetQuaternion.Identity), bytes);
                var reader = new WorldWireReader(); reader.Reset(writer.Segment.AsSpan().ToArray());
                Check(!WorldEntryCodec.TryRead(reader, new WorldEntry()), "wire rejects descriptor before binding a shadow");
            }
        }

        private static void RoutingAndLifetime()
        {
            var rig = new WorldRig();
            var request = BlockerRequest.Box(Vec3.Zero, Size);
            foreach (var role in new[] { NetRealm.Offline, NetRealm.ListenHost, NetRealm.Client })
                foreach (ModNetworkClass networkClass in Enum.GetValues(typeof(ModNetworkClass)))
                    Check(BlockerAuthorityRules.Route(role, new ModNetProfile(networkClass, ModExecutionContext.Participant), IntentSource.None)
                        == ExecutionRoute.NotRun, "participant forbidden in every role and network class");
            using var scope = new OwnerModLifetime();
            var seam = rig.Host.Seam(ModExecutionContext.GamemodeServer, scope);
            var physics = Physics(rig.Host, seam, scope);
            var made = physics.AddBlocker(request).Value!;
            var record = rig.Host.Runtime.Spawns.Live().Single();
            Check(record.Description.Kind == BlockerDescriptor.Kind && record.ShadowKey == 0, "server blocker ledger entry is canonical, without shadow numbering");
            Check(record.Description.Descriptor is InternalSpawnDescriptor description && description.Payload.Length == 14, "ledger holds only descriptor data");
            scope.Dispose();
            Check(!made.IsAlive && rig.Host.Runtime.Spawns.Live().Count == 0, "scope end destroys blocker and ledger record");
            Check(physics.AddBlocker(request).ErrorCode == ModErrorCode.Cancelled, "stopping scope refused");
            using var participant = new OwnerModLifetime();
            Check(Physics(rig.Host, rig.Host.Seam(ModExecutionContext.Participant, participant), participant).AddBlocker(request).ErrorCode
                == ModErrorCode.NotAuthoritative, "participant call refused offline");

            using (var sceneScope = new OwnerModLifetime())
            {
                var scenePhysics = Physics(rig.Host, rig.Host.Seam(lifetime: sceneScope), sceneScope);
                var destroyed = (FakeWorldBlocker)scenePhysics.AddBlocker(request).Value!;
                destroyed.Object.Destroy();
                scenePhysics.AddBlocker(request);
                Check(destroyed.DisposeCalls == 1, "engine destruction still releases the old handle's resources");
            }

            using var a = new OwnerModLifetime(); using var b = new OwnerModLifetime();
            var first = Physics(rig.Host, rig.Host.Seam(lifetime: a), a);
            var second = Physics(rig.Host, rig.Host.Seam(lifetime: b), b);
            IBlocker? last = null;
            for (var index = 0; index < 1024; index++) last = (index % 2 == 0 ? first : second).AddBlocker(request).Value!;
            Check(first.AddBlocker(request).ErrorCode == ModErrorCode.RateLimited && second.AddBlocker(request).ErrorCode == ModErrorCode.RateLimited,
                "live quota is shared across a mod's scopes");
            last!.Dispose();
            Check(first.AddBlocker(request).Succeeded, "disposing frees quota");
        }

        private static void ReplicasAndShadows()
        {
            var rig = new WorldRig(); rig.OpenToMultiplayer();
            var player = rig.AddPlayer("p1"); rig.JoinAndSynchronize(player);
            using var hostScope = new OwnerModLifetime(); using var playerScope = new OwnerModLifetime();
            var request = BlockerRequest.Box(new Vec3(0, 1, 0), Size);
            var host = Physics(rig.Host, rig.Host.Seam(lifetime: hostScope), hostScope);
            var joined = Physics(player, player.Seam(lifetime: playerScope), playerScope);
            var made = host.AddBlocker(request).Value!;
            Check(rig.Run(() => player.Scene.Blockers.Any(box => box.IsAlive), 120), "built-in replica factory is installed without a provider or bundle");
            var replica = player.Scene.Blockers.Single(box => box.IsAlive);
            Check(replica.Size == Size && replica.Object.HasRigidbody && replica.Object.Kinematic, "replica has requested box and kinematic body");
            var shadow = joined.AddBlocker(request).Value!;
            Check(shadow.IsAlive && !replica.IsAlive && player.World.Counters.ShadowsBound == 1, "scripted joined call binds as shadow");
            var mod = new AuthorityMod(player);
            Check(mod.AsPlayer(() => joined.AddBlocker(request)).ErrorCode == ModErrorCode.NotAuthoritative,
                "joined player's own action is refused");
            using var participant = new OwnerModLifetime();
            Check(Physics(rig.Host, rig.Host.Seam(ModExecutionContext.Participant, participant), participant).AddBlocker(request).ErrorCode
                == ModErrorCode.NotAuthoritative, "host participant is refused too");
            Check(player.Runtime.Spawns.Live().Count == 0 && player.World.Counters.AuthorityRequestsSent == 0, "no client canonical record or prediction request");
            rig.Frames(300);
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            rig.Frames(500);
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Check(allocated == 0, "steady state allocates zero bytes: " + allocated);
            hostScope.Dispose();
            Check(rig.Run(() => !shadow.IsAlive, 120) && !made.IsAlive, "scope end despawns bound shadow");
        }

        private static NetPhysicsService Physics(WorldPeer peer, RuntimeReplication seam, IModLifetime lifetime) =>
            new NetPhysicsService(new BoxPhysics(peer.Scene), seam, lifetime);

        private sealed class BoxPhysics : IPhysicsService
        {
            private readonly IBlockerBackend backend;
            internal BoxPhysics(IBlockerBackend backend) { this.backend = backend; }
            public OperationResult<IBlocker> AddBlocker(BlockerRequest request) => backend.CreateBlocker(request);
            public bool TryRaycast(Ray ray, float distance, out PhysicsHit? hit) { hit = null; return false; }
            public bool TrySphereCast(Ray ray, float radius, float distance, out PhysicsHit? hit) { hit = null; return false; }
            public IReadOnlyList<IEntity> Overlap(Bounds bounds, int maximumResults = 64) => Array.Empty<IEntity>();
        }

        private static void UnityConstruction()
        {
            var root = Program.FindRepoRoot();
            var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "src/TopiaForge.ModManager/UnityBlocker.cs"))).GetRoot();
            var components = syntax.DescendantNodes().OfType<GenericNameSyntax>().Where(name => name.Identifier.ValueText == "AddComponent")
                .Select(name => name.TypeArgumentList.Arguments.Single().ToString()).OrderBy(name => name).ToArray();
            Check(components.SequenceEqual(new[] { "BoxCollider", "Rigidbody" }), "Unity blocker has a box and body, no renderer anywhere");
            var assignments = syntax.DescendantNodes().OfType<AssignmentExpressionSyntax>().Select(node => node.WithoutTrivia().ToString()).ToArray();
            Check(assignments.Contains("box.size = UnityPhysicsBackend.ToUnity(request.Size)") && assignments.Contains("body.isKinematic = true"),
                "Unity collider size and kinematic body are set");
            Check(assignments.Contains("root.layer = request.BlocksSight ? 0 : 2"), "Unity uses Default or Ignore Raycast");
            foreach (var file in new[] { "UnityWorldServices.cs", "Multiplayer/World/Unity/UnityNetworkWorldBackend.cs" })
                Check(File.ReadAllText(Path.Combine(root, "src/TopiaForge.ModManager", file)).Contains("UnityBlocker.Create(entities, request)", StringComparison.Ordinal),
                    "canonical and replica paths use the same Unity construction");
        }

        private static void Throws(Action action, string message)
        {
            try { action(); } catch (ArgumentException) { Check(true, message); return; }
            Check(false, message);
        }
        private static void Check(bool condition, string message)
        { assertions++; if (!condition) throw new InvalidOperationException("Blocker test failed: " + message); }
    }
}
