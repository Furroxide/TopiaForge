using System;
using System.Reflection;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager
{
    internal interface ISessionImplementation<out T> where T : class
    {
        PackageIdentity Package { get; }
        string DeclarationId { get; }
        T Create();
    }

    /// <summary>Verified constructor metadata. Binding itself never runs package constructors.</summary>
    internal sealed class SessionImplementation<T> : ISessionImplementation<T> where T : class
    {
        private readonly ConstructorInfo constructor;
        internal SessionImplementation(PackageIdentity package, string declarationId, Type implementation)
        {
            if (package == null) throw new ArgumentNullException(nameof(package));
            Package = new PackageIdentity(package.Id, package.Version);
            DeclarationId = declarationId ?? throw new ArgumentNullException(nameof(declarationId));
            if (!implementation.IsVisible || implementation.IsAbstract || implementation.ContainsGenericParameters
                || !typeof(T).IsAssignableFrom(implementation))
                throw new ArgumentException("Activation requires a public concrete implementation of " + typeof(T).Name + ".");
            constructor = implementation.GetConstructor(Type.EmptyTypes)
                ?? throw new ArgumentException("Activation requires a public parameterless constructor.");
        }
        public PackageIdentity Package { get; }
        public string DeclarationId { get; }
        public T Create() => (T)constructor.Invoke(Array.Empty<object>());
    }
}
