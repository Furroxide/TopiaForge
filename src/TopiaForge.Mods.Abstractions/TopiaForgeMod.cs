using System;

namespace TopiaForge.Mods
{
    /// <summary>
    /// Base class for a TopiaForge mod. Derive from this class and implement <see cref="OnLoad"/>; the loader
    /// attaches <see cref="Context"/> before invoking lifecycle callbacks.
    /// </summary>
    public abstract class TopiaForgeMod
    {
        private IModContext? context;

        /// <summary>
        /// Gets the owner-scoped SDK context while the mod is loading, loaded, or unloading.
        /// </summary>
        /// <exception cref="InvalidOperationException">The mod is outside its managed lifecycle.</exception>
        protected IModContext Context => context ?? throw new InvalidOperationException(
            "The mod context is available only while TopiaForge is loading, running, or unloading this mod.");

        /// <summary>Initializes the mod after its context and dependencies are available.</summary>
        protected abstract void OnLoad();

        /// <summary>
        /// Gives the mod a best-effort opportunity to release untracked resources. SDK resources registered with
        /// <see cref="IModContext.Lifetime"/> are released automatically after this callback, even when it throws.
        /// </summary>
        protected virtual void OnUnload()
        {
        }

        internal void Load(IModContext value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            if (context != null)
            {
                throw new InvalidOperationException("This mod instance is already attached to a context.");
            }

            context = value;
            OnLoad();
        }

        internal void Unload()
        {
            if (context == null)
            {
                return;
            }

            try
            {
                OnUnload();
            }
            finally
            {
                context = null;
            }
        }
    }
}
