namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiGalleryInteractionPreviewTests
    {
        private const string Boundaries = """
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using System.Reflection;
            using TMPro;
            using TopiaForge.ModManager.Core;
            using TopiaForge.Mods;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine;

            namespace UnityEngine
            {
                public static class Time { public static int frameCount; }
                public static class Screen { public static int width = 1920, height = 1080; }
                public static class Mathf { public static bool Approximately(float left, float right) => left == right; }
                public struct Vector2 { public float x, y; public float sqrMagnitude => x * x + y * y; }
                public struct Rect { public float width, height; }
                public sealed class RectTransform
                {
                    public Rect rect = new() { width = 840, height = 460 };
                    public Vector2 localScale = new() { x = 1, y = 1 };
                }

                public abstract class MonoBehaviour { public bool enabled = true; }
                public sealed class Canvas : MonoBehaviour
                {
                    public bool isActiveAndEnabled => enabled;
                    public float scaleFactor = 1;
                }
                public sealed class CanvasGroup : MonoBehaviour { public float alpha = 1; }

                public sealed class GameObject
                {
                    private static readonly List<GameObject> All = new();
                    private readonly List<MonoBehaviour> components = new();
                    public GameObject() => All.Add(this);
                    public bool activeInHierarchy = true;

                    public T? GetComponent<T>() where T : MonoBehaviour => components.OfType<T>().FirstOrDefault();
                    public T? GetComponentInParent<T>() where T : MonoBehaviour => GetComponent<T>();

                    public T AddComponent<T>() where T : MonoBehaviour
                    {
                        var component = (T)Activator.CreateInstance(typeof(T), nonPublic: true)!;
                        components.Add(component);
                        Invoke(component, "Awake");
                        return component;
                    }

                    public void Destroy()
                    {
                        foreach (var component in components) Invoke(component, "OnDestroy");
                        components.Clear();
                        activeInHierarchy = false;
                        All.Remove(this);
                    }

                    public static void Tick()
                    {
                        Time.frameCount++;
                        foreach (var gameObject in All.ToArray())
                            if (gameObject.activeInHierarchy)
                                foreach (var component in gameObject.components.ToArray())
                                    if (component.enabled) Invoke(component, "LateUpdate");
                    }

                    private static void Invoke(MonoBehaviour target, string method) =>
                        target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(target, null);
                }
            }

            namespace UnityEngine.UI
            {
                public sealed class ScrollRect : MonoBehaviour { public Vector2 velocity; }
            }

            namespace TopiaForge.Mods.UnityUi
            {
                internal sealed class TopiaForgeLayerSlot : MonoBehaviour { }
                internal sealed class TopiaForgeGraphConnections { }
                public enum TopiaForgeScheme { Paper, Hud }
                public enum TopiaForgeKey { F3, F6, F11 }
                public enum TopiaForgeGap { None, Sm, Md }
                public enum TopiaForgeTextStyle { Body }
                public enum TopiaForgeButtonStyle { Ghost, Danger, Filled }
                public enum TopiaForgeTone { Neutral, Success, Warning, Danger, Muted, Primary, Accent }

                public sealed record TopiaForgeAccessibilityProfile(bool HighContrast, float UiScale, bool ReducedMotion, float MotionIntensity);
                internal sealed record TopiaForgeResolvedTheme(TopiaForgeScheme Scheme, UiThemeDescriptor? Descriptor);

                public sealed class TopiaForgeUiOptions
                {
                    public string OwnerId { get; set; } = "";
                    public string? DataDirectory { get; set; }
                    public Action<string>? LogInfo { get; set; }
                    public Action<string>? LogWarn { get; set; }
                    public Action<string>? LogError { get; set; }
                }
                public static class TopiaForgeUi { public static UiHost Create(TopiaForgeUiOptions options) => new(options.OwnerId); }
                public static class TopiaForgeToasts { public static void ShowPlain(string message, TopiaForgeTone tone) { } }

                public static class TopiaForgeHotkeys
                {
                    private sealed record Entry(TopiaForgeKey Key, Action Action);
                    private static readonly List<Entry> Entries = new();
                    public static int Count => Entries.Count;
                    public static object Register(string owner, TopiaForgeKey key, Action action)
                    {
                        var entry = new Entry(key, action);
                        Entries.Add(entry);
                        return entry;
                    }
                    public static void Unregister(object handle) => Entries.Remove((Entry)handle);
                    public static void Press(TopiaForgeKey key)
                    {
                        foreach (var entry in Entries.ToArray())
                            if (entry.Key == key && Entries.Contains(entry)) entry.Action();
                    }
                }

                public sealed class UiHost : IDisposable
                {
                    private readonly Dictionary<GameObject, UiThemeDescriptor?> themes = new();
                    private readonly List<object> hotkeys = new();
                    private TopiaForgeAccessibilityProfile? accessibility;
                    public UiHost(string owner) => OwnerId = owner;
                    public string OwnerId { get; }
                    public bool PlainText { get; set; }
                    public static TopiaForgeAccessibilityProfile ViewerAccessibility { get; set; } = new(false, 1, false, 1);
                    public TopiaForgeAccessibilityProfile EffectiveAccessibility => accessibility ?? ViewerAccessibility;
                    public TopiaForgeModals Modal { get; } = new();
                    public event Action<GameObject>? LayerLost;
                    public void SetAccessibilityProfile(TopiaForgeAccessibilityProfile profile) => accessibility = profile;
                    internal void SetSurfaceTheme(GameObject canvas, UiThemeDescriptor? descriptor,
                        Func<UiTextStyle, TMP_FontAsset?>? fonts = null) => themes[canvas] = descriptor;
                    internal UiThemeDescriptor? Theme(GameObject canvas) => themes.GetValueOrDefault(canvas);
                    public object Hotkey(TopiaForgeKey key, Action callback)
                    {
                        var handle = TopiaForgeHotkeys.Register(OwnerId, key, callback);
                        hotkeys.Add(handle);
                        return handle;
                    }
                    public TopiaForgeWindow Window(string id, string title, float width, float height, TopiaForgeScheme scheme, bool persistent) =>
                        new(this, id, title, width, height, scheme, persistent);
                    public TopiaForgeFullscreenTool FullscreenTool(string id, string title, TopiaForgeScheme scheme, bool persistent) =>
                        new(this, scheme);
                    public void Dispose()
                    {
                        foreach (var handle in hotkeys) TopiaForgeHotkeys.Unregister(handle);
                        hotkeys.Clear();
                        LayerLost = null;
                    }
                }

                public class TopiaForgeWidget
                {
                    public TopiaForgeWidget(UiHost host, TopiaForgeScheme scheme) { Host = host; Scheme = scheme; }
                    public UiHost Host { get; }
                    public TopiaForgeScheme Scheme { get; }
                    public GameObject Go { get; } = new();
                    public RectTransform Rect { get; } = new();
                    public virtual void Destroy() => Go.Destroy();
                }
                public sealed class TopiaForgeContainer : TopiaForgeWidget
                {
                    private readonly GameObject canvas;
                    public TopiaForgeContainer(UiHost host, TopiaForgeScheme scheme, GameObject canvas) : base(host, scheme) => this.canvas = canvas;
                    internal TopiaForgeResolvedTheme ResolvedTheme => new(Scheme, Host.Theme(canvas));
                    public TopiaForgeLabel Label(string text, TopiaForgeTextStyle style) => new(text);
                    public TopiaForgeContainer Scroll(TopiaForgeGap gap, TopiaForgeGap padding) => this;
                    public TopiaForgeContainer Content => this;
                    public TopiaForgeContainer Flex(float width, float height) => this;
                    public TopiaForgeContainer Row(TopiaForgeGap gap) => this;
                    public void Spacer() { }
                    public void Button(string text, Action call, TopiaForgeButtonStyle style) { }
                }
                public sealed class TopiaForgeLabel
                {
                    public TopiaForgeLabel(string value) => Value = value;
                    public string Value { get; set; }
                }
                public sealed class TopiaForgeWindow : TopiaForgeWidget
                {
                    public static readonly List<TopiaForgeWindow> Active = new();
                    public TopiaForgeWindow(UiHost host, string id, string title, float width, float height,
                        TopiaForgeScheme scheme, bool persistent) : base(host, scheme)
                    {
                        Id = id; Title = title; Width = width; Height = height; Persistent = persistent;
                        Content = new TopiaForgeContainer(host, scheme, Go);
                        Go.AddComponent<Canvas>();
                    }
                    public string Id { get; }
                    public string Title { get; }
                    public float Width { get; }
                    public float Height { get; }
                    public bool Persistent { get; }
                    public TopiaForgeContainer Content { get; }
                    public TopiaForgeLabel? Body { get; set; }
                    public UiNode? Admitted { get; set; }
                    internal UiThemeDescriptor? ThemeAtAdmission { get; set; }
                    internal TopiaForgeResolvedTheme Theme => Content.ResolvedTheme;
                    public override void Destroy() { Active.Remove(this); Content.Destroy(); base.Destroy(); }
                }
                public sealed class TopiaForgeFullscreenTool : TopiaForgeWidget
                {
                    public TopiaForgeFullscreenTool(UiHost host, TopiaForgeScheme scheme) : base(host, scheme) =>
                        Content = new TopiaForgeContainer(host, scheme, Go);
                    public TopiaForgeContainer Content { get; }
                }
                public sealed class TopiaForgeModals
                {
                    public TopiaForgeModalInstance Custom(string title, TopiaForgeScheme scheme, float size, bool persistent) =>
                        throw new NotSupportedException();
                }
                public sealed class TopiaForgeModalInstance
                {
                    public TopiaForgeContainer Content => throw new NotSupportedException();
                    public event Action? Closed;
                    public void Show() { }
                    public void Close() => Closed?.Invoke();
                }
            }

            namespace TopiaForge.ModManager
            {
                internal sealed class UnityUiHudPlacement { public void Refresh() { } }
                internal static class MultiplayerContentInventory
                {
                    internal sealed class LoadedPackage
                    {
                        public LoadedPackage(ModManifest manifest) => Manifest = manifest;
                        public ModManifest Manifest { get; }
                    }
                }
                internal interface IRemoteUiReceiver
                {
                    OperationResult<IUiSurface> RemoteSurface(UiSurfaceRequest request);
                    OperationResult<IUiModal> RemoteModal(UiModalRequest request, Action<bool> completed);
                    OperationResult<bool> RemoteToast(string message, UiTone tone);
                }
                internal sealed class PreviewLogger : IModLogger
                {
                    public void Debug(string message) { }
                    public void Info(string message) { }
                    public void Warn(string message) { }
                    public void Error(string message) { }
                    public void Error(Exception error, string message) { }
                }
                internal sealed class UiGalleryNativeTree
                {
                    internal UiGalleryNativeTree(Canvas canvas, TopiaForgeResolvedTheme theme,
                        TopiaForgeAccessibilityProfile accessibility, bool fallback)
                    {
                        Theme = theme;
                        Accessibility = accessibility;
                    }
                    internal TopiaForgeResolvedTheme Theme { get; }
                    internal TopiaForgeAccessibilityProfile Accessibility { get; }
                    internal bool TryCapture(UiSurfaceRequest fixture, RectTransform surface, RectTransform tree, RectTransform? imageNotice,
                        out object? result, TopiaForgeGraphConnections? connections = null, string? queuedNotice = null)
                    {
                        result = fixture.Content;
                        return result != null;
                    }
                    internal bool TryCaptureLayoutNotice(UiSurfaceRequest fixture, RectTransform surface, RectTransform notice,
                        UiLayoutException failure, out object? result, string? queuedNotice = null) =>
                        throw new InvalidOperationException("The interaction fixture should fit without a layout notice.");
                }
                internal static class UiGalleryCaptureJson
                {
                    internal sealed record Capture(string Fixture, string Viewer, TopiaForgeResolvedTheme Theme,
                        TopiaForgeAccessibilityProfile Accessibility, UiWireEnvelope? Envelope);
                    internal static readonly List<Capture> Records = new();
                    internal static void Save(string directory, string fixture, string viewer, object tree,
                        UiGalleryNativeTree native, UiWireEnvelope? envelope) =>
                        Records.Add(new Capture(fixture, viewer, native.Theme, native.Accessibility, envelope));
                }

                internal sealed partial class OwnerUiService
                {
                    private readonly Func<double> clock = () => 0;
                    private readonly Func<string> clockEpoch = () => "preview";

                    private UnityUiSurface CreateHud(UiSurfaceRequest request, string? packageDisplayName, ref TopiaForgeWidget? nativeRoot)
                    {
                        var view = new TopiaForgeWidget(host, TopiaForgeScheme.Hud);
                        return new UnityUiSurface(request.Id, view,
                            new TopiaForgeContainer(host, view.Scheme, view.Go), new TopiaForgeLabel(request.Body), lifetime, logger, ReleaseSurface);
                    }

                    private sealed class UnityUiModal : IUiModal
                    {
                        public UnityUiModal(TopiaForgeModalInstance modal, Action<bool> completed, IModLogger logger) { }
                        public bool IsOpen => false;
                        public void AttachLifetimeLease(IDisposable lease) { }
                        public void ArmCompletion() { }
                        public void HandleNativeClosed() { }
                        public void Confirm() { }
                        public void Cancel() { }
                        public void Abort() { }
                        public void Close() { }
                        public void Dispose() { }
                    }

                    private sealed partial class UnityUiSurface : IUiSurface
                    {
                        private TopiaForgeWidget? widget;
                        private TopiaForgeContainer? compositionRoot;
                        private UiNode? admitted;
                        private TopiaForgeContainer? layoutFallback;
                        private TopiaForgeGraphConnections? graphConnections;
                        private UiLayoutException? layoutProblem;
                        private bool hudNoticeOverflowing;
                        private string? overflowToast;
                        private UiThemeDescriptor? declaredTheme;
                        private bool remoteComposition;
                        private readonly UiSurfacePresence presence = new(false, true);
                        private readonly Action<UnityUiSurface> release;
                        private readonly TopiaForgeLabel body;
                        private IDisposable? tracked;
                        private bool disposed;
                        private readonly TopiaForgeContainer parent;

                        internal UnityUiSurface(string id, TopiaForgeWidget widget, TopiaForgeContainer parent,
                            TopiaForgeLabel body, IModLifetime lifetime, IModLogger logger, Action<UnityUiSurface> release)
                        {
                            Id = id; this.widget = widget; this.parent = parent; this.body = body;
                            CanvasRoot = widget.Go; this.release = release; this.lifetime = lifetime; this.logger = logger;
                            if (widget is TopiaForgeWindow window) window.Body = body;
                        }
                        internal static UnityUiSurface ForWindow(string id, TopiaForgeWindow window, TopiaForgeLabel body,
                            TopiaForgeContainer parent, IModLifetime lifetime, IModLogger logger, Action<UnityUiSurface> release) =>
                            new(id, window, parent, body, lifetime, logger, release);
                        internal static UnityUiSurface ForFullscreen(string id, TopiaForgeFullscreenTool window, TopiaForgeLabel body,
                            TopiaForgeContainer parent, IModLifetime lifetime, IModLogger logger, Action<UnityUiSurface> release) =>
                            new(id, window, parent, body, lifetime, logger, release);

                        public string Id { get; }
                        public bool IsVisible => presence.IsVisible;
                        internal GameObject CanvasRoot { get; }
                        internal bool FollowsGameplay => false;
                        internal void ApplyGameplay(bool state) { }
                        internal void HandleLost() => Dispose();
                        internal void SetClock(Func<double> source, Func<string> epoch) { }
                        internal void AttachLifetimeLease(IDisposable lease) => tracked = lease;
                        internal void Present()
                        {
                            if (widget is TopiaForgeWindow window) TopiaForgeWindow.Active.Add(window);
                        }
                        public void Show() => presence.Show();
                        public void Hide() => presence.Hide();
                        public void SetBody(string value) => body.Value = value;
                        public OperationResult<bool> SetContent(UiNode content) =>
                            SetComposition(content, declaredTheme, remoteComposition);
                        private OperationResult<bool> SetComposition(UiNode content, UiThemeDescriptor? descriptor,
                            bool remote, Action<TopiaForgeContainer>? prepareTheme = null,
                            Func<UiNode, UiSurfaceArtwork?>? prepareArtwork = null)
                        {
                            if (widget?.Scheme == TopiaForgeScheme.Hud && widget is not TopiaForgeWindow && UiComposition.ContainsInteractive(content))
                                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "HUD is presentation-only");
                            var staged = prepareArtwork == null ? PrepareArtwork(content) : prepareArtwork(content);
                            ReleaseArtwork();
                            artwork = staged;
                            prepareTheme?.Invoke(parent);
                            declaredTheme = descriptor;
                            admitted = content;
                            compositionRoot = parent;
                            if (widget is TopiaForgeWindow window)
                            {
                                window.Admitted = content;
                                window.ThemeAtAdmission = window.Theme.Descriptor;
                            }
                            return OperationResult<bool>.Success(true);
                        }
                        public void Dispose()
                        {
                            if (disposed) return;
                            disposed = true;
                            presence.Dispose();
                            ReleaseArtwork();
                            ReleaseThemeResources();
                            widget?.Destroy();
                            widget = null;
                            release(this);
                            tracked?.Dispose();
                        }
                    }
                }
            }
            """;
    }
}
