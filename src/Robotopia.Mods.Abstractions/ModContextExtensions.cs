using System;
using System.Diagnostics.CodeAnalysis;

namespace Robotopia.Mods
{
    /// <summary>Convenience helpers over <see cref="IModContext"/> for resolving cross-mod services.</summary>
    public static class ModContextExtensions
    {
        /// <summary>
        /// Resolves a required service, throwing a descriptive <see cref="InvalidOperationException"/> (naming the
        /// service type) when it is not registered — a clearer failure than the silent <c>null</c> that
        /// <see cref="IModContext.GetService{T}"/> returns. Use for services your mod cannot function without.
        /// </summary>
        public static T RequireService<T>(this IModContext context) where T : class
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var service = context.GetService<T>();
            if (service == null)
            {
                throw new InvalidOperationException(
                    "Required mod service '" + typeof(T).FullName + "' is not available. Declare a dependency on " +
                    "the mod that publishes it (and 'loadAfter' it) so it is registered before this mod loads.");
            }

            return service;
        }

        /// <summary>
        /// Tries to resolve an optional service. Returns <c>true</c> and sets <paramref name="service"/> when the
        /// service is registered; otherwise returns <c>false</c>. Use for services your mod can run without.
        /// </summary>
        public static bool TryGetService<T>(this IModContext context, [NotNullWhen(true)] out T? service) where T : class
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            service = context.GetService<T>();
            return service != null;
        }

        public static AssetBundleLoadResult LoadAssetBundle(
            this IModContext context,
            string relativePath,
            AssetBundleLoadOptions? options = null)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            return context.RequireService<IAssetBundleService>().LoadBundle(
                new AssetBundleLoadRequest(context.ModId, context.Paths.PackagePath, relativePath, options));
        }

        public static AssetLoadResult<T> LoadAsset<T>(
            this IModContext context,
            IAssetBundleHandle bundle,
            string assetName) where T : class
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            return context.RequireService<IAssetBundleService>().LoadAsset<T>(bundle, assetName);
        }

        public static SpawnAssetResult<T> SpawnAsset<T>(this IModContext context, T prefab) where T : class
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            return context.RequireService<IAssetBundleService>().SpawnAsset(prefab);
        }

        public static IPromptOverrideHandle RegisterPromptOverride(
            this IModContext context,
            string promptId,
            string replacementText,
            int priority = 0,
            string description = "")
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            return context.RequireService<IPromptOverrideRegistry>().Register(
                new PromptOverrideRequest(context.ModId, promptId, replacementText, priority, description));
        }
    }
}
