using System;

namespace TopiaForge.Mods
{
    /// <summary>Controls whether a command may run optimistically for its owning participant.</summary>
    public enum PredictionMode
    {
        /// <summary>The command runs only after canonical server processing.</summary>
        None = 0,

        /// <summary>The owning client may predict reversible replicated-state changes.</summary>
        Owner = 1
    }

    /// <summary>Marks one partial mod class for generated multiplayer registration.</summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class MultiplayerContractAttribute : Attribute
    {
        /// <summary>Creates a generated multiplayer contract marker.</summary>
        /// <param name="additionalCodecTypes">
        /// Concrete DTO types that are used only by replicated objects and therefore cannot be discovered from a
        /// replicated-state, command, or presentation-event declaration.
        /// </param>
        public MultiplayerContractAttribute(params Type[] additionalCodecTypes)
        {
            AdditionalCodecTypes = additionalCodecTypes ?? throw new ArgumentNullException(nameof(additionalCodecTypes));
        }

        /// <summary>Gets explicitly anchored DTO types that also receive generated bounded codecs.</summary>
        public Type[] AdditionalCodecTypes { get; }

        /// <summary>Gets or sets the required stable contract id. Renaming a class or namespace never changes it.</summary>
        public string Id { get; set; } = string.Empty;
    }

    /// <summary>Marks one <see cref="ReplicatedState{T}"/> field for generated snapshot registration.</summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
    public sealed class ReplicatedStateAttribute : Attribute
    {
        /// <summary>Creates replicated-state metadata with a stable mod-local id.</summary>
        public ReplicatedStateAttribute(string id)
        {
            Id = MultiplayerIdentityValidation.Require(id, nameof(id));
        }

        /// <summary>Gets the stable mod-local state id.</summary>
        public string Id { get; }
    }

    /// <summary>Marks a canonical replicated-object input handler for generated type registration and typed proxies.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class ReplicatedObjectAttribute : Attribute
    {
        /// <summary>Creates replicated-object metadata with a stable mod-local type id.</summary>
        public ReplicatedObjectAttribute(string typeId)
        {
            Id = MultiplayerIdentityValidation.Require(typeId, nameof(typeId));
        }

        /// <summary>Gets the stable mod-local replicated-object type id.</summary>
        public string Id { get; }

        /// <summary>Gets or sets whether the owning client may predict object inputs.</summary>
        public PredictionMode Prediction { get; set; }

        /// <summary>Gets or sets the per-sender object-input rate limit.</summary>
        public int MaximumPerSecond { get; set; } = 30;

        /// <summary>Gets or sets the encoded state or input payload limit.</summary>
        public int MaximumPayloadBytes { get; set; } = 16 * 1024;
    }

    /// <summary>Marks a transactional command handler for generated submission and registration.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class MultiplayerCommandAttribute : Attribute
    {
        /// <summary>Creates command metadata with a stable mod-local id.</summary>
        public MultiplayerCommandAttribute(string id)
        {
            Id = MultiplayerIdentityValidation.Require(id, nameof(id));
        }

        /// <summary>Gets the stable mod-local command id.</summary>
        public string Id { get; }

        /// <summary>Gets or sets prediction behavior.</summary>
        public PredictionMode Prediction { get; set; }

        /// <summary>Gets or sets the per-sender command rate limit.</summary>
        public int MaximumPerSecond { get; set; } = 30;

        /// <summary>Gets or sets the encoded payload limit.</summary>
        public int MaximumPayloadBytes { get; set; } = 16 * 1024;
    }

    /// <summary>Marks a local handler for a canonical transient presentation event.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class PresentationEventAttribute : Attribute
    {
        /// <summary>Creates presentation-event metadata with a stable mod-local id.</summary>
        public PresentationEventAttribute(string id)
        {
            Id = MultiplayerIdentityValidation.Require(id, nameof(id));
        }

        /// <summary>Gets the stable mod-local event id.</summary>
        public string Id { get; }
    }

    /// <summary>Sets an encoded string or collection bound consumed by generated codecs.</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    public sealed class NetworkBoundAttribute : Attribute
    {
        /// <summary>Creates an encoded element or character bound.</summary>
        public NetworkBoundAttribute(int maximum)
        {
            if (maximum < 1 || maximum > 65536) throw new ArgumentOutOfRangeException(nameof(maximum));
            Maximum = maximum;
        }

        /// <summary>Gets the maximum encoded characters or collection elements.</summary>
        public int Maximum { get; }
    }
}
