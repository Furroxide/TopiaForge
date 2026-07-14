using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Mod-facing registry view that pins every mutation to the context owner. The public SDK keeps its
    /// historical owner parameter, but a mod can no longer spoof or unregister another owner's services.
    /// </summary>
    internal sealed class OwnerBoundModServiceRegistry : IModServiceRegistry
    {
        private readonly string ownerModId;
        private readonly IModServiceRegistry registry;

        public OwnerBoundModServiceRegistry(string ownerModId, IModServiceRegistry registry)
        {
            if (string.IsNullOrWhiteSpace(ownerModId))
            {
                throw new ArgumentException("Owner mod id is required.", nameof(ownerModId));
            }

            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.ownerModId = ownerModId;
        }

        public IReadOnlyList<ModServiceRegistration> Services => registry.Services;

        public void Register<T>(string ownerModId, T service) where T : class
        {
            EnsureOwner(ownerModId);
            registry.Register(this.ownerModId, service);
        }

        public void UnregisterOwner(string ownerModId)
        {
            EnsureOwner(ownerModId);
            registry.UnregisterOwner(this.ownerModId);
        }

        public T? Get<T>() where T : class
        {
            return registry.Get<T>();
        }

        private void EnsureOwner(string requestedOwnerModId)
        {
            if (!string.Equals(ownerModId, requestedOwnerModId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "A mod service registry can only mutate registrations owned by " + ownerModId + ".");
            }
        }
    }
}
