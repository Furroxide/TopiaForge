using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using TopiaForge.Mods;
using TopiaForge.Worlds.Maps;

namespace TopiaForge.ModManager.Tests
{
    internal static class MapDocumentTests
    {
        internal static byte[] Project(string entities = "\"entry\":{\"id\":\"entry\",\"name\":\"Entry\",\"components\":{\"spawn-location\":{}}}") =>
            Encoding.UTF8.GetBytes("{\"name\":\"Courtyard\",\"scenes\":{\"scene\":{\"id\":\"scene\",\"entities\":{" + entities + "}}}}");

        internal static void Run(string repo)
        {
            var fixture = new MapDocument(File.ReadAllBytes(Path.Combine(repo, "tests", "fixtures", "maps", "courtyard.json")));
            Assert(fixture.Select("courtyard").EntityCount == 296 && fixture.Select("courtyard").Spawn(null).Id == "entry", "synthetic map shape and spawn");
            var plain = Project(); var compressed = Gzip(plain);
            var document = new MapDocument(compressed);
            Assert(document.Json.SequenceEqual(plain) && document.Sha256 == MapDocument.Hash(compressed), "hash packed bytes, import plain JSON");
            plain[0] = 0;
            Assert(document.Json[0] == '{', "document retains its own bytes");
            Expect<FormatException>(() => new MapDocument(Array.Empty<byte>()));
            Expect<FormatException>(() => new MapDocument(new byte[MapDocument.MaximumPackedBytes + 1]));
            Expect<FormatException>(() => new MapDocument(Gzip(new byte[MapDocument.MaximumJsonBytes + 1])), contains: "32 MiB");
            foreach (var json in new[] { "{\"a\":1,\"a\":2}", "{\"a\":1,\"\\u0061\":2}", "{\"n\":NaN}",
                "{\"n\":01}", "{\"n\":+1}", "{\"n\":1.}", "{\"n\":1e999}", "{\"n\":true,}",
                "{\"a\": [1,]}", "{\"a\":\"\\ud800\"}", "{\"a\":\"\\u 041\"}", "{} {}", "\u00a0{}",
                "{\"a\":" + new string('[', 65) + "0" + new string(']', 65) + "}" })
                Expect<FormatException>(() => MapJson.Read(Encoding.UTF8.GetBytes(json)));
            Expect<FormatException>(() => MapJson.Read(new byte[] { 0xff }));
            MapJson.Read(Encoding.UTF8.GetBytes("{\"n\":-1.25e+2,\"s\":\"\\ud83d\\ude80\"}"));
            Expect<FormatException>(() => new MapDocument(Project("\"x\":{\"id\":\"y\",\"components\":{}}")));
            Expect<FormatException>(() => new MapDocument(Project("\"x\":{\"parent\":\"absent\",\"components\":{}}")));
            Expect<FormatException>(() => new MapDocument(Project("\"x\":{\"parent\":\"x\",\"components\":{}}")));
            var many = string.Join(",", Enumerable.Range(0, MapDocument.MaximumEntities + 1).Select(i => "\"e" + i + "\":{\"components\":{}}"));
            Expect<FormatException>(() => new MapDocument(Project(many)));
            Expect<MapProblem>(() => document.Select("missing"), ModErrorCode.NotFound);
            var noSpawn = new MapDocument(Project("\"x\":{\"components\":{}}"));
            Expect<MapProblem>(() => noSpawn.Select("scene").Spawn(null), ModErrorCode.InvalidState);
            var two = new MapDocument(Project("\"a\":{\"name\":\"Entry\",\"components\":{\"spawn-location\":{}}},\"b\":{\"name\":\"Exit\",\"components\":{\"spawn-location\":{}}}"));
            Expect<MapProblem>(() => two.Select("scene").Spawn(null), ModErrorCode.InvalidState);
            Assert(two.Select("scene").Spawn("Exit").Id == "b", "named editor spawn wins exactly");
            Console.WriteLine("MapDocumentTests passed (strict JSON, size and identity bounds, synthetic 296-entity fixture, spawn selection).");
        }
        internal static byte[] Gzip(byte[] data)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true)) gzip.Write(data);
            return output.ToArray();
        }
        internal static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException("Map assertion: " + message); }
        internal static void Expect<T>(Action action, ModErrorCode? code = null, string? contains = null) where T : Exception
        {
            try { action(); }
            catch (T error)
            {
                if (code.HasValue) Assert(error is MapProblem problem && problem.Code == code, "typed failure");
                if (contains != null) Assert(error.Message.Contains(contains), "specific validation bound");
                Assert(!error.Message.Contains("://") && !error.Message.Contains("\\") && !error.Message.Contains("/"), "failure omits addresses and paths");
                return;
            }
            throw new InvalidOperationException("Expected rejection: " + typeof(T).Name);
        }
    }
}
