using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Mods.Analyzers
{
    internal sealed class AuthoredMapAnalysis
    {
        internal sealed class World
        {
            internal string Id = "", Map = "", Scene = "";
            internal bool IsMap;
        }
        internal sealed class Mode
        {
            internal string Id = "", Type = "";
            internal readonly List<World> Worlds = new List<World>();
            internal bool UnknownWorld;
        }
        internal sealed class Survey
        {
            internal World World = null!;
            internal string Stamp = "";
            internal bool ExactWorld;
            internal readonly Dictionary<string, Record> Records = new Dictionary<string, Record>(StringComparer.Ordinal);
        }
        internal sealed class Record
        {
            internal string Id = "", Name = "", Model = "";
        }
        internal readonly List<Mode> Modes = new List<Mode>();
        internal readonly List<Survey> Surveys = new List<Survey>();
        private readonly Dictionary<string, List<Mode>> owners = new Dictionary<string, List<Mode>>(StringComparer.Ordinal);
        internal static AuthoredMapAnalysis Read(Compilation compilation, ImmutableArray<AdditionalText> files, CancellationToken token)
        {
            var result = new AuthoredMapAnalysis();
            var manifest = files.FirstOrDefault(f => Path.GetFileName(f.Path).Equals("topiaforge.mod.json", StringComparison.OrdinalIgnoreCase));
            var root = ReadJson(manifest, 1024 * 1024, token);
            if (root == null) return result;
            var contribution = Object(Value(root, "contributions"));
            var worlds = Array(contribution, "worlds").Select(raw =>
            {
                var world = Object(raw); var content = Object(Value(world, "content"));
                return new World
                {
                    Id = Text(world, "id"),
                    IsMap = Text(content, "kind") == "map",
                    Map = Text(content, "map") is string map && map.Length > 0 ? map : "file:" + Text(content, "file"),
                    Scene = Text(content, "scene")
                };
            }).Where(w => w.Id.Length > 0).ToArray();
            foreach (var raw in Array(contribution, "gamemodes"))
            {
                var mode = Object(raw);
                result.Modes.Add(new Mode { Id = Text(mode, "id"), Type = Text(Object(Value(mode, "implementation")), "type") });
            }
            foreach (var raw in Array(contribution, "launchTargets"))
            {
                var target = Object(raw); var selection = Object(Value(target, "world"));
                foreach (var mode in result.Modes.Where(m => m.Id == Text(target, "gamemode")))
                {
                    var ids = new[] { Text(selection, "default") }.Concat(Array(selection, "allowed").OfType<string>()).Where(s => s.Length > 0).ToArray();
                    mode.Worlds.AddRange(worlds.Where(w => ids.Contains(w.Id, StringComparer.Ordinal)));
                    // External declarations and open pickers cannot be proven to exclude map worlds here.
                    mode.UnknownWorld |= Text(selection, "policy") == "open" || ids.Any(id => !worlds.Any(w => w.Id == id));
                }
            }
            foreach (var file in files.Where(f => Path.GetFileName(f.Path).StartsWith("topiaforge-map-survey", StringComparison.OrdinalIgnoreCase)
                && f.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            {
                var data = ReadJson(file, 64 * 1024 * 1024, token);
                if (data == null || !(Value(data, "schemaVersion") is double version) || version != 1) continue;
                var candidates = worlds.Where(w => w.IsMap && w.Map == Text(data, "map") &&
                    (w.Scene.Length == 0 || w.Scene == Text(data, "scene")) && (Text(data, "world").Length == 0 || Text(data, "world") == w.Id));
                foreach (var world in candidates)
                {
                    var survey = new Survey { World = world, Stamp = Text(data, "surveyedUtc"), ExactWorld = Text(data, "world") == world.Id };
                    var records = Array(data, "records");
                    if (records.Count > 50000) continue;
                    var valid = true;
                    foreach (var raw in records)
                    {
                        var record = Object(raw); var id = Text(record, "id");
                        if (!TopiaForgeModAnalyzer.MapId(id, 64) || survey.Records.ContainsKey(id)) { valid = false; break; }
                        survey.Records.Add(id, new Record { Id = id, Name = Text(record, "name"), Model = Text(record, "modelId") });
                    }
                    if (valid && Value(data, "records") is List<object?>) result.Surveys.Add(survey);
                }
            }
            var newest = result.Surveys.GroupBy(s => s.World.Id, StringComparer.Ordinal).Select(g =>
                g.OrderByDescending(s => s.ExactWorld).ThenByDescending(s => s.Stamp, StringComparer.Ordinal).First()).ToArray();
            result.Surveys.Clear(); result.Surveys.AddRange(newest);
            result.IndexOwners(compilation, token);
            return result;
        }
        private void IndexOwners(Compilation compilation, CancellationToken token)
        {
            foreach (var mode in Modes)
            {
                var factory = compilation.GetTypeByMetadataName(mode.Type);
                if (factory == null) continue;
                var pending = new Queue<INamedTypeSymbol>(); pending.Enqueue(factory);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                while (pending.Count > 0 && seen.Count < 128)
                {
                    token.ThrowIfCancellationRequested();
                    var type = pending.Dequeue(); var name = type.ToDisplayString();
                    if (!seen.Add(name)) continue;
                    if (!owners.TryGetValue(name, out var modes)) owners[name] = modes = new List<Mode>();
                    modes.Add(mode);
                    foreach (var reference in type.DeclaringSyntaxReferences)
                    {
                        var declaration = reference.GetSyntax(token); var semantic = compilation.GetSemanticModel(declaration.SyntaxTree);
                        foreach (var node in declaration.DescendantNodes().Where(n => n is ObjectCreationExpressionSyntax || n is InvocationExpressionSyntax))
                        {
                            var called = semantic.GetSymbolInfo(node, token).Symbol?.ContainingType;
                            if (called != null && called.DeclaringSyntaxReferences.Length > 0 && SymbolEqualityComparer.Default.Equals(called.ContainingAssembly, compilation.Assembly))
                                pending.Enqueue(called);
                        }
                    }
                    foreach (var nested in type.GetTypeMembers()) pending.Enqueue(nested);
                }
            }
        }
        internal IReadOnlyList<Mode> Owners(ISymbol? symbol)
        {
            for (var type = symbol as INamedTypeSymbol ?? symbol?.ContainingType; type != null; type = type.ContainingType)
                if (owners.TryGetValue(type.ToDisplayString(), out var modes)) return modes;
            return System.Array.Empty<Mode>();
        }
        internal IReadOnlyList<Survey> ForModes(IReadOnlyList<Mode> modes) => Surveys.Where(s => modes.Any(m => m.Worlds.Any(w => w.Id == s.World.Id))).ToArray();
        internal static Dictionary<string, object?>? ReadJson(AdditionalText? file, int limit, CancellationToken token)
        {
            var text = file?.GetText(token);
            if (text == null || text.Length > limit) return null;
            try { return MapJson.Read(Encoding.UTF8.GetBytes(text.ToString())); }
            catch (FormatException) { return null; }
        }
        internal static object? Value(Dictionary<string, object?>? data, string key) => data != null && data.TryGetValue(key, out var value) ? value : null;
        internal static Dictionary<string, object?>? Object(object? value) => value as Dictionary<string, object?>;
        internal static List<object?> Array(Dictionary<string, object?>? data, string key) => Value(data, key) as List<object?> ?? new List<object?>();
        internal static string Text(Dictionary<string, object?>? data, string key) => Value(data, key) as string ?? "";
        internal static string[] Suggestions(IEnumerable<Survey> surveys, string id, string name = "", string model = "") => surveys.SelectMany(s => s.Records.Values)
            .GroupBy(r => r.Id, StringComparer.Ordinal).Select(g => g.First())
            .OrderByDescending(r => name.Length > 0 && r.Name == name && model.Length > 0 && r.Model == model)
            .ThenByDescending(r => name.Length > 0 && r.Name == name).ThenByDescending(r => model.Length > 0 && r.Model == model)
            .ThenBy(r => Distance(id, r.Id)).ThenBy(r => r.Id, StringComparer.Ordinal).Take(3).Select(r => r.Id).ToArray();
        private static int Distance(string left, string right)
        {
            var row = Enumerable.Range(0, right.Length + 1).ToArray();
            for (var i = 1; i <= left.Length; i++)
            {
                var diagonal = row[0]; row[0] = i;
                for (var j = 1; j <= right.Length; j++)
                { var prior = row[j]; row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (left[i - 1] == right[j - 1] ? 0 : 1)); diagonal = prior; }
            }
            return row[right.Length];
        }
    }
}
