namespace TopiaForge.ModManager.Tests
{
    internal static partial class RobotInteractionAuthorityTests
    {
        private const string Scenarios = """
using System;
using System.Collections.Generic;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.RobotKit;
internal sealed class InteractionProbe
{
    private readonly RobotAgentService service = new();
    private readonly List<IRobotAgent> gates = new(), callbacks = new();
    private RobotInteractionContext? lastContext;
    public InteractionProbe() { }
    public IRobotAgent Decorate(IRobotAgent inner, RobotAgentSpawnRequest request)
    {
        var native = new NativeRobot(inner, request);
        service.Last = native;
        return native;
    }
    private RobotInteractionOptions Custom(string prompt = "Repair") =>
        RobotInteractionOptions.Custom(new RobotCustomInteraction(prompt, context =>
        {
            callbacks.Add(context.Agent);
            lastContext = context;
        }, 4f, 0.25f, context =>
        {
            gates.Add(context.Agent);
            lastContext = context;
            return true;
        }));
    public void Run(string scenario, IModLifetime lifetime, IRuntimeReplication replication,
        Func<IRuntimeReplication, RobotAgentSpawnRequest, OperationResult<IRobotAgent>> spawn,
        Func<IDisposable> forwarded, Action stopOwner)
    {
        service.Replication = new RobotReplication { SpawnCore = spawn };
        using var context = new ProbeContext(lifetime, replication);
        var owner = service.ForOwner(context);
        if (scenario == "policies") { Policies(owner, forwarded); return; }

        OperationResult<IRobotAgent> result;
        if (scenario == "guest-custom")
        {
            using (forwarded()) result = owner.Spawn(new RobotAgentSpawnRequest(Vec3.Zero,
                brainMode: RobotBrainMode.Autonomous, interaction: Custom()));
        }
        else
        {
            result = owner.Spawn(new RobotAgentSpawnRequest(Vec3.Zero,
                interaction: scenario == "replacement" ? RobotInteractionOptions.DisableNativeTalk() : Custom()));
        }
        Require(result.Succeeded, "owner spawn succeeds");
        var robot = result.Value!;
        var native = service.Last!;
        Require(gates.Count == 0 && callbacks.Count == 0 && !native.EarlySelectable,
            "construction callbacks remain inert until the owner handle settles");
        if (scenario == "replacement")
            Require(robot.SetInteraction(Custom("Inspect")).Succeeded, "replacement custom installation succeeds");
        if (scenario == "guest-custom")
            Require(native.Request.BrainMode == RobotBrainMode.Dormant &&
                native.Request.Interaction.NativeTalkMode == RobotNativeTalkMode.Disabled &&
                native.Request.Interaction.CustomInteraction?.Prompt == "Repair",
                "forwarded host custom spawn preserves the prompt while disabling native behavior");

        var custom = native.Interaction.CustomInteraction;
        Require(custom != null && custom.Distance == 4f && custom.ScreenRectExpansion == 0.25f,
            "custom interaction metadata survives native construction");
        var bridge = native.Bridge;
        Require(bridge.CanInteract(native.Hand), "production bridge selects the custom interaction");
        bridge.OnInteractAttempt(native.Hand, CancellationToken.None);
        Require(gates.Count == 1 && callbacks.Count == 1, "both production bridge callbacks run after construction");
        UIState.InteractTarget.Set(bridge);
        Require(UIState.InteractAction.Value == (scenario == "replacement" ? "Inspect" : "Repair") &&
            bridge.ScreenRectExpansion == 0.25f, "production prompt rendering keeps custom settings");
        Require(lastContext != null && lastContext.AgentPosition.Equals(native.Position) &&
            lastContext.HandPosition.Equals(new Vec3(1, 0, 0)) && lastContext.Distance == 1f,
            "callback rebinding preserves sampled positions and distance");

        if (scenario == "owner-stop" || scenario == "world-retired")
        {
            if (scenario == "owner-stop") stopOwner();
            else context.Scope.Current = false;
            Require(native.IsAlive, "native body still exists before scoped rejection");
            var expected = scenario == "owner-stop" ? ModErrorCode.Cancelled : ModErrorCode.InvalidState;
            var calls = native.Calls;
            foreach (var handle in new[] { gates[0], callbacks[0] })
                foreach (var mutate in Mutations(handle))
                    Require(mutate().ErrorCode == expected, "stale owner/world mutators refuse before native effects");
            Require(native.Calls == calls, "stale owner/world handles never reach the native adapter");
            var count = gates.Count + callbacks.Count;
            Require(!bridge.CanInteract(native.Hand), "retired owner/world bridge cannot select a prompt");
            bridge.OnInteractAttempt(native.Hand, CancellationToken.None);
            Require(gates.Count + callbacks.Count == count, "retired owner/world callbacks stay suppressed");
            robot.Dispose();
            Require(native.Disposals > 0 && !native.IsAlive, "Dispose still releases native resources after scope retirement");
            return;
        }

        var before = native.Calls;
        using (forwarded())
        {
            // Both handles came from the actual native bridge, then survived beyond that callback invocation.
            foreach (var handle in new[] { gates[0], callbacks[0] })
                Require(handle.SetBrainMode(RobotBrainMode.Autonomous).ErrorCode == ModErrorCode.NotAuthoritative &&
                    handle.SetInteraction(RobotInteractionOptions.NativeTalk()).ErrorCode == ModErrorCode.NotAuthoritative,
                    "retained bridge handles reject forwarded guest wake calls");
            Require(native.Calls == before, "retained guest calls perform no native effects");
            Require(bridge.CanInteract(native.Hand), "guest-origin custom gate remains available");
            bridge.OnInteractAttempt(native.Hand, CancellationToken.None);
        }
        foreach (var handle in gates)
            Require(ReferenceEquals(handle, robot), "gate exposes the exact owner handle");
        foreach (var handle in callbacks)
            Require(ReferenceEquals(handle, robot), "interaction exposes the exact owner handle");
        Require(callbacks[0].SetBrainMode(RobotBrainMode.Autonomous).Succeeded &&
            gates[0].SetInteraction(RobotInteractionOptions.NativeTalk()).Succeeded && native.Calls == before + 2,
            "trusted host controls recover after forwarded context ends");
    }

    private void Policies(IRobotAgentService owner, Func<IDisposable> forwarded)
    {
        Require(owner.Spawn(new RobotAgentSpawnRequest(Vec3.Zero)).Succeeded &&
            service.Last!.Request.Interaction.NativeTalkMode == RobotNativeTalkMode.Enabled,
            "trusted host default retains native talk");
        using (forwarded())
        {
            foreach (var policy in new[] { RobotInteractionOptions.NativeTalk(), RobotInteractionOptions.DisableNativeTalk() })
                Require(owner.Spawn(new RobotAgentSpawnRequest(Vec3.Zero, interaction: policy)).Succeeded &&
                    service.Last!.Request.Interaction.NativeTalkMode == RobotNativeTalkMode.Disabled &&
                    service.Last.Request.Interaction.CustomInteraction == null,
                    "default and explicitly disabled forwarded policies remain distinct from Custom");
            var before = service.Last;
            foreach (var request in new[]
            {
                new RobotAgentSpawnRequest(Vec3.Zero, brainMode: (RobotBrainMode)2, interaction: Custom()),
                new RobotAgentSpawnRequest(Vec3.Zero, interaction: RobotInteractionOptions.NativeTalkAtDistance(float.NaN)),
                new RobotAgentSpawnRequest(Vec3.Zero, interaction: RobotInteractionOptions.NativeTalkAtDistance(101f))
            })
                Require(owner.Spawn(request).ErrorCode == ModErrorCode.InvalidArgument && ReferenceEquals(before, service.Last),
                    "original invalid inputs reject before coercion or native construction");
        }
    }

    private static IEnumerable<Func<OperationResult<bool>>> Mutations(IRobotAgent robot)
    {
        yield return () => robot.SetBrainMode(RobotBrainMode.Dormant);
        yield return () => robot.ConfigureMovement(new RobotMovementSettings());
        yield return () => robot.MoveTo(Vec3.Zero);
        yield return () => robot.Chase(robot);
        yield return () => robot.Stop();
        yield return () => robot.SetTint(new RobotColor(1, 1, 1));
        yield return () => robot.SetEmote("wave");
        yield return () => robot.SetName("Retired");
        yield return () => robot.SetScale(1);
        yield return () => robot.SetInteraction(RobotInteractionOptions.DisableNativeTalk());
        yield return () => robot.ApplyDamage(1, default, "test");
        yield return () => robot.Kill(default, "test");
        yield return () => robot.Ragdoll();
        yield return () => robot.Knockback(Vec3.Zero);
        yield return () => robot.Despawn();
        yield return () => robot.SayAsync("Hello", CancellationToken.None).GetAwaiter().GetResult();
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Robot interaction regression: " + message);
    }
}
""";
    }
}
