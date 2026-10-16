using System;

namespace TopiaForge.ModManager.Core.Multiplayer
{
    /// <summary>How a mod takes part in a networked session, derived from its effective manifest.</summary>
    internal enum ModNetworkClass
    {
        /// <summary>No multiplayer block, or a declared <c>session</c> mod: every peer runs the identical package.</summary>
        Shared = 0,

        /// <summary><c>client-local</c>: runs only on the peer that enables it, and nothing it does is networked.</summary>
        ClientLocal = 1,

        /// <summary><c>server-only</c>: runs where the session's server runs, offline or on the listen host.</summary>
        ServerOnly = 2,

        /// <summary><c>standalone</c>: runs offline only, and its presence refuses every multiplayer session.</summary>
        Standalone = 3
    }

    /// <summary>The execution contexts of the plan's execution model: where a mod context's code runs.</summary>
    internal enum ModExecutionContext
    {
        /// <summary>Process-wide mod code. Runs on every peer.</summary>
        Process = 0,

        /// <summary>The world provider's scope. Runs on every peer and loads the world deterministically.</summary>
        WorldOwner = 1,

        /// <summary>The gamemode factory's server controller. Runs on the host only.</summary>
        GamemodeServer = 2,

        /// <summary>One per interactive peer, owning that player's tools and HUD.</summary>
        Participant = 3
    }

    /// <summary>The network profile every mod context carries: a network class and an execution context.</summary>
    internal readonly struct ModNetProfile : IEquatable<ModNetProfile>
    {
        public ModNetProfile(ModNetworkClass networkClass, ModExecutionContext context)
        {
            if (!ModNetworkClasses.IsDefined(networkClass)) throw new ArgumentOutOfRangeException(nameof(networkClass));
            if (!ModExecutionContexts.IsDefined(context)) throw new ArgumentOutOfRangeException(nameof(context));
            NetworkClass = networkClass;
            Context = context;
        }

        public ModNetworkClass NetworkClass { get; }

        public ModExecutionContext Context { get; }

        /// <summary>Whether code with this profile runs at all in <paramref name="realm"/>.</summary>
        public bool RunsOn(NetRealm realm) =>
            ModNetworkClasses.RunsOn(NetworkClass, realm) && ModExecutionContexts.RunsOn(Context, realm);

        public bool Equals(ModNetProfile other) => NetworkClass == other.NetworkClass && Context == other.Context;

        public override bool Equals(object? obj) => obj is ModNetProfile other && Equals(other);

        public override int GetHashCode() => ((int)NetworkClass * 397) ^ (int)Context;

        public override string ToString() => NetworkClass + "/" + Context;
    }

    /// <summary>Maps manifests to network classes and answers where each class runs.</summary>
    internal static class ModNetworkClasses
    {
        public static bool IsDefined(ModNetworkClass networkClass) =>
            networkClass >= ModNetworkClass.Shared && networkClass <= ModNetworkClass.Standalone;

        /// <summary>Maps an effective manifest mode. Implicit and <c>session</c> mods are both shared.</summary>
        public static ModNetworkClass FromEffectiveMode(ModMultiplayerEffectiveMode mode)
        {
            switch (mode)
            {
                case ModMultiplayerEffectiveMode.ImplicitShared:
                case ModMultiplayerEffectiveMode.Session:
                    return ModNetworkClass.Shared;
                case ModMultiplayerEffectiveMode.ClientLocal:
                    return ModNetworkClass.ClientLocal;
                case ModMultiplayerEffectiveMode.ServerOnly:
                    return ModNetworkClass.ServerOnly;
                case ModMultiplayerEffectiveMode.Standalone:
                    return ModNetworkClass.Standalone;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }

        /// <summary>
        /// Reads the network class from a manifest. Fails when the schema is unsupported or the multiplayer mode is
        /// unknown; manifest validation rejects both before a mod loads.
        /// </summary>
        public static bool TryFromManifest(ModManifest manifest, out ModNetworkClass networkClass)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            var mode = manifest.EffectiveMultiplayerMode;
            networkClass = mode.HasValue ? FromEffectiveMode(mode.Value) : ModNetworkClass.Standalone;
            return mode.HasValue;
        }

        /// <summary>
        /// Reads the network class from a manifest, treating an unreadable multiplayer mode as standalone: such a mod
        /// can still run offline but never takes part in, or silently joins, a session.
        /// </summary>
        public static ModNetworkClass Classify(ModManifest manifest)
        {
            TryFromManifest(manifest, out var networkClass);
            return networkClass;
        }

        /// <summary>
        /// Where each class runs: shared and client-local mods run in every realm (client-local ones only on the peer
        /// that has them), server-only mods wherever the session's server runs, and standalone mods offline only.
        /// </summary>
        public static bool RunsOn(ModNetworkClass networkClass, NetRealm realm)
        {
            if (!IsDefined(networkClass)) throw new ArgumentOutOfRangeException(nameof(networkClass));
            NetRealms.Require(realm);
            switch (networkClass)
            {
                case ModNetworkClass.ServerOnly:
                    return realm != NetRealm.Client;
                case ModNetworkClass.Standalone:
                    return realm == NetRealm.Offline;
                default:
                    return true;
            }
        }

        /// <summary>Whether enabling a mod of this class refuses every multiplayer session.</summary>
        public static bool RefusesMultiplayer(ModNetworkClass networkClass)
        {
            if (!IsDefined(networkClass)) throw new ArgumentOutOfRangeException(nameof(networkClass));
            return networkClass == ModNetworkClass.Standalone;
        }
    }

    /// <summary>The execution-context table of the plan, as data plus queries.</summary>
    internal static class ModExecutionContexts
    {
        // Rows are contexts in enum order; columns are Offline, ListenHost and Client.
        private static readonly bool[,] RunsOnRealm =
        {
            { true, true, true },   // Process: every peer
            { true, true, true },   // WorldOwner: every peer, loading the world deterministically
            { true, true, false },  // GamemodeServer: the host only (single player is its own host)
            { true, true, true }    // Participant: one per interactive peer, including the host's own player
        };

        public static bool IsDefined(ModExecutionContext context) =>
            context >= ModExecutionContext.Process && context <= ModExecutionContext.Participant;

        public static bool RunsOn(ModExecutionContext context, NetRealm realm)
        {
            if (!IsDefined(context)) throw new ArgumentOutOfRangeException(nameof(context));
            NetRealms.Require(realm);
            return RunsOnRealm[(int)context, (int)realm];
        }

        /// <summary>Whether a context exists once per interactive player rather than once per process.</summary>
        public static bool IsPerParticipant(ModExecutionContext context)
        {
            if (!IsDefined(context)) throw new ArgumentOutOfRangeException(nameof(context));
            return context == ModExecutionContext.Participant;
        }
    }
}
