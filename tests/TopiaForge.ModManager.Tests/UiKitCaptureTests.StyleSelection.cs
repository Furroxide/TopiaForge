using System.Linq;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitCaptureTests
    {
        private static void CardAndBoxThemeSwitchesAreAtomic()
        {
            const string styled = """
                {"schema":1,"id":"layout-styles","styles":[
                  {"id":"report","target":"card","radius":"small"},
                  {"id":"panel","target":"box","radius":"small"}]}
                """;
            const string plain = """{"schema":1,"id":"no-layout-styles"}""";
            using var rootLifetime = new FakeModLifetime();
            using var childLifetime = new FakeModLifetime();
            var root = new FakeUiService(rootLifetime);
            var child = CreateThemeScope(root, childLifetime);
            Check(root.AddThemeFixture(styled).Succeeded && root.AddThemeFixture(plain).Succeeded, "Declare both package themes.");
            var pack = new UiThemeRef("layout-styles");
            var wrong = new UiThemeRef("no-layout-styles");
            Check(root.Themes.UseTheme(pack).Succeeded, "Install the shared layout styles.");
            root.CompletePendingThemeRegistrations();
            var content = new UiCard(new UiColumn(new UiText("Ready", id: "ready"),
                new UiBox(40, 32, style: new UiStyleRef("panel"))), style: new UiStyleRef("report"));
            var request = new UiSurfaceRequest("styled-layout", "Styled layout", "", content: content);
            using var local = Make(child, request);
            Check(child.Themes.UseTheme(wrong).ErrorCode == ModErrorCode.InvalidArgument &&
                root.Themes.UseTheme(wrong).ErrorCode == ModErrorCode.InvalidArgument &&
                root.Themes.SelectedTheme == pack && child.Themes.SelectedTheme == null &&
                local.Capture(Viewport).Nodes.Single(node => node.Id == "ready") is { IsRendered: true, ThemeId: "layout-styles" },
                "A direct or inherited refusal keeps the existing Card and Box visible and retains both selections.");
            local.Dispose();
            using var fixedPalette = Make(child, request.WithTheme(pack));
            Check(root.Themes.UseTheme(wrong).Succeeded && fixedPalette.Capture(Viewport).Nodes.All(node => node.ThemeId == pack.Id),
                "An explicit theme remains independent of the inherited selection.");
        }
    }
}
