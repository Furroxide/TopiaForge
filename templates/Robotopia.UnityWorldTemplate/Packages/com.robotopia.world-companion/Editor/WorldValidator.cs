using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Robotopia.WorldCompanion.Editor
{
    /// <summary>
    /// Validates a world prefab against the runtime contract BEFORE it ships, so the errors that would
    /// otherwise only surface in-game (missing scripts, no spawn marker) fail the build here instead:
    /// the game hosts the prefab in its own play scene and cannot resolve modder MonoBehaviours.
    /// </summary>
    public static class WorldValidator
    {
        public const string SpawnPointName = "SpawnPoint";
        private const float MaxAxisMetres = 2000f;

        public sealed class Result
        {
            public List<string> Errors { get; } = new List<string>();
            public List<string> Warnings { get; } = new List<string>();
        }

        [MenuItem("Robotopia/Validate World Prefab")]
        public static void ValidateFromMenu()
        {
            var prefabPath = "Assets/World/World.prefab";
            var selected = Selection.activeObject != null
                ? AssetDatabase.GetAssetPath(Selection.activeObject)
                : string.Empty;
            if (selected.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                prefabPath = selected;
            }

            var result = Validate(prefabPath);
            var summary = result.Errors.Count == 0
                ? "Valid." + (result.Warnings.Count > 0 ? "\n\nWarnings:\n" + string.Join("\n", result.Warnings) : string.Empty)
                : "Errors:\n" + string.Join("\n", result.Errors)
                    + (result.Warnings.Count > 0 ? "\n\nWarnings:\n" + string.Join("\n", result.Warnings) : string.Empty);
            EditorUtility.DisplayDialog("Robotopia — " + prefabPath, summary, "OK");
        }

        public static Result Validate(string prefabPath)
        {
            var result = new Result();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                result.Errors.Add("World prefab not found or not a prefab: " + prefabPath);
                return result;
            }

            CheckSpawnPoint(prefab, result);
            CheckComponents(prefab, result);
            CheckBounds(prefab, result);
            return result;
        }

        private static void CheckSpawnPoint(GameObject prefab, Result result)
        {
            if (FindDescendant(prefab.transform, SpawnPointName) == null)
            {
                result.Errors.Add(
                    "No descendant named '" + SpawnPointName + "': add an empty child marking where the player "
                    + "stands (>= 1m above walkable ground).");
            }
        }

        private static void CheckComponents(GameObject prefab, Result result)
        {
            var hasCollider = false;
            foreach (var transform in prefab.GetComponentsInChildren<Transform>(true))
            {
                // Missing scripts deserialize as null components.
                var missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject);
                if (missing > 0)
                {
                    result.Errors.Add(transform.name + " has " + missing + " missing script(s) — remove them.");
                }

                foreach (var component in transform.GetComponents<Component>())
                {
                    if (component == null)
                    {
                        continue; // already reported as missing above
                    }

                    if (component is Collider)
                    {
                        hasCollider = true;
                    }

                    // The game cannot resolve modder scripts inside a content bundle: only engine/package
                    // components (colliders, lights, HDRP volumes, audio, LODs...) survive the trip.
                    var assembly = component.GetType().Assembly.GetName().Name ?? string.Empty;
                    if (!assembly.StartsWith("UnityEngine", StringComparison.Ordinal)
                        && !assembly.StartsWith("Unity.", StringComparison.Ordinal))
                    {
                        result.Errors.Add(
                            transform.name + " carries custom component '" + component.GetType().FullName
                            + "' (" + assembly + ") — world prefabs must use native Unity/HDRP components only.");
                    }
                }
            }

            if (!hasCollider)
            {
                result.Errors.Add(
                    "No collider anywhere in the prefab — the player would fall straight through. Add colliders "
                    + "to all walkable geometry (MeshCollider for Blender imports).");
            }
        }

        private static void CheckBounds(GameObject prefab, Result result)
        {
            var renderers = prefab.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                result.Warnings.Add("The prefab has no renderers — the world would be invisible.");
                return;
            }

            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers)
            {
                bounds.Encapsulate(renderer.bounds);
            }

            if (bounds.size == Vector3.zero)
            {
                result.Warnings.Add("Renderer bounds are degenerate (zero size).");
            }

            if (bounds.size.x > MaxAxisMetres || bounds.size.y > MaxAxisMetres || bounds.size.z > MaxAxisMetres)
            {
                result.Warnings.Add(
                    "World is very large (" + bounds.size + "); expect long load/placement times.");
            }

            var spawnPoint = FindDescendant(prefab.transform, SpawnPointName);
            if (spawnPoint != null)
            {
                var expanded = bounds;
                expanded.Expand(10f);
                if (!expanded.Contains(spawnPoint.position))
                {
                    result.Warnings.Add(
                        "'" + SpawnPointName + "' sits outside the world's renderer bounds — the player may spawn on a void.");
                }
            }
        }

        private static Transform FindDescendant(Transform root, string name)
        {
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (transform != root && string.Equals(transform.name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return transform;
                }
            }

            return null;
        }
    }
}
