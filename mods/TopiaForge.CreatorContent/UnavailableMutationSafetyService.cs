using System;
using TopiaForge.Mods;

namespace TopiaForge.CreatorContent
{
    internal sealed class UnavailableMutationSafetyService : ICreatorMutationSafetyService
    {
        private static readonly CreatorMutationSafetySnapshot Unavailable = new CreatorMutationSafetySnapshot(
            CreatorMutationSafetyState.Unavailable,
            persistenceIsolationAvailable: false,
            "Global mutation is disabled because no validated persistence-isolation bridge is installed.");

        public CreatorMutationSafetySnapshot Status => Unavailable;

        public OperationResult<ICreatorMutationLease> Acquire(CreatorMutationLeaseRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.Purpose.Length > 128)
            {
                return OperationResult<ICreatorMutationLease>.Failure(ModErrorCode.InvalidArgument, "Mutation purpose exceeds 128 characters.");
            }
            if (!request.UserAcknowledgedTemporaryChanges)
            {
                return OperationResult<ICreatorMutationLease>.Failure(
                    ModErrorCode.Conflict,
                    "Explicit one-time acknowledgement is required before global mutation.");
            }
            return OperationResult<ICreatorMutationLease>.Failure(ModErrorCode.Unavailable, Unavailable.Message);
        }
    }
}
