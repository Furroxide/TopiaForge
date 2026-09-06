using System;
using System.Threading.Tasks;

namespace TopiaForge.ModManager
{
    internal sealed class LegacyLaunchDiscovery
    {
        private readonly Lazy<Task> attempt;
        internal LegacyLaunchDiscovery(Func<Task> discover)
        {
            if (discover == null) throw new ArgumentNullException(nameof(discover));
            attempt = new Lazy<Task>(discover);
        }
        internal Task Start() => attempt.Value;
        internal async Task AfterAsync(Func<Task> launch)
        {
            if (launch == null) throw new ArgumentNullException(nameof(launch));
            await Start();
            await launch();
        }
    }
}
