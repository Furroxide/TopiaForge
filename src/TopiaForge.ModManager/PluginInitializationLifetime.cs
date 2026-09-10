using System;

namespace TopiaForge.ModManager
{
    /// <summary>Owns initialization effects only after admission and UI cleanup before the first UI-producing callback.</summary>
    internal sealed class PluginInitializationLifetime
    {
        private bool startAttempted;
        private bool admitted;
        private bool teardownStarted;
        private bool ownsUi;

        internal void Start(Action admit, Action persist, Action initializeStorage)
        {
            if (admit == null) throw new ArgumentNullException(nameof(admit));
            if (persist == null) throw new ArgumentNullException(nameof(persist));
            if (initializeStorage == null) throw new ArgumentNullException(nameof(initializeStorage));
            if (startAttempted) throw new InvalidOperationException("Plugin initialization can only be attempted once.");
            startAttempted = true;
            admit();
            // A denial, including failed ACK publication, owns no normal plugin effects.
            admitted = true;
            persist();
            initializeStorage();
        }

        internal void InitializeUi(Action initialize)
        {
            if (initialize == null) throw new ArgumentNullException(nameof(initialize));
            if (!admitted || teardownStarted) throw new InvalidOperationException("The plugin does not own initialization.");
            // Constructors and mod callbacks can allocate UI before throwing. Readiness is
            // therefore not the condition for releasing this process-wide responsibility.
            ownsUi = true;
            initialize();
        }

        internal bool TryBeginTeardown()
        {
            if (!admitted || teardownStarted) return false;
            teardownStarted = true;
            return true;
        }

        internal void ShutdownUi(Action shutdown)
        {
            if (shutdown == null) throw new ArgumentNullException(nameof(shutdown));
            if (!teardownStarted || !ownsUi) return;
            ownsUi = false;
            shutdown();
        }
    }
}
