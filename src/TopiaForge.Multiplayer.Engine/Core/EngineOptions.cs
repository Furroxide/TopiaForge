using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>Whether this engine holds world authority (a host) or follows one (a client).</summary>
    internal enum EngineRole
    {
        Host = 0,
        Client = 1
    }

    /// <summary>What the engine does when a mod callback throws.</summary>
    internal enum EngineCallbackFailureMode
    {
        /// <summary>Report the failure to diagnostics and keep running. Production providers use this.</summary>
        Report = 0,

        /// <summary>Rethrow out of the operation that dispatched the callback. The test rig uses this.</summary>
        Propagate = 1
    }

    /// <summary>Tuning and policy for one engine instance. Production defaults follow the transport budget record.</summary>
    internal sealed class EngineOptions
    {
        /// <summary>Gets or sets the canonical fixed-step rate: 20 Hz in production, the rig's tick rate in tests.</summary>
        internal int StepRateHz { get; set; } = 20;

        /// <summary>Gets or sets how often coalesced changes go downstream; zero sends them at every flush.</summary>
        internal int DownstreamRateHz { get; set; } = 10;

        /// <summary>Gets or sets how many overdue steps one pump may run before it skips the rest.</summary>
        internal int MaximumCatchUpSteps { get; set; } = 4;

        /// <summary>Gets or sets how long a departed participant's record survives for a returning identity.</summary>
        internal long ParticipantGraceMicros { get; set; } = 30_000_000;

        /// <summary>Gets or sets the interval between clock probes to each client; zero disables periodic probes.</summary>
        internal long ClockSyncIntervalMicros { get; set; } = 2_000_000;

        /// <summary>
        /// Gets or sets how long a host keeps a link that has not asked to join. A peer that connects and says nothing
        /// would otherwise hold a connection slot on keepalives alone.
        /// </summary>
        internal long JoinRequestTimeoutMicros { get; set; } = 10_000_000;

        /// <summary>
        /// Gets or sets how long a host waits for a player to acknowledge a snapshot before it ends the connection as timed
        /// out: <see cref="InternalReadinessLimits.HostSeconds"/>. It counts from the join's acceptance, or from the
        /// replacement that took a Ready player out of the session and again from the player's report that it arrived; a
        /// replacement while the player is still joining starts nothing over. That is the player's whole allowance, its wait
        /// for the snapshot and its holds, which it counts from link-up or from the replacement's arrival, and a minute more,
        /// so an honest player always gives up first.
        /// </summary>
        internal long ReadyTimeoutMicros { get; set; } = InternalReadinessLimits.HostSeconds * 1_000_000L;

        /// <summary>Gets or sets the most participants a host admits, including its own player.</summary>
        internal int MaximumParticipants { get; set; } = 16;

        /// <summary>Gets or sets the prefix for host-assigned network object ids.</summary>
        internal string ObjectIdPrefix { get; set; } = "net-object-";

        /// <summary>Gets or sets how mod callback exceptions are handled.</summary>
        internal EngineCallbackFailureMode CallbackFailures { get; set; } = EngineCallbackFailureMode.Report;

        /// <summary>Gets or sets where protocol violations and callback failures are reported.</summary>
        internal IEngineDiagnostics? Diagnostics { get; set; }

        /// <summary>Gets or sets the host's admission policy; null uses <see cref="DefaultJoinPolicy"/>.</summary>
        internal IJoinPolicy? JoinPolicy { get; set; }

        /// <summary>
        /// Gets or sets what a client does when its session ends: given how it ended, return the solo session to go back
        /// to, or null to end as before. It runs inside the engine and must not call back into it.
        /// </summary>
        internal Func<ClientSessionEnd, SoloSessionStart?>? AfterClientSessionEnds { get; set; }

        /// <summary>
        /// Gets or sets how a host names the participant behind an authenticated link identity. Null assigns
        /// <c>p1</c>, <c>p2</c>, and so on in admission order.
        /// </summary>
        internal Func<string, ParticipantId>? ParticipantIdForIdentity { get; set; }

        /// <summary>Gets or sets the voice codec and the host's voice limits; null leaves voice off and sends nothing for it.</summary>
        internal VoiceOptions? Voice { get; set; }

        /// <summary>Issues opaque participant lifetimes. Tests may seed this independent of gameplay randomness.</summary>
        internal Func<Guid> NewParticipantLifetime { get; set; } = Guid.NewGuid;

        internal void Validate()
        {
            Voice?.Validate();
            if (StepRateHz < 1 || StepRateHz > 1000) throw new ArgumentOutOfRangeException(nameof(StepRateHz));
            if (DownstreamRateHz < 0 || DownstreamRateHz > 1000) throw new ArgumentOutOfRangeException(nameof(DownstreamRateHz));
            if (MaximumCatchUpSteps < 1) throw new ArgumentOutOfRangeException(nameof(MaximumCatchUpSteps));
            if (ParticipantGraceMicros < 0) throw new ArgumentOutOfRangeException(nameof(ParticipantGraceMicros));
            if (ClockSyncIntervalMicros < 0) throw new ArgumentOutOfRangeException(nameof(ClockSyncIntervalMicros));
            if (ReadyTimeoutMicros < 1) throw new ArgumentOutOfRangeException(nameof(ReadyTimeoutMicros));
            if (MaximumParticipants < 1 || MaximumParticipants > ProtocolLimits.MaximumParticipants)
            {
                throw new ArgumentOutOfRangeException(nameof(MaximumParticipants));
            }

            if (!ProtocolLimits.IsValidIdentity(ObjectIdPrefix + "1")) throw new ArgumentException("The object id prefix is invalid.", nameof(ObjectIdPrefix));
        }
    }

    internal enum EngineDiagnosticKind
    {
        /// <summary>A peer sent bytes that violate the protocol; the connection was ended.</summary>
        ProtocolViolation = 0,

        /// <summary>A mod callback threw while the engine dispatched it.</summary>
        CallbackFailed = 1,

        /// <summary>A canonical value could not be decoded with the local codec.</summary>
        CodecFailed = 2,

        /// <summary>A participant broke the voice limits often enough to be muted automatically or removed.</summary>
        VoiceAbuse = 3,

        /// <summary>The provider's voice worker failed repeatedly and stopped; voice chat is off until the provider restarts.</summary>
        VoiceFailed = 4
    }

    /// <summary>One engine diagnostic. It names participants, owners and wire ids, never network addresses.</summary>
    internal sealed class EngineDiagnostic
    {
        internal EngineDiagnostic(EngineDiagnosticKind kind, string message, ParticipantId? participant = null, Exception? exception = null)
        {
            Kind = kind;
            Message = message;
            Participant = participant;
            Exception = exception;
        }

        internal EngineDiagnosticKind Kind { get; }
        internal string Message { get; }
        internal ParticipantId? Participant { get; }
        internal Exception? Exception { get; }

        public override string ToString() =>
            Kind + ": " + Message +
            (Participant.HasValue ? " (participant " + Participant.Value + ")" : string.Empty) +
            (Exception != null ? " [" + Exception.GetType().Name + ": " + Exception.Message + "]" : string.Empty);
    }

    internal interface IEngineDiagnostics
    {
        void Report(EngineDiagnostic diagnostic);
    }
}
