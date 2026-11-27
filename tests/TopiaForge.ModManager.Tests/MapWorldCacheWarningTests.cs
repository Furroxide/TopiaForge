using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using TopiaForge.Worlds.Maps;
using static TopiaForge.ModManager.Tests.MapDocumentTests;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class MapWorldServiceTests
    {
        private static async Task CacheWarningOnce(string root)
        {
            var blocked = Path.Combine(root, "warning-cache-file");
            File.WriteAllText(blocked, "A file cannot be the cache directory.");
            using var service = new MapWorldService(new MapCache(blocked), new PublishedBytes(), () => new Backend());
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var fixture = Fixture();
                var pending = service.LoadAsync(fixture, new InternalMapWorldRequest("example", "scene", null), default);
                fixture.Native.Succeed();
                var loaded = await pending;
                Assert(loaded.Succeeded, "repeated cache degradation never discards validated playable maps");
                var warnings = fixture.Fake.Logger.Entries.Count(entry => entry.Level == CapturedLogLevel.Warning);
                Assert(warnings == (attempt == 0 ? 1 : 0), "cache degradation reports once per service instead of flooding warnings");
                loaded.Value!.Dispose();
            }
        }
    }
}
