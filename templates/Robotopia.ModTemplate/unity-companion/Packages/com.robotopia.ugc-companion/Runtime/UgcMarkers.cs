using System;
using UnityEngine;

namespace Robotopia.UgcCompanion
{
    /// <summary>
    /// Marks a GameObject as an exported UGC entity. Every object the exporter emits must have one of these.
    /// The stable <see cref="EntityId"/> keeps diffs incremental across exports — do not change it once set.
    /// The GameObject's name becomes the entity name; its local transform becomes the entity transform.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Robotopia UGC/Entity")]
    public sealed class UgcEntityMarker : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Stable unique id for this entity. Auto-generated; keep it stable so live edits patch incrementally.")]
        private string entityId = string.Empty;

        public string EntityId
        {
            get
            {
                if (string.IsNullOrWhiteSpace(entityId))
                {
                    entityId = Guid.NewGuid().ToString("N");
                }

                return entityId;
            }
        }

        private void Reset()
        {
            entityId = Guid.NewGuid().ToString("N");
        }

        private void OnValidate()
        {
            if (string.IsNullOrWhiteSpace(entityId))
            {
                entityId = Guid.NewGuid().ToString("N");
            }
        }
    }

    /// <summary>Renders this entity using a built-in/catalog model asset id (UGC <c>model-renderer</c> component).</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Robotopia UGC/Model Renderer")]
    public sealed class UgcModelRenderer : MonoBehaviour
    {
        [Tooltip("Asset id, e.g. @robotopia/tree-model. Resolved in-game via UgcBuiltInAssetMap or a mod runtime override.")]
        public string assetId = string.Empty;
    }

    /// <summary>Instantiates a built-in prefab asset for this entity (UGC <c>prefab-instance</c> component).</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Robotopia UGC/Prefab Instance")]
    public sealed class UgcPrefabInstance : MonoBehaviour
    {
        [Tooltip("Asset id of the prefab to instantiate.")]
        public string assetId = string.Empty;

        [Tooltip("Optional source scene id for the prefab.")]
        public string scene = string.Empty;

        [Tooltip("Optional accessory asset id.")]
        public string accessory = string.Empty;
    }

    /// <summary>Marks this entity as the player spawn location (UGC <c>spawn-location</c> component).</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Robotopia UGC/Spawn Location")]
    public sealed class UgcSpawnLocationMarker : MonoBehaviour
    {
    }

    /// <summary>Point-of-interest metadata for this entity (UGC <c>poi</c> component).</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Robotopia UGC/Point Of Interest")]
    public sealed class UgcPoiMarker : MonoBehaviour
    {
        [Tooltip("Lore/asset uris this POI is about.")]
        public string[] about = Array.Empty<string>();

        [TextArea]
        public string visualDescription = string.Empty;

        public bool hidden;
    }

    /// <summary>Area-of-interest metadata for this entity (UGC <c>aoi</c> component).</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Robotopia UGC/Area Of Interest")]
    public sealed class UgcAoiMarker : MonoBehaviour
    {
        public string[] about = Array.Empty<string>();

        [TextArea]
        public string visualDescription = string.Empty;

        [Tooltip("Area size in UGC units (x,y,z).")]
        public Vector3 size = Vector3.one;
    }

    /// <summary>Agent (LLM character) metadata for this entity (UGC <c>agent</c> component).</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Robotopia UGC/Agent")]
    public sealed class UgcAgentMarker : MonoBehaviour
    {
        public string[] about = Array.Empty<string>();

        [TextArea]
        public string visualDescription = string.Empty;

        [Tooltip("Personality asset uri (e.g. a local:personality/... id).")]
        public string personality = string.Empty;
    }
}
