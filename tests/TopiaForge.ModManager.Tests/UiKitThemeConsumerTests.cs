using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.TestSupport;
using TopiaForge.Zombies;

namespace TopiaForge.ModManager.Tests
{
    internal static class UiKitThemeConsumerTests
    {
        private const string SurvivalPath = "ui/themes/survival.json";
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        internal static void Run()
        {
            SurvivalCopiesMatch();
            ZombiesHudUsesTheManifestTheme();
        }

        private static void SurvivalCopiesMatch()
        {
            var root = Program.FindRepoRoot();
            var gallery = Path.Combine(root, "mods", "TopiaForge.UiGallery");
            var zombies = Path.Combine(root, "mods", "TopiaForge.Zombies");
            var template = Path.Combine(root, "templates", "mod", "ui");
            var canonicalHash = Hash(gallery);
            Require(canonicalHash.SequenceEqual(Hash(zombies)), "Zombies must ship the canonical Survival theme bytes.");
            Require(canonicalHash.SequenceEqual(Hash(template)), "The UI template must ship the canonical Survival theme bytes.");

            using var galleryManifest = ReadJson(gallery, "topiaforge.mod.json");
            using var zombiesManifest = ReadJson(zombies, "topiaforge.mod.json");
            using var templateDefaults = ReadJson(template, "template.json");
            CheckDeclaration(galleryManifest.RootElement);
            CheckDeclaration(zombiesManifest.RootElement);
            CheckDeclaration(templateDefaults.RootElement.GetProperty("manifestDefaults"));

            static byte[] Hash(string directory) => SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, SurvivalPath)));

            static JsonDocument ReadJson(string directory, string name) =>
                JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, name)));

            static void CheckDeclaration(JsonElement manifest)
            {
                var ui = manifest.GetProperty("ui");
                Require(ui.GetProperty("schema").GetInt32() == 1, "Each consumer uses the schema 1 UI catalogue.");
                Require(ui.GetProperty("themes").EnumerateArray().Any(theme =>
                    theme.GetProperty("id").GetString() == "survival"
                    && theme.GetProperty("definition").GetString() == SurvivalPath),
                    "The Survival pack must be declared by its own consumer.");
                Require(ui.TryGetProperty("default", out var defaultTheme) && defaultTheme.GetString() == "survival",
                    "Gallery, Zombies and the UI template inherit the Survival default.");
            }
        }

        private static void ZombiesHudUsesTheManifestTheme()
        {
            var root = Path.Combine(Program.FindRepoRoot(), "mods", "TopiaForge.Zombies");
            using var context = new FakeModContext();
            string ReadFile(string path) => StrictUtf8.GetString(File.ReadAllBytes(Path.Combine(root, path)));
            var declared = context.Ui.AddThemeCatalogue(ReadFile("topiaforge.mod.json"), ReadFile);
            Require(declared.Succeeded, declared.ErrorMessage);
            context.Scenes.Load("Training Yard");
            var registered = context.Input.RegisterAction(new InputActionDefinition(
                "fire-zapper", "Fire", new[] { InputBinding.Key("K") }));
            Require(registered.TryGetValue(out var fire), registered.ErrorMessage);

            using var presenter = new ZombiesHudPresenter(context);
            var snapshot = new ZombiesHudSnapshot(
                ZombiesPhase.Wave, 3, 7, 1, 2, 84, 100, 420, 25, 2, 2, 3, 0, 0, true, true);
            presenter.Update(snapshot, fire, null, null, null);
            context.Ui.CompletePendingThemeRegistrations();

            var surface = context.Ui.Surfaces.Single();
            Require(context.Ui.Themes.SelectedTheme == null, "Zombies inherits its manifest selection.");
            AssertRole(ZombiesHudPresenter.PhaseNodeId, "HORDE ACTIVE", UiTextStyle.Title);
            AssertRole(ZombiesHudPresenter.WaveNodeId, "WAVE  3", UiTextStyle.Label);
            AssertRole(ZombiesHudPresenter.ScoreNodeId, "SCORE  420  x2", UiTextStyle.Numeral);

            var capture = surface.Capture(new UiCaptureViewport(1920, 1080));
            var phase = capture.Nodes.Single(node => node.Id == ZombiesHudPresenter.PhaseNodeId);
            Require(capture.Nodes.Any(node => node.Id == "zombies-wave-card" && node.IsRendered)
                && capture.Nodes.Any(node => node.Id == "zombies-supplies-card" && node.IsRendered)
                && capture.Nodes.Any(node => node.Id == ZombiesHudPresenter.IntegrityNodeId && node.IsRendered),
                "Zombies uses visible layout cards and wrapped status through the safe UI service.");
            Require(phase is { IsRendered: true, ThemeId: "survival", InkColor: "#BEE487", FillColor: "#141D21" },
                "The published HUD must render the Survival pack's authored ink on its opaque background.");
            Require(capture.CheckAccessibility().Count == 0, "The captured Zombies status must pass the UI guarantees.");

            var admitted = surface.Content;
            var allocated = AllocationProbe.Measure(
                "unchanged Zombies HUD",
                () => presenter.Update(snapshot, fire, null, null, null),
                frames: 1024, warmups: 32);
            Require(allocated == 0 && ReferenceEquals(admitted, surface.Content),
                "A settled Zombies HUD must reuse its content without allocating.");

            void AssertRole(string id, string label, UiTextStyle style)
            {
                Require(surface.TryFindNode(id, out var node)
                    && node is UiText text && text.Text == label && text.Style == style,
                    "The Zombies HUD must publish the phase, wave and score with their distinct type roles.");
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
