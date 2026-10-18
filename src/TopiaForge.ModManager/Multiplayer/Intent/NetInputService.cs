using System;
using System.Collections.Generic;
using System.Threading;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Whether any of one mod's input actions has an edge or is held, as the host's last input sample left them: the test
    /// for an input-active frame (WS3.4). Every context of the mod shares it, so an action a session scope registers counts
    /// as the package's own do. Only actions registered through the SDK's input service count. Main thread only.
    /// </summary>
    internal sealed class ModInputActivity
    {
        private readonly List<NetInputAction> actions = new List<NetInputAction>();

        /// <summary>Gets how many actions are tracked.</summary>
        internal int Count => actions.Count;

        /// <summary>Gets whether one of the actions was pressed or released this frame, or is held. It allocates nothing.</summary>
        internal bool IsActive
        {
            get
            {
                for (var index = 0; index < actions.Count; index++)
                {
                    if (actions[index].IsActive) return true;
                }

                return false;
            }
        }

        internal void Add(NetInputAction action) => actions.Add(action);

        internal void Remove(NetInputAction action) => actions.Remove(action);
    }

    /// <summary>
    /// The network seam's input service. It passes everything through to the service the gameplay host made, and keeps
    /// each action the mod registers in its mod's <see cref="ModInputActivity"/> until the action ends, with the mod's
    /// lifetime or when the mod disposes it, so the runtime can tell an input-active frame. The mod gets a handle that
    /// reads and rebinds exactly as the host's action does.
    /// </summary>
    internal sealed class NetInputService : IInputService
    {
        private readonly IInputService inner;
        private readonly ModInputActivity activity;
        private readonly IModLifetime lifetime;

        internal NetInputService(IInputService inner, ModInputActivity activity, IModLifetime lifetime)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            this.activity = activity ?? throw new ArgumentNullException(nameof(activity));
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        }

        /// <summary>Gets the service the gameplay host made.</summary>
        internal IInputService Inner => inner;

        public bool IsUiFocused => inner.IsUiFocused;

        public IReadOnlyList<InputConflict> GetConflicts() => inner.GetConflicts();

        public OperationResult<IInputAction> RegisterAction(InputActionDefinition definition)
        {
            var registered = inner.RegisterAction(definition);
            if (!registered.TryGetValue(out var action)) return registered;
            var tracked = new NetInputAction(action, activity);
            try
            {
                tracked.AttachLease(lifetime.Track(tracked));
            }
            catch (ObjectDisposedException)
            {
                // The lifetime began to stop in between and ended the action with it, as the host's own registration does.
                tracked.Dispose();
                return OperationResult<IInputAction>.Failure(
                    ModErrorCode.Cancelled,
                    "The mod stopped before its input action could be registered.");
            }

            activity.Add(tracked);
            return OperationResult<IInputAction>.Success(tracked);
        }
    }

    /// <summary>
    /// One action as its mod sees it: every member is the host's action's own. Disposing it, or the end of its context's
    /// lifetime, ends the host's action and stops counting it.
    /// </summary>
    internal sealed class NetInputAction : IInputAction
    {
        private readonly IInputAction inner;
        private readonly ModInputActivity activity;
        private IDisposable? lease;
        private int disposed;

        internal NetInputAction(IInputAction inner, ModInputActivity activity)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            this.activity = activity ?? throw new ArgumentNullException(nameof(activity));
        }

        /// <summary>Gets the host's action.</summary>
        internal IInputAction Inner => inner;

        /// <summary>Gets whether the action has an edge this frame or is held; one that cannot be read counts as idle.</summary>
        internal bool IsActive
        {
            get
            {
                try
                {
                    return inner.IsHeld || inner.WasPressed || inner.WasReleased;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public string Name => inner.Name;

        public IReadOnlyList<InputBinding> Bindings => inner.Bindings;

        public float Value => inner.Value;

        public bool IsHeld => inner.IsHeld;

        public bool WasPressed => inner.WasPressed;

        public bool WasReleased => inner.WasReleased;

        public OperationResult<bool> Rebind(IEnumerable<InputBinding> bindings) => inner.Rebind(bindings);

        public OperationResult<bool> ResetBindings() => inner.ResetBindings();

        internal void AttachLease(IDisposable value)
        {
            lease = value;
            if (Volatile.Read(ref disposed) != 0) Interlocked.Exchange(ref lease, null)?.Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            activity.Remove(this);
            try
            {
                inner.Dispose();
            }
            finally
            {
                Interlocked.Exchange(ref lease, null)?.Dispose();
            }
        }
    }
}
