using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>What no tool, player or forged message can do to a blocker, and the size a move keeps.</summary>
    internal static partial class BlockerTests
    {
        private static readonly Vec3 Unit = new Vec3(1, 1, 1);

        /// <summary>The Gravity Gun grabs through Context.Entities.AcquireMotion, with whatever entity its raycast hit.</summary>
        private static void NoToolHoldsABlocker()
        {
            var offline = new WorldRig();
            using (var scope = new OwnerModLifetime())
            {
                var placed = (FakeWorldBlocker)Physics(offline.Host, offline.Host.Seam(ModExecutionContext.GamemodeServer, scope), scope)
                    .AddBlocker(BlockerRequest.Box(new Vec3(1, 1, 0), Size)).Value!;
                var tool = new AuthorityMod(offline.Host);
                var grab = tool.AsPlayer(() => tool.Entities.AcquireMotion(placed.Object.Entity));
                Check(grab.ErrorCode == ModErrorCode.NotAuthoritative && grab.ErrorMessage == NetPhysicsService.NotHeld && placed.Object.Kinematic,
                    "offline, a tool's grab of the entity its raycast hit is refused, and the box stays kinematic");
                Check(tool.Entities.AcquireMotion(placed).ErrorCode == ModErrorCode.NotAuthoritative, "and so is a grab by the handle its code holds");
            }

            var rig = AuthorityRig.Create(players: 1);
            var host = rig.Host;
            var player = rig.Players[0];
            using var hostScope = new OwnerModLifetime();
            var box = (FakeWorldBlocker)Physics(host, host.Seam(lifetime: hostScope), hostScope)
                .AddBlocker(BlockerRequest.Box(new Vec3(1, 1, 0), Size)).Value!;
            Check(rig.Run(() => player.Scene.Blockers.Any(each => each.IsAlive), 120), "the player has the host's blocker");
            var replica = player.Scene.Blockers.Single(each => each.IsAlive);
            var hostTool = new AuthorityMod(host);
            Check(hostTool.AsPlayer(() => hostTool.Entities.AcquireMotion(box.Object.Entity)).ErrorCode == ModErrorCode.NotAuthoritative,
                "the host's own player cannot hold its blocker");
            var playerTool = new AuthorityMod(player);
            var grabbed = playerTool.AsPlayer(() => playerTool.Entities.AcquireMotion(replica.Object.Entity));
            Check(grabbed.ErrorCode == ModErrorCode.NotAuthoritative && player.World.Motion.LocalCount == 0 && replica.Object.Kinematic,
                "a joined player's grab of its replica is refused there, before anything simulates or is sent");

            using var playerScope = new OwnerModLifetime();
            var shadow = (FakeWorldBlocker)Physics(player, player.Seam(lifetime: playerScope), playerScope)
                .AddBlocker(BlockerRequest.Box(new Vec3(1, 1, 0), Size)).Value!;
            Check(player.World.Counters.ShadowsBound == 1 && shadow.IsAlive, "the player's own run of the script shadows the host's blocker");
            Check(playerTool.AsPlayer(() => playerTool.Entities.AcquireMotion(shadow.Object.Entity)).ErrorCode == ModErrorCode.NotAuthoritative,
                "and no tool holds that shadow, which stands for the host's box");

            // A modified client skips its own check and asks the host directly.
            var forged = new WorldWireWriter();
            MotionWire.Write(forged, MotionMessages.Grab, new MotionMessage
            {
                Target = new MotionTarget(new NetworkEntityId(box.Object.Mark)),
                Request = 1,
                PlayerPosition = new NetVector3(1, 1, 0)
            });
            var refusedGrabs = host.World.Motion.GrabsRefused;
            SpawnAbuseTests.Inject(rig, player, forged.Segment.ToArray());
            rig.Frames(4);
            Check(host.World.Motion.GrabsRefused == refusedGrabs + 1 && host.World.Motion.GrabsGranted == 0 && box.Object.Kinematic &&
                  WorldAssert.Near(box.Object.Position, new NetVector3(1, 1, 0)),
                "the host refuses a forged grab of its blocker, which stays kinematic and in place");

            // Nor may a player make one, move the host's or end it.
            var log = SpawnAbuseTests.Log(rig);
            SpawnAbuseTests.Inject(rig, player, SpawnAbuseTests.Kind(1, BlockerDescriptor.Kind, BlockerDescriptor.Encode(BlockerRequest.Box(Vec3.Zero, Size))));
            var id = new NetworkEntityId(box.Object.Mark);
            var refusedChanges = host.Counters().ChangesRefused;
            SpawnAbuseTests.Inject(rig, player, SpawnAbuseTests.Move(AuthorityTarget.Entity(id), 40));
            SpawnAbuseTests.Inject(rig, player, SpawnAbuseTests.Destroy(AuthorityTarget.Entity(id)));
            rig.Frames(4);
            var verdict = SpawnAbuseTests.Verdicts(log, player).FirstOrDefault(each => each.Provisional == 1);
            Check(verdict.Provisional == 1 && verdict.Reason == SpawnRefusal.NotPermitted && host.Scene.Blockers.Count(each => each.IsAlive) == 1,
                "a player's request for a blocker of its own is refused as NotPermitted (" + verdict.Reason + ")");
            Check(host.Counters().ChangesRefused == refusedChanges + 2 && box.IsAlive && WorldAssert.Near(box.Object.Position, new NetVector3(1, 1, 0)),
                "and its move and destroy of the host's blocker are refused");

            // Leaving ends the shadow with the host's world; nothing of the session becomes this player's own.
            Check(player.Runtime.Leave().Succeeded && rig.Run(() => !shadow.IsAlive && player.Runtime.Realm == NetRealm.Offline, 120),
                "the shadow ends when the player leaves");
            Check(player.Runtime.Spawns.Live().Count == 0, "and no shadow became a record of the player's own");
        }

        /// <summary>BlockerRequest.Box ignores a placement's scale, and so does a move: the box keeps its size on every peer.</summary>
        private static void AMoveKeepsTheSize()
        {
            var rig = new WorldRig();
            using var scope = new OwnerModLifetime();
            var seam = rig.Host.Seam(ModExecutionContext.GamemodeServer, scope);
            var blocker = (FakeWorldBlocker)Physics(rig.Host, seam, scope).AddBlocker(BlockerRequest.Box(Vec3.Zero, Size)).Value!;
            var engine = new RecordingEntities();
            var entities = new NetEntityService(engine, seam);
            var scaled = new TransformState(new Vec3(5, 1, 0), Quat.Identity, new Vec3(3, 3, 3));
            var moved = entities.SetTransform(blocker, scaled);
            Check(engine.Last.Position == scaled.Position && engine.Last.Scale == Unit && moved.Value.Scale == Unit,
                "a move of a blocker reaches the engine with its position and a scale of one");
            entities.SetTransform(blocker.Object.Entity, scaled);
            Check(engine.Last.Scale == Unit, "and so does a move of the entity a query or raycast found for it");
            var prop = rig.Host.Scene.Add(rig.Host.Scene.ActiveScene ?? rig.Host.Scene.AddScene("Yard"), "Prop", rigidbody: true).Entity;
            entities.SetTransform(prop, scaled);
            Check(engine.Last.Scale == scaled.Scale, "while any other entity takes the scale it is given");
        }

        /// <summary>The testing kit answers a blocker's tests as the game does.</summary>
        private static void TheTestingKitAnswersAsTheGame()
        {
            using var context = new FakeModContext();
            var blocker = context.Physics.AddBlocker(BlockerRequest.Box(Vec3.Zero, Size)).Value!;
            var moved = context.Entities.SetTransform(blocker, new TransformState(new Vec3(1, 0, 0), Quat.Identity, new Vec3(3, 3, 3)));
            Check(moved.Value.Scale == Unit && context.Entities.TryGetTransform(blocker, out var read) && read.Scale == Unit && read.Position == new Vec3(1, 0, 0),
                "the testing kit moves a blocker and keeps its size");
            var found = context.Entities.Query(new EntityQuery(nameContains: "Blocker")).Single();
            Check(context.Entities.AcquireMotion(blocker).ErrorCode == ModErrorCode.NotAuthoritative &&
                  context.Entities.AcquireMotion(found).ErrorCode == ModErrorCode.NotAuthoritative,
                "and holds no blocker, by its handle or by the entity a query finds");

            using var server = new FakeModContext();
            using var playerContext = new FakeModContext(server.Identity);
            var participant = new FakeGamemodeParticipantSession(playerContext,
                new WorldReadiness(new WorldSceneIdentity(1, "test.world"), TransformState.Identity));
            var refused = participant.Context.Physics.AddBlocker(BlockerRequest.Box(Vec3.Zero, Size));
            Check(refused.ErrorCode == ModErrorCode.NotAuthoritative && refused.ErrorMessage == NetPhysicsService.NotAuthoritative,
                "a participant controller's context refuses blockers with the game's words, as the game does in every role");
            Check(server.Physics.AddBlocker(BlockerRequest.Box(Vec3.Zero, Size)).Succeeded, "while the server controller's context places them");
        }

        /// <summary>An engine's entity service that records the last transform it was asked to apply.</summary>
        private sealed class RecordingEntities : IEntityService
        {
            internal TransformState Last { get; private set; }

            public bool TryGetTransform(IEntity entity, out TransformState transform)
            {
                transform = Last;
                return true;
            }

            public OperationResult<TransformState> SetTransform(IEntity entity, TransformState transform)
            {
                Last = transform;
                return OperationResult<TransformState>.Success(transform);
            }

            public IReadOnlyList<IEntity> Query(EntityQuery query) => Array.Empty<IEntity>();

            public OperationResult<bool> Destroy(IEntity entity) => OperationResult<bool>.Success(true);

            public OperationResult<IEntityMotion> AcquireMotion(IEntity entity) =>
                OperationResult<IEntityMotion>.Failure(ModErrorCode.Unavailable, "No motion in this test.");
        }
    }
}
