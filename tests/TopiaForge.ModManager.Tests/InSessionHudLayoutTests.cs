using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    // Execute the real overlay lifecycle against a recording widget contract and the real dock math.
    internal static class InSessionHudLayoutTests
    {
        internal static void Run()
        {
            var file = Path.Combine(Program.FindRepoRoot(), "src/TopiaForge.ModManager/Overlay/InSessionOverlay.cs");
            var sources = new[] { File.ReadAllText(file), Contracts, Probe };
            var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            var references = trusted.Concat(new[] { typeof(TopiaForgeHudDock).Assembly.Location,
                typeof(InputActionDefinition).Assembly.Location }).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("InSessionHudProbe" + Guid.NewGuid().ToString("N"),
                sources.Select(source => CSharpSyntaxTree.ParseText(source)), references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release,
                    nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var emitted = compilation.Emit(bytes);
            if (!emitted.Success) throw new InvalidOperationException("Overlay probe compile: " +
                string.Join("; ", emitted.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)));
            var assembly = Assembly.Load(bytes.ToArray());
            try { assembly.GetType("HudProbe")!.GetMethod("Run")!.Invoke(null, null); }
            catch (TargetInvocationException error) { throw error.InnerException!; }
            Console.WriteLine("InSessionHudLayoutTests passed: real overlay build/tick/loss/disposal, join/rehost and open/closed geometry.");
        }

        private const string Contracts = """
            using System;
            using System.Collections.Generic;
            using TopiaForge.Mods;
            using TopiaForge.Mods.UnityUi;
            namespace UnityEngine
            {
                public class GameObject { public bool activeInHierarchy = true; public bool Destroyed; }
            }
            namespace UnityEngine.SceneManagement
            {
                public struct Scene { public int handle; }
                public static class SceneManager { public static int Handle = 1; public static Scene GetActiveScene() => new Scene { handle = Handle }; }
            }
            namespace UnityEngine.InputSystem
            {
                public class Keyboard { }
                public class InputControl { public object device = new Keyboard(); public string name = ""; }
                public class InputAction { public List<InputControl> controls = new List<InputControl>(); }
                public static class InputSystem { public static void ListEnabledActions(List<InputAction> actions) { } }
            }
            namespace TopiaForge.Mods.UnityUi
            {
                public sealed class ProbeRect { public float Width, Height; public TopiaForgeCorner Corner; }
                public class TopiaForgeContainer
                {
                    public readonly UnityEngine.GameObject Go = new UnityEngine.GameObject();
                    public readonly ProbeRect Rect = new ProbeRect();
                    public readonly TopiaForgeLayerBand Band;
                    public TopiaForgeContainer(TopiaForgeLayerBand band) { Band = band; }
                    public TopiaForgeContainer Stack(string name) => new TopiaForgeContainer(Band);
                    public TopiaForgeContainer Size(float width, float height) { Rect.Width = width; Rect.Height = height; return this; }
                    public TopiaForgeContainer Column(TopiaForgeGap gap) => new TopiaForgeContainer(Band);
                    public void Stretch() { }
                    public TopiaForgeContainer Flex(float x, float y) => this;
                    public void Dock(TopiaForgeCorner corner) { Rect.Corner = corner; }
                    public void SetShown(bool shown) { Go.activeInHierarchy = shown; }
                }
                public sealed class TopiaForgeUiOptions { public string OwnerId = ""; }
                public sealed class UiHost : IDisposable
                {
                    public event Action<UnityEngine.GameObject>? LayerLost;
                    public bool Disposed;
                    public TopiaForgeContainer Layer(string name, TopiaForgeLayerBand band, TopiaForgeScheme scheme, bool interactive, bool persistent) => new TopiaForgeContainer(band);
                    public void Lose() { LayerLost?.Invoke(new UnityEngine.GameObject()); }
                    public void Dispose() { Disposed = true; }
                }
                public static class TopiaForgeUi { public static UiHost? Last; public static UiHost Create(TopiaForgeUiOptions options) => Last = new UiHost(); }
                public static class TopiaForgeDismissStack { public static int Count => 0; }
            }
            namespace TopiaForge.ModManager.Sessions
            {
                public sealed class InSessionPresenter
                {
                    public bool IsAvailable = true, IsOpen, IsTyping;
                    public void Open() { IsOpen = true; }
                    public void Close() { IsOpen = false; IsTyping = false; }
                }
                public sealed class MultiplayerSessionsPresenter { public InSessionPresenter InSession = new InSessionPresenter(); public object Session = new object(); }
                public sealed class InSessionPauseEntries : IDisposable
                {
                    public InSessionPauseEntries(object session, Action show) { }
                    public void Tick(IWorldPauseMenuService? service, object? world, bool gameplay) { }
                    public void Dispose() { }
                }
                public sealed class ConnectionIndicator
                {
                    public ConnectionIndicator(TopiaForgeContainer parent, InSessionPresenter screen) { }
                    public void Tick(bool gameplay) { }
                }
                public sealed class ChatOverlay : IDisposable
                {
                    public static TopiaForgeContainer? Parent;
                    private readonly InSessionPresenter screen;
                    public ChatOverlay(TopiaForgeContainer parent, InSessionPresenter screen) { Parent = parent; this.screen = screen; }
                    public void Tick(bool gameplay) { if (!gameplay || !screen.IsAvailable) Close(); }
                    public void Close() { screen.Close(); }
                    public void Lost() { screen.Close(); }
                    public void Dispose() { screen.Close(); }
                }
            }
            namespace TopiaForge.ModManager
            {
                public sealed class ProbeInputs
                {
                    public OperationResult<IInputAction> Register(string owner, InputActionDefinition definition) => OperationResult<IInputAction>.Failure(ModErrorCode.Unavailable, "unused key");
                    public bool IsBoundByOther(string owner, TopiaForge.Mods.InputBinding binding) => false;
                }
                public sealed class ProbePlacement
                {
                    public readonly TopiaForgeHudDock Dock = new TopiaForgeHudDock();
                    public readonly List<(int Handle, ProbeRect Rect)> Entries = new List<(int, ProbeRect)>();
                    public int Add(UiHost host, ProbeRect rect, TopiaForgeCorner corner, float height)
                    { var handle = Dock.Add(corner, height, 1); Entries.Add((handle, rect)); return handle; }
                    public void SetShown(int handle, bool shown) { Dock.SetShown(handle, shown); }
                    public void Remove(int handle) { Dock.Remove(handle); Entries.RemoveAll(e => e.Handle == handle); }
                }
                public sealed class ProbeGameplay { public bool IsGameplay = true; }
                public sealed class CoreGameplayServices
                {
                    public ProbeInputs Inputs = new ProbeInputs(); public ProbePlacement HudPlacement = new ProbePlacement(); public ProbeGameplay UiGameplay = new ProbeGameplay();
                }
                public sealed class ProbeWorld { public object? Current => null; }
                public sealed class ProbeMultiplayer { public bool IsLocalPlayerTyping; }
                public sealed class TopiaForgeModManagerPlugin
                {
                    public CoreGameplayServices Gameplay = new CoreGameplayServices(); public ProbeMultiplayer Multiplayer = new ProbeMultiplayer();
                    public IWorldPauseMenuService? GetPauseMenu() => null; public ProbeWorld GetSessionService() => new ProbeWorld();
                }
            }
            """;

        private const string Probe = """
            using System;
            using System.Reflection;
            using TopiaForge.ModManager;
            using TopiaForge.ModManager.Sessions;
            using TopiaForge.Mods.UnityUi;
            public static class HudProbe
            {
                private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException("HUD regression: " + reason); }
                private static TopiaForgeContainer? Field(InSessionOverlay overlay, string name) =>
                    (TopiaForgeContainer?)typeof(InSessionOverlay).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(overlay);
                public static void Run()
                {
                    foreach (var early in new[] { true, false })
                    {
                        var plugin = new TopiaForgeModManagerPlugin(); var screen = new MultiplayerSessionsPresenter();
                        var placement = plugin.Gameplay.HudPlacement; var dock = placement.Dock;
                        using var overlay = new InSessionOverlay(plugin, screen, () => { });
                        var round = 0; var card = 0;
                        if (!early) { round = Add(dock, 300); card = Add(dock, 540); }
                        overlay.Tick();
                        for (var cycle = 0; cycle < 2; cycle++)
                        {
                            if (cycle == 1 || early) { round = Add(dock, 300); card = Add(dock, 540); }
                            foreach (var open in new[] { false, true, false })
                            {
                                screen.InSession.IsOpen = open; overlay.Tick();
                                dock.TryGetPosition(card, out _, out var y);
                                foreach (var height in new[] { 1080f, 1440f })
                                {
                                    var factor = height / 1080f;
                                    var top = (y + 540f) * factor + height * .12f;
                                    var noChatY = TopiaForgeTokens.SafeMargin + 300f + TopiaForgeHudDock.Gap;
                                    Check(top <= height, "chat before/rehost cannot push card above viewport; top=" + top + " height=" + height +
                                        " observed-capture-shift=" + ((y - noChatY) * .8f));
                                }
                                Check(placement.Entries.Count == 1 && placement.Entries[0].Rect.Height == 28,
                                    "only the actual 28-unit connection status reserves HUD space");
                                Check(Field(overlay, "root")?.Go.activeInHierarchy == true,
                                    "connection status remains visible with chat open or closed");
                                var chat = ChatOverlay.Parent!;
                                Check(chat.Band == TopiaForgeLayerBand.Window && chat.Rect.Corner == TopiaForgeCorner.BottomRight,
                                    "open chat is a separate window pane, not a shared HUD reservation");
                                Check(chat.Rect.Width == 600 && chat.Rect.Height == 330, "recent lines and full composer keep usable geometry");
                            }
                            dock.Remove(round); dock.Remove(card); // Same persistent connection HUD, fresh mode HUDs on rehost.
                        }
                        screen.InSession.IsAvailable = false; overlay.Tick();
                        Check(Field(overlay, "root")?.Go.activeInHierarchy == false && Field(overlay, "chatRoot")?.Go.activeInHierarchy == false,
                            "both layers hide when session is unavailable");
                        screen.InSession.IsAvailable = true; overlay.Tick();
                        plugin.Gameplay.UiGameplay.IsGameplay = false; overlay.Tick();
                        Check(Field(overlay, "root")?.Go.activeInHierarchy == false && Field(overlay, "chatRoot")?.Go.activeInHierarchy == false,
                            "both layers hide outside gameplay");
                        plugin.Gameplay.UiGameplay.IsGameplay = true; overlay.Tick();
                        var old = TopiaForgeUi.Last!; screen.InSession.IsOpen = screen.InSession.IsTyping = true;
                        old.Lose();
                        Check(old.Disposed && !screen.InSession.IsTyping && placement.Entries.Count == 0,
                            "either lost layer clears typing and its reservation and disposes the host");
                        overlay.Tick(); Check(placement.Entries.Count == 1, "lost layers rebuild exactly one status reservation");
                        overlay.Dispose();
                        Check(TopiaForgeUi.Last!.Disposed && placement.Entries.Count == 0 && Field(overlay, "root") == null && Field(overlay, "chatRoot") == null,
                            "dispose retires both roots and the reservation");
                    }
                }
                private static int Add(TopiaForgeHudDock dock, float height)
                { var handle = dock.Add(TopiaForgeCorner.BottomLeft, height, 1); dock.SetShown(handle, true); return handle; }
            }
            """;
    }
}
