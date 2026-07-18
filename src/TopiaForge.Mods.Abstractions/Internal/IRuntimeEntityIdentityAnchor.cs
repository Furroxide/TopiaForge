namespace TopiaForge.Mods.Internal
{
    // Host/framework handshake used to preserve one safe IEntity identity across native child colliders and
    // rigidbodies. The interface intentionally carries no engine types and remains internal to trusted runtime
    // assemblies; ordinary mods continue to see only IEntity.
    internal interface IRuntimeEntityIdentityAnchor
    {
        string RuntimeEntityId { get; }
    }
}
