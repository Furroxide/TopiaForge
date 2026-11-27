namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiSurfaceReapplyTests
    {
        private const string Boundaries = """
            using System;
            using System.Collections.Generic;
            using TopiaForge.Mods;
            using TopiaForge.Mods.Testing;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine;
            using UnityEngine.UI;

            namespace TopiaForge.Mods.UnityUi
            {
                public enum TopiaForgeGap { None, Xs, Sm, Md }
                public enum TopiaForgeTone { Neutral, Success, Warning, Danger, Muted, Primary, Accent }
                public enum TopiaForgeButtonStyle { Filled, Outline, Ghost, Danger }
                public enum TopiaForgeTextStyle { Body, Heading, Caption, Banner, Display, Title, Label, Numeral, Mono }
                public static class TopiaForgeTokens { public const float ControlHeight = 40f; }
                public sealed class TopiaForgeResolvedTheme
                {
                    internal UiThemeDescriptor? Descriptor { get; }
                    internal bool HighContrast { get; }
                    internal TopiaForgeScheme Scheme { get; }
                    internal Color Shadow { get; }
                }
                internal enum TopiaForgeScheme { Paper, Hud }

                public sealed class UiHost
                {
                    public TopiaForgeResolvedTheme ResolvedTheme = new();
                    public int ColumnsBuilt;
                    public int ComponentQueries;
                    public string? FailedLabel;
                    public bool FailNextDestroy;
                    public bool FailNextGraphBind;
                    public readonly List<TopiaForgeGraphCanvas> Graphs = new();
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
                    public BoundaryControl Button(string label, Action action, TopiaForgeButtonStyle style)
                    {
                        var control = new BoundaryControl(Host);
                        control.Rect.SetParent(Rect, false);
                        return control;
                    }
                    public BoundaryControl Toggle(string label, bool value, Action<bool> action) => new(Host);
                    public BoundaryControl Slider(string label, float min, float max, float value, Action<float> action) => new(Host);
                    public BoundaryControl Input(string hint, string value, Action<string> action) => new(Host);
                    public BoundaryControl Dropdown(string[] labels, int selected, Action<int> action) => new(Host);
                    public TopiaForgeListView<T> ListView<T>(float rowHeight) => new();
                    public BoundaryProgress ProgressBar() => new();
                }

                public sealed class TopiaForgeWindow : TopiaForgeContainer
                {
                    public TopiaForgeWindow(UiHost host) : base(host) { }
                    public GameObject CanvasRoot => Go;
                    public event Action? Closed;
                    public void Show() => SetShown(true);
                    public void Close() { SetShown(false); Closed?.Invoke(); }
                }

                public sealed class TopiaForgeFullscreenTool : TopiaForgeContainer
                {
                    public TopiaForgeFullscreenTool(UiHost host) : base(host) { }
                    public GameObject CanvasRoot => Go;
                    public event Action? Closed;
                    public void Show() => SetShown(true);
                    public void Close() { SetShown(false); Closed?.Invoke(); }
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
                    public TopiaForgeLabel Tone(TopiaForgeTone tone) => this;
                }

                public sealed class BoundaryScroll
                {
                    public BoundaryScroll(TopiaForgeContainer content) => Content = content;
                    public TopiaForgeContainer Content { get; }
                    public BoundaryScroll FixedHeight(float height) => this;
                }
                public sealed class BoundaryControl : TopiaForgeWidget
                {
                    public BoundaryControl(UiHost host) : base(host) { }
                    public void SetEnabled(bool enabled) { }
                    public void UseDeclarativePaint(bool enabled) { }
                    public void SetCharacterLimit(int limit) { }
                    internal void SetBackdrop(UiPaintRole role) { }
                }
                internal static class TopiaForgeControlFocus
                {
                    internal static Action<UiPaintRole>? Install(TopiaForgeWidget widget, UiPaintRole backdrop) => null;
                }
                public sealed class BoundaryProgress
                {
                    public BoundaryProgress Tone(TopiaForgeTone tone) => this;
                    public void SetFraction(float fraction) { }
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
                    public UnityWorldLabels(UiHost host, IModLifetime lifetime, UiGameplayGate gameplay, UnityEntityRegistry entities) { }
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

                    private sealed partial class UnityUiSurface
                    {
                        private TopiaForgeLabel? hudTitle;
                        private string? hudTitleText;
                        private static void PrepareStagedTheme(TopiaForgeContainer stage, UiThemeDescriptor? descriptor) { }
                        private void RefreshChangedLayout() { }
                        private void ObserveLayout() { }
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
                        private UiLayoutConstraints MeasureHudContent(UiLayoutExtent frame, int x, int y, float gap, bool includesComposition) =>
                            throw new InvalidOperationException("The scroll fixture has no HUD viewport.");
                        private void ShowLayoutFallback(TopiaForgeContainer parent, TopiaForgeContainer root, UiLayoutException error)
                        {
                            root.SetShown(false);
                            layoutFallback ??= parent.Column(TopiaForgeGap.Sm, TopiaForgeGap.None);
                        }
                    }

                }
            }
            """;
    }
}
