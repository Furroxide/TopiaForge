using System;
using System.IO;
using System.Threading;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using static TopiaForge.ModManager.Tests.PlayersApiTests;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class PresentationRuntimeTests
    {
        private static void BoundsAndLifetime()
        {
            using var rig = new Rig(); rig.Start();
            var initial = rig.Host.Author.ResourceCount;
            for (var i = 0; i < 100; i++) rig.Host.Ui.CreateSurface(Hud("temporary", "body")).Value!.Dispose();
            Assert(rig.Host.Author.ResourceCount == initial, "disposed HUDs release their lifetime registrations");
            for (var i = 0; i < 20; i++)
            {
                rig.Host.Audio.Play(new AudioPlayRequest("cue-" + i));
                rig.Network.Clock.Advance(2_100_000); rig.Frames(1);
            }
            Assert(rig.Host.Author.ResourceCount == initial, "finished cues release their lifetime registrations");
            using var author = new Author(rig.Host.Node, ModExecutionContext.GamemodeServer);
            Assert(!author.Ui.CreateSurface(Hud("huge", new string('x', 4097))).Succeeded, "oversized text rejected");
            for (var i = 0; i < 32; i++) Assert(rig.Host.Ui.CreateSurface(Hud("hud-" + i, "body")).Succeeded, "bounded HUD accepted");
            Assert(rig.Host.Ui.CreateSurface(Hud("overflow", "body")).ErrorCode == ModErrorCode.RateLimited, "surface count is bounded before local creation");
            Assert(rig.Host.LocalUi.Surfaces.Count == 32, "failed creation leaves no local surface");
            rig.Frames(10); Assert(rig.Guest.ReceivedUi.Surfaces.Count == 32, "maximum count round-trips");
            rig.Host.Dispose(); rig.Frames(10);
            Assert(rig.Guest.ReceivedUi.Surfaces.Count == 0 && rig.Guest.ReceivedAudio.ActivePlaybacks.Count == 0,
                "scope cleanup removes the complete presentation");
        }

        private static void ExactContextScopes()
        {
            using var rig = new Rig(); rig.Start();
            using var dispatcher = new HostDispatcher();
            var paths = new ManagerPaths(Path.Combine(Path.GetTempPath(), "TopiaForgePresentation-" + Guid.NewGuid().ToString("N")));
            paths.EnsureCreated();
            var parent = new ModContext(Seam.Manifest("io.example.presentation"), paths, paths.Root,
                new OwnerFacadeStoppingTests.Logger(), new ModServiceRegistry(), null, new PresentationFactory(), null, rig.Host.Node.Replication);
            try
            {
                ModContextScope Scope(string id, ModExecutionContext execution)
                {
                    var task = parent.CreateChildScopeAsync(id, CancellationToken.None, () => { },
                        new NativeTransitionAccessSlot(id, id, () => true), dispatcher, execution);
                    HostDispatcherTests.Pump(dispatcher, task); return task.GetAwaiter().GetResult();
                }
                using var server = Scope("server", ModExecutionContext.GamemodeServer);
                using var participant = Scope("participant", ModExecutionContext.Participant);
                parent.Ui.CreateSurface(Hud("process", "local"));
                participant.Context.Ui.CreateSurface(Hud("participant", "local"));
                server.Context.Ui.CreateSurface(Hud("server", "shared"));
                participant.Context.Audio.Play(new AudioPlayRequest("participant"));
                server.Context.Audio.Play(new AudioPlayRequest("server"));
                rig.Frames(10);
                Assert(rig.Guest.ReceivedUi.Surfaces.Count == 1 && rig.Guest.ReceivedUi.Surfaces[0].Body == "shared" &&
                    rig.Guest.ReceivedAudio.ActivePlaybacks.Count == 1, "actual scoped ModContexts route independently of their process parent");
                server.Dispose(); rig.Frames(10);
                Assert(!parent.Lifetime.IsStopping && !participant.Lifetime.IsStopping && rig.Guest.ReceivedUi.Surfaces.Count == 0,
                    "ending the gamemode scope removes its HUD without ending sibling or process scopes");
            }
            finally { parent.DisposeLifetime(); }
        }

        private sealed class PresentationFactory : IGameplayContextFactory
        {
            public GameplayContextServices Create(string ownerModId, string packagePath, string dataPath, IModLifetime lifetime,
                IModLogger logger, ModNetProfile profile, NativeTransitionAccessSlot? transitionAccess = null)
            {
                var other = GameplayContextServices.Unavailable(lifetime);
                var fakeLifetime = new FakeModLifetime(); lifetime.Track(fakeLifetime);
                return new GameplayContextServices(other.Input, other.LocalPlayer, other.Entities, other.Physics, other.Time, other.Scheduler,
                    other.Scenes, other.Interactions, other.Items, other.Assets, new FakeAudioService(fakeLifetime), new FakeUiService(fakeLifetime),
                    other.UnityInterop, other.SceneTransitions);
            }
        }
    }
}
