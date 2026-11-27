using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace TopiaForge.Mods.Analyzers
{
    public sealed partial class TopiaForgeModAnalyzer
    {
        private static readonly DiagnosticDescriptor InvalidMapWorld = new DiagnosticDescriptor(
            "TF1011", "Map world declaration is invalid", "{0}", Category, DiagnosticSeverity.Error,
            isEnabledByDefault: true, helpLinkUri: DocsRoot + "TF1011", customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

        private static void AnalyzeMapWorlds(CompilationAnalysisContext context)
        {
            if (HasBooleanBuildProperty(context.Options.AnalyzerConfigOptionsProvider, "IsTestProject", expected: true)) return;
            var file = context.Options.AdditionalFiles.FirstOrDefault(item =>
                string.Equals(Path.GetFileName(item.Path), "topiaforge.mod.json", StringComparison.OrdinalIgnoreCase));
            var text = file?.GetText(context.CancellationToken);
            if (file == null || text == null || text.Length > 1024 * 1024) return;
            ManifestSnapshot manifest;
            try { manifest = ManifestSnapshot.Parse(text.ToString()); }
            catch (FormatException) { return; }
            {
                void Report(string message, TextSpan span, string? fix = null, string? value = null)
                {
                    var properties = ImmutableDictionary<string, string?>.Empty.Add("mapFix", fix).Add("mapValue", value);
                    context.ReportDiagnostic(Diagnostic.Create(InvalidMapWorld,
                        Location.Create(file.Path, span, text.Lines.GetLinePositionSpan(span)), properties, message));
                }
                foreach (var world in manifest.MapWorlds)
                {
                    var map = world.Value("map"); var packed = world.Value("file"); var scene = world.Value("scene");
                    if (!manifest.RequiredWorlds) Report("A map world needs a required TopiaForge Worlds dependency.", world.Span, "dependency");
                    if ((map == null) == (packed == null)) Report("A map world needs exactly one published map id or packaged file.", world.Span);
                    if (map != null && !MapId(map, 64))
                    {
                        var id = MapPageId(map);
                        Report(id == null ? "Use a robotopia.gg map page link or id; other sites are not map sources."
                            : "Store the published map id in content.map; the page link belongs in authoring commands.",
                            world.Spans["map"], id == null ? null : "id", id);
                    }
                    if (packed != null && (!PortableMap(packed) || !PackedMapFile(context.Options, packed)))
                        Report("The map file must exist as a project item copied to its declared package path, with a .roboworld, .json or .json.gz extension.", world.Spans["file"]);
                    if (world.Fields.ContainsKey("scene") && !MapId(scene, 128))
                        Report("The map scene must be a bounded ASCII scene id.", world.Spans["scene"]);
                    if (world.Transitions.Any(value => value != "scene-replacement"))
                        Report("Map worlds support scene-replacement only; remove additive-arena.", world.Span);
                }
            }
        }
        internal static bool MapId(string? value, int maximum) => value != null && value.Length > 0 && value.Length <= maximum &&
            value.All(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_');
        internal static string? MapPageId(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "robotopia.gg"
                || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath.Contains("%")) return null;
            var path = uri.AbsolutePath;
            if (path.EndsWith("/", StringComparison.Ordinal)) path = path.Substring(0, path.Length - 1);
            var parts = path.Split('/');
            return parts.Length == 3 && parts[0] == "" && parts[1] == "map" && MapId(parts[2], 64) ? parts[2] : null;
        }
        private static bool PortableMap(string value) => value.Length <= 240 && value.Split('/').All(part => part.Length > 0 && part != "." && part != "..")
            && !value.Any(c => c < 32 || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|')
            && (value.EndsWith(".roboworld", StringComparison.OrdinalIgnoreCase) || value.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || value.EndsWith(".json.gz", StringComparison.OrdinalIgnoreCase));
        private static bool PackedMapFile(AnalyzerOptions options, string path) => options.AdditionalFiles.Any(file =>
            options.AnalyzerConfigOptionsProvider.GetOptions(file).TryGetValue("build_metadata.AdditionalFiles.TopiaForgeMapPackedPath", out var packed)
            && string.Equals(packed.Replace('\\', '/'), path, StringComparison.Ordinal));
    }
}
