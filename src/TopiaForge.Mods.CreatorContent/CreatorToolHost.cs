using System;

namespace TopiaForge.Mods
{
    /// <summary>Explains why the shared creator workbench is closing.</summary>
    public enum CreatorToolCloseReason
    {
        /// <summary>The user pressed the shared toggle again.</summary>
        UserToggle = 0,
        /// <summary>The active world or scene was replaced.</summary>
        SceneChanged = 1,
        /// <summary>The host registration or its owning mod stopped.</summary>
        HostUnavailable = 2,
        /// <summary>The Creator Content provider is stopping.</summary>
        ProviderStopping = 3,
        /// <summary>A caller explicitly requested closure.</summary>
        Requested = 4
    }

    /// <summary>Immutable context supplied when routing the shared creator hotkey.</summary>
    public sealed class CreatorToolOpenContext
    {
        /// <summary>Creates tool-open context.</summary>
        public CreatorToolOpenContext(string activeSceneName)
        {
            ActiveSceneName = activeSceneName ?? string.Empty;
        }

        /// <summary>Gets the active scene name, or an empty string when unavailable.</summary>
        public string ActiveSceneName { get; }
    }

    /// <summary>Immutable metadata for a registered creator workbench host.</summary>
    public sealed class CreatorToolHostDescriptor
    {
        /// <summary>Creates host metadata.</summary>
        public CreatorToolHostDescriptor(
            string hostId,
            string sourceId,
            string localId,
            string displayName,
            int priority)
        {
            if (string.IsNullOrWhiteSpace(hostId)) throw new ArgumentException("A host id is required.", nameof(hostId));
            if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("A source id is required.", nameof(sourceId));
            if (string.IsNullOrWhiteSpace(localId)) throw new ArgumentException("A local id is required.", nameof(localId));
            if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("A display name is required.", nameof(displayName));
            HostId = hostId;
            SourceId = sourceId;
            LocalId = localId;
            DisplayName = displayName;
            Priority = priority;
        }

        /// <summary>Gets the stable source-qualified host id.</summary>
        public string HostId { get; }
        /// <summary>Gets the authenticated source mod id.</summary>
        public string SourceId { get; }
        /// <summary>Gets the stable id inside the source mod.</summary>
        public string LocalId { get; }
        /// <summary>Gets the user-facing host name.</summary>
        public string DisplayName { get; }
        /// <summary>Gets deterministic selection priority.</summary>
        public int Priority { get; }
    }

    /// <summary>Workbench shell invoked by the provider's single shared F5 router.</summary>
    public interface ICreatorToolHost
    {
        /// <summary>Gets whether this shell currently owns the open workbench.</summary>
        bool IsOpen { get; }
        /// <summary>Returns whether the shell can serve the current scene and mode.</summary>
        bool CanOpen(CreatorToolOpenContext context);
        /// <summary>Opens the workbench.</summary>
        OperationResult<bool> Open(CreatorToolOpenContext context);
        /// <summary>
        /// Hides the workbench. User-toggle and requested closure retain its creator session; lifecycle closure
        /// reasons release and roll back that session.
        /// </summary>
        OperationResult<bool> Close(CreatorToolCloseReason reason);
    }

    /// <summary>Describes one authenticated workbench-shell registration.</summary>
    public sealed class CreatorToolHostRegistrationRequest
    {
        /// <summary>Creates a host registration request.</summary>
        public CreatorToolHostRegistrationRequest(
            string localId,
            string displayName,
            int priority,
            ICreatorToolHost host,
            string toggleBinding = "")
        {
            if (string.IsNullOrWhiteSpace(localId)) throw new ArgumentException("A local id is required.", nameof(localId));
            if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("A display name is required.", nameof(displayName));
            if (priority < -1000 || priority > 1000) throw new ArgumentOutOfRangeException(nameof(priority));
            LocalId = localId;
            DisplayName = displayName;
            Priority = priority;
            Host = host ?? throw new ArgumentNullException(nameof(host));
            ToggleBinding = toggleBinding ?? string.Empty;
        }

        /// <summary>Gets the stable id inside the registering mod.</summary>
        public string LocalId { get; }
        /// <summary>Gets the user-facing shell name.</summary>
        public string DisplayName { get; }
        /// <summary>Gets deterministic selection priority.</summary>
        public int Priority { get; }
        /// <summary>Gets the shell callback.</summary>
        public ICreatorToolHost Host { get; }
        /// <summary>
        /// Gets an optional legacy/custom key merged into the provider-owned toggle action. Hosts must not
        /// register a second action for this binding.
        /// </summary>
        public string ToggleBinding { get; }
    }

    /// <summary>Lifetime handle for one creator workbench host.</summary>
    public interface ICreatorToolHostRegistration : IDisposable
    {
        /// <summary>Gets registered host metadata.</summary>
        CreatorToolHostDescriptor Descriptor { get; }
        /// <summary>Gets whether the host remains registered.</summary>
        bool IsAlive { get; }
    }

    /// <summary>Owns the process-wide shared creator hotkey and routes it to one prioritized registered shell.</summary>
    public interface ICreatorToolHostService
    {
        /// <summary>Gets the active host, or <see langword="null"/> while closed.</summary>
        CreatorToolHostDescriptor? ActiveHost { get; }
        /// <summary>Registers a shell attributed to the authenticated calling mod.</summary>
        OperationResult<ICreatorToolHostRegistration> RegisterHost(CreatorToolHostRegistrationRequest request);
        /// <summary>Toggles the active or highest-priority available host.</summary>
        OperationResult<bool> Toggle();
        /// <summary>Closes the active host when one exists.</summary>
        OperationResult<bool> CloseActive(CreatorToolCloseReason reason = CreatorToolCloseReason.Requested);
    }

    /// <summary>Compatibility alias for the original creator tool-host service name.</summary>
    public interface ICreatorToolHostRouter : ICreatorToolHostService
    {
    }
}
