using TopiaForge.ModManager;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>Exercises the production command partial in this linked-source executable.</summary>
internal static class EntityCommandHarnessTests
{
    internal static void Run()
    {
        UnityMainThreadGuard.CaptureCurrentThread();
        using var rig = new ControlRig(3610, PackageSpec.Implicit("io.example.commands", "0.2.0", 'a'));
        var replication = new MultiplayerReplication(rig.Host.Composition);
        var scene = new FakeWorldScene();
        var yard = scene.AddScene("Yard");
        var providerLifetime = new FakeModLifetime();
        var factory = new Commands();
        var provider = replication.Bind("io.example.provider",
            new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Process), providerLifetime);
        That(provider.RegisterReplicaFactory(Commands.Kind, factory).Succeeded, "register command provider");

        foreach (var networkClass in new[] { ModNetworkClass.Standalone, ModNetworkClass.Shared })
        {
            var commandValue = networkClass == ModNetworkClass.Standalone ? (byte)7 : (byte)11;
            var beforeValue = factory.Value;
            That(beforeValue != commandValue, "each profile commands a new provider value");
            var lifetime = new FakeModLifetime();
            var profile = new ModNetProfile(networkClass, ModExecutionContext.Process);
            var context = replication.Bind("io.example.consumer", profile, lifetime);
            var sibling = replication.Bind("io.example.sibling", profile, new FakeModLifetime());
            var entity = scene.Add(yard, "Robot", null, true, false, new NetVector3(0, 0, 0)).Entity;
            var spawn = context.RequestSpawn(factory.Describe(entity), () => OperationResult<IEntity>.Success(entity));
            That(spawn.Succeeded, "spawn through consuming context");
            var record = rig.Host.Composition.Spawns.Find(entity);
            var beforePayload = record?.Description.Descriptor is InternalSpawnDescriptor before ? before.Payload.ToArray() : null;
            That(networkClass == ModNetworkClass.Standalone ? record == null :
                beforePayload is { Length: 1 } && beforePayload[0] == beforeValue && beforePayload[0] != commandValue,
                "the shared spawn records the old provider value before the command");
            var commands = (IInternalEntityCommands)context;
            var calls = factory.ExecuteCalls;
            That(commands.SubmitEntityCommand(entity, Commands.Kind, new byte[] { commandValue }).Succeeded &&
                factory.Value == commandValue && factory.Value != beforeValue && factory.ExecuteCalls == calls + 1,
                "the real production command partial invokes the provider");
            calls = factory.ExecuteCalls;
            record = rig.Host.Composition.Spawns.Find(entity);
            That(networkClass == ModNetworkClass.Standalone ? record == null :
                record?.Description.Descriptor is InternalSpawnDescriptor descriptor &&
                descriptor.Payload.Length == 1 && descriptor.Payload.Span[0] == commandValue &&
                beforePayload != null && !descriptor.Payload.Span.SequenceEqual(beforePayload),
                "local targets stay unrecorded and shared commands refresh the canonical descriptor");
            That(!((IInternalEntityCommands)sibling).SubmitEntityCommand(entity, Commands.Kind, new byte[] { 8 }).Succeeded &&
                factory.Value == commandValue && factory.ExecuteCalls == calls,
                "another consuming mod cannot command the target");
            That(!commands.SubmitEntityCommand(entity, "other.kind", new byte[] { 8 }).Succeeded &&
                factory.Value == commandValue && factory.ExecuteCalls == calls,
                "a wrong kind cannot reach the provider");
            That(!commands.SubmitEntityCommand(entity, Commands.Kind, new byte[] { 8, 9 }).Succeeded &&
                factory.Value == commandValue && factory.ExecuteCalls == calls + 1,
                "malformed provider payloads have no effects");
            calls = factory.ExecuteCalls;
            That(!commands.SubmitEntityCommand(entity, Commands.Kind, new byte[16 * 1024 + 1]).Succeeded &&
                factory.Value == commandValue && factory.ExecuteCalls == calls,
                "runtime payload bounds precede provider execution");
            lifetime.Dispose();
            That(!commands.SubmitEntityCommand(entity, Commands.Kind, new byte[] { 9 }).Succeeded &&
                factory.Value == commandValue && factory.ExecuteCalls == calls,
                "stopped consumers cannot execute commands");
        }

        providerLifetime.Dispose();
        Console.WriteLine("Entity command harness: 2 production command-path groups passed.");
    }

    private sealed class Commands : IInternalReplicaCommands
    {
        internal const string Kind = "test.robot";
        internal byte Value { get; private set; }
        internal int ExecuteCalls { get; private set; }

        public OperationResult<bool> Execute(IEntity target, ReadOnlyMemory<byte> payload, Func<InternalNetRef, IEntity?> resolve)
        {
            ExecuteCalls++;
            if (payload.Length != 1) return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "One byte required.");
            Value = payload.Span[0];
            return OperationResult<bool>.Success(true);
        }

        public InternalSpawnDescriptor Describe(IEntity target) => new(Kind,
            new TransformState(Vec3.Zero, Quat.Identity, new Vec3(1, 1, 1)), new byte[] { Value });

        public OperationResult<IEntity> CreateReplica(InternalSpawnDescriptor descriptor) =>
            OperationResult<IEntity>.Failure(ModErrorCode.Unavailable, "No replica needed by this probe.");

        public void ReleaseReplica(IEntity replica) { }

        public OperationResult<bool> ApplyReplica(IEntity target, InternalSpawnDescriptor descriptor) =>
            OperationResult<bool>.Failure(ModErrorCode.Unavailable, "No replica needed by this probe.");
    }
}
