using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace {{ASSEMBLY_NAME}}.Tests
{
    public sealed class {{TYPE_NAME}}ModTests
    {
        [Test]
        public void ManifestThemeStylesWindowAndPreviewUsesItsLocalOverride()
        {
            using var context = new FakeModContext();
            AddThemeFixtures(context);
            using var runner = ModLifecycleRunner.Create<{{TYPE_NAME}}Mod>(context);
            runner.Load();
            var surface = OpenSettings(context);

            Assert.That(context.Ui.Themes.SelectedTheme, Is.Null);
            context.Ui.CompletePendingThemeRegistrations();
            AssertPalette("#27614D", "survival");
            AssertArtwork();

            SelectTab(surface, {{TYPE_NAME}}Mod.ControlsTabId);
            Assert.That(surface.ChangeText({{TYPE_NAME}}Mod.GreetingInputId, "Trail marked").Succeeded, Is.True);
            SelectTab(surface, {{TYPE_NAME}}Mod.GeneralTabId);
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

            void AssertArtwork()
            {
                Assert.That(surface.TryFindNode({{TYPE_NAME}}Mod.ContainImageId, out var first), Is.True);
                Assert.That(surface.TryFindNode({{TYPE_NAME}}Mod.CoverImageId, out var second), Is.True);
                Assert.That(surface.TryFindNode({{TYPE_NAME}}Mod.PreviewMarkId, out var mark), Is.True);
                var contain = (UiImage)first!;
                var cover = (UiImage)second!;
                var capture = surface.Capture(new UiCaptureViewport(1920, 1080));
                var containBox = capture.Nodes.Single(node => node.Id == {{TYPE_NAME}}Mod.ContainImageId);
                var coverBox = capture.Nodes.Single(node => node.Id == {{TYPE_NAME}}Mod.CoverImageId);
                var contained = capture.Nodes.Single(node => node.Path == containBox.Path + "/sprite");
                var cropped = capture.Nodes.Single(node => node.Path == coverBox.Path + "/sprite");
                Assert.Multiple(() =>
                {
                    Assert.That(contain.Image, Is.EqualTo(new UiImageRef("preview-art", "card")));
                    Assert.That(cover.Image, Is.EqualTo(contain.Image));
                    Assert.That(contain.Fit, Is.EqualTo(UiImageFit.Contain));
                    Assert.That(cover.Fit, Is.EqualTo(UiImageFit.Cover));
                    Assert.That((cover.Width, cover.Height), Is.EqualTo((contain.Width, contain.Height)));
                    Assert.That((coverBox.Width, coverBox.Height), Is.EqualTo((containBox.Width, containBox.Height)));
                    Assert.That(contained.Height, Is.LessThan(containBox.Height));
                    Assert.That(contained.Y, Is.GreaterThan(containBox.Y));
                    Assert.That((cropped.Width, cropped.Height), Is.EqualTo((coverBox.Width, coverBox.Height)));
                    Assert.That(((UiImage)mark!).Tint, Is.EqualTo(UiTone.Primary));
                    Assert.That(((UiImage)mark!).Decorative, Is.True);
                    Assert.That(containBox.SemanticLabel, Is.EqualTo("Sample card"));
                    Assert.That(coverBox.SemanticLabel, Is.EqualTo(containBox.SemanticLabel));
                    Assert.That(contained.IsRendered && cropped.IsRendered && !containBox.UsedFallback && !coverBox.UsedFallback, Is.True);
                    Assert.That(capture.Nodes.Any(node => node.Text == "Contain" && node.IsRendered), Is.True);
                    Assert.That(capture.Nodes.Any(node => node.Text == "Cover" && node.IsRendered), Is.True);
                });
            }
        }

        [Test]
        public void ToggleAction_CreatesShowsAndHidesSafeUiSurface()
        {
            using var context = new FakeModContext();
            AddThemeFixtures(context);
            using var runner = ModLifecycleRunner.Create<{{TYPE_NAME}}Mod>(context);
            runner.Load();

            Assert.Multiple(() =>
            {
                Assert.That(context.Input.ActiveActionCount, Is.EqualTo(1));
                Assert.That(context.Ui.Surfaces, Is.Empty);
            });

            var surface = OpenSettings(context);
            context.Ui.CompletePendingThemeRegistrations();
            Assert.Multiple(() =>
            {
                Assert.That(context.Ui.Surfaces, Has.Count.EqualTo(1));
                Assert.That(surface.IsVisible, Is.True);
                Assert.That(surface.Content, Is.Not.Null);
                Assert.That(surface.TryFindNode({{TYPE_NAME}}Mod.NotifyButtonId, out _), Is.True);
                Assert.That(surface.TryFindNode({{TYPE_NAME}}Mod.SettingsGridId, out var grid), Is.True);
                Assert.That(grid, Is.TypeOf<UiGrid>());
            });
            var tabs = Find<UiTabs>(surface, {{TYPE_NAME}}Mod.SettingsTabsId);
            Assert.That(tabs.Style, Is.EqualTo(UiTabsStyle.Rail));
            Assert.That(tabs.Tabs.Select(tab => tab.Label), Is.EqualTo(new[] { "General", "Controls", "Accessibility" }));

            SelectTab(surface, {{TYPE_NAME}}Mod.ControlsTabId);
            Assert.That(surface.ChangeText({{TYPE_NAME}}Mod.GreetingInputId, "Hello, Robotopia!").Succeeded, Is.True);
            Assert.That(surface.ChangeDropdown({{TYPE_NAME}}Mod.ToneDropdownId, "success").Succeeded, Is.True);
            Assert.That(surface.SelectListItem({{TYPE_NAME}}Mod.TopicListId, "testing").Succeeded, Is.True);
            Assert.That(surface.ActivateButton({{TYPE_NAME}}Mod.NotifyButtonId).Succeeded, Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(surface.Body, Does.Contain("Hello, Robotopia!"));
                Assert.That(surface.Body, Does.Contain("testing"));
                Assert.That(context.Ui.Toasts, Has.Count.EqualTo(1));
                Assert.That(context.Ui.Toasts[0].Tone, Is.EqualTo(UiTone.Success));
            });

            Assert.That(surface.TryFindNode({{TYPE_NAME}}Mod.NotificationBadgeId, out var badge), Is.True);
            Assert.That(((UiBadge)badge!).Text, Is.EqualTo("Notifications on"));
            Assert.That(surface.ChangeCheckbox({{TYPE_NAME}}Mod.NotificationsCheckboxId, false).Succeeded, Is.True);
            Assert.That(surface.TryFindNode({{TYPE_NAME}}Mod.NotificationBadgeId, out badge), Is.True);
            Assert.That(((UiBadge)badge!).Text, Is.EqualTo("Notifications off"));
            Assert.That(surface.TryGetSelectedTab({{TYPE_NAME}}Mod.SettingsTabsId, out var selected), Is.True);
            Assert.That(selected, Is.EqualTo({{TYPE_NAME}}Mod.ControlsTabId));
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

        [Test]
        public void AccessibilityChangesPreserveAuthoredTabAndDisableIntensityDuringReducedMotion()
        {
            using var context = new FakeModContext();
            AddThemeFixtures(context);
            Assert.That(context.Ui.ApplyAccessibility(new UiAccessibilityPreferences(uiScale: 1.13f)).Succeeded, Is.True);
            using var runner = ModLifecycleRunner.Create<{{TYPE_NAME}}Mod>(context);
            runner.Load();
            var surface = OpenSettings(context);
            context.Ui.CompletePendingThemeRegistrations();
            SelectTab(surface, {{TYPE_NAME}}Mod.AccessibilityTabId);

            var scale = Find<UiSlider>(surface, {{TYPE_NAME}}Mod.UiScaleSliderId);
            var intensity = Find<UiSlider>(surface, {{TYPE_NAME}}Mod.MotionIntensitySliderId);
            Assert.Multiple(() =>
            {
                Assert.That((scale.Minimum, scale.Maximum, scale.Divisions), Is.EqualTo((0.75f, 2f, 5)));
                Assert.That((scale.Format.Kind, scale.Format.Decimals), Is.EqualTo((UiValueFormatKind.Multiplier, 2)));
                Assert.That(scale.Value, Is.EqualTo(1.25f));
                Assert.That(context.Ui.Accessibility.UiScale, Is.EqualTo(1.13f));
                Assert.That((intensity.Minimum, intensity.Maximum), Is.EqualTo((0f, 2f)));
                Assert.That((intensity.Format.Kind, intensity.Format.Decimals), Is.EqualTo((UiValueFormatKind.Number, 1)));
            });
            Assert.That(surface.ChangeSlider({{TYPE_NAME}}Mod.UiScaleSliderId, 1.98f).Succeeded, Is.True);
            Assert.That(surface.ChangeSlider({{TYPE_NAME}}Mod.MotionIntensitySliderId, 0.5f).Succeeded, Is.True);
            Assert.That(surface.ChangeToggle({{TYPE_NAME}}Mod.HighContrastToggleId, true).Succeeded, Is.True);
            Assert.That(surface.ChangeToggle({{TYPE_NAME}}Mod.ReducedMotionToggleId, true).Succeeded, Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(context.Ui.Accessibility.UiScale, Is.EqualTo(2f));
                Assert.That(context.Ui.Accessibility.HighContrast, Is.True);
                Assert.That(context.Ui.Accessibility.ReducedMotion, Is.True);
                Assert.That(Find<UiTabs>(surface, {{TYPE_NAME}}Mod.SettingsTabsId).SelectedTabId,
                    Is.EqualTo({{TYPE_NAME}}Mod.AccessibilityTabId));
                Assert.That(surface.TryGetSelectedTab({{TYPE_NAME}}Mod.SettingsTabsId, out var tab), Is.True);
                Assert.That(tab, Is.EqualTo({{TYPE_NAME}}Mod.AccessibilityTabId));
                Assert.That(Find<UiSlider>(surface, {{TYPE_NAME}}Mod.MotionIntensitySliderId).Enabled, Is.False);
            });
            Assert.That(surface.ChangeSlider({{TYPE_NAME}}Mod.MotionIntensitySliderId, 1.5f).Succeeded, Is.False);
            Assert.That(context.Ui.Accessibility.MotionIntensity, Is.EqualTo(0.5f));

            foreach (var viewport in new[] { new UiCaptureViewport(1920, 1080), new UiCaptureViewport(2560, 1440) })
            {
                var capture = surface.Capture(viewport);
                var issues = capture.CheckAccessibility();
                Assert.That(issues, Is.Empty, string.Join(", ", issues.Select(issue => issue.Kind + " " + issue.Path)));
                Assert.That(capture.Nodes.Any(node => node.Text == "2.00×" && node.IsRendered), Is.True);
                Assert.That(capture.Nodes.Any(node => node.Text == "0.5" && node.IsRendered), Is.True);
                Assert.That(capture.Nodes.Any(node => node.Text == "Off while reduced motion is on." && node.IsRendered), Is.True);
                Assert.That(capture.Nodes.Any(node => node.Id == {{TYPE_NAME}}Mod.PanelBindingId && node.IsRendered && !node.Clipped),
                    Is.True);
                Assert.That(capture.Nodes.Any(node => node.Id == {{TYPE_NAME}}Mod.DoneButtonId && node.IsRendered && !node.Clipped),
                    Is.True);
            }
            Assert.That(surface.ChangeToggle({{TYPE_NAME}}Mod.ReducedMotionToggleId, false).Succeeded, Is.True);
            Assert.That(Find<UiSlider>(surface, {{TYPE_NAME}}Mod.MotionIntensitySliderId).Enabled, Is.True);
            Assert.That(surface.ChangeSlider({{TYPE_NAME}}Mod.MotionIntensitySliderId, 1.5f).Succeeded, Is.True);
            Assert.That(context.Ui.Accessibility.MotionIntensity, Is.EqualTo(1.5f));
        }

        [Test]
        public void ResetRestoresTheRegisteredPanelShortcutAndItsLiveKeyLabel()
        {
            using var context = new FakeModContext();
            AddThemeFixtures(context);
            Assert.That(context.Config.Save({{TYPE_NAME}}Config.Definition, new {{TYPE_NAME}}Config { ToggleKey = "J" }).Succeeded,
                Is.True);
            using var runner = ModLifecycleRunner.Create<{{TYPE_NAME}}Mod>(context);
            runner.Load();
            var surface = OpenSettings(context);
            context.Ui.CompletePendingThemeRegistrations();
            SelectTab(surface, {{TYPE_NAME}}Mod.AccessibilityTabId);
            var action = context.Input.GetAction({{TYPE_NAME}}Mod.ToggleActionName);
            var field = Find<UiKeyBindingField>(surface, {{TYPE_NAME}}Mod.PanelBindingId);
            Assert.That(field.ActionName, Is.EqualTo(action.Name));
            Assert.That(field.Label, Is.EqualTo("Open this panel"));
            AssertKey("J");

            Assert.That(surface.CaptureBinding({{TYPE_NAME}}Mod.PanelBindingId, InputBinding.Key("K")).Succeeded, Is.True);
            Assert.That(action.Bindings, Is.EqualTo(new[] { InputBinding.Key("K") }));
            AssertKey("K");

            var unchanged = surface.Content;
            Assert.That(surface.ActivateButton({{TYPE_NAME}}Mod.ResetBindingsId).Succeeded, Is.True);
            Assert.That(action.Bindings, Is.EqualTo(new[] { InputBinding.Key("J") }));
            Assert.That(surface.Content, Is.SameAs(unchanged));
            AssertKey("J");
            Assert.That(surface.ActivateButton({{TYPE_NAME}}Mod.DoneButtonId).Succeeded, Is.True);
            Assert.That(surface.IsVisible, Is.False);

            void AssertKey(string key)
            {
                var capture = surface.Capture(new UiCaptureViewport(1920, 1080));
                Assert.That(capture.Nodes.Any(node => node.KeyLabel == key && node.IsRendered), Is.True,
                    "The visible key field should show the action's current binding.");
            }
        }

        private static T Find<T>(FakeUiSurface surface, string id) where T : UiNode
        {
            Assert.That(surface.TryFindNode(id, out var node), Is.True);
            Assert.That(node, Is.TypeOf<T>());
            return (T)node!;
        }

        private static void SelectTab(FakeUiSurface surface, string tabId) =>
            Assert.That(surface.SelectTab({{TYPE_NAME}}Mod.SettingsTabsId, tabId).Succeeded, Is.True);

        private static FakeUiSurface OpenSettings(FakeModContext context)
        {
            PressToggle(context);
            Assert.That(context.Ui.Surfaces, Has.Count.EqualTo(1),
                string.Join(Environment.NewLine, context.Logger.Entries.Select(entry => entry.Message)));
            return context.Ui.Surfaces[0];
        }

        private static void AddThemeFixtures(FakeModContext context)
        {
            static byte[] Read(string path, int maximumBytes = 1024 * 1024)
            {
                using var input = File.OpenRead(Path.Combine(AppContext.BaseDirectory, path));
                var length = input.Length;
                if (length > maximumBytes)
                    throw new InvalidDataException("The test fixture exceeds its size limit.");
                var bytes = new byte[(int)length];
                input.ReadExactly(bytes);
                if (input.ReadByte() != -1)
                    throw new IOException("The test fixture grew while it was read.");
                return bytes;
            }
            static string ReadStrictUtf8(string path) => new UTF8Encoding(false, true).GetString(Read(path));
            static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

            const string bundlePath = "ui/art/preview.bundle";
            var hash = Sha256(Read(bundlePath, 32 * 1024 * 1024));
            var receiptPath = "ui/receipts/" + Sha256(Encoding.UTF8.GetBytes(bundlePath)) + ".json";
            using var receipt = JsonDocument.Parse(Read(receiptPath));
            var document = receipt.RootElement;
            Assert.That(document.GetProperty("bundleSha256").GetString(), Is.EqualTo(hash));
            var textures = document.GetProperty("textures");
            foreach (var sprite in document.GetProperty("sprites").EnumerateArray())
            {
                var texture = textures[sprite.GetProperty("texture").GetInt32()];
                var mask = texture.GetProperty("format").GetString() == "Alpha8";
                var bytes = (long)texture.GetProperty("width").GetInt32() * texture.GetProperty("height").GetInt32() * (mask ? 1 : 4);
                context.Ui.AddAssetFixture(new FakeUiAssetMetadata(bundlePath, sprite.GetProperty("assetName").GetString()!, hash,
                    sprite.GetProperty("width").GetInt32(), sprite.GetProperty("height").GetInt32(), bytes, isMask: mask));
            }

            var catalogue = context.Ui.AddThemeCatalogue(ReadStrictUtf8("topiaforge.mod.json"), ReadStrictUtf8);
            Assert.That(catalogue.Succeeded, Is.True, catalogue.ErrorMessage);
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
