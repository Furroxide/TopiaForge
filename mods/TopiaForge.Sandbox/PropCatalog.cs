using System;
using System.Collections.Generic;
using System.Reflection;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.Sandbox
{
    internal enum SandboxPropKind
    {
        UgcAsset,
        Primitive
    }

    internal sealed class SandboxPropDefinition
    {
        public SandboxPropDefinition(string id, string displayName, SandboxPropKind kind, PrimitiveType primitive = PrimitiveType.Cube)
        {
            Id = id;
            DisplayName = displayName;
            Kind = kind;
            Primitive = primitive;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public SandboxPropKind Kind { get; }
        public PrimitiveType Primitive { get; }
    }

    /// <summary>
    /// The spawn menu's prop list: the game's own built-in UGC asset catalog (the same assets the in-game
    /// creator places), read reflectively from <c>UgcRuntimeAssetConfig</c>, plus guaranteed primitive shapes so
    /// the menu is never empty. All game access is clean-room reflection and degrades to primitives-only.
    /// </summary>
    internal sealed class PropCatalog
    {
        private readonly IModLogger logger;
        private readonly Func<object, bool>? isRobotPrefab;
        private readonly Type? importHostType;
        private readonly Type? assetConfigType;
        private readonly List<SandboxPropDefinition> items = new List<SandboxPropDefinition>();
        private object? assetConfig;
        private MethodInfo? tryGetPrefab;
        private bool ugcLoaded;
        private bool ugcFailureLogged;

        public PropCatalog(IModLogger logger, Func<object, bool>? isRobotPrefab = null)
        {
            this.logger = logger;
            this.isRobotPrefab = isRobotPrefab;
            importHostType = Type.GetType("UgcImportHostSceneController, GameCode", throwOnError: false);
            assetConfigType = Type.GetType("UgcRuntimeAssetConfig, GameCode", throwOnError: false);
            AppendPrimitives();
        }

        public IReadOnlyList<SandboxPropDefinition> Items => items;

        /// <summary>True once the UGC catalog has been read (primitives are always present regardless).</summary>
        public bool UgcAvailable => ugcLoaded;

        /// <summary>
        /// Attempts to read the UGC catalog. Safe to call every frame while the sandbox scene is still
        /// coming up — it no-ops once loaded and stays quiet (single Warn) when the game surface is missing.
        /// Returns true when the item list changed.
        /// </summary>
        public bool TryLoadUgcCatalog()
        {
            if (ugcLoaded)
            {
                return false;
            }

            try
            {
                assetConfig = ResolveAssetConfig();
                if (assetConfig == null)
                {
                    return false;
                }

                var assetIds = assetConfigType?.GetMethod(
                    "GetCompatibilityOnlyAssetIds",
                    BindingFlags.Public | BindingFlags.Instance)?.Invoke(assetConfig, null) as IEnumerable<string>;
                if (assetIds == null)
                {
                    return false;
                }

                tryGetPrefab = assetConfigType?.GetMethod(
                    "TryGetPrefab",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(string), typeof(GameObject).MakeByRefType() },
                    null);
                if (tryGetPrefab == null)
                {
                    return false;
                }

                var added = ReadAssetIds(assetIds);
                if (added > 0)
                {
                    ugcLoaded = true;
                    // UGC assets first (the interesting content), primitives at the end.
                    items.Sort((a, b) => a.Kind != b.Kind
                        ? (a.Kind == SandboxPropKind.UgcAsset ? -1 : 1)
                        : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
                    logger.Info("Sandbox prop catalog loaded: " + added + " UGC assets + primitives.");
                }

                return added > 0;
            }
            catch (Exception ex)
            {
                if (!ugcFailureLogged)
                {
                    ugcFailureLogged = true;
                    logger.Warn("Sandbox could not read the game's UGC asset catalog (primitives only): " + ex.Message);
                }

                return false;
            }
        }

        // CreatePrimitive's default material (built-in Standard shader) renders magenta in this HDRP build;
        // primitives get one shared HDRP/Lit material instead (bounded static cache — assemblies never unload).
        private static Material? primitiveMaterial;

        /// <summary>Instantiates a catalog item (no placement/physics — the spawner owns that).</summary>
        public bool TryInstantiate(SandboxPropDefinition definition, out GameObject instance)
        {
            instance = null!;
            if (definition.Kind == SandboxPropKind.Primitive)
            {
                instance = GameObject.CreatePrimitive(definition.Primitive);
                if (primitiveMaterial == null)
                {
                    var shader = Shader.Find("HDRP/Lit") ?? Shader.Find("Standard");
                    if (shader != null)
                    {
                        primitiveMaterial = new Material(shader)
                        {
                            name = "Sandbox Primitive",
                            color = new Color(0.75f, 0.78f, 0.82f),
                        };
                    }
                }

                if (primitiveMaterial != null)
                {
                    instance.GetComponent<Renderer>().sharedMaterial = primitiveMaterial;
                }

                return true;
            }

            try
            {
                if (assetConfig == null || tryGetPrefab == null)
                {
                    return false;
                }

                var arguments = new object?[] { definition.Id, null };
                if (!(tryGetPrefab.Invoke(assetConfig, arguments) is bool found) || !found)
                {
                    return false;
                }

                if (!(arguments[1] is GameObject prefab))
                {
                    return false;
                }

                instance = UnityEngine.Object.Instantiate(prefab);
                return true;
            }
            catch (Exception ex)
            {
                logger.Warn("Sandbox could not instantiate UGC asset '" + definition.Id + "': " + ex.Message);
                return false;
            }
        }

        private object? ResolveAssetConfig()
        {
            // Preferred: the sandbox scene's own import host carries the runtime config the game uses there.
            if (importHostType != null)
            {
                var host = UnityEngine.Object.FindAnyObjectByType(importHostType);
                var fromHost = host == null
                    ? null
                    : importHostType.GetProperty("RuntimeAssetConfig", BindingFlags.Public | BindingFlags.Instance)?.GetValue(host);
                if (fromHost != null)
                {
                    return fromHost;
                }
            }

            // Fallback: the config is a ScriptableObject loaded with the scene; find any loaded instance.
            if (assetConfigType != null)
            {
                var all = Resources.FindObjectsOfTypeAll(assetConfigType);
                if (all != null && all.Length > 0)
                {
                    return all[0];
                }
            }

            return null;
        }

        private int ReadAssetIds(IEnumerable<string> assetIds)
        {
            var added = 0;
            var filteredRobots = 0;

            foreach (var id in assetIds)
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                // Robots are not props — they belong to the NPC spawner. Keep the entry on any filter failure
                // (never lose props to a broken check).
                if (IsRobotEntry(id))
                {
                    filteredRobots++;
                    continue;
                }

                items.Add(new SandboxPropDefinition(id, DisplayNameFromId(id), SandboxPropKind.UgcAsset));
                added++;
            }

            if (filteredRobots > 0)
            {
                logger.Info("Sandbox prop catalog: filtered " + filteredRobots + " robot prefab(s) out of the prop list.");
            }

            return added;
        }

        private bool IsRobotEntry(string id)
        {
            if (isRobotPrefab == null || assetConfig == null || tryGetPrefab == null)
            {
                return false;
            }

            try
            {
                var arguments = new object?[] { id, null };
                if (!(tryGetPrefab.Invoke(assetConfig, arguments) is bool found) || !found
                    || !(arguments[1] is GameObject prefab))
                {
                    return false;
                }

                return isRobotPrefab(prefab);
            }
            catch
            {
                return false;
            }
        }

        private static string DisplayNameFromId(string id)
        {
            // "@robotopia/tree-model" -> "Tree Model"
            var name = id;
            var slash = name.LastIndexOf('/');
            if (slash >= 0 && slash < name.Length - 1)
            {
                name = name.Substring(slash + 1);
            }

            name = name.TrimStart('@').Replace('-', ' ').Replace('_', ' ');
            return name.Length == 0 ? id : char.ToUpperInvariant(name[0]) + name.Substring(1);
        }

        private void AppendPrimitives()
        {
            items.Add(new SandboxPropDefinition("primitive.cube", "Cube (primitive)", SandboxPropKind.Primitive, PrimitiveType.Cube));
            items.Add(new SandboxPropDefinition("primitive.sphere", "Sphere (primitive)", SandboxPropKind.Primitive, PrimitiveType.Sphere));
            items.Add(new SandboxPropDefinition("primitive.capsule", "Capsule (primitive)", SandboxPropKind.Primitive, PrimitiveType.Capsule));
            items.Add(new SandboxPropDefinition("primitive.cylinder", "Cylinder (primitive)", SandboxPropKind.Primitive, PrimitiveType.Cylinder));
        }
    }
}
