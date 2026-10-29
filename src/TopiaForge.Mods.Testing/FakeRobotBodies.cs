using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Deterministic persistent-body service. Register it as IRobotBodyService in a fake context.</summary>
    [RequiresTopiaForge("0.2.0")]
    public sealed class FakeRobotBodyService : IRobotBodyService, IDisposable
    {
        private readonly FakeModLifetime lifetime;
        private readonly List<FakeRobotBody> bodies = new List<FakeRobotBody>();
        private bool ended;
        private long serial;
        /// <summary>Creates a body service bound to the consuming scope.</summary>
        public FakeRobotBodyService(FakeModLifetime lifetime) => this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        /// <summary>Gets or sets whether this is a shared gamemode server on the host (or offline).</summary>
        public bool IsAuthoritative { get; set; } = true;
        /// <summary>Gets or sets whether the cosmetic provider can compose a body.</summary>
        public bool IsAvailable { get; set; } = true;
        /// <summary>Gets a snapshot of live bodies for assertions.</summary>
        public IReadOnlyList<FakeRobotBody> Bodies => bodies.ToArray();
        /// <inheritdoc/>
        public OperationResult<IRobotBody> Spawn(RobotBodyRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (lifetime.IsStopping) return Fail(ModErrorCode.Cancelled);
            if (ended || !IsAvailable) return Fail(ModErrorCode.Unavailable);
            if (!IsAuthoritative) return Fail(ModErrorCode.NotAuthoritative);
            if (bodies.Count >= 64) return Fail(ModErrorCode.Conflict);
            var body = new FakeRobotBody("body-" + ++serial, request, value => bodies.Remove(value));
            bodies.Add(body);
            try { body.Tracking = lifetime.Track(body); }
            catch (ObjectDisposedException) { body.Dispose(); return Fail(ModErrorCode.Cancelled); }
            return OperationResult<IRobotBody>.Success(body);
        }
        /// <summary>Ends the provider/world and all bodies; retained service handles refuse subsequent spawns.</summary>
        public void Dispose()
        {
            ended = true;
            foreach (var body in bodies.ToArray()) body.Dispose();
        }
        private static OperationResult<IRobotBody> Fail(ModErrorCode code) => OperationResult<IRobotBody>.Failure(code, "Persistent body creation was refused.");
    }

    /// <summary>An inspectable, immutable body snapshot with an independently owned lifetime.</summary>
    [RequiresTopiaForge("0.2.0")]
    public sealed class FakeRobotBody : IRobotBody
    {
        private readonly RobotBodyRequest request;
        private readonly Action<FakeRobotBody> release;
        internal IDisposable? Tracking;
        internal FakeRobotBody(string id, RobotBodyRequest request, Action<FakeRobotBody> release)
        { Id = id; this.request = request; this.release = release; }
        /// <inheritdoc/>
        public string Id { get; }
        /// <inheritdoc/>
        public string Name => "Powered-down robot";
        /// <inheritdoc/>
        public bool IsAlive { get; private set; } = true;
        /// <inheritdoc/>
        public Vec3 Position => request.Position;
        /// <inheritdoc/>
        public Quat Rotation => request.Rotation;
        /// <inheritdoc/>
        public AvatarAppearance Appearance => request.Appearance;
        /// <inheritdoc/>
        public void Dispose()
        {
            if (!IsAlive) return;
            IsAlive = false;
            release(this);
            var tracking = Tracking; Tracking = null; tracking?.Dispose();
        }
    }
}
