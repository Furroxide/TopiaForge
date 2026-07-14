using TopiaForge.Mods.UnityUi;

namespace TopiaForge.UiGallery.Pages
{
    /// <summary>Every basic control in its states: buttons, toggles, sliders, inputs, badges.</summary>
    internal static class WidgetsPage
    {
        public static void Build(TopiaForgeContainer page)
        {
            page.SectionHeader("TYPOGRAPHY");
            page.Label("Display 26 — Audiowide", TopiaForgeTextStyle.Display);
            page.Label("Title 22 — Audiowide", TopiaForgeTextStyle.Title);
            page.Label("Heading 16 — Quicksand Bold", TopiaForgeTextStyle.Heading);
            page.Label("Body 14 — Quicksand. The quick brown robot jumps over the lazy zombie.", TopiaForgeTextStyle.Body);
            page.Label("Caption 12 — muted detail text", TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);

            page.SectionHeader("BUTTONS");
            var buttons = page.Row(TopiaForgeGap.Sm);
            buttons.Button("FILLED", Noop);
            buttons.Button("OUTLINE", Noop, TopiaForgeButtonStyle.Outline);
            buttons.Button("GHOST", Noop, TopiaForgeButtonStyle.Ghost);
            buttons.Button("DANGER", Noop, TopiaForgeButtonStyle.Danger);
            buttons.IconButton(TopiaForgeIcon.Cross, Noop);
            var disabledRow = page.Row(TopiaForgeGap.Sm);
            var disabled = disabledRow.Button("DISABLED", Noop);
            disabled.SetEnabled(false);
            disabledRow.Button("WITH TOOLTIP", Noop, TopiaForgeButtonStyle.Outline).Tooltip("Tooltips appear after a 450ms hover and follow the cursor.");

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
            page.Keybind("Toggle gallery", TopiaForgeKey.F8, Noop);
            page.Dropdown(new[] { "Balanced", "Performance", "Potato" }, 0, Noop);

            page.SectionHeader("BADGES + PROGRESS");
            var badges = page.Row(TopiaForgeGap.Sm);
            badges.Badge("NEUTRAL");
            badges.Badge("ACCENT", TopiaForgeTone.Accent);
            badges.Badge("ENABLED", TopiaForgeTone.Success);
            badges.Badge("RESTART", TopiaForgeTone.Warning);
            badges.Badge("PENDING REMOVE", TopiaForgeTone.Danger);
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

            page.SectionHeader("SYSTEM STATES");
            page.Label("LOADING  Discovering installed packages…", TopiaForgeTextStyle.Label).Tone(TopiaForgeTone.Accent);
            page.Label("EMPTY  No compatible mods found in this source.", TopiaForgeTextStyle.Body).Tone(TopiaForgeTone.Muted);
            page.Label("WARNING  Restart required before these changes take effect.", TopiaForgeTextStyle.Body)
                .Tone(TopiaForgeTone.Warning);
            page.Label("ERROR  Package validation failed; open Diagnostics for details.", TopiaForgeTextStyle.Body)
                .Tone(TopiaForgeTone.Danger);
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

        private static void Noop(TopiaForgeKey _)
        {
        }
    }
}
