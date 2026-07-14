using System;
using System.Collections.Generic;
using System.Linq;
using Robotopia.Mods;

namespace Robotopia.ModManager
{
    public sealed class ModServiceRegistry : IModServiceRegistry
    {
        private readonly List<ModServiceRegistration> frameworkServices = new List<ModServiceRegistration>();
        private readonly HashSet<Type> frameworkServiceTypes = new HashSet<Type>();
        private readonly List<ModServiceRegistration> services = new List<ModServiceRegistration>();

        public IReadOnlyList<ModServiceRegistration> Services => frameworkServices.Concat(services).ToList();

        /// <summary>
        /// Registers a manager-owned service that mods cannot shadow or remove through the public registry.
        /// Kept internal so only the runtime can establish framework invariants.
        /// </summary>
        internal void RegisterFramework<T>(string ownerModId, T service) where T : class
        {
            ValidateRegistration(ownerModId, service);
            var serviceType = typeof(T);
            frameworkServiceTypes.Add(serviceType);
            frameworkServices.RemoveAll(item =>
                string.Equals(item.OwnerModId, ownerModId, StringComparison.OrdinalIgnoreCase)
                && item.ServiceType == serviceType);
            frameworkServices.Add(new ModServiceRegistration(ownerModId, serviceType, service));
        }

        public void Register<T>(string ownerModId, T service) where T : class
        {
            ValidateRegistration(ownerModId, service);
            var serviceType = typeof(T);
            if (frameworkServiceTypes.Any(protectedType => protectedType.IsAssignableFrom(serviceType)))
            {
                throw new InvalidOperationException(
                    "The framework service type '" + serviceType.FullName + "' is manager-owned and cannot be replaced by a mod.");
            }

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

            // A mod cannot register a type assignable to a protected framework contract, and public owner
            // cleanup never touches this list. Search it after ordinary services so broad Get<object>() calls
            // retain their historical last-mod-registration behavior.
            for (var index = frameworkServices.Count - 1; index >= 0; index--)
            {
                var item = frameworkServices[index];
                if (serviceType.IsAssignableFrom(item.ServiceType) && item.Service is T service)
                {
                    return service;
                }
            }

            return null;
        }

        private static void ValidateRegistration<T>(string ownerModId, T service) where T : class
        {
            if (string.IsNullOrWhiteSpace(ownerModId))
            {
                throw new ArgumentException("Owner mod id is required.", nameof(ownerModId));
            }

            if (service == null)
            {
                throw new ArgumentNullException(nameof(service));
            }
        }
    }
}
