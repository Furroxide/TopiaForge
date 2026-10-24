using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>The engine's body state; no Unity types escape this boundary.</summary>
    internal interface INetworkMotionBody : INetworkWorldObject
    {
        bool IsKinematic { get; set; }
        bool TryReadMotion(out NetPose pose, out NetVector3 angularVelocity);
        void WriteMotion(in NetPose pose, NetVector3 angularVelocity);
    }

    /// <summary>Native pickups are observed once per online frame, independently of mod service calls.</summary>
    internal interface INetworkMotionBackend
    {
        IEntity? MotionRoot(IEntity entity);
        bool TryGetLocalPosition(out NetVector3 position);
        bool TryGetHeldEntity(out IEntity? entity);
        void DropHeldEntity(IEntity entity);
    }

    internal interface IMultiplayerMotion
    {
        OperationResult<IEntityMotion>? Acquire(RuntimeReplication context, IEntity entity, IEntityService inner);
    }

    internal readonly struct MotionTarget
    {
        internal MotionTarget(NetworkEntityId id) { Id = id; SceneKey = default; }
        internal MotionTarget(SceneObjectKey key) { Id = default; SceneKey = key; }
        internal NetworkEntityId Id { get; }
        internal SceneObjectKey SceneKey { get; }
        internal bool IsScene => !Id.IsValid;
    }
}
