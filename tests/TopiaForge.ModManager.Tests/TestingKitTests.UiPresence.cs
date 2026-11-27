using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class TestingKitTests
    {
        /// <summary>
        /// The fake UI follows the fake scenes as the game does: a gameplay HUD shows only while a gameplay scene is active
        /// and no load is pending, a HUD shown everywhere and every window show whenever the mod shows them, and TopiaForge's
        /// hiding never dismisses anything. So a mod's own tests see what the player sees.
        /// </summary>
        private static void TestGameplayHudsFollowFakeScenes()
        {
            using var context = new FakeModContext();
            var hud = Created(context, new UiSurfaceRequest("hud", "Counter", "Shared count: 0", UiSurfaceKind.Hud, 260f, 96f));
            var everywhere = Created(context, new UiSurfaceRequest("help", "Help", "Press K in a world.", UiSurfaceKind.Hud, 200f, 80f)
                .WithHudVisibility(UiHudVisibility.Always));
            var window = Created(context, new UiSurfaceRequest("tool", "Tool", string.Empty));
            var dismissals = 0;
            hud.Dismissed += () => dismissals++;
            Assert(context.Scenes.ActiveScene == GameScenes.MainMenuSceneName && !hud.IsVisible && hud.IsShown,
                "the fake starts at the main menu, where a gameplay HUD the mod shows is hidden");
            Assert(everywhere.IsVisible && window.IsVisible, "a HUD shown everywhere, and a window, show at the main menu");

            context.Scenes.Load("City Streets");
            Assert(hud.IsVisible, "a gameplay HUD shows in a world, made once and never re-created");

            context.Scenes.CompleteLoadsImmediately = false;
            _ = context.Scenes.LoadAsync(new SceneLoadRequest(GameScenes.MainMenuSceneName));
            Assert(!hud.IsVisible && everywhere.IsVisible && window.IsVisible, "while a load is pending, only the gameplay HUD hides");
            Assert(context.Scenes.CompleteNextLoad() && !hud.IsVisible, "and it stays hidden at the main menu");
            context.Scenes.CompleteLoadsImmediately = true;
            context.Scenes.Load("Props", SceneLoadMode.Additive);
            Assert(!hud.IsVisible, "a scene loaded beside the menu does not make it gameplay");
            context.Scenes.Activate("Props");
            Assert(hud.IsVisible, "an activated gameplay scene does");

            hud.Hide();
            Assert(!hud.IsVisible && dismissals == 1, "the mod hiding a showing HUD dismisses it");
            context.Scenes.Load(GameScenes.MainMenuSceneName);
            hud.Show();
            Assert(!hud.IsVisible && hud.IsShown && dismissals == 1, "shown at the main menu, it waits for gameplay without a dismissal");
            context.Scenes.Load("City Streets");
            Assert(hud.IsVisible && dismissals == 1, "and appears in the next world");

            var standalone = new FakeUiService(new FakeModLifetime());
            var created = standalone.CreateSurface(new UiSurfaceRequest("alone", "Alone", string.Empty, UiSurfaceKind.Hud));
            Assert(created.TryGetValue(out var alone) && alone!.IsVisible,
                "a fake UI service used without a fake context has no scenes, so its HUDs show whenever they are shown");

            TestHudRequestOptions();
        }

        private static void TestHudRequestOptions()
        {
            var request = new UiSurfaceRequest("hud", "Counter", "Shared count: 0", UiSurfaceKind.Hud, 260f, 96f);
            Assert(request.HudAnchor == UiHudAnchor.Default && request.HudVisibility == UiHudVisibility.Gameplay,
                "a HUD request docks where TopiaForge chooses and shows in gameplay unless told otherwise");
            var docked = request.WithHudAnchor(UiHudAnchor.BottomRight).WithHudVisibility(UiHudVisibility.Always);
            Assert(docked.HudAnchor == UiHudAnchor.BottomRight && docked.HudVisibility == UiHudVisibility.Always
                && docked.Id == "hud" && docked.Title == "Counter" && docked.Body == "Shared count: 0"
                && docked.Kind == UiSurfaceKind.Hud && docked.Width == 260f && docked.Height == 96f,
                "the copy methods change only what they name");
            Assert(request.HudAnchor == UiHudAnchor.Default && request.HudVisibility == UiHudVisibility.Gameplay,
                "and leave the original request as it was");

            var window = new UiSurfaceRequest("tool", "Tool", string.Empty);
            AssertThrows<InvalidOperationException>(() => window.WithHudAnchor(UiHudAnchor.Left),
                "a window cannot dock: it opens centred and keeps where the player moves it");
            AssertThrows<InvalidOperationException>(() => window.WithHudVisibility(UiHudVisibility.Always),
                "a window shows exactly while the mod shows it");
            AssertThrows<ArgumentOutOfRangeException>(() => request.WithHudAnchor((UiHudAnchor)99), "an undefined anchor");
            AssertThrows<ArgumentOutOfRangeException>(() => request.WithHudVisibility((UiHudVisibility)7), "an undefined visibility");
            Assert(Refusal(() => window.WithHudAnchor(UiHudAnchor.Left)).Contains("UiSurfaceKind.Hud to dock it", StringComparison.Ordinal),
                "the refusal says how to make a surface that docks");
            Assert(Refusal(() => window.WithHudVisibility(UiHudVisibility.Always)).Contains("UiSurfaceKind.Hud to choose when it shows", StringComparison.Ordinal),
                "the refusal says how to make a surface whose visibility the mod chooses, not how to dock one");
        }

        private static string Refusal(Action action)
        {
            try
            {
                action();
            }
            catch (InvalidOperationException exception)
            {
                return exception.Message;
            }

            return string.Empty;
        }

        private static FakeUiSurface Created(FakeModContext context, UiSurfaceRequest request)
        {
            var created = context.Ui.CreateSurface(request);
            Assert(created.TryGetValue(out var surface) && surface is FakeUiSurface, "the fake creates " + request.Id);
            return (FakeUiSurface)surface!;
        }
    }
}
