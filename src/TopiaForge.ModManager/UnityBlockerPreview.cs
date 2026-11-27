using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;
using UnityEngine.Rendering;

namespace TopiaForge.ModManager
{
    /// <summary>Local developer drawing. No renderer or extra collider is ever added to a blocker.</summary>
    internal static class UnityBlockerPreview
    {
        private static readonly Dictionary<Transform, Vector3> boxes = new Dictionary<Transform, Vector3>();
        private static Mesh? mesh;
        private static Material? material;
        private static int themeVersion;
        internal static bool Visible { get; set; }

        internal static void Track(Transform root, Vec3 size) => boxes[root] = UnityPhysicsBackend.ToUnity(size);
        internal static void Untrack(Transform root) => boxes.Remove(root);

        internal static void Draw()
        {
            if (!Visible || boxes.Count == 0) return;
            if (material == null)
            {
                var shader = Shader.Find("HDRP/Unlit");
                if (shader == null) return;
                material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, renderQueue = (int)RenderQueue.Transparent };
                material.SetFloat("_SurfaceType", 1);
                material.SetFloat("_BlendMode", 0);
                material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                material.SetInt("_AlphaSrcBlend", (int)BlendMode.One);
                material.SetInt("_AlphaDstBlend", (int)BlendMode.OneMinusSrcAlpha);
                material.SetInt("_ZWrite", 0);
                material.SetInt("_CullMode", (int)CullMode.Off);
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.EnableKeyword("_BLENDMODE_ALPHA");
                themeVersion = 0;
            }
            if (themeVersion != TopiaForgeTheme.Version)
            {
                var tint = new TopiaForgeResolvedTheme(TopiaForgeScheme.Hud, null).Accent;
                tint.a = TopiaForgeTheme.HighContrast ? 0.4f : 0.22f;
                material.SetColor("_UnlitColor", tint);
                themeVersion = TopiaForgeTheme.Version;
            }
            if (mesh == null) mesh = CreateBoxMesh();
            foreach (var box in boxes)
            {
                if (box.Key == null || !box.Key.gameObject.activeInHierarchy) continue;
                Graphics.DrawMesh(mesh, Matrix4x4.TRS(box.Key.position, box.Key.rotation, Vector3.Scale(box.Key.lossyScale, box.Value)),
                    material, 2, null, 0, null, ShadowCastingMode.Off, false);
            }
        }

        private static Mesh CreateBoxMesh()
        {
            var result = new Mesh { name = "TopiaForge.BlockerPreview", hideFlags = HideFlags.HideAndDontSave };
            result.vertices = new[]
            {
                new Vector3(-.5f, -.5f, -.5f), new Vector3(.5f, -.5f, -.5f),
                new Vector3(.5f, .5f, -.5f), new Vector3(-.5f, .5f, -.5f),
                new Vector3(-.5f, -.5f, .5f), new Vector3(.5f, -.5f, .5f),
                new Vector3(.5f, .5f, .5f), new Vector3(-.5f, .5f, .5f)
            };
            result.triangles = new[] { 0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4,
                3, 7, 6, 3, 6, 2, 0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5 };
            result.RecalculateBounds();
            return result;
        }

        internal static void Reset()
        {
            Visible = false;
            boxes.Clear();
            if (mesh != null) UnityEngine.Object.Destroy(mesh);
            if (material != null) UnityEngine.Object.Destroy(material);
            mesh = null;
            material = null;
        }
    }
}
