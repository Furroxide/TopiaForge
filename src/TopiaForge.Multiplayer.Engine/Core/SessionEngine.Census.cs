namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>
    /// The sizes of the engine's tables that grow with traffic and churn, so soak and fuzz tests can hold each to a
    /// ceiling. It counts entries only and names no participant, peer or value.
    /// </summary>
    internal readonly struct EngineCensus
    {
        internal EngineCensus(
            int connections,
            int participantRecords,
            int objects,
            int tombstones,
            int unboundStates,
            int unboundObjects,
            int pendingSubmissions,
            int rateHistoryEntries,
            int commandRateLimiters,
            int objectRateLimiters,
            int queuedOutbound,
            int queuedCallbacks)
        {
            Connections = connections;
            ParticipantRecords = participantRecords;
            Objects = objects;
            Tombstones = tombstones;
            UnboundStates = unboundStates;
            UnboundObjects = unboundObjects;
            PendingSubmissions = pendingSubmissions;
            RateHistoryEntries = rateHistoryEntries;
            CommandRateLimiters = commandRateLimiters;
            ObjectRateLimiters = objectRateLimiters;
            QueuedOutbound = queuedOutbound;
            QueuedCallbacks = queuedCallbacks;
        }

        /// <summary>Gets a host's connections, joined or still joining.</summary>
        internal int Connections { get; }

        /// <summary>Gets a host's participant records, or the participant list a client last received.</summary>
        internal int ParticipantRecords { get; }

        internal int Objects { get; }

        /// <summary>Gets the despawned object ids a client remembers for the current session.</summary>
        internal int Tombstones { get; }

        /// <summary>Gets the canonical values a client keeps for states it has not registered.</summary>
        internal int UnboundStates { get; }

        /// <summary>Gets the objects a client keeps for types it has not registered.</summary>
        internal int UnboundObjects { get; }

        internal int PendingSubmissions { get; }

        internal int RateHistoryEntries { get; }

        /// <summary>Gets the per-sender rate limiters a host keeps across its commands.</summary>
        internal int CommandRateLimiters { get; }

        /// <summary>Gets the per-sender rate limiters a host keeps across its live objects.</summary>
        internal int ObjectRateLimiters { get; }

        /// <summary>Gets the ordered items a host has queued for its connections and not yet flushed.</summary>
        internal int QueuedOutbound { get; }

        /// <summary>Gets the notifications, events and confirmations waiting for a settle point.</summary>
        internal int QueuedCallbacks { get; }

        public override string ToString() =>
            "connections " + Connections + ", participants " + ParticipantRecords + ", objects " + Objects +
            ", tombstones " + Tombstones + ", unbound " + UnboundStates + "/" + UnboundObjects + ", pending " + PendingSubmissions +
            ", rate history " + RateHistoryEntries + ", limiters " + CommandRateLimiters + "/" + ObjectRateLimiters +
            ", queued " + QueuedOutbound + "/" + QueuedCallbacks;
    }

    internal sealed partial class SessionEngine
    {
        /// <summary>Counts the engine's growing tables; owner thread only.</summary>
        internal EngineCensus Census()
        {
            RequireOwnerThread();
            var commandLimiters = 0;
            foreach (var binding in commands.Values) commandLimiters += binding.LimiterCount;
            var objectLimiters = 0;
            foreach (var record in objectList) objectLimiters += record.LimiterCount;
            var queued = 0;
            foreach (var connection in connections) queued += connection.Queue.Count;
            return new EngineCensus(
                connections.Count,
                Role == EngineRole.Host ? participantRecords.Count : clientParticipants.Count,
                objectList.Count,
                tombstones.Count,
                unboundStates.Count,
                unboundObjects.Count,
                pending.Count,
                RateHistoryEntries,
                commandLimiters,
                objectLimiters,
                queued,
                objectNotifications.Count + changedStates.Count + presentationDeliveries.Count + completions.Count);
        }
    }
}
