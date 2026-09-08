using System;
using System.Collections.Generic;
using System.IO;
using TopiaForge.WorldCompanion.Editor;

namespace TopiaForge.ModManager.Tests
{
    internal static class WorldMarkerHierarchyTests
    {
        private sealed class Node
        {
            internal Node(string name, bool active = true) { Name = name; Active = active; }
            internal readonly string Name;
            internal readonly bool Active;
            internal readonly List<Node> Children = new List<Node>();
        }
        public static void Run(string root)
        {
            var failures = new List<string>(); var count = 0;
            void Case(string label, Action run)
            {
                try { run(); count++; }
                catch (Exception error) { failures.Add(label + ": " + error.Message); }
            }
            Case("zero", () => Assert(Find(new Node("World")).Status == WorldMarkerStatus.Missing, "zero marker must fail"));
            Case("one", () => {
                var world = new Node("World"); var marker = new Node("SpawnPoint"); world.Children.Add(marker);
                var result = Find(world); Assert(result.Status == WorldMarkerStatus.Unique && ReferenceEquals(result.Marker, marker), "one marker identity");
            });
            Case("two", () => {
                var world = new Node("World"); world.Children.Add(new Node("SpawnPoint")); world.Children.Add(new Node("SpawnPoint"));
                var result = Find(world); Assert(result.Status == WorldMarkerStatus.Ambiguous && result.Marker == null, "duplicates cannot select first");
            });
            Case("root", () => {
                var world = new Node("SpawnPoint"); Assert(ReferenceEquals(Find(world).Marker, world), "root participates like runtime");
            });
            Case("inactive", () => {
                var world = new Node("World"); var marker = new Node("SpawnPoint", false); world.Children.Add(marker);
                Assert(!marker.Active && ReferenceEquals(Find(world).Marker, marker), "inactive marker must participate");
            });
            Case("case", () => {
                var world = new Node("World"); world.Children.Add(new Node("spawnpoint"));
                Assert(Find(world).Status == WorldMarkerStatus.Missing, "runtime names use ordinal case");
            });
            foreach (var size in new[] { 16384, 16385 }) Case("bound-" + size, () => {
                var world = new Node("World"); var tail = world;
                for (var i = 1; i < size; i++) { var next = new Node(i == size-1 ? "SpawnPoint" : "Node"); tail.Children.Add(next); tail = next; }
                var result = Find(world);
                Assert(result.Status == (size == 16384 ? WorldMarkerStatus.Unique : WorldMarkerStatus.LimitExceeded), "16384-node traversal boundary");
            });
            Case("production-export-order", () => {
                var editor = Path.Combine(root, "templates", "TopiaForge.UnityWorldTemplate", "Packages",
                    "io.github.furroxide.topiaforge.world-companion", "Editor");
                var source = File.ReadAllText(Path.Combine(editor, "WorldBundleBuilder.cs"));
                var rejection = source.IndexOf("if (issues.Errors.Count > 0)", StringComparison.Ordinal);
                Assert(rejection >= 0 && rejection < source.IndexOf("EnsureHdrpConfiguration();", StringComparison.Ordinal)
                    && rejection < source.IndexOf("importer.assetBundleName =", StringComparison.Ordinal)
                    && rejection < source.IndexOf("Directory.CreateDirectory(OutputDir)", StringComparison.Ordinal),
                    "prefab refusal must precede HDRP, importer and bundle writes");
                var validator = File.ReadAllText(Path.Combine(editor, "WorldValidator.cs"));
                Assert(validator.Contains("WorldMarkerHierarchy.Find(prefab.transform, SpawnPointName", StringComparison.Ordinal)
                    && !validator.Contains("FindDescendant", StringComparison.Ordinal)
                    && validator.Contains("CheckBounds(prefab, spawnPoint, result)", StringComparison.Ordinal),
                    "companion must use one production search and reuse its unique result");
            });
            if (failures.Count != 0) throw new InvalidOperationException("World marker cases passed " + count + "; failed " + failures.Count + ":\n" + string.Join("\n", failures));
            Console.WriteLine("WorldMarkerHierarchyTests passed (" + count + " cases).");
        }
        private static WorldMarkerResult<Node> Find(Node root) => WorldMarkerHierarchy.Find(root, "SpawnPoint",
            value => value.Name, value => value.Children.Count, (value, index) => value.Children[index]);
        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
