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

        /// <summary>
        /// Registers a custom world whose content is a prefab inside a mod-shipped AssetBundle, plus (by
        /// default) a menu entry pairing it with the Sandbox gamemode. The bundle is loaded lazily on the
        /// world's first launch through robotopia.assets — declare that dependency in the manifest.
        /// Returns the registered definition (useful for a matching <c>UnregisterWorld</c> on unload).
        /// </summary>
        public static WorldDefinition RegisterWorldFromBundle(
            this IModContext context,
            IWorldGamemodeService worlds,
            BundleWorldOptions options)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (worlds == null)
            {
                throw new ArgumentNullException(nameof(worlds));
            }

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (string.IsNullOrWhiteSpace(options.Id))
            {
                throw new ArgumentException("BundleWorldOptions.Id is required.", nameof(options));
            }

            if (string.IsNullOrWhiteSpace(options.Name))
            {
                throw new ArgumentException("BundleWorldOptions.Name is required.", nameof(options));
            }

            if (string.IsNullOrWhiteSpace(options.BundleRelativePath))
            {
                throw new ArgumentException("BundleWorldOptions.BundleRelativePath is required.", nameof(options));
            }

            var definition = new WorldDefinition(options.Id, options.Name, options.Description);
            worlds.RegisterWorld(definition, new BundleWorldContent(
                context, options.BundleRelativePath, options.PrefabAssetName, options.Content));

            if (options.RegisterSandboxMenuEntry)
            {
                var menuEntryId = string.IsNullOrWhiteSpace(options.MenuEntryId)
                    ? options.Id + ".menu"
                    : options.MenuEntryId;
                worlds.RegisterMenuEntry(new GamemodeMenuEntry(
                    menuEntryId, options.Name, options.Description,
                    WellKnownIds.SandboxGamemodeId, options.Id));
            }

            return definition;
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
