using System;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>The existing SDK lease, with optimistic simulation and a runtime-owned throw tail.</summary>
    internal sealed class NetEntityMotion : IEntityMotion
    {
        private readonly NetworkWorldMotion owner;
        private readonly IEntityMotion? inner;
        private readonly bool originalKinematic;
        private bool disposed;

        internal NetEntityMotion(NetworkWorldMotion owner, RuntimeReplication? context, INetworkMotionBody body,
            IEntityMotion? inner, MotionTarget target, uint request, uint epoch, long now, bool originalKinematic)
        {
            this.owner = owner;
            Context = context;
            Body = body;
            this.inner = inner;
            Target = target;
            Request = request;
            Epoch = epoch;
            CreatedMicros = now;
            this.originalKinematic = originalKinematic;
            body.TryReadMotion(out var initialPose, out var initialAngular);
            InitialPose = initialPose;
            InitialAngular = initialAngular;
            body.IsKinematic = false;
        }

        internal RuntimeReplication? Context { get; }
        internal INetworkMotionBody Body { get; }
        internal NetPose InitialPose { get; }
        internal NetVector3 InitialAngular { get; }
        internal MotionTarget Target { get; set; }
        internal uint Request { get; }
        internal uint Epoch { get; set; }
        internal bool Granted { get; set; }
        internal bool Ended { get; private set; }
        internal bool Releasing { get; private set; }
        internal bool ReleaseSent { get; set; }
        internal long ReleaseDeadline { get; private set; }
        internal long CreatedMicros { get; }
        internal long NextSampleMicros { get; set; }
        internal bool Native => inner == null;
        internal bool NeedsHandback => disposed && !Releasing;
        public IEntity Entity => Body.Entity;
        public bool IsAlive => !disposed && !Ended && Body.IsAlive && (inner == null || inner.IsAlive);

        public OperationResult<Vec3> MoveToward(Vec3 target, float responsiveness, float damping, float maximumSpeed, float deltaTime)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (!IsAlive || Context?.IsStopping == true || inner == null) return Unavailable();
            using var scope = owner.Intents.EnterMotionLease(Context!.OwnerModId);
            return inner.MoveToward(target, responsiveness, damping, maximumSpeed, deltaTime);
        }

        public OperationResult<Vec3> Throw(Vec3 direction, float speed)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (!IsAlive || Context?.IsStopping == true || inner == null) return Unavailable();
            using var scope = owner.Intents.EnterMotionLease(Context!.OwnerModId);
            var result = inner.Throw(direction, speed);
            if (result.Succeeded) BeginRelease();
            return result;
        }

        internal void BeginRelease()
        {
            if (Ended || Releasing) return;
            Releasing = true;
            ReleaseDeadline = owner.Now + MotionRules.Policy.ReleaseWindowMicros;
            Body.IsKinematic = false;
        }

        public void Dispose()
        {
            UnityMainThreadGuard.AssertCurrent();
            if (disposed) return;
            disposed = true;
            // Throw already released the engine lease. Its one-second simulation tail belongs to the runtime.
            if (!Releasing) inner?.Dispose();
        }

        internal void End(bool joined)
        {
            if (Ended) return;
            Ended = true;
            inner?.Dispose();
            if (Body.IsAlive) Body.IsKinematic = joined || (!Releasing && originalKinematic);
        }

        private static OperationResult<Vec3> Unavailable() => OperationResult<Vec3>.Failure(
            ModErrorCode.NotAuthoritative, "The object is no longer held here. Try grabbing it again.");
    }
}
