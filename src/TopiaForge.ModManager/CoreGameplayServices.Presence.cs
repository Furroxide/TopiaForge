namespace TopiaForge.ModManager
{
    internal sealed partial class CoreGameplayServices : IPresenceBackendSource
    {
        private UnityPresenceBackend? presenceBackend;

        /// <summary>Gets presence's engine side (WS3.10): the local player, avatars, and the world's ground and bounds.</summary>
        public IPresenceBackend PresenceBackend => presenceBackend ??= new UnityPresenceBackend(entities);
    }
}
