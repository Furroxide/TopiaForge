using UnityEngine;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Marks a network root: the object a replicated entity's pose belongs to, carrying the entity's network id. The
    /// session's lookups walk up from any part of an object to the nearest mark. It is deliberately not an
    /// <c>IRuntimeEntityIdentityAnchor</c>: the entity registry finds anchors with one <c>GetComponent</c> on the
    /// interface, and a root that carries both, such as a RobotKit robot, would make that call ambiguous. Its only
    /// behaviour is counting itself in <see cref="NetworkMarks"/> while it carries an id, so a scene census skips the
    /// search for marks when there are none; the network world removes it when the session no longer replicates the
    /// object.
    /// </summary>
    [DisallowMultipleComponent]
    internal sealed class TopiaForgeNetworkIdentity : MonoBehaviour
    {
        private uint networkId;

        /// <summary>Gets or sets the network entity id; zero once the mark has been taken off and is about to go.</summary>
        internal uint NetworkId
        {
            get => networkId;
            set
            {
                NetworkMarks.Changed(networkId, value);
                networkId = value;
            }
        }

        private void OnDestroy() => NetworkId = 0;
    }
}
