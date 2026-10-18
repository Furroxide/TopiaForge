using System;

namespace TopiaForge.ModManager
{
    /// <summary>Where this process's world stands, for the connecting overlay (WS5) and session follow (WS3.8).</summary>
    internal enum NetworkWorldPhase
    {
        /// <summary>Single player: nothing is replicated.</summary>
        Offline = 0,

        /// <summary>A listen host: the world is this process's own.</summary>
        Hosting = 1,

        /// <summary>A joined player that has asked the host for its world and heard nothing yet.</summary>
        Requesting = 2,

        /// <summary>A joined player receiving the host's baseline.</summary>
        ReceivingBaseline = 3,

        /// <summary>A joined player that has the baseline and is still building replicas of it.</summary>
        BuildingReplicas = 4,

        /// <summary>A joined player whose world follows the host's.</summary>
        Synchronized = 5
    }

    /// <summary>How far a joined player's world has come. Reading it allocates nothing.</summary>
    internal readonly struct NetworkWorldProgress
    {
        internal NetworkWorldProgress(NetworkWorldPhase phase, int entriesReceived, int entriesExpected, int replicasPending)
        {
            Phase = phase;
            EntriesReceived = entriesReceived;
            EntriesExpected = entriesExpected;
            ReplicasPending = replicasPending;
        }

        internal NetworkWorldPhase Phase { get; }

        /// <summary>Gets the baseline entries applied so far.</summary>
        internal int EntriesReceived { get; }

        /// <summary>Gets the entries the host's baseline announced. Entities that end meanwhile are left out, so fewer may come.</summary>
        internal int EntriesExpected { get; }

        /// <summary>Gets the replicas still loading.</summary>
        internal int ReplicasPending { get; }

        /// <summary>Gets the share of the world in place, from 0 to 1: 1 once synchronized, and below it until then.</summary>
        internal double Fraction
        {
            get
            {
                switch (Phase)
                {
                    case NetworkWorldPhase.Synchronized:
                    case NetworkWorldPhase.Hosting:
                        return 1d;
                    case NetworkWorldPhase.ReceivingBaseline:
                        return EntriesExpected <= 0 ? 0d : Math.Min(0.99d, 0.9d * EntriesReceived / EntriesExpected);
                    case NetworkWorldPhase.BuildingReplicas:
                        return 0.9d + (0.09d / (1 + ReplicasPending));
                    default:
                        return 0d;
                }
            }
        }

        public override string ToString() => Phase + " " + EntriesReceived + "/" + EntriesExpected + (ReplicasPending > 0 ? " (" + ReplicasPending + " loading)" : string.Empty);
    }

    /// <summary>
    /// What the network world counted, for diagnostics and tests. Counts only: no participant, address or text a host
    /// wrote, so any of it may go in a diagnostics bundle. Main thread only.
    /// </summary>
    internal sealed class NetworkWorldCounters
    {
        // The host.
        internal long Promotions { get; set; }

        internal long RegistrationFailures { get; set; }

        internal long MarkFailures { get; set; }

        internal long SceneObjectsBound { get; set; }

        internal long SceneObjectsRemoved { get; set; }

        internal long Announced { get; set; }

        internal long Despawned { get; set; }

        internal long SnapshotEntries { get; set; }

        internal long Requests { get; set; }

        internal long RequestsRefused { get; set; }

        /// <summary>Gets the progress reports that named no baseline under way for their sender, which move nothing.</summary>
        internal long ProgressIgnored { get; set; }

        internal long StreamRestarts { get; set; }

        internal long Rebaselines { get; set; }

        internal long BaselinesStarted { get; set; }

        internal long BaselinesCompleted { get; set; }

        internal long EntriesTooLarge { get; set; }

        /// <summary>Gets the scenes indexed with keys taken after their load, because their load could not be keyed.</summary>
        internal long ScenesKeyedLate { get; set; }

        // A joined player.
        internal long Joins { get; set; }

        internal long RequestsSent { get; set; }

        internal long BaselinesReceived { get; set; }

        internal long BaselinesApplied { get; set; }

        /// <summary>Gets how many times a baseline named another world of the host, so this player started again from nothing.</summary>
        internal long WorldsReplaced { get; set; }

        internal long EntriesApplied { get; set; }

        internal long EntitiesRefused { get; set; }

        internal long MalformedMessages { get; set; }

        internal long StaleMessages { get; set; }

        internal long SceneKeysUnbound { get; set; }

        /// <summary>Gets the scene keys dropped past <see cref="NetworkWorldClient.MaximumPendingKeys"/>.</summary>
        internal long SceneKeysDropped { get; set; }

        internal long SceneObjectsHidden { get; set; }

        internal long ScenePosesPlaced { get; set; }

        internal long ReplicasBuilt { get; set; }

        internal long ReplicasUnbuildable { get; set; }

        /// <summary>Gets the bundle and prefab loads refused past <see cref="NetworkWorldClient.MaximumBundleLoads"/> and <see cref="NetworkWorldClient.MaximumPrefabLoads"/>.</summary>
        internal long AssetLoadsRefused { get; set; }

        internal long ReplicasReleased { get; set; }

        internal long ReleaseFailures { get; set; }

        internal long SamplesAccepted { get; set; }

        internal long SamplesRefused { get; set; }

        internal long SuppressedSwitches { get; set; }

        internal long SuppressionFailures { get; set; }

        internal long RestoredSwitches { get; set; }

        internal long RestoreFailures { get; set; }

        internal long ActivationFailures { get; set; }

        internal long HoldFailures { get; set; }
    }
}
