namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiGalleryPresentationLifecycleTests
    {
        private const string ShellBoundary = """
            using System;
            using System.Collections.Generic;
            using TopiaForge.Mods;
            using TopiaForge.Mods.UnityUi;

            namespace TopiaForge.Mods.UnityUi
            {
                internal enum TopiaForgeScheme { Paper, Hud }
                internal enum TopiaForgeGap { Sm, Md }
                internal enum TopiaForgeButtonStyle { Outline }
                internal enum TopiaForgeTextStyle { Caption }
                internal readonly record struct TopiaForgeAccessibilityProfile(bool HighContrast, float UiScale, bool ReducedMotion, float MotionIntensity);

                internal sealed class UiHost
                {
                    internal readonly List<TopiaForgeWindow> Windows = new();
                    public TopiaForgeAccessibilityProfile AccessibilityProfile { get; private set; } = new(false, 1f, false, 1f);
                    public event Action? AccessibilityProfileChanged;

                    public TopiaForgeWindow Window(string id, string title, float width, float height, TopiaForgeScheme scheme, bool persistent)
                    {
                        var window = new TopiaForgeWindow(scheme);
                        Windows.Add(window);
                        return window;
                    }

                    public void Clear(TopiaForgeContainer page) => page.Buttons.Clear();
                    public void SetAccessibilityProfile(TopiaForgeAccessibilityProfile preferences)
                    {
                        AccessibilityProfile = preferences;
                        AccessibilityProfileChanged?.Invoke();
                    }
                }

                internal sealed class TopiaForgeWindow(TopiaForgeScheme scheme)
                {
                    public TopiaForgeContainer Content { get; } = new();
                    internal TopiaForgeScheme Scheme { get; } = scheme;
                    public bool IsOpen { get; private set; }
                    public void Show() => IsOpen = true;
                    public void Close() => IsOpen = false;
                }

                internal sealed class TopiaForgeContainer
                {
                    internal void Label(string text, TopiaForgeTextStyle style) { }
                    internal readonly Dictionary<string, Action> Buttons = new();
                    internal readonly Dictionary<string, TopiaForgeToggle> Toggles = new();
                    internal TopiaForgeTabs TabBar = null!;
                    internal TopiaForgeContainer Page = null!;
                    internal TopiaForgeDropdown Palette = null!;

                    public TopiaForgeContainer Row(TopiaForgeGap gap) => this;
                    public void Button(string label, Action press, TopiaForgeButtonStyle style = TopiaForgeButtonStyle.Outline) => Buttons.Add(label, press);
                    public TopiaForgeSlider Slider(string label, float min, float max, float value, Action<float> changed) => new();
                    public TopiaForgeDropdown Dropdown(IReadOnlyList<string> labels, int selected, Action<int> changed) =>
                        Palette = new TopiaForgeDropdown(labels, selected, changed);

                    public TopiaForgeToggle Toggle(string label, bool value, Action<bool> changed)
                    {
                        var toggle = new TopiaForgeToggle(changed);
                        Toggles.Add(label, toggle);
                        return toggle;
                    }

                    public TopiaForgeScrollView Scroll(TopiaForgeGap gap, TopiaForgeGap padding) =>
                        new TopiaForgeScrollView(Page = new TopiaForgeContainer());

                    public TopiaForgeTabs Tabs(params string[] labels) => TabBar = new TopiaForgeTabs(labels);
                    internal void Press(string name) => Buttons[name]();
                }

                internal sealed class TopiaForgeScrollView(TopiaForgeContainer page)
                {
                    public TopiaForgeContainer Content { get; } = page;
                    public void Flex(float width, float height) { }
                    public void ScrollToTop() { }
                }

                internal sealed class TopiaForgeTabs(string[] labels)
                {
                    private Action<int>? selected;
                    public void OnSelected(Action<int> change) => selected = change;
                    internal void Select(string label) => selected!(Array.IndexOf(labels, label));
                }

                internal sealed class TopiaForgeToggle(Action<bool> changed)
                {
                    public void SetValue(bool value) { }
                    internal void UserSet(bool value) => changed(value);
                }

                internal sealed class TopiaForgeSlider
                {
                    public void SetValue(float value) { }
                }

                internal sealed class TopiaForgeDropdown(IReadOnlyList<string> labels, int selected, Action<int> changed)
                {
                    public int Value { get; private set; } = selected;
                    internal string Label => labels[Value];
                    public void SetValue(int value) => Value = value;
                    internal void Choose(int value) { SetValue(value); changed(value); }
                }
            }

            namespace TopiaForge.UiGallery.Pages
            {
                internal enum FoundationPage
                {
                    ThemePacks, Typography, InteractionStates, FlexAndPadding, WrapAndGrid,
                    StackAndBounds, CardsAndDividers, Icons, Images, BadgesAndPips, FoundationIntegration, TrustAndFallback, GraphConnections,
                }

                internal enum ControlGalleryPage { Buttons, Fields, Selections, Bindings, HelpAndTiles }
                internal enum StatusGalleryPage { Status, Countdowns }
                internal enum ActionGalleryPage { KeyGlyphs, Slots }

                internal static class FoundationPages
                {
                    internal static readonly UiThemeRef SurvivalTheme = new UiThemeRef("survival");
                    internal static UiSurfaceRequest CreateRequest(FoundationPage page, UiSurfaceKind kind, UiThemeRef? theme = null, Action<string>? report = null) =>
                        new UiSurfaceRequest("foundation", page.ToString(), "", kind, content: new UiText("Dispatch")).WithTheme(theme);
                }

                internal static class FoundationLayoutPages
                {
                    internal static UiSurfaceRequest LayoutNotice(UiThemeRef? theme = null) => new UiSurfaceRequest("layout-notice", "Layout fit", "");
                }

                internal static class ControlGalleryPages
                {
                    internal static UiSurfaceRequest Create(ControlGalleryPage page, UiThemeRef? theme = null, Action<string>? report = null) =>
                        new UiSurfaceRequest("controls", page.ToString(), "");
                }

                internal static class StatusGalleryPages
                {
                    internal static UiSurfaceRequest CreateRequest(StatusGalleryPage page, UiSurfaceKind kind, double time, UiThemeRef? theme = null) =>
                        new UiSurfaceRequest("status", page.ToString(), "", kind);
                }

                internal static class ActionGalleryPages
                {
                    internal static UiSurfaceRequest CreateRequest(ActionGalleryPage page, UiSurfaceKind kind, double time, UiThemeRef? theme = null) =>
                        new UiSurfaceRequest("actions", page.ToString(), "", kind);
                }

                internal static class WidgetsPage { public static void Build(TopiaForgeContainer page) { } }
                internal static class StatesPage { public static void Build(TopiaForgeContainer page) { } }
                internal static class ListsPage { public static void Build(TopiaForgeContainer page) { } }
                internal static class GraphPage { public static void Build(TopiaForgeContainer page) { } }
                internal static class MotionPage { public static void Build(TopiaForgeContainer page) { } }
                internal static class ShopPage { public static void Build(TopiaForgeContainer page) { } public static void Reset() { } }
                internal static class HudPage { public static void Build(TopiaForgeContainer page) { } public static void Reset() { } }
                internal static class OverlaysPage
                {
                    public static void Build(TopiaForgeContainer page) => page.Button("Existing overlay demos", () => { });
                    public static void Reset() { }
                }

                internal static class MultiplayerPage
                {
                    public static void Build(TopiaForgeContainer page, ILocalPlayerService player) { }
                    public static void Detach(TopiaForgeContainer page) { }
                    public static void Tick(float seconds) { }
                    public static void Reset() { }
                }

                internal static class SessionsPage
                {
                    public static void Build(TopiaForgeContainer page, UiHost host) { }
                    public static void Detach(TopiaForgeContainer page) { }
                    public static void Tick(float seconds) { }
                    public static void Reset() { }
                }
            }
            """;
    }
}
