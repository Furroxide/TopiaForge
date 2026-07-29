using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using UnityEngine;

namespace TopiaForge.RobotKit
{
    // Implementation-only bridge. It is deliberately absent from TopiaForge.Mods.RobotKit.
    internal interface INativeEntityAdapter
    {
        GameObject NativeGameObject { get; }
    }

    internal sealed class NativeEntityAdapter : IEntity, INativeEntityAdapter
    {
        private readonly string id;
        private readonly GameObject gameObject;

        public NativeEntityAdapter(string id, GameObject gameObject)
        {
            this.id = id;
            this.gameObject = gameObject;
        }

        public string Id => id;
        public string Name => gameObject != null ? gameObject.name : string.Empty;
        public bool IsAlive => gameObject != null;
        public Vec3 Position
        {
            get
            {
                if (gameObject == null)
                {
                    return Vec3.Zero;
                }

                var position = gameObject.transform.position;
                return new Vec3(position.x, position.y, position.z);
            }
        }

        public GameObject NativeGameObject => gameObject;
    }

    // Attached to a spawned robot's root before activation. The host entity registry discovers this marker while
    // walking upward from any hit collider/body and emits the agent's stable SDK id instead of a child object id.
    internal sealed class RobotAgentEntityIdentityAnchor : MonoBehaviour, IRuntimeEntityIdentityAnchor
    {
        private string runtimeEntityId = string.Empty;

        public string RuntimeEntityId => runtimeEntityId;

        public void Initialize(string entityId)
        {
            if (string.IsNullOrWhiteSpace(entityId))
            {
                throw new System.ArgumentException("A runtime entity id is required.", nameof(entityId));
            }

            runtimeEntityId = entityId;
        }
    }
}
