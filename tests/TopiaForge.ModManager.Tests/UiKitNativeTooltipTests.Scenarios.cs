namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitNativeTooltipTests
    {
        private const string TooltipScenarios = """
            using System;
            using System.Linq;
            using System.Reflection;
            using System.Text;
            using TMPro;
            using TopiaForge.Mods;
            using TopiaForge.Mods.UnityUi;
            using TopiaForge.TestSupport;
            using UnityEngine;
            using UnityEngine.EventSystems;
            using UnityEngine.UI;
            using UObject = UnityEngine.Object;

            public static class TooltipScenarios
            {
                private static readonly Action<TopiaForgeTooltipLayer> Tick = typeof(TopiaForgeTooltipLayer)
                    .GetMethod("LateUpdate", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .CreateDelegate<Action<TopiaForgeTooltipLayer>>();
                private static readonly PointerEventData Pointer = new();
                private static readonly BaseEventData Navigation = new();

                public static void Run()
                {
                    HoverUsesTheOwningCanvasAndStyle();
                    FocusAndBackUseExistingNavigation();
                    IdleAndTeardownReleaseThePopup();
                }

                private static void HoverUsesTheOwningCanvasAndStyle()
                {
                    var canvas = Surface();
                    var scroll = Child(canvas.transform, "Viewport", 68, 42, 120, -72);
                    scroll.AddComponent<RectMask2D>().padding = new Vector4(1, 2, 3, 4);
                    var wrapper = new TopiaForgeWidget(Child(scroll.transform, "Tooltip", 40, 40, 12, -1));
                    var descriptor = UiThemePackParser.Parse(Encoding.UTF8.GetBytes(
                        "{\"schema\":1,\"id\":\"tooltip-probe\",\"shape\":{\"card\":31}," +
                        "\"paper\":{\"colors\":{\"raised\":{\"fill\":\"#273747\",\"on\":\"#FFFFFF\"}}}," +
                        "\"components\":{\"tooltip\":{\"radius\":\"card\",\"border\":\"thin\",\"shadow\":\"card\"}}}"
                    )).Value!;
                    wrapper.ResolvedTheme = new TopiaForgeResolvedTheme(descriptor);
                    const string message = "Literal <b>hint</b> \\n for the selected tool";
                    TopiaForgeScopedTooltip.Install(wrapper, message, true, UiPaintRole.Selected);
                    var layer = canvas.gameObject.GetComponent<TopiaForgeTooltipLayer>()!;
                    var trigger = wrapper.Go.GetComponent<TopiaForgeScopedTooltipTrigger>()!;
                    Require(trigger.isActiveAndEnabled && !layer.enabled, "An idle tooltip still receives the pointer enter event.");

                    trigger.OnPointerEnter(Pointer);
                    Frame(layer, 0.449d);
                    Require(Popup(wrapper) == null, "Pointer hover waits a full 450 ms.");
                    Frame(layer, 0.45d);
                    var popup = Popup(wrapper)!;
                    Require(popup != null && popup.parent == canvas.transform &&
                        popup.gameObject.GetComponentInParent<Canvas>() == canvas && canvas.transform.Children[^1] == popup,
                        "Popup escapes scroll clipping within the same owning canvas and draws last.");
                    var fill = Graphic(popup, "Fill");
                    var ring = Graphic(popup, "Ring");
                    var shadow = Graphic(popup, "Shadow");
                    var label = popup.gameObject.GetComponentsInChildren<TextMeshProUGUI>().Single();
                    Require(label.text == message && !label.richText && !label.parseCtrlCharacters && !label.raycastTarget &&
                        fill.color == wrapper.ResolvedTheme.SurfaceAlt && label.color == wrapper.ResolvedTheme.ForegroundOn(UiPaintRole.Raised) &&
                        ring.color == wrapper.ResolvedTheme.BoundaryOn(UiPaintRole.Selected) && ring.Stroke == 1f &&
                        fill.Radius == 31f && shadow.gameObject.activeSelf &&
                        ReferenceEquals(TopiaForgeTypography.LastTheme, wrapper.ResolvedTheme) &&
                        TopiaForgeTypography.LastStyle == TopiaForgeTextStyle.Label,
                        "Visible tooltip uses scoped component paint, its own Label role, and literal click-through text.");
                    var extent = popup.sizeDelta;
                    var position = popup.anchoredPosition;
                    var bounds = canvas.transform.rect;
                    Require(position.x - 4f >= bounds.xMin && position.x + extent.x <= bounds.xMax &&
                        position.y - extent.y - 8f >= bounds.yMin && position.y <= bounds.yMax,
                        "The screen clamp contains both the popup and authored shadow at the far edge.");

                    Child(canvas.transform, "New surface content", 40, 40, 0, 0);
                    wrapper.ResolvedTheme = new TopiaForgeResolvedTheme(descriptor, highContrast: true);
                    Frame(layer, 0.46d);
                    Require(Popup(wrapper) == popup && !shadow.gameObject.activeSelf && ring.Stroke == 3f &&
                        ReferenceEquals(TopiaForgeTypography.LastTheme, wrapper.ResolvedTheme) && canvas.transform.Children[^1] == popup,
                        "A visible popup repaints from the current scoped theme without replacing its view.");

                    wrapper.Rect.anchoredPosition = new Vector2(200, 0);
                    Frame(layer, 0.5d);
                    Require(Popup(wrapper) == null && TopiaForgeDismissStack.Count == 0, "An anchor scrolled out of its mask hides the tooltip.");
                    wrapper.Rect.anchoredPosition = new Vector2(12, -1);
                    Frame(layer, 0.51d);
                    Require(Popup(wrapper) == popup, "A ready anchor can reveal its tooltip again when it is brought into view.");
                    trigger.OnPointerExit(Pointer);
                    Require(Popup(wrapper) == null && !layer.enabled, "Pointer exit hides the popup and stops the clock driver.");
                    UObject.Destroy(canvas.gameObject);
                }

                private static void FocusAndBackUseExistingNavigation()
                {
                    var canvas = Surface();
                    var wrapper = new TopiaForgeWidget(Child(canvas.transform, "Passive", 50, 40, 0, 0));
                    var disabled = new TopiaForgeWidget(Child(canvas.transform, "Disabled", 40, 40, 40, 20));
                    var interactive = new TopiaForgeWidget(Child(canvas.transform, "Wrapped button", 60, 40, -40, -20));
                    var alreadyFocusable = Child(interactive.Rect, "Button", 60, 40, 0, 0).AddComponent<Selectable>();
                    TopiaForgeControlFocus.Installed = 0;
                    TopiaForgeScopedTooltip.Install(wrapper, "Read the existing value", true, UiPaintRole.Surface);
                    TopiaForgeScopedTooltip.Install(disabled, "Disabled branch", false, UiPaintRole.Surface);
                    TopiaForgeScopedTooltip.Install(interactive, "Button help", true, UiPaintRole.Raised);
                    var layout = wrapper.Go.GetComponent<LayoutElement>()!;
                    Require(wrapper.Go.GetComponent<Selectable>() is { interactable: true } &&
                        layout.minWidth == 40f && layout.minHeight == 40f && interactive.Go.GetComponent<Selectable>() == null &&
                        disabled.Go.GetComponent<TopiaForgeScopedTooltipTrigger>() == null && TopiaForgeControlFocus.Installed == 1,
                        "Only an enabled passive child gains one 40-unit navigation anchor and ring.");

                    var events = EventSystem.current!;
                    var layer = canvas.gameObject.GetComponent<TopiaForgeTooltipLayer>()!;
                    events.Select(wrapper.Go, Pointer);
                    Require(Popup(wrapper) == null, "Pointer selection does not bypass its hover delay.");
                    events.Select(null, Pointer);
                    events.Select(wrapper.Go, Navigation);
                    Require(Popup(wrapper) != null && TopiaForgeDismissStack.Claims == TopiaForgeInputClaim.Back,
                        "Keyboard and controller focus show immediately and the popup claims only Back.");

                    var surface = new BackProbe(TopiaForgeLayerBand.Window);
                    var modal = new BackProbe(TopiaForgeLayerBand.Modal);
                    TopiaForgeDismissStack.Push(surface, TopiaForgeInputClaim.NavigationWhileSelected, canvas.gameObject);
                    events.Select(alreadyFocusable.gameObject, Navigation);
                    Require(Popup(wrapper) == null && Popup(interactive) != null, "Focus moves to the existing child without adding another stop.");
                    TopiaForgeDismissStack.Push(modal);
                    TapBack();
                    Require(modal.Dismissed && !surface.Dismissed && Popup(interactive) != null,
                        "A tooltip cannot steal Back from a higher-band surface.");
                    TapBack();
                    Require(Popup(interactive) == null && !surface.Dismissed && !layer.enabled &&
                        events.currentSelectedGameObject == alreadyFocusable.gameObject,
                        "Back dismisses the tooltip alone and retains the child focus.");
                    Frame(layer, 5d);
                    Require(Popup(interactive) == null, "A dismissed tooltip stays hidden until a new pointer or focus entry.");
                    TapBack();
                    Require(surface.Dismissed, "The following Back press belongs to the original surface.");

                    events.Select(wrapper.Go, Navigation);
                    Require(Popup(wrapper) != null, "A fresh focus entry can show a dismissed tooltip.");
                    events.Select(null, Navigation);
                    Require(Popup(wrapper) == null && !layer.enabled, "Leaving keyboard or controller focus closes the tooltip.");
                    UObject.Destroy(canvas.gameObject);
                }

                private static void IdleAndTeardownReleaseThePopup()
                {
                    var canvas = Surface();
                    var wrapper = new TopiaForgeWidget(Child(canvas.transform, "Tooltip source", 60, 40, 0, 0));
                    TopiaForgeScopedTooltip.Install(wrapper, "One stable message", true, UiPaintRole.Surface);
                    var trigger = wrapper.Go.GetComponent<TopiaForgeScopedTooltipTrigger>()!;
                    var layer = canvas.gameObject.GetComponent<TopiaForgeTooltipLayer>()!;
                    var hiddenBytes = AllocationProbe.Measure("idle tooltip", () => Frame(layer, 0d), 4096, 64);
                    trigger.OnPointerEnter(Pointer);
                    var delayBytes = AllocationProbe.Measure("pending hover", () => Frame(layer, 0.1d), 4096, 64);
                    wrapper.Go.SetActive(false);
                    Frame(layer, 0.5d);
                    Require(Popup(wrapper) == null && !layer.enabled && TopiaForgeDismissStack.Count == 0,
                        "Hiding an owner during the hover delay cancels its pending popup.");
                    wrapper.Go.SetActive(true);
                    trigger.OnPointerEnter(Pointer);
                    Frame(layer, 1d);
                    var popup = Popup(wrapper)!;
                    var label = popup.gameObject.GetComponentsInChildren<TextMeshProUGUI>().Single();
                    var priorMeasure = label.Measurements;
                    var visibleBytes = AllocationProbe.Measure("visible unchanged tooltip", () => Frame(layer, 1d), 4096, 64);
                    Require(hiddenBytes == 0 && delayBytes == 0 && visibleBytes == 0 && label.Measurements == priorMeasure,
                        "Unchanged tooltips allocate nothing and do not remeasure when hidden, waiting, or visible.");

                    wrapper.Go.SetActive(false);
                    Require(Popup(wrapper) == null && !layer.enabled && TopiaForgeDismissStack.Count == 0 &&
                        label.text.Length == 0 && ReferenceEquals(label.font, TopiaForgeFonts.Fallback),
                        "Hiding the owner clears the popup, active font and dismiss claim.");
                    wrapper.Go.SetActive(true);
                    trigger.OnPointerEnter(Pointer);
                    Frame(layer, 2d);
                    Require(Popup(wrapper) == popup && TopiaForgeDismissStack.Count == 1, "The re-opened owner reuses its canvas popup.");
                    UObject.Destroy(popup.gameObject);
                    Frame(layer, 2.1d);
                    Require(Popup(wrapper) is { } replacement && replacement != popup && TopiaForgeDismissStack.Count == 1,
                        "Losing popup art releases its stale Back root and rebuilds in the same canvas.");
                    UObject.Destroy(canvas.gameObject);
                    Require(TopiaForgeDismissStack.Count == 0 && Popup(wrapper) == null, "Canvas destruction removes every active tooltip claim.");
                }

                private static Canvas Surface()
                {
                    TopiaForgeDismissStack.Reset();
                    EventSystem.current = new EventSystem();
                    Time.unscaledTimeAsDouble = 0d;
                    var root = new GameObject("Window canvas", typeof(RectTransform));
                    root.transform.sizeDelta = new Vector2(320, 200);
                    var canvas = root.AddComponent<Canvas>();
                    canvas.sortingOrder = TopiaForgeLayerBands.DefaultWindowBase;
                    return canvas;
                }

                private static GameObject Child(Transform parent, string name, float width, float height, float x, float y)
                {
                    var go = new GameObject(name, typeof(RectTransform));
                    go.transform.SetParent(parent, false);
                    go.transform.sizeDelta = new Vector2(width, height);
                    go.transform.anchoredPosition = new Vector2(x, y);
                    return go;
                }

                private static TopiaForgeRoundedImage Graphic(RectTransform popup, string child) =>
                    popup.Children.Single(item => item.gameObject.name == child).gameObject.GetComponent<TopiaForgeRoundedImage>()!;

                private static RectTransform? Popup(TopiaForgeWidget wrapper) => TopiaForgeScopedTooltip.VisiblePopup(wrapper.Go);

                private static void Frame(TopiaForgeTooltipLayer layer, double now)
                {
                    Time.unscaledTimeAsDouble = now;
                    if (layer.isActiveAndEnabled) Tick(layer);
                }

                private static void TapBack()
                {
                    TopiaForgeInput.Back = true;
                    TopiaForgeDismissStack.TickEscape();
                    TopiaForgeInput.Back = false;
                    TopiaForgeDismissStack.TickEscape();
                }

                private static void Require(bool condition, string message)
                {
                    if (!condition) throw new InvalidOperationException(message);
                }

                private sealed class BackProbe : ITopiaForgeDismissable
                {
                    internal BackProbe(TopiaForgeLayerBand band) => Band = band;
                    public TopiaForgeLayerBand Band { get; }
                    internal bool Dismissed { get; private set; }
                    public void Dismiss() => Dismissed = true;
                }
            }
            """;
    }
}
