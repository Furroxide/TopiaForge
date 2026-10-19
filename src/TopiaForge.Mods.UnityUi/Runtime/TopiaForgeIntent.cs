using System;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// What the kit needs of the runtime's intent scope (WS3.4). The loader installs one; a process without it, such as a
    /// test, opens no scope at all. A sink must not throw for control flow: the kit drops a failing sink's scope, never the
    /// player's click.
    /// </summary>
    internal interface ITopiaForgeIntentSink
    {
        /// <summary>Opens the scope of the player's own action on a control <paramref name="ownerId"/> owns; returns what <see cref="End"/> restores.</summary>
        object? BeginPlayerAction(string ownerId);

        /// <summary>Captures the intent the calling code acts on, for a callback the kit defers.</summary>
        object? Capture();

        /// <summary>Enters what <see cref="Capture"/> returned; returns what <see cref="End"/> restores.</summary>
        object? Resume(object? captured);

        /// <summary>Restores what <see cref="BeginPlayerAction"/> or <see cref="Resume"/> returned.</summary>
        void End(object? restore);
    }

    /// <summary>
    /// The kit's side of intent tracking. A player's own input on a control (a click, a confirm, a value chosen, typed,
    /// dragged or submitted, a selection, a close, a hotkey) runs inside the scope of that player's action, named after the
    /// control's owner, so the mod's callback, and the SDK UI service's callbacks that run inside it, act on the player's
    /// intent. What only happens to a control, and anything code does to one, opens nothing. A callback the kit defers,
    /// such as a close animation's completion, runs with the intent captured where the animation began. Unity-free.
    /// </summary>
    internal static class TopiaForgeIntent
    {
        private static ITopiaForgeIntentSink? sink;

        /// <summary>Gets whether a sink is installed.</summary>
        internal static bool IsInstalled => sink != null;

        /// <summary>Gets how many times a sink threw and lost its scope.</summary>
        internal static int SinkFailures { get; private set; }

        /// <summary>Installs the runtime's sink; the latest one wins.</summary>
        internal static void Install(ITopiaForgeIntentSink value)
        {
            sink = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>Removes <paramref name="value"/> if it is still the installed sink.</summary>
        internal static void Uninstall(ITopiaForgeIntentSink value)
        {
            if (ReferenceEquals(sink, value)) sink = null;
        }

        internal static void Reset()
        {
            sink = null;
            SinkFailures = 0;
        }

        /// <summary>Opens the scope of the player's own action on a control <paramref name="ownerId"/> owns, until the scope is disposed.</summary>
        internal static Scope PlayerAction(string? ownerId)
        {
            var current = sink;
            if (current == null || string.IsNullOrEmpty(ownerId)) return default;
            try
            {
                return new Scope(current, current.BeginPlayerAction(ownerId!));
            }
            catch (Exception)
            {
                SinkFailures++;
                return default;
            }
        }

        /// <summary>Captures the intent the calling code acts on, for a callback the kit defers; null when there is none.</summary>
        internal static object? Capture()
        {
            var current = sink;
            if (current == null) return null;
            try
            {
                return current.Capture();
            }
            catch (Exception)
            {
                SinkFailures++;
                return null;
            }
        }

        /// <summary>Re-enters what <see cref="Capture"/> returned, until the scope is disposed.</summary>
        internal static Scope Resume(object? captured)
        {
            var current = sink;
            if (current == null) return default;
            try
            {
                return new Scope(current, current.Resume(captured));
            }
            catch (Exception)
            {
                SinkFailures++;
                return default;
            }
        }

        /// <summary>One open scope; disposing it restores what flowed before. The default scope restores nothing.</summary>
        internal readonly struct Scope : IDisposable
        {
            private readonly ITopiaForgeIntentSink? owner;
            private readonly object? restore;

            internal Scope(ITopiaForgeIntentSink owner, object? restore)
            {
                this.owner = owner;
                this.restore = restore;
            }

            public void Dispose()
            {
                if (owner == null || restore == null) return;
                try
                {
                    owner.End(restore);
                }
                catch (Exception)
                {
                    SinkFailures++;
                }
            }
        }
    }
}
