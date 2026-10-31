using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.RobotKit
{
    internal sealed class RobotBodyService : IRobotBodyService, IOwnerContextBoundExtensionFactory,
        IInternalReplicaFactory, IInternalSharedSpawnHandler, IDisposable
    {
        internal const int MaximumPerScope = 64;
        internal const int MaximumBodies = 256;
        private readonly Func<RobotBodyRequest, OperationResult<IRobotBody>> create;
        private readonly IInternalNativeEntityReadContext? replicaReadContext;
        private readonly HashSet<Body> bodies = new HashSet<Body>();
        private bool disposed;
        private int generation;

        internal RobotBodyService(Func<RobotBodyRequest, OperationResult<IRobotBody>> create,
            IInternalNativeEntityReadContext? replicaReadContext = null)
        { this.create = create; this.replicaReadContext = replicaReadContext; }

        // Only a bound facade may spawn. No implicit provider-owned lifetime is permitted.
        public OperationResult<IRobotBody> Spawn(RobotBodyRequest request) => Fail(ModErrorCode.NotAuthoritative,
            "Obtain persistent bodies from the gamemode server's scoped context.");

        public object CreateOwnerFacade(Type contractType, IModContext context)
        {
            if (contractType != typeof(IRobotBodyService)) throw new ArgumentException("Unsupported body contract.");
            return Bind(context.Lifetime, (context as IInternalReplicationContext)?.Replication, context as IInternalNativeEntityReadContext);
        }

        internal IRobotBodyService Bind(IModLifetime lifetime, IRuntimeReplication? replication,
            IInternalNativeEntityReadContext? readContext = null) => new Facade(this, lifetime, replication, readContext);

        private OperationResult<IRobotBody> Make(RobotBodyRequest request, Facade? owner, IInternalNativeEntityReadScope? readScope = null)
        {
            if (disposed) return Fail(ModErrorCode.Unavailable, "The persistent body provider has stopped.");
            if (bodies.Count >= MaximumBodies) return Fail(ModErrorCode.Conflict, "The persistent body limit has been reached.");
            var epoch = generation;
            IRobotBody? native = null;
            try
            {
                var result = create(request);
                if (!result.TryGetValue(out native)) return Fail(result.ErrorCode, "RobotKit could not compose the requested body. Check the loaded robot and accessory catalog.");
                if (disposed || generation != epoch)
                { native.Dispose(); return Fail(ModErrorCode.Unavailable, "The body provider or world changed during composition."); }
                if (!native.IsAlive) { native.Dispose(); return Fail(ModErrorCode.Unavailable, "The cosmetic body is unavailable."); }
                var body = new Body(this, native, owner, readScope);
                bodies.Add(body);
                owner?.Bodies.Add(body);
                return OperationResult<IRobotBody>.Success(body);
            }
            catch (Exception)
            {
                try { native?.Dispose(); } catch (Exception) { }
                return Fail(ModErrorCode.External, "RobotKit could not compose the persistent body.");
            }
        }

        public OperationResult<IEntity> CreateReplica(InternalSpawnDescriptor descriptor)
        {
            try
            {
                var request = RobotBodyWire.Read(descriptor);
                var result = Make(request, null, replicaReadContext?.CaptureNativeEntityReadScope());
                return result.TryGetValue(out var body) ? OperationResult<IEntity>.Success(body)
                    : OperationResult<IEntity>.Failure(result.ErrorCode, result.ErrorMessage);
            }
            catch (Exception) { return OperationResult<IEntity>.Failure(ModErrorCode.InvalidArgument, "Invalid persistent body description."); }
        }

        // The public contract is host-only. Even a forged participant spawn never creates a body on the host.
        public OperationResult<IEntity> Spawn(InternalSpawnDescriptor descriptor) =>
            OperationResult<IEntity>.Failure(ModErrorCode.NotAuthoritative, "Persistent bodies are created by the gamemode server.");
        public void ReleaseReplica(IEntity replica) => Despawn(replica);
        public void Despawn(IEntity entity)
        {
            if (entity is Body body && ReferenceEquals(body.Service, this)) body.Dispose();
        }

        internal void EndWorld()
        {
            generation++;
            var ended = new Body[bodies.Count];
            bodies.CopyTo(ended);
            foreach (var body in ended) body.Dispose();
        }

        public void Dispose() { if (disposed) return; disposed = true; EndWorld(); }

        private static OperationResult<IRobotBody> Fail(ModErrorCode code, string message) => OperationResult<IRobotBody>.Failure(code, message);

        private sealed class Facade : IRobotBodyService
        {
            private readonly RobotBodyService service;
            private readonly IModLifetime lifetime;
            private readonly IRuntimeReplication? replication;
            private readonly IInternalNativeEntityReadContext? readContext;
            internal readonly HashSet<Body> Bodies = new HashSet<Body>();

            internal Facade(RobotBodyService service, IModLifetime lifetime, IRuntimeReplication? replication,
                IInternalNativeEntityReadContext? readContext)
            { this.service = service; this.lifetime = lifetime; this.replication = replication; this.readContext = readContext; }

            public OperationResult<IRobotBody> Spawn(RobotBodyRequest request)
            {
                if (request == null) throw new ArgumentNullException(nameof(request));
                if (lifetime.IsStopping) return Fail(ModErrorCode.Cancelled, "The body scope has stopped.");
                if (service.disposed) return Fail(ModErrorCode.Unavailable, "The body provider has stopped.");
                if (replication == null) return Fail(ModErrorCode.Unavailable, "Persistent body replication is unavailable.");
                if (replication.Realm == InternalNetRealm.Client || replication.ExecutionContext != InternalExecutionContext.GamemodeServer
                    || replication.NetworkClass != InternalNetworkClass.Shared)
                    return Fail(ModErrorCode.NotAuthoritative, "Persistent bodies require a shared gamemode server context.");
                if (Bodies.Count >= MaximumPerScope) return Fail(ModErrorCode.Conflict, "This scope already owns 64 persistent bodies.");
                Body? made = null;
                try
                {
                    var descriptor = RobotBodyWire.Describe(request);
                    // Mint read authority before construction; a replacement world cannot adopt the result.
                    var readScope = readContext?.CaptureNativeEntityReadScope();
                    var created = service.Make(request, this, readScope);
                    if (!created.TryGetValue(out var body)) return created;
                    made = (Body)body;
                    made.Tracking = lifetime.Track(made);
                    // Unlike the generic spawn convenience's quiet recording path, registration must succeed before
                    // this public contract promises a replicated lease. A refusal rolls back the actual native body.
                    var result = replication.RegisterCanonical(made, descriptor);
                    if (result.Succeeded && made != null && made.IsAlive) return OperationResult<IRobotBody>.Success(made);
                    made?.Dispose();
                    return Fail(result.Succeeded ? ModErrorCode.Unavailable : result.ErrorCode, "The runtime could not retain the persistent body.");
                }
                catch (Exception)
                {
                    made?.Dispose();
                    return Fail(lifetime.IsStopping ? ModErrorCode.Cancelled : ModErrorCode.External, "The runtime could not retain the persistent body.");
                }
            }
        }

        private sealed class Body : IRobotBody, IInternalOwnedNativeEntity
        {
            internal readonly RobotBodyService Service;
            private readonly IRobotBody native;
            private readonly Facade? owner;
            private bool ended;
            internal IDisposable? Tracking;
            internal Body(RobotBodyService service, IRobotBody native, Facade? owner, IInternalNativeEntityReadScope? readScope)
            { Service = service; this.native = native; this.owner = owner; NativeReadScope = readScope; }
            public IInternalNativeEntityReadScope? NativeReadScope { get; }
            public string Id => native.Id;
            public string Name => "Powered-down robot";
            public bool IsAlive => !ended && native.IsAlive;
            public Vec3 Position => native.Position;
            public Quat Rotation => native.Rotation;
            public AvatarAppearance Appearance => native.Appearance;
            public object? NativeObject => IsAlive ? (native as IInternalNativeObjectEntity)?.NativeObject : null;
            public void Dispose()
            {
                if (ended) return;
                ended = true;
                Service.bodies.Remove(this);
                owner?.Bodies.Remove(this);
                var tracking = Tracking; Tracking = null;
                try { native.Dispose(); } catch (Exception) { }
                try { tracking?.Dispose(); } catch (Exception) { }
            }
        }
    }
}
