using System;

namespace TopiaForge.Mods.Internal
{
    // Opt-in for trusted providers whose factory serves consuming mods' exact contexts.
    internal interface IInternalSharedSpawnHandler : IInternalSpawnHandler { }

    internal interface IInternalEntityCommands
    {
        // Runtime-derived current caller, across provider/consumer identities. Never supplied by a payload.
        // Includes guest-local calls, forwarded participant callbacks and intent-free remote spawns.
        bool IsGuestOrigin { get; }
        OperationResult<bool> SubmitEntityCommand(IEntity target, string kind, byte[] payload);
    }

    // Optional notification when a stream retires queued/in-flight commands but preserves a predicted body.
    internal interface IInternalReplicaCommandLifetime
    {
        void ResetPendingCommands(IEntity target);
    }

    // A provider owns the payload schema. The runtime authenticates the target and consuming mod,
    // applies per-participant budgets, and preserves the original record's lifetime and ownership.
    internal interface IInternalReplicaCommands : IInternalReplicaFactory
    {
        OperationResult<bool> Execute(IEntity target, ReadOnlyMemory<byte> payload,
            Func<InternalNetRef, IEntity?> resolve);
        InternalSpawnDescriptor Describe(IEntity target);
        OperationResult<bool> ApplyReplica(IEntity target, InternalSpawnDescriptor descriptor);
    }
}
