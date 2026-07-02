using Robotopia.Mods.UnityUi;

namespace Robotopia.UiGallery.Pages
{
    /// <summary>Virtualization proof: a 1,000-row list stays a dozen pooled rows.</summary>
    internal static class ListsPage
    {
        public static void Build(QwContainer page)
        {
            page.SectionHeader("VIRTUALIZED LIST (1,000 ROWS)");
            page.Label("Scroll: only the visible rows exist. Click to select; selection survives scrolling.", QwTextStyle.Caption).Tone(QwTone.Muted);

            var items = new string[1000];
            for (var index = 0; index < items.Length; index++)
            {
                items[index] = "Package " + (index + 1);
            }

            var status = page.Label("Nothing selected", QwTextStyle.Caption).Tone(QwTone.Muted);

            var list = page.ListView<string>();
            list.FixedHeight(320f);
            list.Bind((row, item, index) =>
            {
                row.Title.SetText(item);
                row.Subtitle.SetText("1.0." + (index % 40));
                row.Badge.Set(index % 7 == 0 ? "RESTART" : "ENABLED", index % 7 == 0 ? QwTone.Warning : QwTone.Success);
            });
            list.OnSelected(index => status.SetText("Selected: " + items[index]));
            list.SetItems(items);

            page.SectionHeader("KEY-VALUE ROWS");
            page.KeyValueRow("Mode", "trusted local packages");
            page.KeyValueRow("Restart required", "NO");
            page.KeyValueRow("Loaded mods", "11");
        }
    }
}
