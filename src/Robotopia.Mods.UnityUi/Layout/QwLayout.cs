using UnityEngine;
using UnityEngine.UI;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>
    /// Layout-group configuration with token spacing/padding. Wraps the two boilerplate
    /// blocks NeonUi repeated, with sane child-control defaults.
    /// </summary>
    internal static class QwLayout
    {
        public static void ApplyColumn(GameObject go, QwGap gap, QwGap padding, bool expandChildWidth = true)
        {
            var layout = QwComponents.GetOrAdd<VerticalLayoutGroup>(go);
            layout.spacing = (int)gap;
            var pad = (int)padding;
            layout.padding = new RectOffset(pad, pad, pad, pad);
            layout.childForceExpandWidth = expandChildWidth;
            layout.childForceExpandHeight = false;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childAlignment = TextAnchor.UpperLeft;
        }

        public static void ApplyRow(GameObject go, QwGap gap, QwGap padding, bool expandChildWidth = false)
        {
            var layout = QwComponents.GetOrAdd<HorizontalLayoutGroup>(go);
            layout.spacing = (int)gap;
            var pad = (int)padding;
            layout.padding = new RectOffset(pad, pad, pad, pad);
            layout.childForceExpandWidth = expandChildWidth;
            layout.childForceExpandHeight = false;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childAlignment = TextAnchor.MiddleLeft;
        }

        public static void ApplyGrid(GameObject go, float cellWidth, float cellHeight, QwGap gap, QwGap padding)
        {
            var layout = QwComponents.GetOrAdd<GridLayoutGroup>(go);
            layout.cellSize = new Vector2(cellWidth, cellHeight);
            layout.spacing = new Vector2((int)gap, (int)gap);
            var pad = (int)padding;
            layout.padding = new RectOffset(pad, pad, pad, pad);
        }
    }
}
