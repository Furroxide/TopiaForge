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
            PressToggle(context);

            Assert.That(context.Ui.Themes.SelectedTheme, Is.Null);
            var surface = context.Ui.Surfaces.Single();
            context.Ui.CompletePendingThemeRegistrations();
            AssertPalette("#27614D", "survival");
            AssertArtwork();

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

            PressToggle(context);
            context.Ui.CompletePendingThemeRegistrations();
            var surface = context.Ui.Surfaces[0];
            Assert.Multiple(() =>
            {
                Assert.That(context.Ui.Surfaces, Has.Count.EqualTo(1));
                Assert.That(surface.IsVisible, Is.True);
                Assert.That(surface.Content, Is.Not.Null);
                Assert.That(surface.TryFindNode({{TYPE_NAME}}Mod.NotifyButtonId, out _), Is.True);
                Assert.That(surface.TryFindNode({{TYPE_NAME}}Mod.SettingsGridId, out var grid), Is.True);
                Assert.That(grid, Is.TypeOf<UiGrid>());
                Assert.That(surface.TryFindNode({{TYPE_NAME}}Mod.SettingsSummaryId, out var summary), Is.True);
                Assert.That(((UiText)summary!).Style, Is.EqualTo(UiTextStyle.Mono));
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
                Assert.That(context.Ui.Toasts[0].Tone, Is.EqualTo(UiTone.Success));
            });

            Assert.That(surface.TryFindNode({{TYPE_NAME}}Mod.NotificationBadgeId, out var badge), Is.True);
            Assert.That(((UiBadge)badge!).Text, Is.EqualTo("Notifications on"));
            Assert.That(surface.ChangeToggle({{TYPE_NAME}}Mod.NotificationsToggleId, false).Succeeded, Is.True);
            Assert.That(surface.TryFindNode({{TYPE_NAME}}Mod.NotificationBadgeId, out badge), Is.True);
            Assert.That(((UiBadge)badge!).Text, Is.EqualTo("Notifications off"));
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
