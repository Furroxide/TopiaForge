using System;
using System.Threading;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>Author-facing lifetime: disposal requests a session stop, never destroys a scope.</summary>
    internal sealed class ScopedModLifetime : IModLifetime
    {
        private readonly ModContextScope scope;
        internal ScopedModLifetime(ModContextScope scope) { this.scope = scope; }
        public CancellationToken StoppingToken => scope.OwnerLifetime.StoppingToken;
        public bool IsStopping => scope.OwnerLifetime.IsStopping;
        public IDisposable Track(IDisposable resource)
        {
            if (ReferenceEquals(resource, this)) throw new ArgumentException("A lifetime cannot track itself.", nameof(resource));
            return scope.Track(resource);
        }
        public IDisposable Defer(Action cleanup)
        {
            if (cleanup == null) throw new ArgumentNullException(nameof(cleanup));
            return Track(new Deferred(cleanup));
        }
        public void Dispose() => scope.RequestSessionStop();
        internal void DisposeResource(IDisposable resource) => scope.DisposeResource(resource);
        private sealed class Deferred : IDisposable
        {
            private Action? action;
            internal Deferred(Action action) { this.action = action; }
            public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
        }
    }
}
