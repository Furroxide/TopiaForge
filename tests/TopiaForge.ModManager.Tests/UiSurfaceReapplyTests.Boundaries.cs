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

            namespace UnityEngine
            {
                public static class Screen
                {
                    public static int width = 1920;
                    public static int height = 1080;
                }

                public struct Rect { public float width, height; }
                public sealed class RectTransform { public Rect rect; }
                public abstract class MonoBehaviour { }

                public sealed class GameObject
                {
                    internal readonly List<GameObject> Children = new();
                    private readonly List<MonoBehaviour> components = new();
                    internal Action? Queried;
                    public bool Shown { get; set; } = true;

                    public T AddComponent<T>() where T : MonoBehaviour
                    {
                        var created = (T)Activator.CreateInstance(typeof(T), nonPublic: true)!;
                        components.Add(created);
                        return created;
                    }

                    public T[] GetComponentsInChildren<T>(bool includeInactive) where T : MonoBehaviour
                    {
                        Queried?.Invoke();
                        var found = new List<T>();
                        Collect(this, found, includeInactive);
                        return found.ToArray();
                    }

                    private static void Collect<T>(GameObject root, List<T> found, bool includeInactive)
                        where T : MonoBehaviour
                    {
                        if (!root.Shown && !includeInactive) return;
                        foreach (var component in root.components)
                            if (component is T typed) found.Add(typed);
                        foreach (var child in root.Children)
                            Collect(child, found, includeInactive);
                    }
                }
            }

            namespace UnityEngine.UI
            {
                public static class LayoutRebuilder
                {
                    public static int Requests { get; private set; }
                    public static void MarkLayoutForRebuild(RectTransform _) => Requests++;
                }
            }

            namespace TopiaForge.Mods.UnityUi
            {
                public enum TopiaForgeGap { None, Xs, Sm, Md }
                public enum TopiaForgeTone { Neutral, Success, Warning, Danger, Muted, Primary, Accent }
                public enum TopiaForgeButtonStyle { Filled, Outline, Ghost, Danger }
                public enum TopiaForgeTextStyle { Body, Heading, Caption, Banner, Display, Title, Label, Numeral, Mono }
                public static class TopiaForgeTokens { public const float ControlHeight = 40f; }
                public sealed class TopiaForgeResolvedTheme { }

                public sealed class UiHost
                {
                    public TopiaForgeResolvedTheme ResolvedTheme = new();
                    public int ColumnsBuilt;
                    public int ComponentQueries;
                    public string? FailedLabel;
                    public bool FailNextDestroy;
                }

                public class TopiaForgeWidget
                {
                    internal TopiaForgeWidget(UiHost host)
                    {
                        Host = host;
                        Go.Queried = () => host.ComponentQueries++;
                    }

                    public readonly UiHost Host;
                    public readonly GameObject Go = new();
                    public readonly RectTransform Rect = new() { rect = new Rect { width = 840, height = 460 } };
                    internal TopiaForgeResolvedTheme ResolvedTheme => Host.ResolvedTheme;
                    public void SetShown(bool shown) => Go.Shown = shown;
                    public virtual void Destroy() => SetShown(false);
                }

                public class TopiaForgeContainer : TopiaForgeWidget
                {
                    private readonly TopiaForgeContainer? parent;
                    public readonly List<TopiaForgeContainer> Children = new();
                    public readonly List<TopiaForgeLabel> Labels = new();

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
                        parent?.Go.Children.Remove(Go);
                        base.Destroy();
                    }

                    public TopiaForgeContainer Column(TopiaForgeGap gap, TopiaForgeGap padding)
                    {
                        Host.ColumnsBuilt++;
                        var child = new TopiaForgeContainer(Host, this);
                        Children.Add(child);
                        Go.Children.Add(child.Go);
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
                        var label = new TopiaForgeLabel();
                        label.SetText(value);
                        Labels.Add(label);
                        return label;
                    }

                    public BoundaryControl Button(string label, Action action, TopiaForgeButtonStyle style) => new(Host);
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

                public sealed class TopiaForgeLabel
                {
                    public int Seconds { get; private set; } = -1;
                    public void SetText(string value) { }
                    public void SetText(string prefix, int seconds) => Seconds = seconds;
                    public TopiaForgeLabel PlainText() => this;
                    public TopiaForgeLabel Tone(TopiaForgeTone tone) => this;
                }

                public sealed class TopiaForgeGraphCanvas { }
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
                }
                internal static class TopiaForgeControlFocus
                {
                    internal static void Install(TopiaForgeWidget widget) { }
                }
                public sealed class BoundaryProgress
                {
                    public BoundaryProgress Tone(TopiaForgeTone tone) => this;
                    public void SetFraction(float fraction) { }
                }
                public sealed class TopiaForgeListView<T>
                {
                    public TopiaForgeListView<T> FixedHeight(float height) => this;
                    public void UseDeclarativeRows(int visibleRows) { }
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

                    private sealed class UiGraphRetentionTransaction
                    {
                        private readonly IReadOnlyDictionary<string, TopiaForgeGraphCanvas> previous;
                        public UiGraphRetentionTransaction(IReadOnlyDictionary<string, TopiaForgeGraphCanvas> previous)
                            => this.previous = previous;
                        public IReadOnlyDictionary<string, TopiaForgeGraphCanvas> Commit() => previous;
                        public Exception? Rollback(TopiaForgeContainer parent) => null;
                        public void Render(UiGraphCanvas graph, TopiaForgeContainer parent, UiCallbackGate callbacks)
                            => throw new NotSupportedException("Graph construction is outside this surface probe.");
                    }
                }
            }
            """;
    }
}
