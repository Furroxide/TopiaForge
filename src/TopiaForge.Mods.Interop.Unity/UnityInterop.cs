using System;
using System.Reflection;
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

        /// <summary>
        /// Creates a uniquely identified Harmony patch owner scoped to the current mod. The returned lease is
        /// automatically tracked by the mod lifetime and removes every patch owned by its Harmony instance when
        /// disposed or when the mod unloads.
        /// </summary>
        /// <param name="purpose">A short stable label describing the patch group.</param>
        /// <returns>The lifetime-owned Harmony lease.</returns>
        /// <exception cref="InvalidOperationException">
        /// Called outside Robotopia's game thread. Queue creation through the SDK scheduler before retrying.
        /// </exception>
        IHarmonyLease CreateHarmonyLease(string purpose);
    }

    /// <summary>
    /// Owns one uniquely identified Harmony patch group. This unstable contract is available only from the explicit
    /// interop package; it is not part of the safe SDK surface. Patch application and the first disposal must run on
    /// Robotopia's game thread. Disposal remains idempotent after teardown and may then be repeated from any thread.
    /// </summary>
    public interface IHarmonyLease : IDisposable
    {
        /// <summary>
        /// Applies supported Harmony patch methods under this lease's unique owner id. Null patch phases are omitted.
        /// </summary>
        /// <param name="original">The original method or constructor to patch.</param>
        /// <param name="prefix">Optional prefix method.</param>
        /// <param name="postfix">Optional postfix method.</param>
        /// <param name="transpiler">Optional transpiler method.</param>
        /// <param name="finalizer">Optional finalizer method.</param>
        /// <exception cref="InvalidOperationException">
        /// Called outside Robotopia's game thread. Queue the patch through the SDK scheduler before retrying.
        /// </exception>
        /// <exception cref="ObjectDisposedException">The lease has already begun teardown.</exception>
        void Patch(
            MethodBase original,
            MethodInfo? prefix = null,
            MethodInfo? postfix = null,
            MethodInfo? transpiler = null,
            MethodInfo? finalizer = null);

        /// <summary>Gets whether teardown has already run.</summary>
        bool IsDisposed { get; }
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

        /// <summary>
        /// Creates a uniquely identified Harmony owner whose teardown is guaranteed by the current mod lifetime.
        /// The manifest must declare <c>unsafe-native</c>.
        /// </summary>
        /// <param name="context">The current mod context.</param>
        /// <param name="purpose">A short stable label describing the patch group.</param>
        /// <returns>The owner-scoped, lifetime-tracked patch lease.</returns>
        /// <exception cref="InvalidOperationException">
        /// Called outside Robotopia's game thread, or native interop is unavailable. Queue creation through the SDK
        /// scheduler when invoking from worker-originated code.
        /// </exception>
        public static IHarmonyLease CreateHarmonyLease(this IModContext context, string purpose)
        {
            return RequireUnityInterop(context).CreateHarmonyLease(purpose);
        }
    }
}
