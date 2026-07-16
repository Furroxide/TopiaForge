using System;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.Mods.Interop.Unity
{
    /// <summary>
    /// Exposes native Unity objects for advanced compatibility and patch mods. This contract is intentionally
    /// unstable and is excluded from the TopiaForge V1 compatibility guarantee.
    /// </summary>
    public interface IUnityInteropService
    {
        /// <summary>Tries to obtain the live Unity object represented by a safe SDK entity.</summary>
        /// <param name="entity">An entity created by the current TopiaForge runtime.</param>
        /// <param name="gameObject">Receives the live native object when available.</param>
        /// <returns><see langword="true"/> when the entity belongs to this runtime and remains alive.</returns>
        bool TryGetGameObject(IEntity entity, out GameObject? gameObject);

        /// <summary>Wraps a live Unity object in the safe SDK entity identity used by the current runtime.</summary>
        /// <param name="gameObject">The live object to wrap.</param>
        /// <returns>The corresponding entity or an expected failure.</returns>
        OperationResult<IEntity> Wrap(GameObject gameObject);

        /// <summary>Tries to read a Unity component from a live safe entity.</summary>
        /// <typeparam name="T">The Unity component type.</typeparam>
        /// <param name="entity">An entity created by the current TopiaForge runtime.</param>
        /// <param name="component">Receives the component when present.</param>
        /// <returns><see langword="true"/> when the entity and component both exist.</returns>
        bool TryGetComponent<T>(IEntity entity, out T? component) where T : Component;
    }

    /// <summary>Internal host hook implemented only by the TopiaForge game runtime.</summary>
    public interface IUnityInteropContext
    {
        /// <summary>Gets the capability-gated native Unity service.</summary>
        IUnityInteropService UnityInterop { get; }
    }

    /// <summary>Discovers the explicitly unstable native service from a mod context.</summary>
    public static class UnityInteropExtensions
    {
        /// <summary>
        /// Gets the Unity escape hatch. The manifest must declare <c>unsafe-native</c> and the project must
        /// reference this package; ordinary mods should use <see cref="IModContext"/> services instead.
        /// </summary>
        /// <param name="context">The current mod context.</param>
        /// <returns>The runtime-owned Unity interop service.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
        /// <exception cref="InvalidOperationException">The host is not the game runtime or the capability is absent.</exception>
        public static IUnityInteropService RequireUnityInterop(this IModContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (context is IUnityInteropContext interopContext)
            {
                return interopContext.UnityInterop;
            }

            throw new InvalidOperationException(
                "Unity interop is unavailable. Add TopiaForge.Mods.Interop.Unity and declare the 'unsafe-native' capability. " +
                "This API is intentionally unstable and is not covered by V1 compatibility guarantees.");
        }
    }
}
