using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.SandboxAcceptance
{
    public sealed partial class SandboxAcceptanceMod
    {
        private sealed class TrackingFactory : ICreatorContentFactory
        {
            private readonly SandboxAcceptanceMod owner;
            private readonly string localId;
            private readonly ICreatorContentFactory factory;
            public TrackingFactory(SandboxAcceptanceMod owner, string localId, ICreatorContentFactory factory)
            { this.owner = owner; this.localId = localId; this.factory = factory; }
            public OperationResult<ICreatorSourceInstance> Spawn(TransformState transform)
            {
                if (owner.sources.Count >= 1024) return OperationResult<ICreatorSourceInstance>.Failure(ModErrorCode.RateLimited, "Fixture observation capacity reached.");
                var spawned = factory.Spawn(transform);
                if (!spawned.TryGetValue(out var source)) return spawned;
                var tracked = new TrackedSource(source, localId);
                owner.sources.Add(tracked);
                return OperationResult<ICreatorSourceInstance>.Success(tracked);
            }
        }
        private sealed class TrackedSource : ICreatorSourceInstance
        {
            private ICreatorSourceInstance? source;
            private readonly IEntity entity;
            private readonly string contentId;
            public TrackedSource(ICreatorSourceInstance source, string localId)
            { this.source = source; entity = source.Entity; contentId = "dev.topiaforge.sandbox-acceptance:" + localId; }
            public bool Disposed => source == null;
            public IEntity Entity => entity;
            public bool IsAlive => source?.IsAlive == true && entity.IsAlive;
            public bool TryGetTransform(out TransformState transform)
            { if (source != null) return source.TryGetTransform(out transform); transform = TransformState.Identity; return false; }
            public OperationResult<TransformState> SetTransform(TransformState transform) => source == null
                ? OperationResult<TransformState>.Failure(ModErrorCode.InvalidState, "Fixture is disposed.") : source.SetTransform(transform);
            public void Dispose() { var current = source; source = null; current?.Dispose(); }
            public FixtureEntitySnapshot Snapshot() => new FixtureEntitySnapshot
            { Id = entity.Id, ContentId = contentId, Alive = IsAlive, Transform = TryGetTransform(out var transform) ? Values(transform) : Array.Empty<float>() };
        }
        private sealed class RobotFactory : ICreatorContentFactory
        {
            private readonly IModContext context;
            private readonly IRobotAgentService robots;
            public RobotFactory(IModContext context, IRobotAgentService robots) { this.context = context; this.robots = robots; }
            public OperationResult<ICreatorSourceInstance> Spawn(TransformState transform)
            {
                var spawned = robots.Spawn(new RobotAgentSpawnRequest(transform.Position, brainMode: RobotBrainMode.Dormant, name: "Acceptance character"));
                if (!spawned.TryGetValue(out var robot)) return OperationResult<ICreatorSourceInstance>.Failure(spawned.ErrorCode, spawned.ErrorMessage);
                var set = context.Entities.SetTransform(robot, transform);
                if (!set.Succeeded) { robot.Despawn(); robot.Dispose(); return OperationResult<ICreatorSourceInstance>.Failure(set.ErrorCode, set.ErrorMessage); }
                return OperationResult<ICreatorSourceInstance>.Success(new RobotSource(context, robot));
            }
        }
        private sealed class RobotSource : ICreatorSourceInstance
        {
            private readonly IModContext context;
            private readonly IRobotAgent robot;
            private bool disposed;
            public RobotSource(IModContext context, IRobotAgent robot) { this.context = context; this.robot = robot; }
            public IEntity Entity => robot;
            public bool IsAlive => !disposed && robot.IsAlive;
            public bool TryGetTransform(out TransformState transform) => context.Entities.TryGetTransform(robot, out transform);
            public OperationResult<TransformState> SetTransform(TransformState transform) => context.Entities.SetTransform(robot, transform);
            public void Dispose() { if (disposed) return; disposed = true; try { robot.Despawn(); } finally { robot.Dispose(); } }
        }
        private static float[] Values(TransformState t) => new[] { t.Position.X, t.Position.Y, t.Position.Z,
            t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W, t.Scale.X, t.Scale.Y, t.Scale.Z };
        private CreatorEventProject BuildProject(string worldId)
        {
            CreatorGraphNode Node(string id, CreatorGraphNodeKind kind, params string[] pairs)
            {
                var parameters = new Dictionary<string, string>();
                for (var index = 0; index < pairs.Length; index += 2) parameters[pairs[index]] = pairs[index + 1];
                return new CreatorGraphNode(id, kind, Vec2.Zero, parameters);
            }
            return new CreatorEventProject(1, ProjectId, "Sandbox acceptance graph", "Synthetic supplementary fixture.",
                CreatorProjectScope.Sandbox, worldId, "", DateTimeOffset.UtcNow,
                entities: new[] {
                    new CreatorProjectEntity("prop", "Acceptance graph prop", "dev.topiaforge.sandbox-acceptance:prop", "0.1.0-rc.1", TransformState.Identity, true),
                    new CreatorProjectEntity("speaker", "Acceptance graph robot", "io.github.furroxide.topiaforge.robotkit:default", "0.1.0-rc.1", TransformState.Identity, true) },
                personas: new[] { new CreatorPersona("synthetic", "Sandbox acceptance preview", "Synthetic QA persona; remain dormant.", "Synthetic acceptance only.") },
                nodes: new[] {
                    Node("start", CreatorGraphNodeKind.ProjectStart),
                    Node("condition", CreatorGraphNodeKind.StateCondition, "value", "true"),
                    Node("personality", CreatorGraphNodeKind.SetRobotPersonality, "entityId", "speaker", "personaId", "synthetic"),
                    Node("chat", CreatorGraphNodeKind.BeginConversation, "entityId", "speaker", "personaId", "synthetic"),
                    Node("audio", CreatorGraphNodeKind.PlayAudio, "cueId", "sandbox-acceptance-graph"),
                    Node("delay", CreatorGraphNodeKind.Delay, "seconds", "0.12", "maxActivations", "1000"),
                    Node("interact", CreatorGraphNodeKind.InteractionTrigger, "entityId", "prop", "prompt", "ACCEPTANCE", "radius", "3"),
                    Node("toast", CreatorGraphNodeKind.ShowToast, "text", "Sandbox acceptance interaction"),
                    Node("wrong", CreatorGraphNodeKind.ShowToast, "text", "Sandbox acceptance WRONG BRANCH") },
                edges: new[] { new CreatorGraphEdge("start", "fired", "condition", "in"), new CreatorGraphEdge("condition", "true", "personality", "in"),
                    new CreatorGraphEdge("condition", "false", "wrong", "in"), new CreatorGraphEdge("personality", "success", "chat", "in"),
                    new CreatorGraphEdge("chat", "success", "audio", "in"), new CreatorGraphEdge("chat", "failure", "audio", "in"),
                    new CreatorGraphEdge("audio", "success", "delay", "in"), new CreatorGraphEdge("delay", "done", "audio", "in"),
                    new CreatorGraphEdge("interact", "fired", "toast", "in") }, origin: CreatorProjectOrigin.PlayerAtRun);
        }
    }
}
