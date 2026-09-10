using System;
using System.Threading.Tasks;

namespace TopiaForge.ModManager
{
    internal sealed class LaunchDiscoveryGate
    {
        private readonly IHostDispatcher dispatcher;
        private readonly Lazy<Task> attempt;
        private object generation = new object();

        internal LaunchDiscoveryGate(IHostDispatcher dispatcher, Func<Task> discover)
        {
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            if (discover == null) throw new ArgumentNullException(nameof(discover));
            attempt = new Lazy<Task>(() => dispatcher.InvokeAsync(discover).Unwrap());
        }

        internal Task Start() => attempt.Value;

        internal Task<T> ExplicitAsync<T>(Func<Task<T>> command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            return dispatcher.InvokeAsync(() =>
            {
                generation = new object();
                return command();
            }).Unwrap();
        }

        internal Task AfterAsync(Func<Task> launch, bool requireDiscovery = true)
            => AfterAsync(_ => launch(), requireDiscovery);

        internal async Task AfterAsync(Func<Func<bool>, Task> launch, bool requireDiscovery = true)
        {
            if (launch == null) throw new ArgumentNullException(nameof(launch));
            var captured = await dispatcher.InvokeAsync(() => generation).ConfigureAwait(false);
            if (requireDiscovery) await Start().ConfigureAwait(false);
            await dispatcher.InvokeAsync(() =>
                ReferenceEquals(captured, generation) ? launch(() => ReferenceEquals(captured, generation)) : Task.CompletedTask).Unwrap().ConfigureAwait(false);
        }
    }
}
