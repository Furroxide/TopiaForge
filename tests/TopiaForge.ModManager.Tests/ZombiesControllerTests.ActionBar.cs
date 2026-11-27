using System;
using System.IO;
using System.Linq;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.Zombies;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class ZombiesControllerTests
    {
        private static void ActionBarFollowsInputAndRequisitionsPhase()
        {
            var config = FastConfig();
            config.StartingCountdownSeconds = 2f;
            config.OverrideEnabled = true;
            config.ShopEnabled = true;
            config.ChargeShotEnabled = true;
            config.ChargeShotSeconds = 1f;
            using var harness = new Harness(config);
            var hud = FindSurface(harness.Context, "zombies-status");

            var root = Path.Combine(Program.FindRepoRoot(), "mods", "TopiaForge.Zombies");
            string ReadThemeFile(string path) => File.ReadAllText(Path.Combine(root, path));
            Assert(harness.Context.Ui.AddThemeCatalogue(ReadThemeFile("topiaforge.mod.json"), ReadThemeFile).Succeeded,
                "Load the actual Zombies Survival theme");
            harness.Context.Ui.CompletePendingThemeRegistrations();
            Assert(harness.Context.Ui.ApplyAccessibility(new UiAccessibilityPreferences(highContrast: true, uiScale: 2f)).Succeeded,
                "Set the game HUD to the visual-gate scale");

            Assert(ShopSlot().UnavailableReason != null, "Requisitions are unavailable while waiting for the world");
            harness.Advance(.01f);
            Assert(harness.Controller.TestingPhase == ZombiesPhase.Starting && ShopSlot().UnavailableReason == null,
                "Requisitions are ready during Starting");
            PressShop();
            var window = FindSurface(harness.Context, "zombies-requisitions");
            Assert(window.IsVisible && ShopSlot().UnavailableReason != null,
                "The existing shop key opens Requisitions and the paused HUD no longer offers another opening");
            window.Hide();
            harness.Advance(.01f);

            harness.Controller.TestingSetWavePhase();
            Assert(harness.Controller.TestingSpawn(ZombieKind.Grunt, new Vec3(0f, 0f, 5f)),
                "Prepare one live wave target");
            AimAtProxy(harness, (FakeRobotAgent)harness.Robots.Agents.ActiveAgents[0]);
            PressShop();
            Assert(ShopSlot().UnavailableReason == "After the wave" &&
                !harness.Context.Ui.Surfaces.Any(surface => surface.Id == "zombies-requisitions"),
                "The existing shop key is refused during a wave and the slot shows After the wave");

            var fire = harness.Context.Input.GetAction("zapper-fire");
            var keyboard = new UiCaptureViewport(1920, 1080);
            var existing = hud.Content;
            Assert(fire.Bindings[0].Kind == InputBindingKind.MouseButton &&
                CapturedSlot(hud.Capture(keyboard), ZombiesHudPresenter.FireSlotId).KeyLabel == "Mouse 1",
                "FIRE starts with the existing Primary mouse binding");
            Assert(fire.Rebind(new[] { InputBinding.Key("K") }).Succeeded, "Rebind the actual FIRE action");
            harness.Advance(.01f);
            Assert(ReferenceEquals(existing, hud.Content) &&
                CapturedSlot(hud.Capture(keyboard), ZombiesHudPresenter.FireSlotId).KeyLabel == "K",
                "Binding labels update through the kit without replacing settled authored content");
            Assert(fire.ResetBindings().Succeeded, "Restore the existing FIRE binding");

            fire.SetValue(1f);
            harness.Advance(.5f);
            Assert(harness.Controller.TestingCharging &&
                hud.TryFindNode(ZombiesHudPresenter.FireSlotId, out var slot) &&
                slot is UiActionSlot { ActionName: "zapper-fire", Label: "FIRE", Emphasis: true, Detail: "Charge 50%" },
                "Holding the actual FIRE action attaches its charge percentage to the emphasized slot");

            foreach (var scale in new[] { .75f, 1f, 2f })
                foreach (var highContrast in new[] { false, true })
                    foreach (var viewport in new[] { keyboard, new UiCaptureViewport(2560, 1440) })
                    {
                        Assert(harness.Context.Ui.ApplyAccessibility(new UiAccessibilityPreferences(highContrast, scale)).Succeeded,
                            "Apply each viewer's size and contrast setting");
                        var capture = hud.Capture(viewport);
                        Assert(!capture.Nodes.Any(node => node.Kind == "LayoutFallback"),
                            "The action bar fits at scale " + scale + ". " +
                            string.Join(", ", capture.CheckAccessibility().Select(issue => issue.Kind + " at " + issue.Path)));
                        var fireSlot = CapturedSlot(capture, ZombiesHudPresenter.FireSlotId);
                        var jackSlot = CapturedSlot(capture, ZombiesHudPresenter.JackInSlotId);
                        var standDown = CapturedSlot(capture, ZombiesHudPresenter.StandDownSlotId);
                        var requisitions = CapturedSlot(capture, ZombiesHudPresenter.RequisitionsSlotId);
                        Assert(fireSlot is { State: "emphasis", KeyLabel: "Mouse 1" } &&
                            OwnedPaint(capture, fireSlot, "ActionEmphasis") is { IsRendered: true } ring &&
                            ring.BorderWidthPixels >= 3f * Math.Max(1f, scale) &&
                            OwnedPaint(capture, fireSlot, "ActionStatus") is { Text: "Charge 50%", IsRendered: true } &&
                            jackSlot.KeyLabel == "E" && standDown is { State: "ready", KeyLabel: "Q" } &&
                            requisitions is { State: "unavailable", KeyLabel: "B" } &&
                            OwnedPaint(capture, requisitions, "ActionStatus") is { Text: "After the wave", IsRendered: true } &&
                            OwnedPaint(capture, requisitions, "ActionStatusGlyph").Path.EndsWith("cue:Lock", StringComparison.Ordinal),
                            "The HUD capture shows the configured keys, FIRE emphasis and charge, and the Requisitions lock");
                        var issues = capture.CheckAccessibility();
                        Assert(issues.Count == 0 && !capture.Nodes.Any(node => node.Kind == "LayoutFallback" ||
                            node.Text.Contains("ZAPPER CHARGE", StringComparison.Ordinal) ||
                            node.Text.Contains("REQUISITIONS OPEN", StringComparison.Ordinal)),
                            "The real action bar fits with no redundant charge or key hints. " +
                            string.Join(", ", issues.Select(issue => issue.Kind + " at " + issue.Path)));
                    }

            fire.SetValue(0f);
            harness.Advance(.01f);
            Assert(harness.Controller.TestingPhase == ZombiesPhase.InterWave && ShopSlot().UnavailableReason == null &&
                hud.TryFindNode(ZombiesHudPresenter.FireSlotId, out slot) && slot is UiActionSlot { Detail: null },
                "After the held shot ends the wave, FIRE clears its charge and Requisitions becomes ready again");
            PressShop();
            Assert(FindSurface(harness.Context, "zombies-requisitions").IsVisible,
                "The existing Requisitions action also opens the shop during InterWave");
            Console.WriteLine("Zombies ActionBar passed for real bindings, charge, Requisitions phases and 12 viewport, scale and contrast combinations.");

            UiActionSlot ShopSlot()
            {
                Assert(hud.TryFindNode(ZombiesHudPresenter.RequisitionsSlotId, out var node) &&
                    node is UiActionSlot { ActionName: "field-requisitions" },
                    "The HUD retains the real Requisitions action slot. " +
                    string.Join(", ", harness.Context.Logger.Entries.Select(entry => entry.Message)));
                return (UiActionSlot)node!;
            }

            void PressShop()
            {
                harness.Context.Input.SetValue("field-requisitions", 1f);
                harness.Advance(.01f);
                harness.Context.Input.SetValue("field-requisitions", 0f);
            }
        }

        private static UiCapturedNode CapturedSlot(UiTreeCapture capture, string id)
        {
            var match = capture.Nodes.SingleOrDefault(node => node.Id == id);
            Assert(match is { IsRendered: true, Clipped: false, Occluded: false, ThemeId: "survival" },
                "Capture an unclipped Survival slot for " + id);
            return match!;
        }

        private static UiCapturedNode OwnedPaint(UiTreeCapture capture, UiCapturedNode slot, string kind) =>
            capture.Nodes.Single(node => node.Kind == kind && node.Path.StartsWith(slot.Path + "/", StringComparison.Ordinal));
    }
}
