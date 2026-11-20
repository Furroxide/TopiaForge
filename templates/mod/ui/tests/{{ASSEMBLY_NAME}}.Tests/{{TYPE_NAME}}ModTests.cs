using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using TopiaForge.Mods.Testing;

namespace {{ASSEMBLY_NAME}}.Tests
{
    public sealed class {{TYPE_NAME}}ModTests
    {
        [Test]
        public void ManifestThemeStylesWindowAndPreviewUsesItsLocalOverride()
        {
            static string ReadPackageFile(string path)
            {
                const int maximumFixtureBytes = 1024 * 1024;
                using var fixture = File.OpenRead(Path.Combine(AppContext.BaseDirectory, path));
                var length = fixture.Length;
                if (length > maximumFixtureBytes)
                    throw new InvalidDataException("The package fixture exceeds its 1 MiB read limit.");

                var bytes = new byte[(int)length];
                fixture.ReadExactly(bytes);
                if (fixture.ReadByte() != -1)
                    throw new IOException("The package fixture grew while it was read.");

                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            }

            using var context = new FakeModContext();
            var catalogue = context.Ui.AddThemeCatalogue(ReadPackageFile("topiaforge.mod.json"), ReadPackageFile);
            Assert.That(catalogue.Succeeded, Is.True, catalogue.ErrorMessage);
            using var runner = ModLifecycleRunner.Create<{{TYPE_NAME}}Mod>(context);
            runner.Load();
            PressToggle(context);

            Assert.That(context.Ui.Themes.SelectedTheme, Is.Null);
            var surface = context.Ui.Surfaces.Single();
            context.Ui.CompletePendingThemeRegistrations();
            AssertPalette("#27614D", "survival");

            Assert.That(surface.ChangeText({{TYPE_NAME}}Mod.GreetingInputId, "Trail marked").Succeeded, Is.True);
            var content = surface.Content;
            Assert.That(surface.ChangeToggle({{TYPE_NAME}}Mod.ThemePreviewToggleId, true).Succeeded, Is.True);
            context.Ui.CompletePendingThemeRegistrations();
            Assert.That(context.Ui.Themes.SelectedTheme?.Id, Is.EqualTo("controls-preview"));
            AssertPalette("#51428D", "controls-preview");
            Assert.That(surface.Content, Is.SameAs(content));
            Assert.That(surface.TryGetTextValue({{TYPE_NAME}}Mod.GreetingInputId, out var greeting), Is.True);
            Assert.That(greeting, Is.EqualTo("Trail marked"));

            Assert.That(surface.ChangeToggle({{TYPE_NAME}}Mod.ThemePreviewToggleId, false).Succeeded, Is.True);
            context.Ui.CompletePendingThemeRegistrations();
            Assert.That(context.Ui.Themes.SelectedTheme, Is.Null);
            AssertPalette("#27614D", "survival");

            runner.Unload();
            context.AssertNoLeaks();

            void AssertPalette(string expectedInk, string expectedTheme)
            {
                var capture = surface.Capture(new UiCaptureViewport(1920, 1080));
                var heading = capture.Nodes.Single(node => node.Id == {{TYPE_NAME}}Mod.PaletteHeadingId);
                Assert.Multiple(() =>
                {
                    Assert.That(heading.ThemeId, Is.EqualTo(expectedTheme));
                    Assert.That(heading.InkColor, Is.EqualTo(expectedInk));
                    Assert.That(capture.CheckAccessibility(), Is.Empty);
                });
            }
        }

        [Test]
        public void ToggleAction_CreatesShowsAndHidesSafeUiSurface()
        {
            var context = new FakeModContext();
            using var runner = ModLifecycleRunner.Create<{{TYPE_NAME}}Mod>(context);
            runner.Load();

            Assert.Multiple(() =>
            {
                Assert.That(context.Input.ActiveActionCount, Is.EqualTo(1));
                Assert.That(context.Ui.Surfaces, Is.Empty);
            });

            PressToggle(context);
            var surface = context.Ui.Surfaces[0];
            Assert.Multiple(() =>
            {
                Assert.That(context.Ui.Surfaces, Has.Count.EqualTo(1));
                Assert.That(surface.IsVisible, Is.True);
                Assert.That(surface.Content, Is.Not.Null);
                Assert.That(surface.TryFindNode({{TYPE_NAME}}Mod.NotifyButtonId, out _), Is.True);
                Assert.That(surface.TryFindNode({{TYPE_NAME}}Mod.SettingsGridId, out var grid), Is.True);
                Assert.That(grid, Is.TypeOf<TopiaForge.Mods.UiGrid>());
                Assert.That(surface.TryFindNode({{TYPE_NAME}}Mod.SettingsSummaryId, out var summary), Is.True);
                Assert.That(((TopiaForge.Mods.UiText)summary!).Style, Is.EqualTo(TopiaForge.Mods.UiTextStyle.Mono));
            });

            Assert.That(surface.ChangeText({{TYPE_NAME}}Mod.GreetingInputId, "Hello, Robotopia!" ).Succeeded, Is.True);
            Assert.That(surface.ChangeDropdown({{TYPE_NAME}}Mod.ToneDropdownId, "success").Succeeded, Is.True);
            Assert.That(surface.SelectListItem({{TYPE_NAME}}Mod.TopicListId, "testing").Succeeded, Is.True);
            Assert.That(surface.ChangeSlider({{TYPE_NAME}}Mod.UiScaleSliderId, 1.25f).Succeeded, Is.True);
            Assert.That(surface.ActivateButton({{TYPE_NAME}}Mod.NotifyButtonId).Succeeded, Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(surface.Body, Does.Contain("Hello, Robotopia!"));
                Assert.That(surface.Body, Does.Contain("testing"));
                Assert.That(context.Ui.Accessibility.UiScale, Is.EqualTo(1.25f));
                Assert.That(context.Ui.Toasts, Has.Count.EqualTo(1));
                Assert.That(context.Ui.Toasts[0].Tone, Is.EqualTo(TopiaForge.Mods.UiTone.Success));
            });

            Assert.That(surface.ChangeToggle({{TYPE_NAME}}Mod.NotificationsToggleId, false).Succeeded, Is.True);
            Assert.That(surface.ActivateButton({{TYPE_NAME}}Mod.NotifyButtonId).Succeeded, Is.True);
            Assert.That(context.Ui.Toasts, Has.Count.EqualTo(1),
                "the toggle callback should disable new notifications");

            PressToggle(context);
            Assert.That(surface.IsVisible, Is.False);

            PressToggle(context);
            Assert.That(surface.IsVisible, Is.True);

            // The window lives as long as the mod: loading a world and returning to the main menu keep it, open, and the
            // toggle still closes and opens the same window.
            context.Scenes.Load("City Streets");
            context.Scenes.Load(TopiaForge.Mods.GameScenes.MainMenuSceneName);
            Assert.Multiple(() =>
            {
                Assert.That(context.Ui.Surfaces, Has.Count.EqualTo(1));
                Assert.That(context.Ui.Surfaces[0], Is.SameAs(surface));
                Assert.That(surface.IsVisible, Is.True);
            });
            PressToggle(context);
            Assert.That(surface.IsVisible, Is.False);
            PressToggle(context);
            Assert.That(surface.IsVisible, Is.True);

            runner.Unload();

            Assert.Multiple(() =>
            {
                Assert.That(context.Input.ActiveActionCount, Is.Zero);
                Assert.That(context.Ui.Surfaces, Is.Empty);
            });
            context.AssertNoLeaks();
        }

        private static void PressToggle(FakeModContext context)
        {
            context.Input.SetValue({{TYPE_NAME}}Mod.ToggleActionName, 0f);
            context.AdvanceFrame(TimeSpan.FromMilliseconds(16));
            context.Input.SetValue({{TYPE_NAME}}Mod.ToggleActionName, 1f);
            context.AdvanceFrame(TimeSpan.FromMilliseconds(16));
        }
    }
}
