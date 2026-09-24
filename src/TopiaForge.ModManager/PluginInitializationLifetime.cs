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

        internal bool ProvisioningObservationRecorded { get; private set; }
        private bool provisioningQuitRequested;

        internal bool ProvisioningQuitPending => ProvisioningObservationRecorded && !provisioningQuitRequested;

        internal bool TryRequestProvisioningQuit(Action requestQuit)
        {
            if (requestQuit == null) throw new ArgumentNullException(nameof(requestQuit));
            if (!ProvisioningQuitPending) return false;
            // Claim before dispatch: reentrancy and a throwing/cancelled request cannot become a retry loop.
            provisioningQuitRequested = true;
            requestQuit();
            return true;
        }

        internal void ObserveProvisioning(Action observe)
        {
            if (observe == null) throw new ArgumentNullException(nameof(observe));
            if (startAttempted) throw new InvalidOperationException("Plugin initialization can only be attempted once.");
            startAttempted = true;
            // Success and failure both remain unadmitted and own no UI/storage teardown.
            observe();
            ProvisioningObservationRecorded = true;
        }

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
