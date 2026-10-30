using System;
using TopiaForge.Mods;
using TopiaForge.Worlds.Maps;
using static TopiaForge.ModManager.Tests.MapDocumentTests;

namespace TopiaForge.ModManager.Tests
{
    internal static class NativePublishedMapBindingsTests
    {
        private static int initializations;
        internal static void Run()
        {
            var before = initializations;
            var current = new NativePublishedMapBindings(typeof(Current));
            Assert(initializations == before, "literal base inspection does not run a native type initializer");
            Assert(current.UsesManifest && current.PublicBase.Scheme == "https", "current exact shape selects manifest protocol");
            var manifest = current.Resolve("example");
            Assert(initializations == (before == 0 ? 1 : before) && manifest.AbsolutePath.EndsWith("/manifest.json", StringComparison.Ordinal), "resolver supplies manifest, not a guessed project URL");
            Assert(current.ReadProject(manifest, "opaque native manifest").AbsolutePath.EndsWith("/project.roboworld", StringComparison.Ordinal), "native reader receives manifest URL then JSON");
            Current.Foreign = true;
            try { current.ReadProject(manifest, "opaque native manifest"); throw new Exception("cross-origin project accepted"); }
            catch (MapProblem error) { Assert(error.Code == ModErrorCode.Unavailable && !error.Message.Contains("://", StringComparison.Ordinal), "foreign native project fails privately"); }
            finally { Current.Foreign = false; }
            var legacy = new NativePublishedMapBindings(typeof(Legacy));
            Assert(!legacy.UsesManifest && legacy.Resolve("example").AbsolutePath.EndsWith("/project.roboworld", StringComparison.Ordinal), "2545 direct-project path remains");
            try { new NativePublishedMapBindings(typeof(Unsupported)); throw new Exception("readonly base treated as literal"); }
            catch (MapProblem error) { Assert(error.Code == ModErrorCode.Unavailable, "unsupported base refuses without cctor"); }
            Console.WriteLine("Native published-map bindings passed: legacy/current shapes, literal safety, reader ordering and origin refusal (native behavior unproved).");
        }
        private static class Legacy
        {
            public const string DefaultPublicBaseUrl = "https://maps.example.invalid/";
            public static bool TryResolveProjectUrl(string id, out string project, out string baseUrl, out string ignored, out string error)
            { project = DefaultPublicBaseUrl + "map/" + id + "/project.roboworld"; baseUrl = DefaultPublicBaseUrl; ignored = error = ""; return true; }
        }
        private static class Current
        {
            private const string ProductionMapsBaseUrl = "https://maps.example.invalid/";
            private const string ManifestFileName = "manifest.json";
            public static bool Foreign;
            static Current() { initializations++; }
            public static bool TryResolveManifestUrl(string input, out string manifest, out string id, out string robotopia, out string error)
            { id = input; manifest = ProductionMapsBaseUrl + "map/" + input + "/" + ManifestFileName; robotopia = error = ""; return true; }
            public static string ReadProjectUrl(string manifestUrl, string manifestJson)
            {
                Assert(manifestUrl.EndsWith("/manifest.json", StringComparison.Ordinal) && manifestJson == "opaque native manifest", "native reader arguments are ordered");
                return Foreign ? "https://foreign.example.invalid/project.roboworld" : ProductionMapsBaseUrl + "map/example/project.roboworld";
            }
        }
        private static class Unsupported
        {
            private static readonly string ProductionMapsBaseUrl = "https://maps.example.invalid/";
            private const string ManifestFileName = "manifest.json";
            static Unsupported() { throw new InvalidOperationException("must not initialize an unsupported base"); }
            public static bool TryResolveManifestUrl(string input, out string a, out string b, out string c, out string d)
            { a = b = c = d = ""; return false; }
            public static string ReadProjectUrl(string a, string b) => ProductionMapsBaseUrl + ManifestFileName + a;
        }
    }
}
