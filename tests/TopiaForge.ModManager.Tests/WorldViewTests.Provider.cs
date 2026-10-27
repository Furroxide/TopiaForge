using System;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using TopiaForge.Worlds;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class WorldViewTests
    {
        private static void Provider()
        {
            using var providerContext = new FakeModContext();
            var initialResources = providerContext.Lifetime.TrackedResourceCount;
            using var worldScope = new FakeModLifetime();
            using var first = new ViewFixture(); using var second = new ViewFixture();
            var readiness = new WorldReadiness(new WorldSceneIdentity(42, "Workshop"), TransformState.Identity);
            var services = MultiplayerReplication.Offline();
            foreach (var caller in new[] { first, second })
            {
                caller.ViewReadiness = readiness; caller.ViewLifetime = worldScope;
                caller.Replication = new RuntimeReplication(services, "example.mode", new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.GamemodeServer), caller.Lifetime);
            }
            var built = 0; Renderer? renderer = null;
            var provider = new WorldViewProvider(providerContext, (world, context) =>
            {
                Check(ReferenceEquals(context, providerContext), "native renderer uses provider events, not the first caller's short lifetime");
                built++; renderer = new Renderer(); return renderer;
            });
            var a = (IWorldView)provider.CreateOwnerFacade(typeof(IWorldView), first);
            var b = (IWorldView)provider.CreateOwnerFacade(typeof(IWorldView), second);
            var narrow = Lease(a.Fog(VisionFog.Blackout)); var wide = Lease(b.Fog(VisionFog.Radius(24)));
            Check(built == 1 && renderer!.Fog!.Value.VisibleDistance == 6, "consumer facades share the selected primary world's renderer");
            first.Dispose(); Check(!narrow.IsActive && wide.IsActive && renderer!.Fog!.Value.VisibleDistance == 24, "each facade retains the exact consuming lifetime");
            worldScope.Dispose(); Check(!wide.IsActive && renderer!.Fog == null && !b.IsAvailable, "world teardown clears every caller");
            Check(providerContext.Lifetime.TrackedResourceCount == initialResources, "world end releases provider tracking");
            using var nextWorld = new FakeModLifetime();
            second.ViewReadiness = new WorldReadiness(new WorldSceneIdentity(43, "Courtyard"), TransformState.Identity); second.ViewLifetime = nextWorld;
            var fresh = Lease(b.Fog(VisionFog.Radius(12)));
            Check(built == 2 && renderer!.Fog!.Value.VisibleDistance == 12, "new world starts without prior view state");
            providerContext.Dispose(); Check(!fresh.IsActive && b.Fog(VisionFog.Blackout).ErrorCode == ModErrorCode.Cancelled, "provider unload ends active callers and refuses stale facade");
            using var participant = new ViewFixture();
            participant.Replication = new RuntimeReplication(services, "example.mode", new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Participant), participant.Lifetime);
            var refused = (IWorldView)provider.CreateOwnerFacade(typeof(IWorldView), participant);
            Check(refused.Fog(VisionFog.Blackout).ErrorCode == ModErrorCode.NotAuthoritative && built == 2, "participant refused before constructing any native view");
        }
        private sealed class Node { internal Node? Parent; internal bool Authored; }
        private static void Selection()
        {
            var root = new Node { Authored = true };
            var own = new Node { Parent = root };
            var nested = new Node { Parent = root, Authored = true };
            var nestedVisual = new Node { Parent = nested };
            Check(ViewRendererSelection.BelongsTo(root, root, n => n.Parent, n => n.Authored), "target's own authored root is included");
            Check(ViewRendererSelection.BelongsTo(root, own, n => n.Parent, n => n.Authored), "own visual descendants included");
            Check(!ViewRendererSelection.BelongsTo(root, nestedVisual, n => n.Parent, n => n.Authored), "nested authored visual excluded");
            Check(!ViewRendererSelection.BelongsTo(root, new Node(), n => n.Parent, n => n.Authored), "unrelated root excluded");
            var cycle = new Node(); cycle.Parent = cycle;
            Check(!ViewRendererSelection.BelongsTo(root, cycle, n => n.Parent, n => n.Authored), "malformed hierarchy does not loop");
        }
    }
}
