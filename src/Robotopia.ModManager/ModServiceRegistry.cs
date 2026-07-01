using System;
using System.Collections.Generic;
using System.Linq;
using Robotopia.Mods;

namespace Robotopia.ModManager
{
    public sealed class ModServiceRegistry : IModServiceRegistry
    {
        private readonly List<ModServiceRegistration> services = new List<ModServiceRegistration>();

        public IReadOnlyList<ModServiceRegistration> Services => services.ToList();

        public void Register<T>(string ownerModId, T service) where T : class
        {
            if (string.IsNullOrWhiteSpace(ownerModId))
            {
                throw new ArgumentException("Owner mod id is required.", nameof(ownerModId));
            }

            if (service == null)
            {
                throw new ArgumentNullException(nameof(service));
            }

            var serviceType = typeof(T);
            services.RemoveAll(item =>
                string.Equals(item.OwnerModId, ownerModId, StringComparison.OrdinalIgnoreCase) &&
                item.ServiceType == serviceType);
            services.Add(new ModServiceRegistration(ownerModId, serviceType, service));
        }

        public void UnregisterOwner(string ownerModId)
        {
            services.RemoveAll(item => string.Equals(item.OwnerModId, ownerModId, StringComparison.OrdinalIgnoreCase));
        }

        public T? Get<T>() where T : class
        {
            var serviceType = typeof(T);
            for (var index = services.Count - 1; index >= 0; index--)
            {
                var item = services[index];
                if (serviceType.IsAssignableFrom(item.ServiceType) && item.Service is T service)
                {
                    return service;
                }
            }

            return null;
        }
    }
}
