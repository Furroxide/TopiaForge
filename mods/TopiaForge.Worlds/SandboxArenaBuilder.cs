using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.Worlds
{
    /// <summary>
    /// Builds the Open Sandbox arena geometry: a gm_construct-style creator stage made from primitives —
    /// flat ground, boundary walls, a spawn platform, ramps, stairs, pillars, loose-feeling blocks, and
    /// tinted colour zones. Everything is static scenery (colliders, no rigidbodies) parented under the
    /// caller's root so the existing arena teardown (destroy the root) cleans all of it up.
    /// </summary>
    internal static class SandboxArenaBuilder
    {
        public const float GroundSize = 200f;
        private const float WallHeight = 10f;

        // CreatePrimitive's default material uses the built-in Standard shader, which this HDRP build does
        // not ship — primitives render magenta with it. Each tint therefore gets a real HDRP/Lit material
        // (the GravityGunModel pattern), cached per colour: the set of arena colours is small and constant,
        // and mod assemblies never unload under Mono, so a bounded static cache is the leak-free option.
        private static readonly Dictionary<Color, Material> TintMaterials = new Dictionary<Color, Material>();

        /// <summary>Builds the arena centred at <paramref name="center"/> (the sandbox player spawn).</summary>
        public static void Build(GameObject root, Vector3 center, IModLogger logger)
        {
            BuildGroundAndWalls(root, center);

            try
            {
                BuildSpawnPlatform(root, center);
                BuildRamps(root, center);
                BuildStairs(root, center);
                BuildPillars(root, center);
                BuildBlocks(root, center);
                BuildColorZones(root, center);
            }
            catch (Exception ex)
            {
                // The ground/walls above are the playable minimum; decorative content failing (e.g. a shader
                // rename breaking tints) must not take the whole arena down with it.
                logger.Warn("Worlds sandbox arena decoration failed part-way (arena stays playable): " + ex.Message);
            }
        }

        private static void BuildGroundAndWalls(GameObject root, Vector3 center)
        {
            var ground = Spawn(root, "Sandbox Ground", center + new Vector3(0f, -0.5f, 0f),
                new Vector3(GroundSize, 1f, GroundSize));
            Tint(ground, new Color(0.42f, 0.44f, 0.40f));

            var half = GroundSize / 2f;
            for (var index = 0; index < 4; index++)
            {
                var wall = Spawn(root, "Sandbox Boundary " + index,
                    center + index switch
                    {
                        0 => new Vector3(0f, WallHeight / 2f, half),
                        1 => new Vector3(0f, WallHeight / 2f, -half),
                        2 => new Vector3(half, WallHeight / 2f, 0f),
                        _ => new Vector3(-half, WallHeight / 2f, 0f)
                    },
                    index < 2
                        ? new Vector3(GroundSize, WallHeight, 1f)
                        : new Vector3(1f, WallHeight, GroundSize));
                Tint(wall, new Color(0.55f, 0.53f, 0.48f));
            }
        }

        private static void BuildSpawnPlatform(GameObject root, Vector3 center)
        {
            var platform = Spawn(root, "Sandbox Spawn Platform", center + new Vector3(0f, 0.2f, 0f),
                new Vector3(12f, 0.4f, 12f));
            Tint(platform, new Color(0.78f, 0.70f, 0.55f));
        }

        private static void BuildRamps(GameObject root, Vector3 center)
        {
            var rampA = Spawn(root, "Sandbox Ramp A", center + new Vector3(18f, 1.6f, 10f),
                new Vector3(6f, 0.5f, 16f), Quaternion.Euler(-12f, 0f, 0f));
            Tint(rampA, new Color(0.62f, 0.60f, 0.55f));

            var rampB = Spawn(root, "Sandbox Ramp B", center + new Vector3(-22f, 2.2f, -14f),
                new Vector3(8f, 0.5f, 22f), Quaternion.Euler(11f, 35f, 0f));
            Tint(rampB, new Color(0.62f, 0.60f, 0.55f));
        }

        private static void BuildStairs(GameObject root, Vector3 center)
        {
            // A five-step block staircase up to a small lookout slab: cheap parkour + a physgun vantage point.
            for (var step = 0; step < 5; step++)
            {
                var height = 1.5f + step * 3f;
                var block = Spawn(root, "Sandbox Stair " + step,
                    center + new Vector3(-10f - step * 3f, height / 2f, 22f),
                    new Vector3(3f, height, 6f));
                Tint(block, new Color(0.50f, 0.55f, 0.60f));
            }

            var lookout = Spawn(root, "Sandbox Lookout", center + new Vector3(-28f, 13.75f, 22f),
                new Vector3(8f, 0.5f, 8f));
            Tint(lookout, new Color(0.78f, 0.70f, 0.55f));
        }

        private static void BuildPillars(GameObject root, Vector3 center)
        {
            var positions = new[]
            {
                new Vector3(35f, 6f, -30f),
                new Vector3(42f, 6f, -22f),
                new Vector3(30f, 6f, 35f),
                new Vector3(-40f, 6f, 30f)
            };
            for (var index = 0; index < positions.Length; index++)
            {
                var pillar = Spawn(root, "Sandbox Pillar " + index, center + positions[index],
                    new Vector3(3f, 12f, 3f));
                Tint(pillar, new Color(0.58f, 0.56f, 0.52f));
            }
        }

        private static void BuildBlocks(GameObject root, Vector3 center)
        {
            // Oversized static blocks: cover to hide behind and surfaces to throw spawned props against.
            var block = Spawn(root, "Sandbox Block A", center + new Vector3(12f, 3f, -25f), new Vector3(6f, 6f, 6f));
            Tint(block, new Color(0.72f, 0.48f, 0.30f));

            var slab = Spawn(root, "Sandbox Block B", center + new Vector3(-30f, 2f, -35f), new Vector3(14f, 4f, 4f));
            Tint(slab, new Color(0.72f, 0.48f, 0.30f));
        }

        private static void BuildColorZones(GameObject root, Vector3 center)
        {
            // Flat tinted pads toward the corners — gm_construct's colour rooms, minus the rooms. Handy as
            // spawn-sorting areas and as visual landmarks for orientation on an otherwise uniform ground.
            var zones = new[]
            {
                (position: new Vector3(70f, 0.11f, 70f), color: new Color(0.85f, 0.30f, 0.28f)),
                (position: new Vector3(-70f, 0.11f, 70f), color: new Color(0.32f, 0.72f, 0.38f)),
                (position: new Vector3(70f, 0.11f, -70f), color: new Color(0.30f, 0.50f, 0.85f))
            };
            for (var index = 0; index < zones.Length; index++)
            {
                var pad = Spawn(root, "Sandbox Color Zone " + index, center + zones[index].position,
                    new Vector3(20f, 0.2f, 20f));
                Tint(pad, zones[index].color);
            }
        }

        private static GameObject Spawn(GameObject root, string name, Vector3 position, Vector3 scale)
        {
            return Spawn(root, name, position, scale, Quaternion.identity);
        }

        private static GameObject Spawn(GameObject root, string name, Vector3 position, Vector3 scale, Quaternion rotation)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            cube.transform.SetParent(root.transform, false);
            cube.transform.localScale = scale;
            cube.transform.SetPositionAndRotation(position, rotation);
            return cube;
        }

        private static void Tint(GameObject target, Color color)
        {
            var renderer = target.GetComponent<Renderer>();
            if (renderer == null)
            {
                return;
            }

            if (!TintMaterials.TryGetValue(color, out var material) || material == null)
            {
                var shader = Shader.Find("HDRP/Lit")
                    ?? Shader.Find("Standard")
                    ?? Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null)
                {
                    return; // keep whatever the primitive shipped with rather than assigning a null shader
                }

                material = new Material(shader) { name = "Sandbox Arena Tint", color = color };
                TintMaterials[color] = material;
            }

            renderer.sharedMaterial = material;
        }
    }
}
