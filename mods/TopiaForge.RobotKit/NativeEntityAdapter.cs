using TopiaForge.Mods;
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
}
