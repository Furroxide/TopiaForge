using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using TopiaForge.RobotKit;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class RobotBodyTests
    {
        private static int assertions;
        private static readonly AvatarAppearance Appearance = new AvatarAppearance(new RgbaColor(0.8f, 0.1f, 0.2f),
            new RgbaColor(0.2f, 0.3f, 0.4f), "", AvatarAccessorySlot.Glasses);
        private static RobotBodyRequest Request(float x = 3) => new RobotBodyRequest(Appearance, new Vec3(x, 2, 1));

        internal static void Run()
        {
            WireAndContract();
            AuthorityAndLimits();
            ConstructionRollbackAndProviderLimit();
            LifetimeAndCatchup();
            ProviderRecovery();
            CachedFacadeWorldRecovery();
            RobotComposedCurrentnessTests.Run();
            ViewTargets();
            TestingKit();
            NativeConstructionContract();
            Console.WriteLine("Persistent body tests passed (" + assertions + " assertions).");
        }

        private static void Check(bool value, string message)
        { assertions++; if (!value) throw new InvalidOperationException("Persistent body: " + message); }

        private static void WireAndContract()
        {
            foreach (var accessory in new string?[] { null, "", "Catalog hat", new string('a', 64) })
            {
                var appearance = new AvatarAppearance(Appearance.Plastic, Appearance.Metal, accessory);
                var request = new RobotBodyRequest(appearance, new Vec3(1, 2, 3), new Quat(0, 1, 0, 0));
                var descriptor = RobotBodyWire.Describe(request);
                var copy = RobotBodyWire.Read(descriptor);
                Check(copy.Appearance.Equals(appearance) && copy.Position.Equals(request.Position) && copy.Rotation.Equals(request.Rotation), "payload roundtrip");
                Check(descriptor.Payload.Length <= RobotBodyWire.MaximumBytes, "bounded payload");
                for (var length = 0; length < descriptor.Payload.Length; length++)
                {
                    var malformed = new InternalSpawnDescriptor(RobotBodyWire.Kind, descriptor.Transform, descriptor.Payload.Slice(0, length).ToArray());
                    using var provider = new RobotBodyService(_ => throw new Exception("must not construct"));
                    Check(provider.CreateReplica(malformed).ErrorCode == ModErrorCode.InvalidArgument, "truncated data refused before construction");
                }
            }
            var good = RobotBodyWire.Describe(Request());
            var mutations = new List<byte[]>();
            var bytes = good.Payload.ToArray(); bytes[0] = 9; mutations.Add(bytes);
            bytes = good.Payload.ToArray(); bytes[1] = 0; mutations.Add(bytes);
            bytes = good.Payload.ToArray(); bytes[26] = 99; mutations.Add(bytes);
            bytes = good.Payload.ToArray().Concat(new byte[] { 0 }).ToArray(); mutations.Add(bytes);
            bytes = good.Payload.ToArray(); Array.Copy(BitConverter.GetBytes(float.NaN), 0, bytes, 2, 4); mutations.Add(bytes);
            mutations.Add(new byte[129]);
            using var refused = new RobotBodyService(_ => throw new Exception("must not construct"));
            foreach (var payload in mutations) Check(refused.CreateReplica(new InternalSpawnDescriptor(RobotBodyWire.Kind, good.Transform, payload)).ErrorCode == ModErrorCode.InvalidArgument, "invalid payload rejected");
            foreach (var bad in new[] { float.NaN, float.PositiveInfinity, 100001f })
            {
                try { _ = new RobotBodyRequest(Appearance, new Vec3(bad, 0, 0)); Check(false, "bad coordinate accepted"); }
                catch (ArgumentOutOfRangeException) { assertions++; }
            }
            try { _ = new RobotBodyRequest(Appearance, Vec3.Zero, new Quat(0, 0, 0, 0)); Check(false, "zero quaternion accepted"); }
            catch (ArgumentOutOfRangeException) { assertions++; }
            foreach (var type in new[] { typeof(IRobotBody), typeof(IRobotBodyService), typeof(RobotBodyRequest), typeof(FakeRobotBody), typeof(FakeRobotBodyService) })
                Check(type.GetCustomAttributes(typeof(RequiresTopiaForgeAttribute), false).Cast<RequiresTopiaForgeAttribute>().Single().Version == "0.2.0", "public additions are version-gated");
        }

        private static void AuthorityAndLimits()
        {
            var rig = NewRig();
            using var provider = new Bodies(rig.Host);
            foreach (var execution in new[] { ModExecutionContext.Process, ModExecutionContext.WorldOwner, ModExecutionContext.Participant })
            {
                var owner = new AuthorityMod(rig.Host, execution);
                Check(provider.Bind(owner).Spawn(Request()).ErrorCode == ModErrorCode.NotAuthoritative, "only server scope may spawn");
            }
            foreach (var kind in new[] { ModNetworkClass.ClientLocal, ModNetworkClass.Standalone, ModNetworkClass.ServerOnly })
            {
                var owner = new AuthorityMod(rig.Host, ModExecutionContext.GamemodeServer, networkClass: kind);
                Check(provider.Bind(owner).Spawn(Request()).ErrorCode == ModErrorCode.NotAuthoritative, "shared consumer required");
            }
            var server = new AuthorityMod(rig.Host, ModExecutionContext.GamemodeServer);
            var service = provider.Bind(server);
            Check(provider.Provider.Spawn(Request()).ErrorCode == ModErrorCode.NotAuthoritative, "unbound service refuses");
            Check(provider.Provider.Spawn(RobotBodyWire.Describe(Request())).ErrorCode == ModErrorCode.NotAuthoritative, "forged participant spawn refuses");
            Check(provider.Provider.Bind(server.Lifetime, null).Spawn(Request()).ErrorCode == ModErrorCode.Unavailable, "missing seam refuses");
            for (var i = 0; i < 64; i++) Check(service.Spawn(Request()).Succeeded, "scope quota permits live body");
            Check(service.Spawn(Request()).ErrorCode == ModErrorCode.Conflict, "scope quota refuses overflow");
            var first = provider.Native[0];
            server.Lifetime.Dispose();
            Check(!first.IsAlive && provider.Native.All(body => !body.IsAlive), "scope disposes actual bodies");
            Check(service.Spawn(Request()).ErrorCode == ModErrorCode.Cancelled, "retired scope refuses");
            var next = new AuthorityMod(rig.Host, ModExecutionContext.GamemodeServer);
            var nextService = provider.Bind(next);
            Check(nextService.Spawn(Request()).Succeeded, "new scope has fresh quota");
            provider.Provider.EndWorld();
            provider.Refuse = true; // The replacement world is not ready yet.
            Check(nextService.Spawn(Request()).ErrorCode == ModErrorCode.Unavailable, "old facade cannot enter next world");
            Check(provider.Native.All(body => !body.IsAlive), "world ends actual bodies");
            provider.Refuse = false;
            rig.OpenToMultiplayer();
            var player = rig.AddPlayer("guest"); using var guest = new Bodies(player);
            rig.JoinAndSynchronize(player);
            Check(guest.Bind(new AuthorityMod(player, ModExecutionContext.GamemodeServer)).Spawn(Request()).ErrorCode == ModErrorCode.NotAuthoritative, "joined server context refuses");
        }

        private static WorldRig NewRig()
        {
            var rig = new WorldRig();
            rig.UseScene(scene => scene.AddScene("World"));
            return rig;
        }

        private static void ConstructionRollbackAndProviderLimit()
        {
            var rig = NewRig(); using var provider = new Bodies(rig.Host);
            var owner = new AuthorityMod(rig.Host, ModExecutionContext.GamemodeServer);
            provider.Created = owner.Lifetime.Dispose;
            Check(provider.Bind(owner).Spawn(Request()).ErrorCode == ModErrorCode.Cancelled && provider.Native.All(body => !body.IsAlive), "scope termination during construction rolls back native body");
            owner = new AuthorityMod(rig.Host, ModExecutionContext.GamemodeServer);
            provider.Created = provider.Provider.EndWorld;
            Check(provider.Bind(owner).Spawn(Request()).ErrorCode == ModErrorCode.Unavailable && provider.Native.All(body => !body.IsAlive), "world replacement during construction rolls back native body");
            provider.Created = null;
            IRobotBody? first = null;
            for (var scope = 0; scope < 4; scope++)
            {
                var service = provider.Bind(new AuthorityMod(rig.Host, ModExecutionContext.GamemodeServer));
                for (var i = 0; i < 64; i++)
                {
                    var body = service.Spawn(Request());
                    Check(body.Succeeded, "provider bound permits 256 live bodies across scopes");
                    first ??= body.Value;
                }
            }
            var fifth = provider.Bind(new AuthorityMod(rig.Host, ModExecutionContext.GamemodeServer));
            Check(fifth.Spawn(Request()).ErrorCode == ModErrorCode.Conflict, "provider total is bounded independently of scope count");
            first!.Dispose();
            Check(fifth.Spawn(Request()).Succeeded, "explicit disposal returns provider capacity");
        }

        private sealed class Bodies : IDisposable
        {
            internal readonly RobotBodyService Provider;
            internal readonly List<NativeBody> Native = new List<NativeBody>();
            internal bool Refuse;
            internal bool Throw;
            internal int Attempts;
            internal Action? Created;
            private readonly IDisposable factory;
            private readonly IDisposable handler;
            internal Bodies(WorldPeer peer, IInternalNativeEntityReadContext? readContext = null)
            {
                peer.Scene.ProviderObject = entity => Native.FirstOrDefault(body => body.Id == entity.Id)?.Object;
                Provider = new RobotBodyService(request =>
                {
                    Attempts++;
                    if (Throw) throw new InvalidOperationException("private diagnostic must not escape");
                    if (Refuse) return OperationResult<IRobotBody>.Failure(ModErrorCode.Unavailable, "private provider failure");
                    var p = request.Position;
                    var obj = peer.Scene.Add(peer.Scene.ActiveScene!, "Body", rigidbody: true, position: new NetVector3(p.X, p.Y, p.Z));
                    var native = new NativeBody(obj, request);
                    Native.Add(native);
                    Created?.Invoke();
                    return OperationResult<IRobotBody>.Success(native);
                }, readContext);
                var seam = peer.Replication.Bind("io.example.body-provider", new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Process), peer.Lifetime);
                factory = seam.RegisterReplicaFactory(RobotBodyWire.Kind, Provider).Value!;
                handler = seam.RegisterSpawnHandler(RobotBodyWire.Kind, Provider).Value!;
            }
            internal IRobotBodyService Bind(AuthorityMod owner, IInternalNativeEntityReadContext? readContext = null) =>
                Provider.Bind(owner.Lifetime, owner.Seam, readContext);
            public void Dispose() { factory.Dispose(); handler.Dispose(); Provider.Dispose(); }
        }

        private sealed class NativeBody : IRobotBody, IInternalNativeObjectEntity
        {
            internal readonly FakeWorldObject Object;
            private readonly RobotBodyRequest request;
            internal NativeBody(FakeWorldObject obj, RobotBodyRequest request) { Object = obj; this.request = request; }
            public object NativeObject => Object;
            public string Id => Object.Entity.Id;
            public string Name => "Body";
            public bool IsAlive => Object.IsAlive;
            public Vec3 Position => request.Position;
            public AvatarAppearance Appearance => request.Appearance;
            public Quat Rotation => request.Rotation;
            public void Dispose() => Object.Destroy();
        }
    }
}
