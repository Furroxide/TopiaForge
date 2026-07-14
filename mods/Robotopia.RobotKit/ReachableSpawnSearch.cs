using System;
using System.Collections.Generic;
using System.Threading;
using Robotopia.Mods;
using UnityEngine;

namespace Robotopia.RobotKit
{
    // Finds a spawn point near a ring origin that an agent can stand on AND actually reach the player from, by
    // reusing the game's own navigation. It works across frames (the native pathfinder is frame-budgeted and
    // main-thread only), driven by the service tick:
    //   1. (synchronous) generate ring candidates, downward-raycast each to ground, and snap+filter to a walkable
    //      navigation cell via RaycastedGraph.SampleAt;
    //   2. (asynchronous) for each walkable survivor, run one Pathfinder.Pathfind to the player and accept the
    //      first whose Path.complete is true — exactly the gate the native WalkSession uses.
    // When the scene has no pathfinder it degrades to a best-effort grounded point (no reachability guarantee), so
    // robot-less/camera-only scenes behave as before.
    internal sealed class ReachableSpawnSearch : IReachableSpawn
    {
        // How close to the reachability anchor (the player) a path must get to count as "reached". Generous enough
        // that the player's own footprint/curb does not produce a false negative, tight enough to mean "right here".
        private const float GoalStopDistance = 1.25f;

        private readonly Vector3 reachFrom;
        private readonly float heightOffset;
        private readonly float verticalScan;
        private readonly float groundProbeDepth;
        private readonly object? settingsBox;
        private readonly object? sampler;
        private readonly IModLogger logger;

        private readonly List<Vector3> ringPoints = new List<Vector3>();
        private int nextCandidate;

        private object? pendingAwaiter;
        private CancellationTokenSource? cts;
        private Vector3 pendingPoint;

        private bool complete;
        private bool found;
        private Vec3 position;

        public ReachableSpawnSearch(
            ReachableSpawnRequest request,
            object? settingsBox,
            System.Random random,
            IModLogger logger)
        {
            this.settingsBox = settingsBox;
            this.logger = logger;
            heightOffset = request.HeightOffset;

            var origin = new Vector3(request.Origin.X, request.Origin.Y, request.Origin.Z);
            var anchor = request.ReachableFrom ?? request.Origin;
            reachFrom = new Vector3(anchor.X, anchor.Y, anchor.Z);

            var min = Mathf.Max(0f, request.MinRadius);
            var max = Mathf.Max(min, request.MaxRadius);
            var attempts = Mathf.Max(1, request.MaxCandidates);
            for (var i = 0; i < attempts; i++)
            {
                var angle = (float)(random.NextDouble() * Math.PI * 2.0);
                var distance = Mathf.Lerp(min, max, (float)random.NextDouble());
                ringPoints.Add(origin + new Vector3(Mathf.Cos(angle) * distance, 0f, Mathf.Sin(angle) * distance));
            }

            verticalScan = Mathf.Max(0f, request.VerticalScan);
            groundProbeDepth = Mathf.Max(0.1f, request.GroundProbeDepth);

            if (!LocomotionBridge.NavAvailable())
            {
                // No pathfinder in this scene — cannot evaluate reachability. Fall back to the first grounded point
                // (best-effort, same guarantee the old code gave) so robot-less/camera-only scenes still spawn.
                if (TryFirstGroundedPoint(out var grounded))
                {
                    Finish(true, grounded);
                }
                else
                {
                    Finish(false, Vector3.zero);
                }

                return;
            }

            sampler = LocomotionBridge.CreateWalkabilitySampler(settingsBox);
        }

        public bool IsComplete => complete;
        public bool Found => found;
        public Vec3 Position => position;

        // Advance the search one frame's worth of work. Drives at most one in-flight native pathfind at a time.
        public void Step()
        {
            if (complete)
            {
                return;
            }

            // Drain a finished reachability pathfind. PollPathfind consumes the pooled UniTask source (GetResult)
            // exactly once on completion, so the awaiter is spent here — drop it and dispose the token WITHOUT
            // polling again (a second GetResult on a pooled source is a use-after-recycle).
            if (pendingAwaiter != null)
            {
                var poll = LocomotionBridge.PollPathfind(pendingAwaiter);
                if (poll == LocomotionBridge.PathPoll.Pending)
                {
                    return;
                }

                pendingAwaiter = null;
                DisposeCts();
                if (poll == LocomotionBridge.PathPoll.Reachable)
                {
                    Finish(true, pendingPoint + (Vector3.up * heightOffset));
                    return;
                }
                // Unreachable: fall through and try the next candidate.
            }

            // Find the next walkable candidate and start a reachability pathfind to the player for it.
            while (nextCandidate < ringPoints.Count)
            {
                var ringPoint = ringPoints[nextCandidate++];
                if (!TryGroundPoint(ringPoint, out var groundPoint))
                {
                    continue;
                }

                // Snap to a walkable navigation cell when the sampler is available; if the sampler symbol could not
                // be resolved, fall back to the raw ground point and let the pathfind itself be the gate.
                var standPoint = groundPoint;
                if (sampler != null && !LocomotionBridge.SampleWalkable(sampler, groundPoint, out standPoint))
                {
                    continue;
                }

                // Reject candidates within the goal stop distance of the anchor: the native pathfind short-circuits
                // to a "complete" no-op when the start is already at the goal, which would bypass route validation
                // (and we don't want a zombie spawning on top of the player anyway). Only reachable with a
                // misconfigured sub-stop-distance MinRadius; the default ring is far outside this.
                if ((standPoint - reachFrom).sqrMagnitude <= GoalStopDistance * GoalStopDistance)
                {
                    continue;
                }

                cts = new CancellationTokenSource();
                var awaiter = LocomotionBridge.BeginPathfind(standPoint, reachFrom, GoalStopDistance, settingsBox, cts.Token);
                if (awaiter == null)
                {
                    // Could not start a pathfind (nav vanished mid-search). Stop and report no point rather than
                    // fabricating an unvalidated one.
                    DisposeCts();
                    Finish(false, Vector3.zero);
                    return;
                }

                pendingAwaiter = awaiter;
                pendingPoint = standPoint;
                return;
            }

            // Exhausted every candidate without a reachable one.
            Finish(false, Vector3.zero);
        }

        // Abandon an in-flight search (scene change / service dispose). Cancels the native pathfind and completes
        // the handle as "not found" so a late poll never spawns into a torn-down scene. Idempotent.
        public void Cancel()
        {
            ReleasePending();
            if (!complete)
            {
                Finish(false, Vector3.zero);
            }
        }

        private bool TryFirstGroundedPoint(out Vector3 grounded)
        {
            for (var i = 0; i < ringPoints.Count; i++)
            {
                if (TryGroundPoint(ringPoints[i], out var groundPoint))
                {
                    grounded = groundPoint + (Vector3.up * heightOffset);
                    return true;
                }
            }

            grounded = Vector3.zero;
            return false;
        }

        // Downward raycast from above the ring point to find ground, rejecting hits that land on a game robot (so a
        // candidate never stacks on top of another robot/zombie).
        private bool TryGroundPoint(Vector3 ringPoint, out Vector3 groundPoint)
        {
            var origin = ringPoint + (Vector3.up * verticalScan);
            if (Physics.Raycast(origin, Vector3.down, out var hit, groundProbeDepth, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) &&
                !GameReflection.IsGameRobotInParent(hit.collider))
            {
                groundPoint = hit.point;
                return true;
            }

            groundPoint = Vector3.zero;
            return false;
        }

        // Tear-down release for the abandon/cancel path (no later Step to drain): poll the awaiter once so a
        // completed pooled UniTask source is consumed exactly once (PollPathfind no-ops GetResult while pending),
        // then cancel + dispose the token.
        private void ReleasePending()
        {
            if (pendingAwaiter != null)
            {
                LocomotionBridge.PollPathfind(pendingAwaiter);
                pendingAwaiter = null;
            }

            DisposeCts();
        }

        private void DisposeCts()
        {
            if (cts != null)
            {
                try
                {
                    cts.Cancel();
                }
                catch (Exception ex)
                {
                    logger.Debug("RobotKit reachable-spawn cancellation failed: " + ex.Message);
                }

                cts.Dispose();
                cts = null;
            }
        }

        private void Finish(bool didFind, Vector3 point)
        {
            complete = true;
            found = didFind;
            position = didFind ? new Vec3(point.x, point.y, point.z) : Vec3.Zero;
        }
    }
}
