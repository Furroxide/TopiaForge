using System;
using System.Collections.Generic;

namespace TopiaForge.Mods
{
    /// <summary>Provides children in source and paint order without allocating a collection for wrappers.</summary>
    internal readonly struct UiNodeChildren
    {
        private readonly IReadOnlyList<UiNode>? many;
        private readonly UiNode? first;
        private readonly UiNode? second;

        private UiNodeChildren(IReadOnlyList<UiNode> children)
        {
            many = children;
            first = null;
            second = null;
        }

        private UiNodeChildren(UiNode? first, UiNode? second = null)
        {
            many = null;
            this.first = first;
            this.second = second;
        }

        internal static UiNodeChildren Of(UiNode node) => node switch
        {
            UiLayoutNode layout => new UiNodeChildren(layout.Children),
            UiPadding padding => new UiNodeChildren(padding.Child),
            UiSizedBox sized => new UiNodeChildren(sized.Child),
            UiExpanded expanded => new UiNodeChildren(expanded.Child),
            UiPositioned positioned => new UiNodeChildren(positioned.Child),
            UiCard card => card.Header is UiNode header ? new UiNodeChildren(header, card.Child) : new UiNodeChildren(card.Child),
            UiScroll scroll => new UiNodeChildren(scroll.Content),
            UiSplitPane split => new UiNodeChildren(split.Primary, split.Secondary),
            _ => default,
        };

        internal int Count => many?.Count ?? (second != null ? 2 : first != null ? 1 : 0);

        internal UiNode this[int index]
        {
            get
            {
                if (many != null) return many[index];
                if (index == 0 && first != null) return first;
                if (index == 1 && second != null) return second;
                throw new ArgumentOutOfRangeException(nameof(index));
            }
        }
    }
}
