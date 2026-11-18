using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Draws a theme-defined rounded fill or an inset border without allocating a texture per radius.</summary>
    internal sealed class TopiaForgeRoundedImage : Image
    {
        private const int ArcSegments = 8;
        private const int OutlineVertices = 4 * (ArcSegments + 1);
        private float radius = -1f;
        private float border;

        public override Texture mainTexture => radius >= 0f ? Texture2D.whiteTexture : base.mainTexture;

        internal void SetShape(float? cornerRadius, float borderWidth = 0f)
        {
            var next = cornerRadius ?? -1f;
            if (radius == next && border == borderWidth) return;
            if ((radius < 0f) != (next < 0f)) SetMaterialDirty();
            radius = next;
            border = borderWidth;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            if (radius < 0f)
            {
                base.OnPopulateMesh(mesh);
                return;
            }

            mesh.Clear();
            var rect = GetPixelAdjustedRect();
            if (rect.width <= 0f || rect.height <= 0f) return;
            var outerRadius = Mathf.Min(radius, Mathf.Min(rect.width, rect.height) * 0.5f);
            var ink = (Color32)color;
            AddOutline(mesh, rect, outerRadius, ink);

            if (border <= 0f || Mathf.Min(rect.width, rect.height) <= 2f * border)
            {
                mesh.AddVert(rect.center, ink, Vector2.zero);
                for (var edge = 0; edge < OutlineVertices; edge++)
                    mesh.AddTriangle(OutlineVertices, (edge + 1) % OutlineVertices, edge);
                return;
            }

            var interior = Rect.MinMaxRect(rect.xMin + border, rect.yMin + border, rect.xMax - border, rect.yMax - border);
            AddOutline(mesh, interior, Mathf.Max(0f, outerRadius - border), ink);
            for (var edge = 0; edge < OutlineVertices; edge++)
            {
                var next = (edge + 1) % OutlineVertices;
                mesh.AddTriangle(edge, edge + OutlineVertices, next + OutlineVertices);
                mesh.AddTriangle(edge, next + OutlineVertices, next);
            }
        }

        private static void AddOutline(VertexHelper mesh, Rect rect, float radius, Color32 ink)
        {
            for (var corner = 0; corner < 4; corner++)
            {
                var centerX = corner < 2 ? rect.xMax - radius : rect.xMin + radius;
                var centerY = corner == 1 || corner == 2 ? rect.yMax - radius : rect.yMin + radius;
                for (var segment = 0; segment <= ArcSegments; segment++)
                {
                    var bearing = (corner - 1f + segment / (float)ArcSegments) * (Mathf.PI * 0.5f);
                    mesh.AddVert(new Vector2(centerX + Mathf.Cos(bearing) * radius,
                        centerY + Mathf.Sin(bearing) * radius), ink, Vector2.zero);
                }
            }
        }
    }
}
