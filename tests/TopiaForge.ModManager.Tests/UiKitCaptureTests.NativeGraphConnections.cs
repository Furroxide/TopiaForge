using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitCaptureTests
    {
        private static void NativeGraphConnectionsUseTheActualControls()
        {
            var root = Program.FindRepoRoot();
            const string widgets = "src/TopiaForge.Mods.UnityUi/Widgets/";
            var files = new[]
            {
                widgets + "TopiaForgeGraphConnections.cs",
                widgets + "TopiaForgeGraphConnections.Rows.cs",
                widgets + "TopiaForgeGraphConnectionControl.cs",
                widgets + "TopiaForgeGraphCanvas.Connections.cs",
                "src/TopiaForge.ModManager/UnityUiSurface.Connections.cs",
                "src/TopiaForge.Mods.Testing/UiCapturedNode.cs",
                "src/TopiaForge.Mods.Testing/UiCaptureViewport.cs",
                "tests/Shared/AllocationProbe.cs",
            };
            var canvasMembers = Member(widgets + "TopiaForgeGraphCanvas.cs", "TopiaForgeGraphCanvas", "HandleEdgeSelected");
            var edgeMembers = Member(widgets + "TopiaForgeGraphCanvasRuntime.cs", "TopiaForgeGraphEdgeView",
                "SetDirectPickingOnly", "SetGeometry", "Select");
            var recorderMembers = Member("src/TopiaForge.ModManager/UiGalleryRecorder/UiGalleryNativeTree.Connections.cs",
                "UiGalleryNativeTree", "GraphConnectionsLauncher", "LocalSize");
            var nativeCapture = new CSharpParseOptions(preprocessorSymbols: new[] { "TOPIAFORGE_NATIVE_CAPTURE" });
            var trees = files.Select(file => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, file)), nativeCapture, file))
                .Append(CSharpSyntaxTree.ParseText(GraphConnectionBoundaries.Replace("/* canvas action */", canvasMembers)
                    .Replace("/* edge actions */", edgeMembers), path: "GraphConnectionBoundaries.cs"))
                .Append(CSharpSyntaxTree.ParseText(GraphConnectionScenarios, path: "GraphConnectionScenarios.cs"))
                .Append(CSharpSyntaxTree.ParseText(GraphConnectionRecorder.Replace("/* recorder methods */", recorderMembers),
                    path: "GraphConnectionRecorder.cs"));
            var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            var references = platform.Append(typeof(UiNode).Assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(path => !Path.GetFileName(path).StartsWith("UnityEngine", StringComparison.Ordinal) &&
                    Path.GetFileName(path) != "TopiaForge.Mods.UnityUi.dll")
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("TopiaForge.ModManager", trees, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release,
                    nullableContextOptions: NullableContextOptions.Enable));
            using var output = new MemoryStream();
            var emit = compilation.Emit(output);
            Check(emit.Success, "The native graph controller compiles at the real control boundary. " + string.Join("\n",
                emit.Diagnostics.Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)));
            var probe = Assembly.Load(output.ToArray()).GetType("TopiaForge.ModManager.OwnerUiService", throwOnError: true)!;
            ((Action)probe.GetMethod("ProbeGraphConnections", BindingFlags.Public | BindingFlags.Static)!
                .CreateDelegate(typeof(Action)))();
            Console.WriteLine("Native graph Connections passed for crossing edges, exact callbacks, keyboard focus, scaled targets and 4,096 settled frames at 0 B.");

            string Member(string path, string type, params string[] names)
            {
                var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, path))).GetRoot();
                return string.Join("\n", syntax.DescendantNodes().OfType<ClassDeclarationSyntax>()
                    .Single(item => item.Identifier.Text == type).Members.OfType<MethodDeclarationSyntax>()
                    .Where(item => names.Contains(item.Identifier.Text)));
            }
        }

        private const string GraphConnectionScenarios = """
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using System.Reflection;
            using TopiaForge.Mods;
            using TopiaForge.Mods.UnityUi;
            using TopiaForge.TestSupport;
            using TMPro;
            using UnityEngine;
            using UnityEngine.EventSystems;
            using UnityEngine.UI;

            namespace TopiaForge.ModManager
            {
                internal sealed partial class OwnerUiService
                {
                    public static void ProbeGraphConnections()
                    {
                        CheckLongLabels(1f);
                        CheckLongLabels(2f);
                        TopiaForgeResolvedTheme.CollapseGraphColors = true;
                        var owner = new UiHost();
                        var window = new TopiaForgeWindow(owner);
                        var map = new Dictionary<string, TopiaForgeGraphCanvas>();
                        var ownerEvents = new List<string>();
                        TopiaForgeGraphCanvas left = null!;
                        UnityUiSurface surface = null!;
                        left = new TopiaForgeGraphCanvas(window.ScrollContent, Graph("first", "West ridge", "East relay",
                            new[] { "z", "a" }, RouteFirst));
                        var right = new TopiaForgeGraphCanvas(window.ScrollContent, Graph("second", "Station", "Lookout",
                            new[] { "station-link" }, _ => ownerEvents.Add("second")));
                        map.Add("first", left);
                        map.Add("second", right);
                        surface = new UnityUiSurface(window, map);
                        surface.Refresh();
                        var chrome = surface.Connections!;
                        var frame = window.TitleBar.Go.GetComponent<TopiaForgeGraphConnectionsFrame>()!;
                        var tick = (Action)typeof(TopiaForgeGraphConnectionsFrame).GetMethod("LateUpdate",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.CreateDelegate(typeof(Action), frame);
                        tick();
                        Require(window.TitleBar.Rect.childCount == 3 && chrome.Launcher.Button.gameObject.activeInHierarchy,
                            "The actual owning surface creates one launcher across both retained graph canvases.");
                        Require(left.FirstEdgeMode == Navigation.Mode.None && right.FirstEdgeMode == Navigation.Mode.None,
                            "The direct edge hit targets keep pointer input and leave keyboard navigation to the list.");
                        Require(left.HasCrossingEdges(),
                            "The shipped edge geometry retains overlapping pointer lines at their real crossing.");
                        left.DirectPointerPick();
                        Require(ownerEvents.SequenceEqual(new[] { "direct:z" }) && TopiaForgeCallbacks.LastOwner == "example.graphs",
                            "Direct picking keeps the existing exact owner-scoped edge callback.");
                        ownerEvents.Clear();

                        right.Header.Select();
                        chrome.Launcher.Button.Select();
                        chrome.Launcher.Button.OnSubmit(new BaseEventData());
                        Require(chrome.ActiveGraph?.Id == "second",
                            "The focused second graph wins while the first graph also has actionable edges.");
                        var titleHeight = window.TitleBar.EnsureLayoutElement().preferredHeight;
                        surface.SuspendForNotice();
                        surface.Refresh();
                        chrome.RemoveControl.Button.OnSubmit(new BaseEventData());
                        Require(!chrome.IsOpen && !chrome.Launcher.Button.gameObject.activeInHierarchy &&
                            right.ConnectionList == null && left.ConnectionList == null && ownerEvents.Count == 0 &&
                            window.TitleBar.EnsureLayoutElement().preferredHeight == titleHeight,
                            "A layout notice retires the Connections chrome and refuses the old Remove action.");
                        surface.RestoreFromNotice();
                        Require(ReferenceEquals(chrome, surface.Connections) && chrome.Launcher.Button.gameObject.activeInHierarchy,
                            "Layout recovery restores the launcher using the same controller.");

                        left.Header.Select();
                        chrome.Launcher.Button.Select();
                        chrome.Launcher.Button.OnSubmit(new BaseEventData());
                        Require(chrome.IsOpen && chrome.ActiveGraph?.Id == "first" && chrome.SelectedEdgeId == "z" &&
                            chrome.Rows.Count == 2, "Keyboard submit opens the graph most recently focused.");
                        Require(chrome.Rows[0].SemanticLabel == "West ridge / Send to East relay / Run" &&
                            chrome.RemoveControl.SemanticLabel == "Remove connection from West ridge / Send to East relay / Run",
                            "Each pooled row labels both endpoints and Remove has a separate full semantic label.");
                        var first = chrome.Rows[0].Button;
                        Require(EventSystem.current.currentSelectedGameObject == first.gameObject &&
                            window.Scroll.GetComponent<CanvasGroup>() is { alpha: 0f, interactable: false, blocksRaycasts: false },
                            "Focus starts on the selected row and authored graph controls cannot receive input underneath.");
                        CheckAperture(chrome, 2f);
                        Require(chrome.Launcher.Button.GetComponent<TopiaForgeControlCues>() is { Applied: true, AuthoredSelectionCue: true } &&
                            chrome.RemoveControl.Button.GetComponent<TopiaForgeControlCues>() is { Applied: true, AuthoredSelectionCue: false },
                            "The open launcher and Remove control configure their individual paint drivers.");
                        first.OnMove(new AxisEventData(MoveDirection.Down));
                        var second = chrome.Rows[1].Button;
                        Require(chrome.SelectedEdgeId == "a" && EventSystem.current.currentSelectedGameObject == second.gameObject,
                            "Keyboard Down reaches the next authored row.");
                        second.OnMove(new AxisEventData(MoveDirection.Right));
                        Require(EventSystem.current.currentSelectedGameObject == chrome.RemoveControl.Button.gameObject &&
                            ownerEvents.Count == 0, "A separate move enters Remove without committing an edge action.");
                        Require(second.GetComponent<TopiaForgeControlCues>() is { Applied: true, AuthoredSelectionCue: true } &&
                            first.GetComponent<TopiaForgeControlCues>() is { AuthoredSelectionCue: false },
                            "The authored edge stays marked while focus moves to Remove and the old row is deselected.");
                        chrome.RemoveControl.Button.OnSubmit(new BaseEventData());
                        Require(ownerEvents.SequenceEqual(new[] { "list:a" }) && chrome.SelectedEdgeId == "z" &&
                            EventSystem.current.currentSelectedGameObject == first.gameObject,
                            "Remove routes the exact edge once and restores focus to the surviving row.");
                        first.OnMove(new AxisEventData(MoveDirection.Right));
                        chrome.RemoveControl.Button.OnSubmit(new BaseEventData());
                        Require(ownerEvents.SequenceEqual(new[] { "list:a", "list:z" }) && !chrome.IsOpen &&
                            EventSystem.current.currentSelectedGameObject == left.Header.gameObject &&
                            window.Scroll.GetComponent<CanvasGroup>() is { alpha: 1f, interactable: true, blocksRaycasts: true },
                            "Removing the last edge closes the list, restores authored input and returns graph focus.");
                        right.Header.Select();
                        chrome.Launcher.Button.Select();
                        chrome.Launcher.Button.OnSubmit(new BaseEventData());
                        Require(chrome.ActiveGraph?.Id == "second" && chrome.Rows.Count == 2 &&
                            chrome.Rows.Count(row => row.Button.gameObject.activeSelf) == 1,
                            "The same launcher and row objects switch to the second graph.");
                        var allocations = AllocationProbe.Measure("settled native Connections frame", tick, 4096, 128);
                        Require(allocations == 0, "Unchanged native Connections frames allocate 0 B.");

                        window.Content.Rect.sizeDelta = new Vector2(240, 68);
                        tick();
                        CheckAperture(chrome, 2f);
                        Require(chrome.RemoveControl.DisplayLabel == "Remove" &&
                            chrome.RemoveControl.SemanticLabel == "Remove connection from Station / Send to Lookout / Run",
                            "The compact viewport preserves a readable Remove caption and full semantic label.");
                        ((ITopiaForgeDismissable)chrome).Dismiss();
                        Require(!chrome.IsOpen && EventSystem.current.currentSelectedGameObject == right.Header.gameObject,
                            "Native Back closes the local list and restores the graph's previous focus.");

                        var remoteWindow = new TopiaForgeWindow(owner);
                        var remote = new UnityUiSurface(remoteWindow, map, remote: true);
                        remote.Refresh();
                        Require(remote.Connections == null && remoteWindow.TitleBar.Rect.childCount == 2,
                            "Remote composition never receives the graph Connections launcher.");
                        surface.Retire();
                        Require(surface.Connections == null && right.ConnectionList == null && left.ConnectionList == null &&
                            right.FirstEdgeMode == Navigation.Mode.Automatic,
                            "Surface retirement releases its chrome and graph relays while restoring legacy edge navigation.");

                        void RouteFirst(string edge)
                        {
                            ownerEvents.Add((surface.Connections?.IsOpen == true ? "list:" : "direct:") + edge);
                            if (surface.Connections?.IsOpen != true) return;
                            left.SetGraph(Graph("first", "West ridge", "East relay", left.Graph.Edges.Where(e => e.Id != edge)
                                .Select(e => e.Id).ToArray(), RouteFirst));
                            surface.Refresh();
                        }

                    }

                    private static void CheckLongLabels(float scale)
                    {
                        var owner = new UiHost { EffectiveUiScale = scale };
                        var window = new TopiaForgeWindow(owner);
                        window.Content.Rect.sizeDelta = new Vector2(240f, 220f);
                        window.TitleBar.Rect.sizeDelta = new Vector2(240f, 42f);
                        var source = new string('W', 255) + "A";
                        var port = new string('M', 255) + "B";
                        var target = new string('X', 255) + "C";
                        var input = new string('Y', 255) + "D";
                        var removed = new List<string>();
                        var view = new TopiaForgeGraphCanvas(window.ScrollContent, new UiGraphCanvas("maximal", new[]
                        {
                            new UiGraphNode("s", "relay", source, new Vec2(-90, 30),
                                new[] { new UiGraphPort("out", port, UiGraphPortDirection.Output) }),
                            new UiGraphNode("a", "relay", target, new Vec2(90, -30),
                                new[] { new UiGraphPort("in", input, UiGraphPortDirection.Input) }),
                            new UiGraphNode("b", "relay", "Beta station", new Vec2(90, 30),
                                new[] { new UiGraphPort("in", "Input", UiGraphPortDirection.Input) }),
                        }, new[]
                        {
                            new UiGraphEdge("alpha", "s", "out", "a", "in"),
                            new UiGraphEdge("beta", "s", "out", "b", "in"),
                        }, _ => { }, height: 176f, connectionRemoved: removed.Add));
                        var surface = new UnityUiSurface(window, new Dictionary<string, TopiaForgeGraphCanvas> { ["maximal"] = view });
                        surface.Refresh();
                        var chrome = surface.Connections!;
                        var frame = window.TitleBar.Go.GetComponent<TopiaForgeGraphConnectionsFrame>()!;
                        var tick = (Action)typeof(TopiaForgeGraphConnectionsFrame).GetMethod("LateUpdate",
                            BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate(typeof(Action), frame);
                        var launcher = chrome.Launcher;
                        var launcherSlot = (RectTransform)launcher.Rect.parent!;
                        var allocated = launcherSlot.gameObject.GetComponent<LayoutElement>()!;
                        window.TitleBar.Go.AddComponent<Image>();
                        launcher.Label.CharacterAdvance = 9.09091f;
                        window.TitleBar.Rect.sizeDelta = new Vector2(242f, 42f);
                        tick();
                        launcherSlot.sizeDelta = new Vector2(allocated.preferredWidth, allocated.preferredHeight);
                        var recorder = new UiGalleryNativeTree();
                        Require(launcher.DisplayLabel == "Connections" && launcher.MeasureText("Connections").Width >
                            launcher.Label.rectTransform.rect.width && recorder.CaptureLauncher(chrome) &&
                            recorder.CapturedCaption == "Connections" && recorder.CapturedLabelFits,
                            "The real recorder preserves the assigned full caption across fractional native layout rounding.");

                        launcher.Label.CharacterAdvance = 14f;
                        window.TitleBar.Rect.sizeDelta = new Vector2(241f, 42f);
                        tick();
                        launcherSlot.sizeDelta = new Vector2(allocated.preferredWidth, allocated.preferredHeight);
                        var labelSize = launcher.MeasureText(launcher.DisplayLabel);
                        Require(launcher.DisplayLabel == "Links" && launcher.SemanticLabel == "Connections" &&
                            labelSize.Width <= launcher.Label.rectTransform.rect.width &&
                            labelSize.Height <= launcher.Label.rectTransform.rect.height,
                            "The real launcher measures the short caption inside its padded text box.");
                        Require(recorder.CaptureLauncher(chrome) && recorder.CapturedCaption == "Links" && recorder.CapturedLabelFits,
                            "The native capture reports the actual compact caption and usable geometry.");
                        launcher.Label.text = "Unlisted value";
                        Require(recorder.CaptureLauncher(chrome) && recorder.CapturedCaption.Length == 0 && !recorder.CapturedLabelFits,
                            "The native recorder refuses a caption outside its two authored variants.");
                        launcher.Label.text = "Links";
                        launcher.Button.Select();
                        launcher.Button.OnSubmit(new BaseEventData());
                        Require(chrome.IsOpen, "The compact launcher opens the pane.");
                        window.TitleBar.Rect.sizeDelta = new Vector2(160f, 42f);
                        tick();
                        launcher.Button.OnSubmit(new BaseEventData());
                        Require(chrome.LauncherNeedsRoom && !launcher.Button.gameObject.activeInHierarchy && !chrome.IsOpen,
                            "Losing readable launcher room retires the pane and its submit action.");
                        window.TitleBar.Rect.sizeDelta = new Vector2(240f, 42f);
                        tick();
                        Require(launcher.Button.gameObject.activeInHierarchy && !chrome.IsOpen,
                            "A restored title aperture shows the launcher and waits for player activation.");
                        launcher.Button.Select();
                        launcher.Button.OnSubmit(new BaseEventData());
                        Require(chrome.IsOpen && chrome.Rows.Count == 2, "Both compact authored rows open.");
                        CheckAperture(chrome, scale);
                        Require(chrome.Rows[1].DisplayLabel.Contains("Beta station") &&
                            chrome.RemoveControl.SemanticLabel == "Remove connection from " + source + " / " + port + " to " + target + " / " + input,
                            "The native compact rows distinguish the shorter target and retain the full selected name.");
                        var row = chrome.Rows[0];
                        var label = row.Label;
                        var display = label.text;
                        var suffixes = new[] { "A", "B", "C", "D" };
                        var revealed = new HashSet<string>();
                        for (var page = 0; page < row.LabelPages; page++)
                        {
                            var size = label.GetPreferredValues(label.text, float.PositiveInfinity, 0f);
                            Require(label.overflowMode == TextOverflowModes.Overflow &&
                                size.x <= label.rectTransform.rect.width && size.y <= label.rectTransform.rect.height,
                                "The native widget shows each page within its actual padded label rectangle without ellipsis.");
                            foreach (var suffix in suffixes)
                                if (label.text.Contains(suffix)) revealed.Add(suffix);
                            row.Button.OnSubmit(new BaseEventData());
                        }
                        Require(label.text == display && revealed.Count == suffixes.Length &&
                            chrome.SelectedEdgeId == "alpha" && removed.Count == 0,
                            "Submitting the selected row reveals both full endpoints, wraps and never removes.");
                        var bytes = AllocationProbe.Measure("settled native long labels", tick, 4096, 128);
                        Require(bytes == 0, "The settled native viewport repeats no label measurements or allocations.");
                        row.Button.OnMove(new AxisEventData(MoveDirection.Down));
                        chrome.Rows[1].Button.OnMove(new AxisEventData(MoveDirection.Right));
                        chrome.RemoveControl.Button.OnSubmit(new BaseEventData());
                        Require(removed.SequenceEqual(new[] { "beta" }), "Only the separate Remove action sends the intended edge.");
                        surface.Retire();
                    }

                    private static UiGraphCanvas Graph(string id, string source, string target, string[] edges, Action<string> remove) =>
                        new UiGraphCanvas(id, new[]
                        {
                            new UiGraphNode("s", "relay", source, new Vec2(-90, 30),
                                new[] { new UiGraphPort("o", "Send", UiGraphPortDirection.Output) }),
                            new UiGraphNode("t", "relay", target, new Vec2(90, -30),
                                new[] { new UiGraphPort("i", "Run", UiGraphPortDirection.Input) }),
                        }, edges.Select(edge => new UiGraphEdge(edge, "s", "o", "t", "i")), _ => { }, connectionRemoved: remove);

                    private static void CheckAperture(TopiaForgeGraphConnections chrome, float scale)
                    {
                        var remove = chrome.RemoveControl.Rect;
                        var targets = chrome.Rows.Where(row => row.Button.gameObject.activeSelf).Select(row => row.Rect).Append(remove).ToArray();
                        var pane = chrome.Rect.rect;
                        for (var index = 0; index < targets.Length; index++)
                        {
                            var current = targets[index];
                            var position = current.anchoredPosition;
                            var size = current.rect.size;
                            Require(size.x * scale >= 40f * scale && size.y * scale >= 40f * scale,
                                "The drawn controls meet the physical target floor.");
                            Require(position.x >= 5 && -position.y >= 5 && position.x + size.x <= pane.width - 5 &&
                                -position.y + size.y <= pane.height - 5, "Every target and its focus ring fit the actual chrome aperture.");
                            for (var later = index + 1; later < targets.Length; later++)
                            {
                                var other = targets[later];
                                var p = other.anchoredPosition;
                                var s = other.rect.size;
                                Require(position.x + size.x <= p.x || p.x + s.x <= position.x ||
                                    -position.y + size.y <= -p.y || -p.y + s.y <= -position.y,
                                    "The drawn native targets never overlap.");
                            }
                        }
                    }

                    private static void Require(bool condition, string message)
                    {
                        if (!condition) throw new InvalidOperationException(message);
                    }

                    private sealed partial class UnityUiSurface
                    {
                        private readonly bool remoteComposition;
                        private readonly TopiaForgeWindow? window;
                        private readonly TopiaForgeFullscreenTool? fullscreen = null;
                        private readonly TopiaForgeContainer? compositionParent;
                        private TopiaForgeContainer? layoutFallback;
                        private readonly IReadOnlyDictionary<string, TopiaForgeGraphCanvas> retainedGraphs;

                        internal UnityUiSurface(TopiaForgeWindow window, IReadOnlyDictionary<string, TopiaForgeGraphCanvas> graphs,
                            bool remote = false)
                        {
                            this.window = window;
                            retainedGraphs = graphs;
                            compositionParent = window.ScrollContent;
                            remoteComposition = remote;
                        }

                        internal TopiaForgeGraphConnections? Connections => graphConnections;
                        internal void Refresh() => SyncGraphConnections();
                        internal void Retire() => ResetGraphConnections(retire: true);
                        internal void SuspendForNotice()
                        {
                            SuspendGraphConnections();
                            layoutFallback = compositionParent;
                        }
                        internal void RestoreFromNotice()
                        {
                            layoutFallback = null;
                            SyncGraphConnections();
                        }
                        partial void SuspendGraphConnections();
                        partial void SyncGraphConnections();
                        partial void ResetGraphConnections(bool retire);
                    }
                }
            }
            """;
    }
}
