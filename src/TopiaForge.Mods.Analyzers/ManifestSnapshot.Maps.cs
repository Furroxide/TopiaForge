using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Text;

namespace TopiaForge.Mods.Analyzers
{
    internal sealed partial class ManifestSnapshot
    {
        internal const string WorldsDependency = "io.github.furroxide.topiaforge.worlds";
        internal ImmutableArray<ManifestMapWorld> MapWorlds { get; private set; } = ImmutableArray<ManifestMapWorld>.Empty;
        internal bool RequiredWorlds { get; private set; }
        internal int DependencyInsertion { get; private set; } = -1;
        internal int DependencyCount { get; private set; }
        internal int RootInsertion { get; private set; } = -1;

        private sealed partial class ManifestParser
        {
            private readonly List<ManifestMapWorld> mapWorlds = new List<ManifestMapWorld>();
            private bool requiredWorlds;
            private int dependencyInsertion = -1, dependencyCount;
            private void ReadContributions() => ObjectFields((key, ignoredStart) =>
            {
                if (key != "worlds" || Peek() != '[') { SkipValue(2); return; }
                Expect('[');
                if (TryConsume(']')) return;
                do
                {
                    if (mapWorlds.Count >= MaximumTrackedEntries) throw InvalidJson();
                    var world = new ManifestMapWorld();
                    ObjectFields((field, start) =>
                    {
                        if (field == "content") ObjectFields((content, ignoredContentStart) =>
                        {
                            var value = ReadMapText(out var span);
                            world.Fields[content] = value;
                            world.Spans[content] = span;
                        }, 4);
                        else if (field == "transitions" && Peek() == '[')
                        {
                            Expect('[');
                            if (TryConsume(']')) return;
                            do { world.Transitions.Add(ReadMapText(out _)); } while (TryConsume(','));
                            Expect(']');
                        }
                        else if (field == "id") { world.Id = ReadMapText(out _); world.Span = new TextSpan(start, position - start); }
                        else SkipValue(4);
                    }, 3);
                    if (world.Value("kind") == "map") mapWorlds.Add(world);
                } while (TryConsume(','));
                Expect(']');
            }, 1);

            private void ObjectFields(Action<string, int> read, int depth)
            {
                if (Peek() != '{') { SkipValue(depth); return; }
                Expect('{');
                var seen = new HashSet<string>(StringComparer.Ordinal);
                if (TryConsume('}')) return;
                do
                {
                    var key = ReadString();
                    if (!seen.Add(key)) hasDuplicateKeys = true;
                    Expect(':'); SkipWhitespace();
                    read(key, position);
                } while (TryConsume(','));
                Expect('}');
            }
            private string? ReadMapText(out TextSpan span)
            {
                SkipWhitespace(); var start = position;
                string? value = null;
                if (Peek() == '"') value = ReadString(); else SkipValue(5);
                span = TextSpan.FromBounds(start, position);
                return value;
            }
        }
    }
    internal sealed class ManifestMapWorld
    {
        internal string? Id;
        internal TextSpan Span;
        internal readonly Dictionary<string, string?> Fields = new Dictionary<string, string?>(StringComparer.Ordinal);
        internal readonly Dictionary<string, TextSpan> Spans = new Dictionary<string, TextSpan>(StringComparer.Ordinal);
        internal readonly List<string?> Transitions = new List<string?>();
        internal string? Value(string name) => Fields.TryGetValue(name, out var value) ? value : null;
    }
}
