using UnityEngine;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Marks a network root: the object a replicated entity's pose belongs to, carrying the entity's network id. The
    /// session's lookups walk up from any part of an object to the nearest mark. It is deliberately not an
    /// <c>IRuntimeEntityIdentityAnchor</c>: the entity registry finds anchors with one <c>GetComponent</c> on the
    /// interface, and a root that carries both, such as a RobotKit robot, would make that call ambiguous. It has no
    /// behaviour of its own, and the network world removes it when the session no longer replicates the object.
    /// </summary>
    [DisallowMultipleComponent]
    internal sealed class TopiaForgeNetworkIdentity : MonoBehaviour
    {
        /// <summary>Gets or sets the network entity id; zero once the mark has been taken off and is about to go.</summary>
        internal uint NetworkId { get; set; }
    }
}
