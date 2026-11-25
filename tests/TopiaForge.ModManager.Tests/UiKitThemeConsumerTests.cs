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
                "zapper-fire", "Fire", new[] { InputBinding.Key("K") }));
            Require(registered.TryGetValue(out var fire), registered.ErrorMessage);
            var jack = context.Input.RegisterAction(new InputActionDefinition(
                "jack-in", "Jack in", new[] { InputBinding.Key("E") })).Value!;
            var broadcast = context.Input.RegisterAction(new InputActionDefinition(
                "stand-down-broadcast", "Stand down", new[] { InputBinding.Key("Q") })).Value!;
            var shop = context.Input.RegisterAction(new InputActionDefinition(
                "field-requisitions", "Requisitions", new[] { InputBinding.Key("B") })).Value!;

            using var presenter = new ZombiesHudPresenter(context);
            var snapshot = new ZombiesHudSnapshot(
                ZombiesPhase.Wave, 3, 7, 1, 2, 84, 100, 420, 25, 2, 2, 3, 0, null, true, true);
            presenter.Update(snapshot, fire, jack, broadcast, shop);
            context.Ui.CompletePendingThemeRegistrations();

            var surface = context.Ui.Surfaces.Single();
            Require(context.Ui.Themes.SelectedTheme == null, "Zombies inherits its manifest selection.");
            AssertRole(ZombiesHudPresenter.PhaseNodeId, "HORDE ACTIVE", UiTextStyle.Title);
            AssertRole(ZombiesHudPresenter.WaveNodeId, "WAVE  3", UiTextStyle.Label);
            AssertRole(ZombiesHudPresenter.ScoreNodeId, "SCORE  420", UiTextStyle.Numeral);
            Require(surface.TryFindNode(ZombiesHudPresenter.ComboNodeId, out var combo) &&
                combo is UiBadge { Text: "Combo ×2", Icon: UiGlyph.Star }, "The real score multiplier supplies the combo badge.");
            Require(surface.TryFindNode(ZombiesHudPresenter.UplinkNodeId, out var uplink) &&
                uplink is UiPips { Filled: 2, Total: 3, Presentation: UiPipStyle.Segments },
                "The uplink track shows the exact available and maximum charge snapshot.");
            AssertGlyph(ZombiesHudPresenter.PhaseIconId, UiGlyph.Flag);
            AssertGlyph(ZombiesHudPresenter.IntegrityIconId, UiGlyph.Health);
            AssertGlyph(ZombiesHudPresenter.CreditsIconId, UiGlyph.Notes);
            Require(surface.TryFindNode(ZombiesHudPresenter.IntegrityNodeId, out var integrity) &&
                integrity is UiStatBar
                {
                    Value: 84, Maximum: 100, Format: UiStatFormat.ValueOfMaximum,
                    Thresholds: { Direction: UiThresholdDirection.AtOrBelow, Warning: 50, Critical: 25 }
                },
                "The real Zombies integrity snapshot supplies its stat bar and low-integrity thresholds.");

            var capture = surface.Capture(new UiCaptureViewport(1920, 1080));
            var phase = capture.Nodes.Single(node => node.Id == ZombiesHudPresenter.PhaseNodeId);
            Require(capture.Nodes.Any(node => node.Id == "zombies-wave-card" && node.IsRendered)
                && capture.Nodes.Any(node => node.Id == "zombies-supplies-card" && node.IsRendered)
                && capture.Nodes.Any(node => node.Id == ZombiesHudPresenter.IntegrityNodeId && node.IsRendered),
                "Zombies uses visible layout cards and wrapped status through the safe UI service.");
            Require(phase is { IsRendered: true, ThemeId: "survival", InkColor: "#BEE487", FillColor: "#141D21" },
                "The published HUD must render the Survival pack's authored ink on its opaque background.");
            Require(capture.CheckAccessibility().Count == 0, "The captured Zombies status must pass the UI guarantees.");
            Require(context.Ui.ApplyAccessibility(new UiAccessibilityPreferences(highContrast: true, uiScale: 2f)).Succeeded,
                "The fake accepts the largest readable scale and high-contrast palette.");
            foreach (var viewport in new[] { new UiCaptureViewport(1920, 1080), new UiCaptureViewport(2560, 1440) })
            {
                var scaled = surface.Capture(viewport);
                Require(scaled.Nodes.Any(node => node.Id == ZombiesHudPresenter.ComboNodeId && node.IsRendered)
                    && scaled.Nodes.Any(node => node.Id == ZombiesHudPresenter.UplinkNodeId && node.IsRendered)
                    && scaled.CheckAccessibility().Count == 0,
                    "Both Zombies status controls remain visible and accessible at scale 2 in high contrast.");
            }

            var admitted = surface.Content;
            var allocated = AllocationProbe.Measure(
                "unchanged Zombies HUD",
                () => presenter.Update(snapshot, fire, jack, broadcast, shop),
                frames: 1024, warmups: 32);
            Require(allocated == 0 && ReferenceEquals(admitted, surface.Content),
                "A settled Zombies HUD must reuse its content without allocating.");
            Console.WriteLine("Zombies HUD: 0 B over 1,024 unchanged status updates.");

            var largeCapacity = new ZombiesHudSnapshot(
                ZombiesPhase.InterWave, 3, 0, 1, 0, 84, 100, 420, 25, 1, 33, 34, 0, null, true, false,
                uplinkRefillPercent: 62);
            presenter.Update(largeCapacity, fire, jack, null, null);
            AssertRole(ZombiesHudPresenter.UplinkNodeId, "Uplink: 33 / 34 (62% refilling)", UiTextStyle.Label);
            Require(surface.TryFindNode(ZombiesHudPresenter.JackInSlotId, out var jackFallback) &&
                jackFallback is UiActionSlot { Charges: null, Detail: "33 / 34" },
                "An upgraded capacity over 32 stays visible on JACK IN without changing the ledger.");
            Require(!surface.TryFindNode(ZombiesHudPresenter.ComboNodeId, out _) &&
                !surface.TryFindNode(ZombiesHudPresenter.CreditsIconId, out _),
                "An inactive combo and disabled credits have no status decoration.");

            void AssertRole(string id, string label, UiTextStyle style)
            {
                Require(surface.TryFindNode(id, out var node)
                    && node is UiText text && text.Text == label && text.Style == style,
                    "The Zombies HUD must publish " + id + " with its expected text and type role.");
            }

            void AssertGlyph(string id, UiGlyph glyph) => Require(surface.TryFindNode(id, out var node) &&
                node is UiIcon { Decorative: true, SemanticLabel: "" } icon && icon.Glyph == glyph,
                "A redundant Zombies glyph follows its adjacent visible label.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
