using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace TopiaForge.Mods
{
    /// <summary>Classifies content shown by creator tools.</summary>
    public enum CreatorContentKind
    {
        /// <summary>Content without a more specific classification.</summary>
        Other = 0,
        /// <summary>A robot controlled through RobotKit or a custom content adapter.</summary>
        Robot = 1,
        /// <summary>A non-robot character or event actor.</summary>
        Character = 2,
        /// <summary>An inventory or world item.</summary>
        Item = 3,
        /// <summary>A static or interactive scene prop.</summary>
        Prop = 4,
        /// <summary>A vehicle supplied by a validated adapter or custom mod.</summary>
        Vehicle = 5
    }

    /// <summary>Identifies transform components that a spawned instance permits changing.</summary>
    [Flags]
    public enum CreatorTransformCapabilities
    {
        /// <summary>No post-spawn transform edits are supported.</summary>
        None = 0,
        /// <summary>World position can be changed.</summary>
        Position = 1,
        /// <summary>World rotation can be changed.</summary>
        Rotation = 2,
        /// <summary>Local scale can be changed.</summary>
        Scale = 4,
        /// <summary>Every transform component can be changed.</summary>
        All = Position | Rotation | Scale
    }

    /// <summary>Reports whether one catalog source is currently usable.</summary>
    public enum CreatorCatalogSourceState
    {
        /// <summary>The source is usable.</summary>
        Ready = 0,
        /// <summary>The source is partially usable.</summary>
        Degraded = 1,
        /// <summary>The source cannot currently provide content.</summary>
        Unavailable = 2
    }

    /// <summary>Immutable metadata for one spawnable catalog entry.</summary>
    public sealed class CreatorContentDescriptor
    {
        /// <summary>Creates catalog metadata.</summary>
        public CreatorContentDescriptor(
            string contentId,
            string sourceId,
            string sourceVersion,
            string localId,
            string displayName,
            string description,
            CreatorContentKind kind,
            CreatorTransformCapabilities transformCapabilities)
        {
            ContentId = Require(contentId, nameof(contentId));
            SourceId = Require(sourceId, nameof(sourceId));
            SourceVersion = sourceVersion ?? string.Empty;
            LocalId = Require(localId, nameof(localId));
            DisplayName = Require(displayName, nameof(displayName));
            Description = description ?? string.Empty;
            if (!Enum.IsDefined(typeof(CreatorContentKind), kind))
            {
                throw new ArgumentOutOfRangeException(nameof(kind));
            }
            if ((transformCapabilities & ~CreatorTransformCapabilities.All) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(transformCapabilities));
            }
            Kind = kind;
            TransformCapabilities = transformCapabilities;
        }

        /// <summary>Gets the stable source-qualified content id.</summary>
        public string ContentId { get; }
        /// <summary>Gets the authenticated provider mod id.</summary>
        public string SourceId { get; }
        /// <summary>Gets the source version used for diagnostics and project compatibility warnings.</summary>
        public string SourceVersion { get; }
        /// <summary>Gets the stable id inside the source mod.</summary>
        public string LocalId { get; }
        /// <summary>Gets the user-facing content name.</summary>
        public string DisplayName { get; }
        /// <summary>Gets the optional user-facing description.</summary>
        public string Description { get; }
        /// <summary>Gets the content classification.</summary>
        public CreatorContentKind Kind { get; }
        /// <summary>Gets supported post-spawn transform edits.</summary>
        public CreatorTransformCapabilities TransformCapabilities { get; }

        private static string Require(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A value is required.", name);
            return value;
        }
    }

    /// <summary>Describes one catalog source and any compatibility limitation.</summary>
    public sealed class CreatorCatalogSourceStatus
    {
        /// <summary>Creates source status information.</summary>
        public CreatorCatalogSourceStatus(
            string sourceId,
            string displayName,
            CreatorCatalogSourceState state,
            string message,
            int entryCount)
        {
            if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("A source id is required.", nameof(sourceId));
            if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("A display name is required.", nameof(displayName));
            if (!Enum.IsDefined(typeof(CreatorCatalogSourceState), state)) throw new ArgumentOutOfRangeException(nameof(state));
            if (entryCount < 0) throw new ArgumentOutOfRangeException(nameof(entryCount));
            SourceId = sourceId;
            DisplayName = displayName;
            State = state;
            Message = message ?? string.Empty;
            EntryCount = entryCount;
        }

        /// <summary>Gets the stable source id.</summary>
        public string SourceId { get; }
        /// <summary>Gets the user-facing source name.</summary>
        public string DisplayName { get; }
        /// <summary>Gets the availability state.</summary>
        public CreatorCatalogSourceState State { get; }
        /// <summary>Gets an optional explanation or remediation.</summary>
        public string Message { get; }
        /// <summary>Gets the number of entries supplied by this source.</summary>
        public int EntryCount { get; }
    }

    /// <summary>Immutable revisioned view of the creator catalog.</summary>
    public sealed class CreatorCatalogSnapshot
    {
        private readonly IReadOnlyList<CreatorContentDescriptor> entries;
        private readonly IReadOnlyList<CreatorCatalogSourceStatus> sources;

        /// <summary>Creates a catalog snapshot.</summary>
        public CreatorCatalogSnapshot(
            long revision,
            IEnumerable<CreatorContentDescriptor> entries,
            IEnumerable<CreatorCatalogSourceStatus> sources)
        {
            if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
            Revision = revision;
            this.entries = new ReadOnlyCollection<CreatorContentDescriptor>((entries ?? throw new ArgumentNullException(nameof(entries))).ToList());
            this.sources = new ReadOnlyCollection<CreatorCatalogSourceStatus>((sources ?? throw new ArgumentNullException(nameof(sources))).ToList());
        }

        /// <summary>Gets the revision, incremented only when catalog contents change.</summary>
        public long Revision { get; }
        /// <summary>Gets deterministically ordered spawnable entries.</summary>
        public IReadOnlyList<CreatorContentDescriptor> Entries => entries;
        /// <summary>Gets availability information for built-in and custom sources.</summary>
        public IReadOnlyList<CreatorCatalogSourceStatus> Sources => sources;
    }

    /// <summary>Creates source-owned instances for one custom catalog entry.</summary>
    public interface ICreatorContentFactory
    {
        /// <summary>Creates a source-owned instance at an initial transform.</summary>
        OperationResult<ICreatorSourceInstance> Spawn(TransformState transform);
    }

    /// <summary>Source-owned adapter returned by a creator content factory.</summary>
    public interface ICreatorSourceInstance : IDisposable
    {
        /// <summary>Gets the safe entity handle.</summary>
        IEntity Entity { get; }
        /// <summary>Gets whether the source object remains usable.</summary>
        bool IsAlive { get; }
        /// <summary>Tries to read its complete transform.</summary>
        bool TryGetTransform(out TransformState transform);
        /// <summary>Applies a transform through the source owner's safe services.</summary>
        OperationResult<TransformState> SetTransform(TransformState transform);
    }

    /// <summary>Describes one owner-authenticated custom content registration.</summary>
    public sealed class CreatorContentRegistrationRequest
    {
        /// <summary>Creates a custom content registration request.</summary>
        public CreatorContentRegistrationRequest(
            string localId,
            string displayName,
            string description,
            CreatorContentKind kind,
            CreatorTransformCapabilities transformCapabilities,
            ICreatorContentFactory factory)
        {
            if (string.IsNullOrWhiteSpace(localId)) throw new ArgumentException("A local id is required.", nameof(localId));
            if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("A display name is required.", nameof(displayName));
            if (!Enum.IsDefined(typeof(CreatorContentKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            if ((transformCapabilities & ~CreatorTransformCapabilities.All) != 0) throw new ArgumentOutOfRangeException(nameof(transformCapabilities));
            LocalId = localId;
            DisplayName = displayName;
            Description = description ?? string.Empty;
            Kind = kind;
            TransformCapabilities = transformCapabilities;
            Factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        /// <summary>Gets the stable id inside the registering mod.</summary>
        public string LocalId { get; }
        /// <summary>Gets the user-facing content name.</summary>
        public string DisplayName { get; }
        /// <summary>Gets the optional description.</summary>
        public string Description { get; }
        /// <summary>Gets the content classification.</summary>
        public CreatorContentKind Kind { get; }
        /// <summary>Gets supported post-spawn transform edits.</summary>
        public CreatorTransformCapabilities TransformCapabilities { get; }
        /// <summary>Gets the source-owned factory.</summary>
        public ICreatorContentFactory Factory { get; }
    }

    /// <summary>Lifetime handle for a custom content registration.</summary>
    public interface ICreatorContentRegistration : IDisposable
    {
        /// <summary>Gets the registered descriptor.</summary>
        CreatorContentDescriptor Descriptor { get; }
        /// <summary>Gets whether the entry remains registered.</summary>
        bool IsAlive { get; }
    }
}
