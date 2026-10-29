using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.RobotKit;

namespace TopiaForge.ModManager.Tests
{
    internal static class FirstPartyBrainAuthorityTests
    {
        internal static void Run()
        {
            using var lifetime = new FakeModLifetime();
            var inner = new CountingBrain();
            var authoritative = false;
            var facade = new OwnerRobotBrainQueries(inner, lifetime, () => authoritative);
            var request = new BrainQueryRequest("Choose a direction", Array.Empty<BrainOutputField>());
            if (facade.IsAvailable || facade.QueryAsync(request).Result.ErrorCode != ModErrorCode.NotAuthoritative || inner.Calls != 0)
                throw new Exception("A guest must be refused before touching the brain backend or credential availability.");
            authoritative = true;
            if (!facade.IsAvailable || facade.QueryAsync(request).Result.ErrorCode != ModErrorCode.Unavailable || inner.Calls != 2)
                throw new Exception("The current host must retain local credential behavior.");
            authoritative = false;
            inner.QueryAsync(request).GetAwaiter().GetResult();
            if (inner.Calls != 3) throw new Exception("Local dialogue must retain the local service path.");
            Console.WriteLine("First-party brain authority: 1 group passed.");
        }

        private sealed class CountingBrain : IRobotBrainQueryService
        {
            internal int Calls;
            public bool IsAvailable { get { Calls++; return true; } }
            public Task<OperationResult<BrainQueryResult>> QueryAsync(BrainQueryRequest request, CancellationToken cancellationToken = default)
            {
                Calls++;
                return Task.FromResult(OperationResult<BrainQueryResult>.Failure(ModErrorCode.Unavailable, "No local backend."));
            }
        }
    }
}
