namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiSurfaceReapplyTests
    {
        private const string Boundaries = """
            using System;
            using System.Collections.Generic;
            using TopiaForge.ModManager.Core;
            using TopiaForge.Mods;
            using TopiaForge.Mods.Testing;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine;
            using UnityEngine.UI;

            namespace UnityEngine
            {
                public sealed class Sprite { }
            }

            namespace TopiaForge.Mods.UnityUi
            {
                public enum TopiaForgeGap { None, Xs, Sm, Md }
                public enum TopiaForgeTone { Neutral, Success, Warning, Danger, Muted, Primary, Accent }
                public enum TopiaForgeButtonStyle { Filled, Outline, Ghost, Danger }
                public enum TopiaForgeTextStyle { Body, Heading, Caption, Banner, Display, Title, Label, Numeral, Mono }
                public enum TopiaForgeScheme { Paper, Hud }
                public static class TopiaForgeTokens { public const float ControlHeight = 40f; }
                public sealed class TopiaForgeResolvedTheme
                {
                    internal UiThemeDescriptor? Descriptor { get; }
                    internal bool HighContrast { get; }
                    internal TopiaForgeScheme Scheme { get; }
                    internal Color Shadow { get; }
                }

                public sealed class UiHost
                {
                    public TopiaForgeResolvedTheme ResolvedTheme = new();
                    public int ColumnsBuilt;
                    public int ComponentQueries;
                    public string? FailedLabel;
                    public bool FailNextDestroy;
                    public bool FailNextGraphBind;
                    public readonly List<TopiaForgeGraphCanvas> Graphs = new();
                    public readonly Dictionary<string, TopiaForgeButton> Buttons = new();
                    public int OverlaysCreated;
                    public string OwnerId => "owner";

                    public TopiaForgeWindow Window(string id, string title, float width, float height, TopiaForgeScheme scheme,
                        bool persistent) => new TopiaForgeWindow(this);
                    public TopiaForgeFullscreenTool FullscreenTool(string id, string title, TopiaForgeScheme scheme,
                        bool persistent) => new TopiaForgeFullscreenTool(this);
                    internal TopiaForgeOverlay Overlay(string id, string title, float width, float height,
                        UiOverlayContentKind kind, bool dismissible)
                    {
                        OverlaysCreated++;
                        return new TopiaForgeOverlay(this, new TopiaForgeContainer(this), dismissible);
                    }
                }

                public class TopiaForgeWidget
                {
                    internal TopiaForgeWidget(UiHost host, GameObject? go = null)
                    {
                        Host = host;
                        Go = go ?? new GameObject();
                        Go.Queried = () => host.ComponentQueries++;
                    }

                    public readonly UiHost Host;
                    public readonly GameObject Go;
                    public RectTransform Rect => Go.transform;
                    internal TopiaForgeResolvedTheme ResolvedTheme => Host.ResolvedTheme;
                    public void SetShown(bool shown) => Go.Shown = shown;
                    public virtual void Destroy() => SetShown(false);
                    internal UnityEngine.UI.LayoutElement EnsureLayoutElement() =>
                        Go.GetComponent<UnityEngine.UI.LayoutElement>() ?? Go.AddComponent<UnityEngine.UI.LayoutElement>();
                }

                public class TopiaForgeContainer : TopiaForgeWidget
                {
                    private readonly TopiaForgeContainer? parent;
                    public readonly List<TopiaForgeContainer> Children = new();

                    public TopiaForgeContainer(UiHost host, TopiaForgeContainer? parent = null) : base(host)
                    {
                        this.parent = parent;
                    }

                    public override void Destroy()
                    {
                        if (Host.FailNextDestroy)
                        {
                            Host.FailNextDestroy = false;
                            throw new InvalidOperationException("Native destruction error fixture");
                        }
                        parent?.Children.Remove(this);
                        Go.transform.SetParent(null, false);
                        base.Destroy();
                    }

                    public TopiaForgeContainer CreateChild(string name)
                    {
                        var child = new TopiaForgeContainer(Host, this);
                        child.Go.transform.SetParent(Go.transform, false);
                        Children.Add(child);
                        return child;
                    }
                    public GameObject CreateChildGameObject(string name)
                    {
                        var child = new GameObject();
                        child.transform.SetParent(Go.transform, false);
                        return child;
                    }
                    public TopiaForgeContainer Column(TopiaForgeGap gap, TopiaForgeGap padding)
                    {
                        Host.ColumnsBuilt++;
                        var child = CreateChild("Column");
                        child.Go.AddComponent<UnityEngine.UI.VerticalLayoutGroup>();
                        return child;
                    }

                    public TopiaForgeContainer Row(TopiaForgeGap gap, TopiaForgeGap padding, bool expandChildWidth = false)
                        => Column(gap, padding);
                    public TopiaForgeContainer Dynamic() => this;
                    public TopiaForgeContainer Flex(float width, float height) => this;
                    public BoundaryScroll Scroll(TopiaForgeGap gap, TopiaForgeGap padding) => new(Column(gap, padding));
                    public TopiaForgeLabel Label(string value, TopiaForgeTextStyle style = TopiaForgeTextStyle.Body)
                    {
                        if (value == Host.FailedLabel)
                            throw new InvalidOperationException("Native label render error fixture");
                        var label = CreateChildGameObject("Label").AddComponent<TopiaForgeLabel>();
                        label.SetText(value);
                        return label;
                    }

                    public TopiaForgeContainer Fixed(float width, float height) => this;
                    public TopiaForgeGraphCanvas GraphCanvas(UiGraphCanvas graph)
                    {
                        var canvas = new TopiaForgeGraphCanvas(this, graph);
                        Host.Graphs.Add(canvas);
                        return canvas;
                    }
                    public TopiaForgeButton Button(string label, Action action, TopiaForgeButtonStyle style)
                    {
                        var button = new TopiaForgeButton(Host, CreateChildGameObject("Button"), action);
                        Host.Buttons[label] = button;
                        return button;
                    }
                    public BoundaryControl Toggle(string label, bool value, Action<bool> action) => new(Host);
                    public BoundaryControl Slider(string label, float min, float max, float value, Action<float> action) => new(Host);
                    public BoundaryControl Checkbox(string label, bool value, Action<bool> action) => new(Host);
                    public BoundaryControl Input(string hint, string value, Action<string> action) => new(Host);
                    public BoundaryControl Dropdown(string[] labels, int selected, Action<int> action) => new(Host);
                    public TopiaForgeListView<T> ListView<T>(float rowHeight) => new();
                    public BoundaryProgress ProgressBar() => new();
                }

                public sealed class TopiaForgeWindow : TopiaForgeContainer
                {
                    public TopiaForgeWindow(UiHost host) : base(host) { }
                    public GameObject CanvasRoot => Go;
                    public TopiaForgeContainer Content => this;
                    public event Action? Closed;
                    public void Show() => SetShown(true);
                    public void Close() { SetShown(false); Closed?.Invoke(); }
                }

                public sealed class TopiaForgeFullscreenTool : TopiaForgeContainer
                {
                    public TopiaForgeFullscreenTool(UiHost host) : base(host) { }
                    public GameObject CanvasRoot => Go;
                    public TopiaForgeContainer Content => this;
                    public event Action? Closed;
                    public void Show() => SetShown(true);
                    public void Close() { SetShown(false); Closed?.Invoke(); }
                }

                internal sealed class TopiaForgeOverlay : TopiaForgeContainer
                {
                    private Action? retired;
                    public TopiaForgeOverlay(UiHost host, TopiaForgeContainer content, bool dismissible) : base(host)
                    {
                        Content = content;
                        Dismissible = dismissible;
                    }
                    public GameObject CanvasRoot => Go;
                    public TopiaForgeContainer Content { get; }
                    public bool Dismissible { get; }
                    public int ViewportRevision { get; set; }
                    public event Action? Closed;
                    public void Show() => SetShown(true);
                    public void Close() => SetShown(false);
                    public void Back() { Close(); Closed?.Invoke(); }
                    public void RefreshViewport() { }
                    public void CompositionChanged() { }
                    public void SetCompositionReady(bool ready) { }
                    public UiLayoutConstraints MeasureContent(TopiaForgeLabel body)
                    {
                        LayoutRebuilder.ForceRebuildLayoutImmediate(Rect);
                        var rect = Content.Rect.rect;
                        return UiLayoutConstraints.Loose(rect.width, rect.height);
                    }
                    public void Retire(Action onRetired) => retired = onRetired;
                    public void FinishRetirement() { Destroy(); retired?.Invoke(); }
                }

                public sealed class TopiaForgeHudLayer : TopiaForgeContainer
                {
                    public TopiaForgeHudLayer(UiHost host) : base(host) { }
                }

                public sealed class TopiaForgeLabel : TMPro.TMP_Text
                {
                    public GameObject Go => gameObject;
                    public RectTransform Rect => transform;
                    public int Seconds { get; private set; } = -1;
                    public void SetShown(bool shown) { }
                    public void SetText(string value) => text = value;
                    public void SetText(string prefix, int seconds)
                    {
                        Seconds = seconds;
                        text = prefix + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }
                    internal void SetBackdrop(UiPaintRole role) { }
                    public TopiaForgeLabel PlainText() => this;
                    public TopiaForgeLabel AlignCenter() => this;
                    public TopiaForgeLabel Tone(TopiaForgeTone tone) => this;
                }

                public sealed class BoundaryScroll
                {
                    public BoundaryScroll(TopiaForgeContainer content) => Content = content;
                    public TopiaForgeContainer Content { get; }
                    public BoundaryScroll FixedHeight(float height) => this;
                    public BoundaryScroll Flex(float width, float height) => this;
                }
                public class BoundaryControl : TopiaForgeWidget
                {
                    public BoundaryControl(UiHost host, GameObject? go = null) : base(host, go) { }
                    public virtual void SetEnabled(bool enabled) { }
                    public void UseDeclarativePaint(bool enabled, int steps = 0, UiValueFormat? format = null) { }
                    public void SetCharacterLimit(int limit) { }
                    public void SetValidationBackdrop(UiPaintRole role) { }
                    public void SetError(bool error) { }
                    internal void SetBackdrop(UiPaintRole role) { }
                }
                public sealed class TopiaForgeButton : BoundaryControl
                {
                    private readonly Action action;
                    internal UnityEngine.UI.Selectable Button { get; }
                    public TopiaForgeButton(UiHost host, GameObject go, Action action) : base(host, go)
                    {
                        this.action = action;
                        Button = go.AddComponent<UnityEngine.UI.Selectable>();
                    }
                    public override void SetEnabled(bool enabled) => Button.interactable = enabled;
                    internal void Click() { if (Button.interactable) action(); }
                }
                internal static class TopiaForgeScopedTooltip
                {
                    internal static void Install(TopiaForgeContainer container, string message, bool enabled, UiPaintRole role) { }
                }
                internal sealed class TopiaForgeInputBindingField
                {
                    internal TopiaForgeInputBindingField(TopiaForgeContainer container, UiKeyBindingField field, string owner,
                        Func<(IInputAction Action, string DisplayName)?> resolve, Func<IInputAction, InputBinding, string?> conflict,
                        bool enabled, UiPaintRole role) { }
                    internal void SetBackdrop(UiPaintRole role) { }
                }
                internal static class TopiaForgeInputOwnership
                {
                    internal static UiInputDevice LastViewerDevice => UiInputDevice.KeyboardMouse;
                }
                internal sealed class TopiaForgeActionKeyCap
                {
                    internal TopiaForgeActionKeyCap(TopiaForgeContainer parent) { }
                    internal UiLayoutLeafMeasurement Measure(UiActionBindingPresentation binding, double width)
                        => new UiLayoutLeafMeasurement(new UiLayoutExtent(32, 24), UiLayoutPaintKind.Protected);
                    internal void Apply(UiActionBindingPresentation binding, UiLayoutRect allocation, UiPaintRole backdrop, UiTone tone) { }
                }
                internal static class TopiaForgeControlFocus
                {
                    internal static Action<UiPaintRole>? Install(TopiaForgeWidget widget, UiPaintRole backdrop) => null;
                }
                public sealed class BoundaryProgress
                {
                    public BoundaryProgress Tone(TopiaForgeTone tone) => this;
                    public void SetFraction(float fraction) { }
                    public void SetHorizontalLayoutFraction(float fraction) { }
                }
                public sealed class TopiaForgeListView<T>
                {
                    public TopiaForgeListView<T> FixedHeight(float height) => this;
                    public void UseDeclarativeRows(int visibleRows, UiPaintRole role) { }
                    public void SetRowBackdrop(UiPaintRole role) { }
                    public void Bind(Action<BoundaryRow, T, int> bind) { }
                    public void OnSelected(Action<int> action) { }
                    public void SetItems(IReadOnlyList<T> items) { }
                    public void SetEnabled(bool enabled) { }
                    public void SetSelectedIndex(int index) { }
                }
                public sealed class BoundaryRow
                {
                    public TopiaForgeLabel Title = new();
                    public TopiaForgeLabel Subtitle = new();
                    public BoundaryBadge Badge = new();
                }
                public sealed class BoundaryBadge { public void Set(string value, TopiaForgeTone tone) { } }
            }

            namespace TopiaForge.ModManager
            {
                internal sealed class UnityUiHudPlacement
                {
                    internal bool TryGetViewportSizes(int handle, out UiLayoutExtent frame, out UiLayoutExtent safe)
                    {
                        frame = safe = default;
                        return false;
                    }
                    internal int DockVersion => 0;
                    internal void SetShown(int handle, bool shown) { }
                    internal void Remove(int handle) { }
                }

                internal sealed class UiGameplayGate { }
                internal sealed class UnityEntityRegistry { }
                internal sealed class UnityWorldLabels : IUiWorldLabels, IDisposable
                {
                    public UnityWorldLabels(UiHost host, IModLifetime lifetime, UiGameplayGate gameplay,
                        UnityEntityRegistry entities, Func<IEntity, bool> canShowEntity) { }
                    public OperationResult<IUiWorldLabel> Add(UiWorldLabelRequest request) => throw new NotSupportedException();
                    public void Dispose() { }
                }

                internal sealed class BoundaryLogger : IModLogger
                {
                    public int Errors { get; private set; }
                    public void Debug(string message) { }
                    public void Info(string message) { }
                    public void Warn(string message) { }
                    public void Error(string message) => Errors++;
                    public void Error(Exception exception, string message) => Errors++;
                }

                internal sealed partial class OwnerUiService
                {
                    private readonly UiHost host = new();
                    private readonly IModLifetime lifetime = new FakeModLifetime();
                    private readonly UiGameplayGate gameplay = new();
                    private static TopiaForgeTone ToNativeTone(UiTone tone) => (TopiaForgeTone)tone;
                    private static Action<UiPaintRole> RenderValidationMessage(TopiaForgeContainer parent, string message,
                        UiPaintRole role) => _ => { };

                    private sealed partial class UnityUiSurface
                    {
                        private TopiaForgeLabel? hudTitle;
                        private string? hudTitleText;
                        private void RefreshChangedLayout() { }
                        private UiThemeScope? localThemeScope;
                        private AdmittedUiCatalogue? remoteCatalogue;
                        private UiThemeAcquisitions? remoteAcquisitions;

                        internal void ConfigureArtwork(UiThemeScope scope, AdmittedUiCatalogue catalogue,
                            UiThemeAcquisitions acquisitions, bool remote)
                        {
                            localThemeScope = remote ? null : scope;
                            remoteCatalogue = remote ? catalogue : null;
                            remoteAcquisitions = remote ? acquisitions : null;
                            remoteComposition = remote;
                        }

                        private void ReleaseThemeResources() { }
                        private bool CanShowImageNotice(TopiaForgeContainer stage, UiLayoutConstraints limits) => true;
                        private void ShowLayoutNotice(TopiaForgeContainer parent, TopiaForgeContainer root, string message,
                            string path, bool preferContentViewport, bool canMoveImageNotice) =>
                            throw new InvalidOperationException("The scroll fixture has no unavailable artwork.");
                        private void ObserveLayout() { }
                        private void RefreshTabbedPage() { }
                        private void RemoveLayoutFallback()
                        {
                            layoutFallback?.Destroy();
                            ClearLayoutNotice();
                        }
                        private void ClearLayoutNotice() { layoutFallback = null; }
                        internal bool GraphConnectionsSuspended { get; private set; }
                        partial void SuspendGraphConnections() => GraphConnectionsSuspended = true;
                        partial void SyncGraphConnections() => GraphConnectionsSuspended = false;
                        private void UpdateRetainedLayout(TopiaForgeContainer root)
                        {
                            if (outerScroll != null) UpdateNativeRetainedLayout(root);
                        }
                        private UiLayoutConstraints MeasureContentArea(TopiaForgeContainer parent, TopiaForgeWidget view) =>
                            outerScroll == null ? UiLayoutConstraints.Loose(view.Rect.rect.width, view.Rect.rect.height)
                                : MeasureNativeContentArea(parent, view);
                        private UiLayoutConstraints MeasureHudContent(UiLayoutExtent frame, int x, int y, float gap,
                            bool includesComposition) =>
                            throw new InvalidOperationException("The scroll fixture has no HUD viewport.");
                        private void ShowLayoutFallback(TopiaForgeContainer parent, TopiaForgeContainer root, UiLayoutException error)
                        {
                            root.SetShown(false);
                            layoutFallback ??= parent.Column(TopiaForgeGap.Sm, TopiaForgeGap.None);
                        }
                    }

                    private sealed class UiSurfaceInputContext
                    {
                        internal UiSurfaceInputContext(object? registry, string ownerPackageId) => OwnerPackageId = ownerPackageId;
                        internal string OwnerPackageId { get; }
                        internal UiSurfaceInputContext ForRemote(UiWireEnvelope? envelope) =>
                            new UiSurfaceInputContext(null, envelope?.OwnerPackageId ?? string.Empty);
                        internal void Validate(UiNode? node) { }
                        internal Func<(IInputAction Action, string DisplayName)?> Resolve(string name) => static () => null;
                        internal UiNativeActionBinding BindAction(string name, IReadOnlyList<UiInputBindingHint> defaults)
                            => new UiNativeActionBinding(static () => null, Array.Empty<UiInputBindingHint>());
                        internal string? Conflict(IInputAction action, InputBinding binding) => null;
                    }

                    private sealed partial class UnityUiLayoutRenderer
                    {
                        // This probe drives real surface admission and artwork acquisition without Unity drawing.
                        private sealed class NativeArtLeaf
                        {
                            private readonly UiImage image;
                            private readonly UiSurfaceArtwork artwork;

                            internal NativeArtLeaf(TopiaForgeContainer host, UiNode node, UiSurfaceArtwork? artwork, UiCallbackGate callbacks)
                            {
                                image = (UiImage)node;
                                this.artwork = artwork!;
                            }

                            internal bool IsMissingSemanticImage => !image.Decorative && artwork.Resolve(image).Sprite == null;
                            internal bool NeedsImageNotice(UiLayoutRect bounds) => false;

                            internal UiLayoutLeafMeasurement Measure(UiLayoutConstraints constraints, UiThemeDescriptor effective,
                                UiThemeDescriptor? declared, bool highContrast, bool protectImageLabels)
                            {
                                var frame = artwork.Resolve(image).Frame;
                                return new UiLayoutLeafMeasurement(new UiLayoutExtent(frame.Width, frame.Height), UiLayoutPaintKind.Image);
                            }

                            internal void Apply(UiLayoutRect bounds, UiPaintRole backdrop, UiThemeScheme scheme,
                                TopiaForgeResolvedTheme theme, UiThemeDescriptor effective, UiThemeDescriptor? declared) { }
                        }

                        private readonly Dictionary<UiNode, NativeActionSlot> actionSlots = new();
                        private readonly List<NativeActionSlot> actionSlotViews = new();

                        private static void CreateActionSlot(NativeNode view, UiActionSlot node, UiCallbackGate callbacks) =>
                            throw new System.NotSupportedException();

                        private sealed class NativeActionSlot
                        {
                            internal bool RefreshBinding() => false;

                            internal UiLayoutLeafMeasurement Measure(UiThemeDescriptor effective,
                                UiThemeDescriptor? declared, bool highContrast) => throw new System.NotSupportedException();

                            internal void Apply(UiPaintRole surface, UiThemeScheme scheme, TopiaForgeResolvedTheme theme,
                                UiThemeDescriptor effective, UiThemeDescriptor? declared) { }
                        }

                        private sealed class NativeStatusLeaf
                        {
                            internal NativeStatusLeaf(TopiaForgeContainer host, UiNode node, UiCallbackGate callbacks) { }

                            internal static bool Handles(UiNode node) => false;

                            internal UiLayoutLeafMeasurement Measure(UiLayoutConstraints constraints, UiThemeDescriptor effective,
                                UiThemeDescriptor? declared, bool highContrast) => throw new System.NotSupportedException();

                            internal void Apply(UiLayoutRect bounds, UiPaintRole backdrop, UiThemeScheme scheme,
                                TopiaForgeResolvedTheme theme, UiThemeDescriptor effective, UiThemeDescriptor? declared) { }
                        }
                    }
                }
            }
            """;
    }
}
