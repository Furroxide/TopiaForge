using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal interface IRuntimeSessionShutdown
    {
        Task<OperationResult<bool>> StopOwnerAsync(string packageId);
        Task<OperationResult<bool>> ShutdownAsync();
    }
}
