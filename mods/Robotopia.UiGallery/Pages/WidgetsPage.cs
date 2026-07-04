using Robotopia.Mods.UnityUi;

namespace Robotopia.UiGallery.Pages
{
    /// <summary>Every basic control in its states: buttons, toggles, sliders, inputs, badges.</summary>
    internal static class WidgetsPage
    {
        public static void Build(QwContainer page)
        {
            page.SectionHeader("TYPOGRAPHY");
            page.Label("Display 26 — Audiowide", QwTextStyle.Display);
            page.Label("Title 22 — Audiowide", QwTextStyle.Title);
            page.Label("Heading 16 — Quicksand Bold", QwTextStyle.Heading);
            page.Label("Body 14 — Quicksand. The quick brown robot jumps over the lazy zombie.", QwTextStyle.Body);
            page.Label("Caption 12 — muted detail text", QwTextStyle.Caption).Tone(QwTone.Muted);

            page.SectionHeader("BUTTONS");
            var buttons = page.Row(QwGap.Sm);
            buttons.Button("FILLED", Noop);
            buttons.Button("OUTLINE", Noop, QwButtonStyle.Outline);
            buttons.Button("GHOST", Noop, QwButtonStyle.Ghost);
            buttons.Button("DANGER", Noop, QwButtonStyle.Danger);
            buttons.IconButton(QwIcon.Cross, Noop);
            var disabledRow = page.Row(QwGap.Sm);
            var disabled = disabledRow.Button("DISABLED", Noop);
            disabled.SetEnabled(false);
            disabledRow.Button("WITH TOOLTIP", Noop, QwButtonStyle.Outline).Tooltip("Tooltips appear after a 450ms hover and follow the cursor.");

            page.SectionHeader("TOGGLES + SLIDERS");
            page.Toggle("Switch (on)", true, Noop);
            page.Toggle("Switch (off)", false, Noop);
            page.Checkbox("Checkbox", true, Noop);
            page.Slider("Volume", 0f, 1f, 0.65f, Noop);

            page.SectionHeader("INPUTS");
            page.Input("Type here…", string.Empty, Noop);
            page.SearchInput("Search mods…", Noop);
            var errorField = page.Input("This one is angry", "bad value", Noop);
            errorField.SetError(true);
            page.Keybind("Toggle gallery", QwKey.F8, Noop);
            page.Dropdown(new[] { "Balanced", "Performance", "Potato" }, 0, Noop);

            page.SectionHeader("BADGES + PROGRESS");
            var badges = page.Row(QwGap.Sm);
            badges.Badge("NEUTRAL");
            badges.Badge("ACCENT", QwTone.Accent);
            badges.Badge("ENABLED", QwTone.Success);
            badges.Badge("RESTART", QwTone.Warning);
            badges.Badge("PENDING REMOVE", QwTone.Danger);
            page.ProgressBar().SetFraction(0.35f);
            var stat = page.StatBar("INTEGRITY 87");
            stat.Thresholds(0.5f, 0.25f);
            stat.SetFraction(0.87f);
            var low = page.StatBar("INTEGRITY 12");
            low.Thresholds(0.5f, 0.25f);
            low.SetFraction(0.12f);
            var pips = page.PipRow();
            pips.SetCount(6);
            pips.SetFilled(3, 0.6f);
        }

        private static void Noop()
        {
        }

        private static void Noop(bool _)
        {
        }

        private static void Noop(float _)
        {
        }

        private static void Noop(string _)
        {
        }

        private static void Noop(int _)
        {
        }

        private static void Noop(QwKey _)
        {
        }
    }
}
