using System;
using System.Collections.Generic;

namespace TopiaForge.Mods
{
    /// <summary>
    /// Well-known ids published by the framework mods, for consumers that must not reference their
    /// assemblies. The publishing mod asserts these at runtime, so they cannot silently drift.
    /// </summary>
    public static class WellKnownIds
    {
        /// <summary>The freeform Sandbox gamemode registered by io.github.furroxide.topiaforge.worlds.</summary>
        public const string SandboxGamemodeId = "io.github.furroxide.topiaforge.worlds.sandbox";

        /// <summary>The generated Open Sandbox world registered by io.github.furroxide.topiaforge.worlds.</summary>
        public const string OpenSandboxWorldId = "io.github.furroxide.topiaforge.worlds.open_sandbox";
    }

    /// <summary>
    /// Mod-provided content backing a custom world (registered via
    /// <see cref="IWorldGamemodeService.RegisterWorld(WorldDefinition, ICustomWorldContent)"/>).
    /// Launching such a world loads the game's clean play stage (a real player spawns natively) and the
    /// world service places this content at the player spawn.
    /// </summary>
    public interface ICustomWorldContent
    {
        /// <summary>
        /// Returns the world content: either a prefab asset (typically loaded from a mod-shipped
        /// AssetBundle) or a freshly created scene instance (procedural worlds). Must be a
        /// <c>UnityEngine.GameObject</c> at runtime (typed <c>object</c> to keep this assembly Unity-free).
        /// The world service instantiates prefab assets itself, takes ownership of the resulting root,
        /// repositions it so its spawn point aligns with the player spawn, and destroys it on session end.
        /// Return <c>null</c> (after logging why) to fail the launch cleanly. Called once per launch, on
        /// the main thread.
        /// </summary>
        object? CreateContentRoot();

        CustomWorldOptions Options { get; }
    }

    /// <summary>Placement/behaviour options for a custom world's content.</summary>
    public sealed class CustomWorldOptions
    {
        public static CustomWorldOptions Default => new CustomWorldOptions();

        /// <summary>
        /// Name of the descendant transform (any depth, case-insensitive) marking where the player stands.
        /// When absent the content root's pivot is used (with a warning).
        /// </summary>
        public string SpawnPointName { get; set; } = "SpawnPoint";

        /// <summary>
        /// Apply the framework's default HDRP sky/exposure/sun. Automatically skipped when the content
        /// carries its own global HDRP Volume.
        /// </summary>
        public bool ApplyDefaultEnvironment { get; set; } = true;

        /// <summary>Respawn the player at the spawn point when it falls below the kill depth.</summary>
        public bool EnableKillPlane { get; set; } = true;

        /// <summary>Metres below the spawn point before the kill plane triggers.</summary>
        public float KillPlaneDepth { get; set; } = 100f;
    }

    /// <summary>Options for <see cref="ModContextExtensions.RegisterWorldFromBundle"/>.</summary>
    public sealed class BundleWorldOptions
    {
        /// <summary>Stable unique world id, e.g. "mymod.worlds.skyisland". Required.</summary>
        public string Id { get; set; } = "";

        /// <summary>Display name. Required.</summary>
        public string Name { get; set; } = "";

        public string Description { get; set; } = "";

        /// <summary>Path of the AssetBundle inside the mod package, e.g. "AssetBundles/skyisland.bundle". Required.</summary>
        public string BundleRelativePath { get; set; } = "";

        /// <summary>Prefab asset to use as the world root. Blank = the bundle's single *.prefab.</summary>
        public string PrefabAssetName { get; set; } = "";

        public CustomWorldOptions Content { get; set; } = CustomWorldOptions.Default;

        /// <summary>Also register a menu entry pairing this world with the Sandbox gamemode (default true).</summary>
        public bool RegisterSandboxMenuEntry { get; set; } = true;

        /// <summary>Menu entry id. Blank = <see cref="Id"/> + ".menu".</summary>
        public string MenuEntryId { get; set; } = "";
    }

    /// <summary>
    /// <see cref="ICustomWorldContent"/> backed by a prefab inside a mod-shipped AssetBundle. Lazy: the
    /// bundle is loaded on first launch through <c>IAssetBundleService</c> (io.github.furroxide.topiaforge.assets), which
    /// caches it; a missing service or asset logs a descriptive error and fails that launch only.
    /// </summary>
    public sealed class BundleWorldContent : ICustomWorldContent
    {
        private readonly IModContext context;
        private readonly string bundleRelativePath;
        private readonly string prefabAssetName;

        public BundleWorldContent(
            IModContext context,
            string bundleRelativePath,
            string prefabAssetName = "",
            CustomWorldOptions? options = null)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            if (string.IsNullOrWhiteSpace(bundleRelativePath))
            {
                throw new ArgumentException("A bundle path relative to the mod package is required.", nameof(bundleRelativePath));
            }

            this.bundleRelativePath = bundleRelativePath;
            this.prefabAssetName = prefabAssetName ?? "";
            Options = options ?? CustomWorldOptions.Default;
        }

        public CustomWorldOptions Options { get; }

        public object? CreateContentRoot()
        {
            try
            {
                var assets = context.RequireService<IAssetBundleService>();
                var load = assets.LoadBundle(new AssetBundleLoadRequest(
                    context.ModId, context.Paths.PackagePath, bundleRelativePath));
                if (!load.Ok || load.Bundle == null)
                {
                    context.Logger.Error("Custom world bundle '" + bundleRelativePath + "' failed to load: " + load.Error);
                    return null;
                }

                var assetName = ResolvePrefabName(assets, load.Bundle);
                if (assetName == null)
                {
                    return null;
                }

                // The non-generic overload takes the Unity type explicitly, keeping this assembly free of
                // any UnityEngine reference; this only ever executes inside the game process.
                var gameObjectType = Type.GetType("UnityEngine.GameObject, UnityEngine.CoreModule")
                    ?? Type.GetType("UnityEngine.GameObject, UnityEngine");
                if (gameObjectType == null)
                {
                    context.Logger.Error("Custom world content requires a Unity runtime (UnityEngine.GameObject was not found).");
                    return null;
                }

                var asset = assets.LoadAsset(load.Bundle, assetName, gameObjectType);
                if (!asset.Ok || asset.Asset == null)
                {
                    context.Logger.Error("Custom world prefab '" + assetName + "' failed to load from '"
                        + bundleRelativePath + "': " + asset.Error);
                    return null;
                }

                return asset.Asset;
            }
            catch (Exception ex)
            {
                context.Logger.Error(ex, "Custom world content for bundle '" + bundleRelativePath + "' could not be created.");
                return null;
            }
        }

        private string? ResolvePrefabName(IAssetBundleService assets, IAssetBundleHandle bundle)
        {
            if (!string.IsNullOrWhiteSpace(prefabAssetName))
            {
                return prefabAssetName;
            }

            var prefabs = new List<string>();
            foreach (var name in assets.GetAllAssetNames(bundle))
            {
                if (name.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    prefabs.Add(name);
                }
            }

            if (prefabs.Count == 1)
            {
                return prefabs[0];
            }

            context.Logger.Error(prefabs.Count == 0
                ? "Custom world bundle '" + bundleRelativePath + "' contains no prefab."
                : "Custom world bundle '" + bundleRelativePath + "' contains " + prefabs.Count
                    + " prefabs; set PrefabAssetName to pick one of: " + string.Join(", ", prefabs));
            return null;
        }
    }
}
