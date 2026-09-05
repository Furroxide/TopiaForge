using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>Capture is atomic at the production registry; constructor validation alone is not package verification.</summary>
    internal interface IRuntimeSessionEnvironment
    {
        RuntimeSessionSnapshot Capture();
        Task<OperationResult<bool>> LoadMainMenuAsync(IInternalSceneTransitionService transitions, CancellationToken cancellationToken);
    }

    /// <summary>Constructor metadata supplied by the binding registry after package verification in the activation slice.</summary>
    internal sealed class SessionImplementation<T> where T : class
    {
        private readonly ConstructorInfo constructor;
        internal SessionImplementation(PackageIdentity package, string declarationId, Type implementation)
        {
            Package = package ?? throw new ArgumentNullException(nameof(package));
            DeclarationId = declarationId ?? throw new ArgumentNullException(nameof(declarationId));
            if (!implementation.IsVisible || implementation.IsAbstract || implementation.ContainsGenericParameters
                || !typeof(T).IsAssignableFrom(implementation))
                throw new ArgumentException("Activation requires a public concrete implementation of " + typeof(T).Name + ".");
            constructor = implementation.GetConstructor(Type.EmptyTypes)
                ?? throw new ArgumentException("Activation requires a public parameterless constructor.");
        }
        internal PackageIdentity Package { get; }
        internal string DeclarationId { get; }
        internal T Create() => (T)constructor.Invoke(Array.Empty<object>());
    }

    internal sealed class RuntimeSessionSnapshot
    {
        internal RuntimeSessionSnapshot(EffectiveProfile profile, RuntimeBindingSnapshot bindings,
            IReadOnlyDictionary<string, ModContext> contexts,
            IEnumerable<SessionImplementation<IGamemodeFactory>> gamemodes,
            IEnumerable<SessionImplementation<IWorldContentProvider>> worlds,
            RuntimeObservation? observation = null)
        {
            Profile = profile;
            Bindings = bindings;
            Contexts = new ReadOnlyDictionary<string, ModContext>(contexts.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));
            Gamemodes = Array.AsReadOnly(gamemodes.ToArray());
            Worlds = Array.AsReadOnly(worlds.ToArray());
            Observation = observation ?? RuntimeObservation.None;
        }
        internal EffectiveProfile Profile { get; }
        internal RuntimeBindingSnapshot Bindings { get; }
        internal RuntimeObservation Observation { get; }
        internal IReadOnlyDictionary<string, ModContext> Contexts { get; }
        internal IReadOnlyList<SessionImplementation<IGamemodeFactory>> Gamemodes { get; }
        internal IReadOnlyList<SessionImplementation<IWorldContentProvider>> Worlds { get; }
    }
}
