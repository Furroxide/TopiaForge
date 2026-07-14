using TopiaForge.Mods.UnityUi;

namespace TopiaForge.UiGallery.Pages
{
    /// <summary>Virtualization proof: a 1,000-row list stays a dozen pooled rows.</summary>
    internal static class ListsPage
    {
        public static void Build(TopiaForgeContainer page)
        {
            page.SectionHeader("VIRTUALIZED LIST (1,000 ROWS)");
            page.Label("Scroll: only the visible rows exist. Click to select; selection survives scrolling.", TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);

            var items = new string[1000];
            for (var index = 0; index < items.Length; index++)
            {
                items[index] = "Package " + (index + 1);
            }

            var status = page.Label("Nothing selected", TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);

            var list = page.ListView<string>();
            list.FixedHeight(320f);
            list.Bind((row, item, index) =>
            {
                row.Title.SetText(item);
                row.Subtitle.SetText("1.0." + (index % 40));
                row.Badge.Set(index % 7 == 0 ? "RESTART" : "ENABLED", index % 7 == 0 ? TopiaForgeTone.Warning : TopiaForgeTone.Success);
            });
            list.OnSelected(index => status.SetText("Selected: " + items[index]));
            list.SetItems(items);

            page.SectionHeader("CARD GRID");
            page.Label("Cards wrap to the window width. Hover for the ring + tooltip; click to select.", TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);
            var cardStatus = page.Label("No card selected", TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);
            var grid = page.Grid(118f, 148f, TopiaForgeGap.Sm);
            var cards = new TopiaForgeCard[6];
            for (var index = 0; index < cards.Length; index++)
            {
                var selectedIndex = index;
                var card = grid.Card("Sample " + (index + 1), () =>
                {
                    cardStatus.SetText("Selected: Sample " + (selectedIndex + 1));
                    for (var other = 0; other < cards.Length; other++)
                    {
                        cards[other].SetSelected(other == selectedIndex);
                    }
                });
                card.Tooltip("Sample " + (index + 1) + "\nA TopiaForgeCard: preview + caption + badge.");
                card.SetBadge(index % 2 == 0 ? "UGC" : "PRIM", index % 2 == 0 ? TopiaForgeTone.Accent : TopiaForgeTone.Neutral);
                cards[index] = card;
            }

            page.SectionHeader("KEY-VALUE ROWS");
            page.KeyValueRow("Mode", "trusted local packages");
            page.KeyValueRow("Restart required", "NO");
            page.KeyValueRow("Loaded mods", "11");
        }
    }
}
