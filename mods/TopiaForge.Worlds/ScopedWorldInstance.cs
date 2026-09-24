using System;
using TopiaForge.Mods;

namespace TopiaForge.Worlds
{
    internal sealed class ScopedWorldInstance : IWorldInstance
    {
        private readonly WorldResourceScope resources;
        public ScopedWorldInstance(WorldReadiness readiness, WorldResourceScope resources)
        {
            Readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
            this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
        }
        public WorldReadiness Readiness { get; }
        public void Dispose() => resources.Dispose();
    }
}
