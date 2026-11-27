using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.ModManager
{
    internal sealed class UnityUiLayoutDivider : MaskableGraphic
    {
        private bool dashed;

        internal void SetDashed(bool value)
        {
            if (dashed == value) return;
            dashed = value;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            var rect = GetPixelAdjustedRect();
            if (rect.width <= 0 || rect.height <= 0) return;
            if (!dashed)
            {
                AddDash(mesh, rect.xMin, rect.xMax, rect.yMin, rect.yMax, color);
                return;
            }
            for (var start = rect.xMin; start < rect.xMax; start += 10f)
            {
                AddDash(mesh, start, Mathf.Min(start + 6f, rect.xMax), rect.yMin, rect.yMax, color);
            }
        }

        private static void AddDash(VertexHelper mesh, float left, float right, float bottom, float top, Color32 color)
        {
            var first = mesh.currentVertCount;
            mesh.AddVert(new Vector2(left, bottom), color, Vector2.zero);
            mesh.AddVert(new Vector2(left, top), color, Vector2.zero);
            mesh.AddVert(new Vector2(right, top), color, Vector2.zero);
            mesh.AddVert(new Vector2(right, bottom), color, Vector2.zero);
            mesh.AddTriangle(first, first + 1, first + 2);
            mesh.AddTriangle(first, first + 2, first + 3);
        }
    }
}
