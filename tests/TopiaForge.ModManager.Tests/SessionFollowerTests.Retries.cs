using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using static TopiaForge.ModManager.Tests.Seam;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// When a follow cannot begin at once: a game busy with another native change is tried again every quarter second and
    /// given up on after 30 seconds, and a host's world this game has not found has the game look for its own worlds once,
    /// then follow again, before the join ends in its own words.
    /// </summary>
    internal static partial class SessionFollowerTests
    {
        private static void ABusyGameIsTriedAgainThenGivenUp(string root)
        {
            using (var rig = new FollowRig(root))
            {
                var held = HoldTheScene(rig);
                rig.Join();
                rig.Announce(rig.World(SessionFollowTests.NewSessionId()));
                for (var frame = 0; frame < 60; frame++) rig.Frame();
                That(rig.Status.Stage == FollowStage.Preparing && rig.Test.Hosted.Current.Phase == SessionPhase.Idle && rig.Follower.HoldsReadiness,
                    "while another native change holds the game, the follow waits to try again, holding readiness (" + rig.Status.Stage + ")");
                held.Dispose();
                rig.Until(() => rig.Status.Stage == FollowStage.Running, "once the game is free, the follow tries again and the host's world runs");
            }

            using (var rig = new FollowRig(root + "-stays"))
            {
                var held = HoldTheScene(rig);
                rig.Join();
                rig.Announce(rig.World(SessionFollowTests.NewSessionId()));
                var frames = 0;
                while (rig.Status.Stage != FollowStage.Failed && frames < 4_000)
                {
                    rig.Frame();
                    frames++;
                }

                var failure = rig.Status.Failure;
                var waited = frames * 0.016f;
                That(failure != null && failure.Kind == FollowFailureKind.PrepareFailed && failure.Code == ModErrorCode.Conflict && failure.Retryable
                    && waited >= SessionFollower.BusyGiveUpSeconds && waited < SessionFollower.BusyGiveUpSeconds + 2f,
                    "a game that stays busy ends the join in its own words after 30 seconds (" + failure?.Kind + ", " + waited + " s)");
                rig.Frame();
                That(rig.Runtime.Realm == NetRealm.Offline, "and leaves the session");
                held.Dispose();
            }
        }

        private static void AWorldNotFoundIsLookedForOnce(string root)
        {
            const string family = "io.github.furroxide.topiaforge.worlds.levels";
            using var rig = new FollowRig(root, configure: manifest => manifest.Contributions!.Worlds.Add(new ModWorldDeclaration
            {
                Id = family,
                Name = "levels",
                Content = new ModWorldContent { Kind = ModWorldContent.DiscoveredKind, Implementation = new ModImplementationBinding { Type = "Fixture.Levels" } },
                Transitions = new List<string> { ModTransitions.SceneReplacement },
                Spawn = new ModSpawnPolicy { Kind = ModSpawnPolicy.ProviderDefaultKind },
                OpenToAnyCompatible = true
            }));
            rig.Test.BoundWorlds.Add(family);
            rig.Join();

            // The host runs a level its game discovered, which this game has not listed among its own worlds.
            var target = rig.Test.Plan.Descriptor;
            var world = family + ".a1b2c3";
            rig.Announce(HostWorldState.World(SessionFollowTests.NewSessionId(), new LaunchRequest(target.Request.TargetId, world), target.GamemodeId, world,
                family, ModTransitions.SceneReplacement, null, null));
            rig.Until(() => rig.Status.Stage == FollowStage.Failed, "a host's world this game cannot find ends the join");
            var failure = rig.Status.Failure!;
            That(rig.Discoveries == 1 && failure.Kind == FollowFailureKind.WorldNotFound && failure.Retryable,
                "after the game looked for its own worlds once and followed again, in its own words (" + rig.Discoveries + " looks, " + failure.Kind + ")");
            That(rig.Test.Hosted.Current.Phase == SessionPhase.Idle && rig.Test.Parent.ActiveChildScopeCount == 0, "and nothing of the follow is left");
        }

        /// <summary>Another native change holds the game, as a scene the game loads by itself does, until it is disposed.</summary>
        private static IDisposable HoldTheScene(FollowRig rig)
        {
            var held = rig.Test.Native.TryReserve(new NativeTransitionOwner("io.example.busy", "busy:" + Guid.NewGuid().ToString("N"), null),
                "busy-" + Guid.NewGuid().ToString("N"));
            That(held.Succeeded, "another native change holds the game: " + held.ErrorMessage);
            return held.Value!;
        }
    }
}
