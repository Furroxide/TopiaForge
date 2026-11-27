namespace TopiaForge.Mods.Internal
{
    /// <summary>
    /// Loader-owned replication access for trusted first-party providers. Every mod context the TopiaForge runtime creates
    /// implements it, so a provider such as RobotKit, CreatorContent, Chronos or Worlds reaches the replication seam bound
    /// to the context it was given. A context that does not implement it, such as a test double, is single player. It is
    /// deliberately absent from the public SDK surface.
    /// </summary>
    internal interface IInternalReplicationContext
    {
        /// <summary>Gets the replication seam bound to this context's mod and network profile.</summary>
        IRuntimeReplication Replication { get; }
    }
}
