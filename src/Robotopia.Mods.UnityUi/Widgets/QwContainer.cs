using UnityEngine;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>
    /// A widget that hosts children and exposes the authoring factories, so consumer
    /// code reads top-down: create a container, then col.Label(...), row.Button(...).
    /// </summary>
    public class QwContainer : QwWidget
    {
        public QwContainer(UiHost host, QwScheme scheme, GameObject go)
            : base(host, scheme, go)
        {
        }

        // ---- structure ----

        /// <summary>Vertical stack with token gap/padding.</summary>
        public QwContainer Column(QwGap gap = QwGap.Sm, QwGap padding = QwGap.None, bool expandChildWidth = true)
        {
            var child = CreateChild("Column");
            QwLayout.ApplyColumn(child.Go, gap, padding, expandChildWidth);
            return child;
        }

        /// <summary>Horizontal row with token gap/padding.</summary>
        public QwContainer Row(QwGap gap = QwGap.Sm, QwGap padding = QwGap.None, bool expandChildWidth = false)
        {
            var child = CreateChild("Row");
            QwLayout.ApplyRow(child.Go, gap, padding, expandChildWidth);
            return child;
        }

        /// <summary>No layout management — children place themselves (HUD free rects).</summary>
        public QwContainer Stack(string name = "Stack")
        {
            var child = CreateChild(name);
            QwAnchors.Stretch(child.Rect);
            return child;
        }

        /// <summary>Fixed-cell grid.</summary>
        public QwContainer Grid(float cellWidth, float cellHeight, QwGap gap = QwGap.Sm, QwGap padding = QwGap.None)
        {
            var child = CreateChild("Grid");
            QwLayout.ApplyGrid(child.Go, cellWidth, cellHeight, gap, padding);
            return child;
        }

        /// <summary>Flexible empty space inside a Row/Column.</summary>
        public QwWidget Spacer(float flex = 1f)
        {
            var child = CreateChild("Spacer");
            return child.Flex(flex, flex);
        }

        // ---- widgets ----

        public QwLabel Label(string text, QwTextStyle style = QwTextStyle.Body)
        {
            return new QwLabel(this, text, style);
        }

        public QwLabel Label(QwTextStyle style)
        {
            return new QwLabel(this, string.Empty, style);
        }

        public QwButton Button(string text, System.Action onClick, QwButtonStyle style = QwButtonStyle.Filled)
        {
            return new QwButton(this, text, onClick, style);
        }

        public QwButton IconButton(QwIcon icon, System.Action onClick, QwButtonStyle style = QwButtonStyle.Ghost)
        {
            return new QwButton(this, icon, onClick, style);
        }

        public QwPanel Panel(QwPanelStyle style = QwPanelStyle.Plain)
        {
            return new QwPanel(this, style);
        }

        /// <summary>Free-placed raw image (reticles, vignettes, flashes). Not layout-managed.</summary>
        public QwImage FreeImage(string name = "Image")
        {
            return new QwImage(this, name, free: true);
        }

        /// <summary>Layout-managed raw image.</summary>
        public QwImage Image(string name = "Image")
        {
            return new QwImage(this, name, free: false);
        }

        /// <summary>1px brand divider line.</summary>
        public QwImage Divider()
        {
            var divider = new QwImage(this, "Divider", free: false);
            divider.FixedHeight(QwTokens.BorderHairline);
            divider.SetColor(Theme.Tint);
            return divider;
        }

        internal QwContainer CreateChild(string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(Go.transform, false);
            return new QwContainer(Host, Scheme, go);
        }

        internal GameObject CreateChildGameObject(string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(Go.transform, false);
            return go;
        }
    }
}
