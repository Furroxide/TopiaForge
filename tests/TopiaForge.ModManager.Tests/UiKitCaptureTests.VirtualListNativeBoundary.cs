using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitCaptureTests
    {
        private static void NativeVirtualListUsesPooledTextMetrics()
        {
            const string widgets = "src/TopiaForge.Mods.UnityUi/Widgets";
            var files = new[]
            {
                "TopiaForgeListViewRuntime.cs", "TopiaForgeListView.Declarative.cs", "TopiaForgeListRow.Layout.cs",
            };
            // Run the shipped row projection and fit cache with deterministic metrics at the Unity boundary.
            var root = Program.FindRepoRoot();
            var sources = files.Select(file =>
                CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, widgets, file)), path: file))
                .Append(CSharpSyntaxTree.ParseText(NativeListProbe, path: "NativeVirtualListProbe.cs"));
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(path => !Path.GetFileName(path).StartsWith("UnityEngine", StringComparison.Ordinal) &&
                    Path.GetFileName(path) is not "TopiaForge.Mods.UnityUi.dll" and not "Unity.TextMeshPro.dll")
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("TopiaForge.Mods.UnityUi", sources, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var emit = compilation.Emit(bytes);
            if (!emit.Success)
                throw new InvalidOperationException(string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            var probe = Assembly.Load(bytes.ToArray()).GetType("NativeVirtualListProbe", throwOnError: true)!;
            ((Action)probe.GetMethod("Run", BindingFlags.Static | BindingFlags.Public)!.CreateDelegate(typeof(Action)))();
        }

        private const string NativeListProbe = """
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using TMPro;
            using TopiaForge.Mods;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine;
            using UnityEngine.UI;

            public static class NativeVirtualListProbe
            {
                public static void Run()
                {
                    var watcher = new TopiaForgeListViewRuntime();
                    var view = new TopiaForgeListView<UiListItem>();
                    var row = view.Row;
                    view.Connect(watcher);
                    view.SetItems(Enumerable.Range(0, 11).Select(i => new UiListItem("row-" + i, "Base"))
                        .Append(new UiListItem("too-long", new string('W', 200), badge: "Offline")).ToArray());
                    try
                    {
                        watcher.RequireRowsFit(320);
                        throw new InvalidOperationException("An unfit hidden row passed native list admission.");
                    }
                    catch (ArgumentException error) when (error.Message.Contains("index 11", StringComparison.Ordinal))
                    {
                        Require(row.Title.Label.Measures == 12 && row.Badge.RowLabel.Measures == 1,
                            "Actual row measurement must visit hidden title and badge using the pooled label types.");
                    }
                    Require(row.Title.Rect.sizeDelta.x == 0, "A rejected measurement does not place the retained row.");

                    view.SetItems(new[] { new UiListItem("accepted", "North", "Sector 7", "Offline") });
                    Require(watcher.RequireRowsFit(320) > 140, "Native admission must account for all three labels.");
                    watcher.PlaceRows(320);
                    var badge = row.Badge.Rect;
                    var title = row.Title.Rect;
                    var subtitle = row.Subtitle.Rect;
                    Require(title.anchoredPosition.x > 0 && title.anchoredPosition.x + title.sizeDelta.x < subtitle.anchoredPosition.x &&
                        subtitle.anchoredPosition.x + subtitle.sizeDelta.x < badge.anchoredPosition.x &&
                        badge.anchoredPosition.x + badge.sizeDelta.x < 320 - 2 * UiVirtualListGeometry.Gutter,
                        "The actual native placement must fit the title, trailing subtitle, and padded badge in separate slots.");

                    for (var i = 0; i < 100; i++) { watcher.RequireRowsFit(320); watcher.PlaceRows(320); }
                    var measurements = row.Title.Label.Measures;
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    for (var i = 0; i < 4096; i++) { watcher.RequireRowsFit(320); watcher.PlaceRows(320); }
                    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                    Require(row.Title.Label.Measures == measurements && allocated == 0,
                        "A settled row width/theme/scale must allocate zero bytes and make no further text measurement calls.");
                    view.Host.EffectiveUiScale = 2;
                    watcher.RequireRowsFit(320);
                    Require(row.Title.Label.Measures == ++measurements, "A changed UI scale invalidates native text measurements.");
                    view.NewTheme();
                    watcher.RequireRowsFit(320);
                    Require(row.Title.Label.Measures == ++measurements, "A changed resolved theme invalidates native text measurements.");
                    Console.WriteLine("Native virtual list adapter passed: hidden row index 11, separate title/subtitle/badge bounds, 4,096 settled calls / 0 bytes, theme and scale invalidation.");
                }

                private static void Require(bool condition, string message)
                {
                    if (!condition) throw new InvalidOperationException(message);
                }
            }

            namespace UnityEngine
            {
                public class MonoBehaviour { }
                public sealed class GameObject
                {
                    internal object? Component { get; init; }
                    internal bool Active { get; private set; } = true;
                    public T GetComponent<T>() where T : class => (T)Component!;
                    public void SetActive(bool active) => Active = active;
                }
                public sealed class RectTransform
                {
                    public Vector2 anchorMin, anchorMax, pivot, anchoredPosition, sizeDelta;
                    public Rect rect;
                }
                public struct Rect { public float height; }
                public readonly record struct Vector2(float x, float y);
            }

            namespace UnityEngine.UI
            {
                public sealed class HorizontalLayoutGroup { public bool enabled; }
            }

            namespace TMPro
            {
                public sealed class TMP_Text
                {
                    internal int Measures { get; private set; }
                    internal RectTransform rectTransform { get; } = new RectTransform();
                    internal Vector2 GetPreferredValues(string text)
                    {
                        Measures++;
                        return new Vector2(text.Length * 9f, 20f);
                    }
                }
            }

            namespace TopiaForge.Mods.UnityUi
            {
                public sealed class ProbeHost { public float EffectiveUiScale = 1; }
                public sealed class TopiaForgeResolvedTheme { internal UiThemeDescriptor? Descriptor { get; init; } }
                public sealed class ProbeLabel
                {
                    internal ProbeLabel() => Go = new GameObject { Component = Label };
                    internal TMP_Text Label { get; } = new TMP_Text();
                    internal GameObject Go { get; }
                    internal RectTransform Rect => Label.rectTransform;
                    internal ProbeLabel PlainText() => this;
                    internal ProbeLabel NoWrap() => this;
                    internal void ApplyTheme(TopiaForgeResolvedTheme theme) { }
                }
                public sealed class ProbeBadge
                {
                    internal TMP_Text RowLabel { get; } = new TMP_Text();
                    internal GameObject Go { get; } = new GameObject();
                    internal RectTransform Rect { get; } = new RectTransform();
                    internal void UseDeclarativeRow() { }
                    internal void ApplyTheme(TopiaForgeResolvedTheme theme) { }
                }
                public sealed partial class TopiaForgeListRow
                {
                    internal TopiaForgeListRow() => ConfigureDeclarativeText();
                    internal GameObject Go { get; } = new GameObject { Component = new HorizontalLayoutGroup() };
                    internal TopiaForgeResolvedTheme Theme { get; set; } = new TopiaForgeResolvedTheme();
                    internal ProbeLabel Title { get; } = new ProbeLabel();
                    internal ProbeLabel Subtitle { get; } = new ProbeLabel();
                    internal ProbeBadge Badge { get; } = new ProbeBadge();
                }
                public sealed partial class TopiaForgeListView<T>
                {
                    private readonly List<TopiaForgeListRow> pool = new() { new TopiaForgeListRow() };
                    private readonly List<int> poolBinding = new() { 0 };
                    private IReadOnlyList<T> items = Array.Empty<T>();
                    internal ProbeHost Host { get; } = new ProbeHost();
                    internal TopiaForgeListRow Row => pool[0];
                    private TopiaForgeResolvedTheme Theme => Row.Theme;
                    internal void NewTheme() => Row.Theme = new TopiaForgeResolvedTheme();
                    internal void SetItems(IReadOnlyList<T> source) => items = source;
                    internal void Connect(TopiaForgeListViewRuntime runtime) => runtime.UseDeclarativeLayout(RequireRowWidth, PlaceRows);
                }
            }
            """;
    }
}
