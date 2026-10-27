using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>Production view dispatches through a bridge to the real presenter; engine hierarchy is simulated.</summary>
    internal static class ScoreboardInteractionTests
    {
        internal static void Run()
        {
            var root = Program.FindRepoRoot();
            var contracts = (string)typeof(ScoreboardViewContractTests).GetField("Contracts", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
            var start = contracts.IndexOf("public class Transform", StringComparison.Ordinal);
            var end = contracts.IndexOf("public struct Vector2", StringComparison.Ordinal);
            contracts = contracts.Substring(0, start) + Hierarchy + contracts.Substring(end);
            var kit = "src/TopiaForge.Mods.UnityUi/";
            var widget = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, kit + "Widgets/TopiaForgeWidget.cs")))
                .GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == "SetSiblingIndex");
            var paths = new[] { "src/TopiaForge.ModManager/Overlay/Multiplayer/ScoreboardView.cs",
                "src/TopiaForge.ModManager/Multiplayer/Screens/IScoreboardScreen.cs", kit + "Runtime/TopiaForgeFocus.cs",
                kit + "Core/TopiaForgeDismissOrder.cs", kit + "Core/TopiaForgeInputClaim.cs",
                kit + "Core/TopiaForgeNavigationDriver.cs", kit + "Core/TopiaForgeInputGate.cs" };
            var source = paths.Select(path => File.ReadAllText(Path.Combine(root, path))).Concat(new[] { contracts, Probe, MatrixProbe,
                "namespace TopiaForge.Mods.UnityUi { public partial class Widget { " + widget + " } public static partial class TopiaForgeInputOwnership { internal static bool HoldsSelection(object root) => false; } }" });
            var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            var compilation = CSharpCompilation.Create("IndependentScoreboard" + Guid.NewGuid().ToString("N"),
                source.Select(text => CSharpSyntaxTree.ParseText(text)), trusted.Select(path => MetadataReference.CreateFromFile(path)),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
            using var bytes = new MemoryStream(); var result = compilation.Emit(bytes);
            That(result.Success, "independent production view compiles: " + string.Join("; ", result.Diagnostics.Where(item => item.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)));
            using var bridge = new Bridge();
            try
            {
                var assembly = Assembly.Load(bytes.ToArray());
                using var matrix = new Bridge();
                assembly.GetType("MatrixProbe")!.GetMethod("Run")!.Invoke(null,
                    new object[] { (Func<int[]>)matrix.Read, (Action<string, int, float>)matrix.Dispatch });
                assembly.GetType("InteractionProbe")!.GetMethod("Run")!.Invoke(null,
                    new object[] { (Func<int[]>)bridge.Read, (Action<string, int, float>)bridge.Dispatch });
            }
            catch (TargetInvocationException error) { throw error.InnerException!; }
            Console.WriteLine("ScoreboardInteractionTests passed: 576 permutation transitions, ties, paging, rejoin, retained gestures, hierarchy ownership and zero resting moves/allocations.");
        }

        private sealed class Bridge : IDisposable
        {
            private readonly MultiplayerParticipant[] players = {
                SessionsRig.Player("p1", "Alpha", "a"), SessionsRig.Player("p2", "Bravo", "b"), SessionsRig.Player("p3", "Charlie", "c") };
            private readonly RecordingSessionControl control = new RecordingSessionControl { Role = InternalSessionRole.ListenHost };
            private readonly VoiceRig voice = VoiceRig.On(InternalSessionRole.ListenHost);
            private readonly ScoreboardData[] scores = Enumerable.Range(0, 8)
                .Select(i => new ScoreboardData("example", new ScoreboardColumnDefinition("points" + i, "Points " + i, i))).ToArray();
            private ScoreboardData Score => scores[0];
            private readonly ScoreboardPresenter screen;
            private readonly List<ScoreboardRow> identities = new List<ScoreboardRow>();
            private ScreenPrompt? prompt;
            internal Bridge()
            {
                control.SessionSnapshot = SessionsRig.HostSnapshot(MultiplayerSessionState.Ready, "s", players);
                screen = new ScoreboardPresenter(() => control, () => scores, () => voice.Runtime, value => prompt = value, (_, _) => { });
                screen.Tick(); identities.AddRange(screen.Rows);
            }
            internal int[] Read()
            {
                screen.Tick();
                foreach (var row in screen.Rows) if (!identities.Contains(row)) identities.Add(row);
                return screen.Rows.Select(row => identities.IndexOf(row)).ToArray();
            }
            internal void Dispatch(string action, int id, float value)
            {
                if (action == "permutation")
                {
                    for (var i = 0; i < 4; i++)
                    {
                        var row = identities[(id >> (2 * i)) & 3];
                        var participant = control.SessionSnapshot.Participants.Single(player => player.DisplayName == row.Name);
                        foreach (var column in scores) column.Values[participant.Id] = new ScoreboardValue(i.ToString(), 4 - i);
                    }
                    if (screen.SortColumn != 0 || !screen.Descending) screen.Sort(0);
                    return;
                }
                if (action == "ties")
                {
                    foreach (var column in scores) foreach (var player in control.SessionSnapshot.Participants) column.Values[player.Id] = new ScoreboardValue("tie", 1);
                    return;
                }
                if (action == "rejoin") { control.SessionSnapshot = SessionsRig.HostSnapshot(MultiplayerSessionState.Ready, "s", players); return; }
                if (action == "sort") { screen.Sort(id); return; }
                if (action == "reorder")
                {
                    var order = new[] { 2, 1, 3, 0 };
                    for (var i = 0; i < order.Length; i++)
                    {
                        var participant = control.SessionSnapshot.Participants.Single(player => player.DisplayName == identities[order[i]].Name);
                        Score.Values[participant.Id] = new ScoreboardValue(i.ToString(), 4 - i);
                    }
                    screen.Sort(0); return;
                }
                if (action == "depart") { control.SessionSnapshot = SessionsRig.HostSnapshot(MultiplayerSessionState.Ready, "s", players.Skip(1).ToArray()); return; }
                if (action == "reuse") { control.SessionSnapshot = SessionsRig.HostSnapshot(MultiplayerSessionState.Ready, "s", SessionsRig.Player("p1", "Replacement", "other"), players[1], players[2]); return; }
                if (action == "kick") { screen.Kick(identities[id]); prompt?.Answer(true); }
                else if (action == "gain") screen.Volume(identities[id], value / 100f);
                else if (action == "mute") screen.Mute(identities[id]);
                else if (action == "verify")
                {
                    var failures = new List<string>();
                    if (!control.Calls.SequenceEqual(new[] { "Kick:p1" })) failures.Add("pointer press redirected canonical participant");
                    if (voice.Runtime.Playback.Volumes.Get(players[0], control.SessionSnapshot.Id) != 0.25f) failures.Add("slider drag missed original canonical player");
                    if (voice.Runtime.Playback.Volumes.Get(players[2], control.SessionSnapshot.Id) != 1f) failures.Add("slider drag changed replacement sorted player");
                    That(failures.Count == 0, string.Join("; ", failures));
                }
                else if (action == "clear") { control.Calls.Clear(); prompt = null; }
                else if (action == "stale")
                {
                    That(control.Calls.Count == 0 && prompt == null, "departed/reused control callback has no provider authority");
                    That(voice.Runtime.Playback.Volumes.Get(players[0], control.SessionSnapshot.Id) == 0.25f, "departed callback cannot alter original gain");
                    That(voice.Runtime.Playback.Volumes.Get(SessionsRig.Player("p1", "Replacement", "other"), control.SessionSnapshot.Id) == 1f, "recycled ID does not inherit callback gain");
                }
            }
            public void Dispose() => voice.Runtime.Dispose();
        }

        private const string Hierarchy = """
            public class Transform
            {
                public readonly GameObject Go; private Transform owner;
                public static int Moves, ParentChanges;
                public readonly List<Transform> Children = new List<Transform>();
                public Transform(GameObject go) { Go = go; }
                public Transform parent { get => owner; set { ParentChanges++; owner?.Children.Remove(this); owner = value; owner?.Children.Add(this); } }
                public bool IsChildOf(Transform root) => this == root || parent?.IsChildOf(root) == true;
                public int GetSiblingIndex() => parent == null ? 0 : parent.Children.IndexOf(this);
                public void SetSiblingIndex(int value) { Moves++; if (parent == null) return; var list = parent.Children; list.Remove(this); list.Insert(Math.Min(value, list.Count), this); }
            }
            """;

        private const string Probe = """
            using System;
            using System.Linq;
            using System.Collections.Generic;
            using System.Reflection;
            using TopiaForge.ModManager.Sessions;
            using TopiaForge.Mods.UnityUi;
            namespace TopiaForge.ModManager.Sessions { internal enum VoiceIndicator { Idle } }
            public static class InteractionProbe
            {
                public static void Run(Func<int[]> read, Action<string, int, float> action)
                {
                    Widget.All.Clear(); var screen = new Adapter(read, action); screen.Refresh();
                    var view = new ScoreboardView(new TopiaForgeContainer(), screen);
                    var rowRoot = Widget.All.OfType<TopiaForgeLabel>().Single(label => label.Text == "Player 0").Go.transform.parent.parent;
                    var list = rowRoot.parent;
                    var kick = Widget.All.OfType<TopiaForgeButton>().Single(button => button.Text == "KICK" && button.Go.transform.IsChildOf(rowRoot));
                    var slider = Widget.All.OfType<TopiaForgeSlider>().Single(control => control.Go.transform.IsChildOf(rowRoot));
                    var mute = Widget.All.OfType<TopiaForgeButton>().Single(button => button.Text == "MUTE" && button.Go.transform.IsChildOf(rowRoot));
                    // Retain the actual delegates as already queued UI dispatch may outlive destruction.
                    var click = (Action)typeof(TopiaForgeButton).GetField("callback", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(kick);
                    var muted = (Action)typeof(TopiaForgeButton).GetField("callback", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(mute);
                    kick.Focus(); action("reorder", 0, 0); screen.Refresh(); view.Tick();
                    Check(UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject == null, "re-sort releases keyboard/controller selection");
                    click(); slider.Change(25); action("verify", 0, 0);
                    Check(ReferenceEquals(rowRoot.parent, list), "reorder preserves layout parent ownership");
                    var actual = list.Children.Where(child => child.Go.activeInHierarchy).Select(child => Widget.All.OfType<TopiaForgeLabel>()
                        .Single(label => label.Text.StartsWith("Player ") && label.Go.transform.IsChildOf(child)).Text).ToArray();
                    Check(actual.SequenceEqual(screen.Rows.Select(row => row.Name)), "four-row permutation [2,1,3,0] must render in canonical sorted order: " + string.Join(",", actual));
                    action("clear", 0, 0); kick.Focus(); action("depart", 0, 0); screen.Refresh(); view.Tick();
                    Check(!rowRoot.Go.activeInHierarchy && UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject == null, "departure destroys and releases selected row");
                    click(); slider.Change(5); muted(); action("stale", 0, 0);
                    action("reuse", 0, 0); screen.Refresh(); view.Tick();
                    click(); slider.Change(5); muted(); action("stale", 0, 0);
                }
                private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
                private sealed class Adapter : IScoreboardScreen
                {
                    private readonly Func<int[]> read; private readonly Action<string,int,float> action;
                    private readonly Dictionary<int, ScoreboardRow> byId = new Dictionary<int, ScoreboardRow>();
                    private readonly Dictionary<ScoreboardRow, int> ids = new Dictionary<ScoreboardRow,int>();
                    private readonly List<ScoreboardRow> rows = new List<ScoreboardRow>();
                    public Adapter(Func<int[]> read, Action<string,int,float> action) { this.read = read; this.action = action; }
                    public void Refresh() { rows.Clear(); foreach (var id in read()) { if (!byId.TryGetValue(id, out var row)) { row = new ScoreboardRow("Player " + id, false, false) { CanHear = true, CanModerate = true }; byId.Add(id,row); ids.Add(row,id); } rows.Add(row); } }
                    public IReadOnlyList<ScoreboardRow> Rows => rows;
                    public IReadOnlyList<string> Columns => Array.Empty<string>(); public string Note => "";
                    public int SortColumn => -1; public bool Descending => false; public void Sort(int column) { }
                    public void Mute(ScoreboardRow row) => action("mute", ids[row], 0);
                    public void Volume(ScoreboardRow row, float gain) => action("gain", ids[row], gain * 100);
                    public void ServerMute(ScoreboardRow row) { }
                    public void Kick(ScoreboardRow row) => action("kick", ids[row], 0);
                    public void Ban(ScoreboardRow row) { }
                }
            }
            """;

        private const string MatrixProbe = """
            using System;
            using System.Linq;
            using System.Collections.Generic;
            using TopiaForge.ModManager.Sessions;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine;
            public static class MatrixProbe
            {
                public static void Run(Func<int[]> read, Action<string,int,float> action)
                {
                    Widget.All.Clear(); var screen = new Screen(read, action); screen.Refresh();
                    var view = new ScoreboardView(new TopiaForgeContainer(), screen);
                    var first = Label("Player 0").Go.transform.parent.parent;
                    var list = first.parent;
                    var kick = Widget.All.OfType<TopiaForgeButton>().Single(button => button.Text == "KICK" && button.Go.transform.IsChildOf(first));
                    var slider = Widget.All.OfType<TopiaForgeSlider>().Single(control => control.Go.transform.IsChildOf(first));
                    var click = (Action)typeof(TopiaForgeButton).GetField("callback", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(kick);
                    var parents = Transform.ParentChanges;
                    var permutations = new List<int[]>();
                    for (var a = 0; a < 4; a++) for (var b = 0; b < 4; b++)
                    for (var c = 0; c < 4; c++) for (var d = 0; d < 4; d++)
                    { var order = new[] { a,b,c,d }; if (order.Distinct().Count() == 4) permutations.Add(order); }
                    void Set(int[] order)
                    {
                        action("permutation", order[0] | order[1] << 2 | order[2] << 4 | order[3] << 6, 0);
                        screen.Refresh(); view.Tick();
                        Check(screen.Rows.Select(row => row.Name).SequenceEqual(order.Select(id => "Player " + id)), "presenter obeys requested numeric permutation");
                        Order(list, screen);
                    }
                    foreach (var from in permutations) foreach (var to in permutations) { Set(from); Set(to); }
                    Check(Transform.ParentChanges == parents, "all 576 transitions preserve layout parents");
                    Set(new[] { 2,1,3,0 }); action("sort", 0, 0); screen.Refresh(); view.Tick(); Order(list, screen);
                    Check(screen.Rows.Select(row => row.Name).SequenceEqual(new[] { "Player 0", "Player 3", "Player 1", "Player 2" }), "ascending numeric sort reverses distinct score order");
                    action("ties", 0, 0); screen.Refresh(); view.Tick(); Order(list, screen);
                    Check(screen.Rows.Select(row => row.Name).SequenceEqual(new[] { "Player 3", "Player 0", "Player 1", "Player 2" }), "ties use canonical participant ids");
                    // Page changes and ascending/descending sorts must leave actual hierarchy and values aligned.
                    Widget.All.OfType<TopiaForgeButton>().Single(button => button.Text == "NEXT").Click(); view.Tick();
                    var sorts = Widget.All.OfType<TopiaForgeButton>().Where(button => button.Text == "SORT").ToArray();
                    sorts[2].Click(); Check(screen.LastColumn == 6, "paged sort uses current column");
                    screen.Refresh(); view.Tick(); Order(list, screen);
                    Check(Widget.All.OfType<TopiaForgeLabel>().Any(label => label.Text == "0:7"), "paged values belong to retained player");
                    click(); slider.Change(25); action("verify", 0, 0);
                    // Reuse the same id/key, retaining old callbacks across both removal and return.
                    action("clear", 0, 0); action("depart", 0, 0); screen.Refresh(); view.Tick(); Order(list, screen);
                    click(); slider.Change(5); action("stale", 0, 0);
                    action("rejoin", 0, 0); screen.Refresh(); view.Tick(); Order(list, screen);
                    click(); slider.Change(5); action("stale", 0, 0); Check(!first.Go.activeInHierarchy, "old same-key controls stay retired");
                    Check(screen.Rows.Any(row => row.Name == "Player 4"), "same-key rejoin creates fresh row authority");
                    // The helper itself must skip already-correct indices and correct externally displaced siblings.
                    var widget = new TopiaForgeContainer(); widget.Go.transform.parent = list;
                    widget.SetSiblingIndex(0); var moves = Transform.Moves;
                    widget.SetSiblingIndex(0); Check(Transform.Moves == moves, "helper dirty-checks actual sibling index");
                    widget.Go.transform.SetSiblingIndex(list.Children.Count - 1); widget.SetSiblingIndex(0);
                    Check(widget.Go.transform.GetSiblingIndex() == 0, "helper corrects actual displacement");
                    widget.Go.transform.parent = null; view.Tick(); Order(list, screen);
                    for (var i = 0; i < 200; i++) view.Tick();
                    moves = Transform.Moves; parents = Transform.ParentChanges;
                    var allocated = GC.GetAllocatedBytesForCurrentThread();
                    for (var i = 0; i < 10000; i++) view.Tick();
                    Check(GC.GetAllocatedBytesForCurrentThread() == allocated, "hierarchy-aware production view allocates zero at rest");
                    Check(Transform.Moves == moves && Transform.ParentChanges == parents, "resting frames perform zero sibling writes or reparents");
                }
                private static TopiaForgeLabel Label(string text) => Widget.All.OfType<TopiaForgeLabel>().Single(label => label.Text == text);
                private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
                private static void Order(Transform list, Screen screen)
                {
                    var actual = list.Children.Where(child => child.Go.activeInHierarchy).Select(child => Widget.All.OfType<TopiaForgeLabel>()
                        .Single(label => label.Text.StartsWith("Player ") && label.Go.transform.IsChildOf(child)).Text);
                    Check(actual.SequenceEqual(screen.Rows.Select(row => row.Name)), "actual sibling order differs from canonical presenter order");
                }
                private sealed class Screen : IScoreboardScreen
                {
                    private readonly Func<int[]> read; private readonly Action<string,int,float> action;
                    private readonly Dictionary<int,ScoreboardRow> byId = new Dictionary<int,ScoreboardRow>();
                    private readonly Dictionary<ScoreboardRow,int> ids = new Dictionary<ScoreboardRow,int>();
                    private readonly List<ScoreboardRow> rows = new List<ScoreboardRow>();
                    public Screen(Func<int[]> read, Action<string,int,float> action) { this.read = read; this.action = action; }
                    public void Refresh()
                    {
                        rows.Clear(); foreach (var id in read())
                        {
                            if (!byId.TryGetValue(id, out var row))
                            {
                                row = new ScoreboardRow("Player " + id, false, false) { CanHear = true, CanModerate = true };
                                for (var c = 0; c < 8; c++) row.Values[c] = id + ":" + c;
                                byId.Add(id,row); ids.Add(row,id);
                            }
                            rows.Add(row);
                        }
                    }
                    public IReadOnlyList<ScoreboardRow> Rows => rows;
                    public IReadOnlyList<string> Columns { get; } = Enumerable.Range(0,8).Select(i => "Column " + i).ToArray();
                    public string Note => ""; public int SortColumn => -1; public bool Descending => false; public int LastColumn;
                    public void Sort(int column) { LastColumn = column; action("sort", column, 0); }
                    public void Mute(ScoreboardRow row) => action("mute", ids[row], 0);
                    public void Volume(ScoreboardRow row, float gain) => action("gain", ids[row], gain * 100);
                    public void ServerMute(ScoreboardRow row) { }
                    public void Kick(ScoreboardRow row) => action("kick", ids[row], 0);
                    public void Ban(ScoreboardRow row) { }
                }
            }
            """;
    }
}
