using System;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods.Internal
{
    internal interface IInternalWorldSessionContext
    {
        IWorldSessionService Sessions { get; }
        IInternalWorldSessionOperations ContentOperations { get; }
    }

    internal interface IInternalWorldSessionOperations
    {
        Task<OperationResult<bool>> RunContentOperationAsync(string sessionId,
            Func<IInternalSessionContentContext, CancellationToken, Task<OperationResult<IDisposable>>> operation,
            CancellationToken cancellationToken = default);
    }

    internal interface IInternalSessionContentContext
    {
        string SessionId { get; }
        WorldReadiness World { get; }
        IModContext Context { get; }
    }
}
