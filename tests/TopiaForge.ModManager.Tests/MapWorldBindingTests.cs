using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Worlds.Maps;
using static TopiaForge.ModManager.Tests.MapDocumentTests;

namespace TopiaForge.ModManager.Tests
{
    internal static class MapWorldBindingTests
    {
        internal static async Task RunAsync()
        {
            var owned = Directory.CreateTempSubdirectory("map-binding-");
            var root = owned.FullName;
            try
            {
                var bytes = Project(); var file = Path.Combine(root, "courtyard.roboworld"); File.WriteAllBytes(file, bytes);
                var world = new ModWorldDeclaration
                {
                    Id = "example.worlds.world",
                    Name = "Courtyard",
                    Content = new ModWorldContent { Kind = ModWorldContent.MapKind, File = "courtyard.roboworld" }
                };
                var manifest = new ModManifest { Id = "example.worlds", Version = "1.0.0", Contributions = new ModContributions() };
                manifest.Contributions.Worlds.Add(world);
                manifest.Hashes.Add("courtyard.roboworld", MapDocument.Hash(bytes));
                var package = new ModPackage(root, manifest, null, Array.Empty<string>());
                // A built-in map must never ask the assembly loader to instantiate author code.
                var batch = new RuntimeDeclarationBinder(null!).Bind(package);
                Assert(batch.Worlds.Count == 1 && batch.Failures.Count == 0, "production binder recognizes map content without author loading code");
                using var fixture = new BuiltinWorldFixture();
                fixture.SpawnPolicy = new WorldSpawnPolicy(WorldSpawnKind.ProviderDefault);
                var service = new Service(); fixture.Fake.Extensions.Register<IInternalMapWorldService>(service);
                await batch.Worlds[0].Create().LoadAsync(fixture, default);
                Assert(service.Read!.Succeeded && service.Read.Value!.AsSpan().SequenceEqual(bytes), "bound packaged map verifies and passes the same bytes");
                File.WriteAllText(file, "changed");
                await batch.Worlds[0].Create().LoadAsync(fixture, default);
                Assert(service.Read!.ErrorCode == ModErrorCode.Conflict, "file replacement after binding is refused before native parsing");
                File.Delete(file);
                await batch.Worlds[0].Create().LoadAsync(fixture, default);
                Assert(service.Read!.ErrorCode == ModErrorCode.NotFound, "missing file gives repair guidance");
                manifest.Hashes.Clear();
                var incomplete = new RuntimeDeclarationBinder(null!).Bind(package);
                Assert(incomplete.Worlds.Count == 0 && incomplete.Failures.Count == 1, "unhashed packaged maps never bind");
                world.Content = new ModWorldContent { Kind = ModWorldContent.MapKind, Map = "courtyard" };
                var published = new RuntimeDeclarationBinder(null!).Bind(package);
                await published.Worlds[0].Create().LoadAsync(fixture, default);
                Assert(service.Published == "courtyard", "published declaration uses the same internal provider seam");
                fixture.Transition = WorldLoadTransition.AdditiveArena;
                var calls = service.Calls;
                Assert((await published.Worlds[0].Create().LoadAsync(fixture, default)).ErrorCode == ModErrorCode.InvalidArgument && service.Calls == calls,
                    "wrong transition refuses before any source access");
                Console.WriteLine("Map production binding tests passed (7 cases).");
            }
            finally { owned.Delete(true); }
        }
        private sealed class Service : IInternalMapWorldService
        {
            internal OperationResult<byte[]>? Read; internal string? Published; internal int Calls;
            public async Task<OperationResult<IWorldInstance>> LoadAsync(IWorldLoadContext context, InternalMapWorldRequest request, CancellationToken token)
            {
                Calls++; Published = request.PublishedId;
                if (request.ReadFile != null) Read = await request.ReadFile(token);
                return OperationResult<IWorldInstance>.Failure(ModErrorCode.Unavailable, "Synthetic native boundary reached.");
            }
        }
    }
}
