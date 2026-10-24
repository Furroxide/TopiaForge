using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using static TopiaForge.ModManager.Tests.Seam;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class SessionFollowerTests
    {
        private static void NativeReloadsOfTheSameLevelAreCorrected(string root)
        {
            using var rig = new FollowRig(root);
            var instance = 100;
            rig.Test.Level = (scene, _) =>
            {
                rig.Scene.Activated(scene, ++instance);
                return Task.FromResult(OperationResult<bool>.Success(true));
            };
            rig.Join();
            rig.Announce(HostWorldState.Level("Downtown"));
            rig.Until(() => rig.Status.Stage == FollowStage.InLevel, "the native level runs");
            rig.Scene.Activated("Downtown", instance);
            rig.Frame();
            That(rig.Follower.Refollows == 0, "duplicate events for the arrived instance do not re-follow");
            rig.Scene.Activated("Downtown", ++instance);
            rig.Until(() => rig.Test.Levels.Count == 2 && rig.Status.Stage == FollowStage.InLevel,
                "a native restart into a different instance of the same level re-follows");
            That(rig.Follower.Refollows == 1, "the native restart counts against the correction bound");
        }

        private static void NativeLoadsWhileWaitingAtTheMenuAreCorrected(string root)
        {
            foreach (var missing in new[] { false, true })
            {
                using var rig = new FollowRig(root + (missing ? "-missing" : "-menu"));
                rig.Test.Menu = _ =>
                {
                    rig.Scene.Activated(GameScenes.MainMenuSceneName);
                    return Task.FromResult(OperationResult<bool>.Success(true));
                };
                rig.Test.Level = (_, _) => Task.FromResult(OperationResult<bool>.Failure(ModErrorCode.NotFound, "Level unavailable."));
                rig.Join();
                rig.Announce(missing ? HostWorldState.Level("Downtown") : HostWorldState.Menu);
                rig.Until(() => rig.Status.Stage == FollowStage.AtMenu, "the guest waits at the menu");
                var menus = rig.Test.MenuLoads;
                rig.Scene.Activated("Elsewhere");
                rig.Until(() => rig.Test.MenuLoads == menus + 1 && rig.Scene.IsMainMenu && rig.Status.Stage == FollowStage.AtMenu,
                    "a native load while waiting returns to the menu");
                That(rig.Follower.Refollows == 1 && rig.Runtime.Realm == NetRealm.Client,
                    "the correction is bounded and keeps the guest joined");
                That(rig.Test.Levels.Count == (missing ? 1 : 0), "an unavailable host level is not retried");
                for (var correction = 2; correction <= SessionFollower.MaximumRefollows; correction++)
                {
                    rig.Scene.Activated("Elsewhere");
                    var expected = menus + correction;
                    rig.Until(() => rig.Test.MenuLoads == expected && rig.Scene.IsMainMenu && rig.Status.Stage == FollowStage.AtMenu,
                        "each allowed native escape returns to the waiting menu");
                }
                rig.Scene.Activated("Elsewhere");
                rig.Until(() => rig.Runtime.Realm == NetRealm.Offline && rig.Scene.IsMainMenu,
                    "exhausting corrections leaves the join at the menu, even when the host was always there");
                That(rig.Status.Stage == FollowStage.Failed && rig.Status.Failure?.Message == FollowFailureWords.For(FollowFailureKind.KeepsLeaving, ModErrorCode.InvalidState).Message,
                    "the ninth escape keeps the fixed failure words");
            }
        }

        private static void KeepControllerWorldsStillFollowNativeLoads(string root)
        {
            using var rig = new FollowRig(root);
            rig.Join();
            var host = SessionFollowTests.NewSessionId();
            rig.Announce(rig.World(host));
            rig.Until(() => rig.Status.Stage == FollowStage.Running, "the keep-controller world runs");
            var loads = 0;
            SessionFixture.Load = (_, _) =>
            {
                loads++;
                rig.Scene.Activated("World");
                return Task.FromResult(OperationResult<IWorldInstance>.Success(new SessionTestWorld()));
            };
            rig.Scene.Activated("Elsewhere");
            rig.Test.Hosted.OnSceneLifecycle(new SceneLifecycleEvent(77, "Elsewhere", SceneLifecyclePhase.Activated,
                SceneLoadMode.Single, isActive: true));
            rig.Until(() => loads == 1 && rig.Status.Stage == FollowStage.Running,
                "native replacement re-follows a keep-controller world too");
            That(rig.Test.Hosted.FollowedSessionId == host && rig.Follower.Refollows == 1,
                "the same host world resumes after one correction");
        }
    }
}
