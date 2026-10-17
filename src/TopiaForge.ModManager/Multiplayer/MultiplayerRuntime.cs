using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>One stage a later slice plugs into the multiplayer frame. Main thread; it must not throw for control flow.</summary>
    internal interface IMultiplayerFrameStage
    {
        void Run(float deltaTime);
    }

    /// <summary>
    /// ModManager's multiplayer composition root. It finds the provider's internal control in the service registry,
    /// attaches its <see cref="MultiplayerRuntimeHost"/> to it, and drives the multiplayer frame around the mods:
    /// <list type="number">
    /// <item><see cref="BeforeModsUpdate"/>: the provider's pump, which receives on the main thread, then world events and
    /// the session follower.</item>
    /// <item>The mods' Update.</item>
    /// <item><see cref="AfterModsUpdate"/>: pose and voice send and the host's snapshots, then the provider's flush.</item>
    /// <item><see cref="BeforeModsLateUpdate"/>: replica interpolation, before the mods' LateUpdate.</item>
    /// </list>
    /// The stages are named seams that later slices fill; an empty one costs a field read. With no provider attached,
    /// each hook costs one field read and allocates nothing. The realm and its rules live in the other part of this class.
    /// See docs/internal/MultiplayerRuntimeIntegration.md.
    /// </summary>
    internal sealed partial class MultiplayerRuntime
    {
        private readonly Func<IInternalMultiplayerControl?> findControl;
        private readonly IMultiplayerLog log;
        private IInternalMultiplayerControl? control;
        private InternalSessionRole role;
        private bool pumpFailureLogged;
        private bool flushFailureLogged;

        // One flag per stage, so a healthy stage never re-arms the report of a failing one.
        private bool worldFailureLogged;
        private bool presenceFailureLogged;
        private bool snapshotFailureLogged;
        private bool replicaFailureLogged;

        /// <param name="findControl">Reads the provider's control from the service registry; null when no provider published one.</param>
        /// <param name="host">The runtime's side of the SPI, handed to the control on attach.</param>
        /// <param name="log">Where a failing pump, flush or stage is reported, once until it recovers.</param>
        internal MultiplayerRuntime(Func<IInternalMultiplayerControl?> findControl, MultiplayerRuntimeHost host, IMultiplayerLog log)
        {
            this.findControl = findControl ?? throw new ArgumentNullException(nameof(findControl));
            Host = host ?? throw new ArgumentNullException(nameof(host));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        internal MultiplayerRuntimeHost Host { get; }

        /// <summary>Gets where the runtime reports what fails, redacted; the replication seam reports there too.</summary>
        internal IMultiplayerLog Log => log;

        /// <summary>Gets the attached control, or null in a process without the multiplayer provider.</summary>
        internal IInternalMultiplayerControl? Control => control;

        /// <summary>Gets this process's role as of the last pump.</summary>
        internal InternalSessionRole Role => role;

        /// <summary>Step 2, online only: world events and the follower's hooks (WS3.6 network world, WS3.8 session follow).</summary>
        internal IMultiplayerFrameStage? WorldEvents { get; set; }

        /// <summary>Step 4, online only: the local player's pose and voice (WS3.10 presence, WS5 voice capture).</summary>
        internal IMultiplayerFrameStage? PresenceSend { get; set; }

        /// <summary>Step 4, listen host only: snapshots of the host's world for each player (WS3.6).</summary>
        internal IMultiplayerFrameStage? HostSnapshots { get; set; }

        /// <summary>LateUpdate, joined players only: interpolating replicas before mods see them (WS3.6 replica driver).</summary>
        internal IMultiplayerFrameStage? ReplicaInterpolation { get; set; }

        /// <summary>
        /// Attaches to the control the registry holds now: ModRuntime calls it once packages have loaded. Attaching again
        /// to the same control changes nothing; a different one replaces it.
        /// </summary>
        internal bool Connect()
        {
            IInternalMultiplayerControl? found;
            try
            {
                found = findControl();
            }
            catch (Exception exception)
            {
                log.Error("Multiplayer: the provider's control could not be read: " + exception.GetType().Name + ".");
                return false;
            }

            if (found == null || !ReferenceEquals(found, control)) Disconnect();
            return found != null && Attach(found);
        }

        /// <summary>Attaches <paramref name="candidate"/> unless a control is already attached.</summary>
        internal bool Attach(IInternalMultiplayerControl candidate)
        {
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            if (control != null) return ReferenceEquals(control, candidate);
            bool attached;
            try
            {
                attached = candidate.AttachRuntime(Host);
            }
            catch (Exception exception)
            {
                log.Error("Multiplayer: attaching to the provider failed: " + exception.GetType().Name + ".");
                return false;
            }

            if (!attached)
            {
                log.Warn("Multiplayer: the provider is already driven by another runtime; this one stays offline.");
                return false;
            }

            control = candidate;
            role = candidate.Role;
            return true;
        }

        /// <summary>Detaches from the provider, as shutdown begins; the game's background setting is restored.</summary>
        internal void Disconnect()
        {
            var current = control;
            if (current == null) return;
            control = null;
            role = InternalSessionRole.Solo;
            FollowRealm();
            try
            {
                current.DetachRuntime(Host);
            }
            catch (Exception exception)
            {
                log.Error("Multiplayer: detaching from the provider failed: " + exception.GetType().Name + ".");
            }

            Host.SetRunInBackground(false);
        }

        /// <summary>Steps 1 and 2, before the mods' Update.</summary>
        internal void BeforeModsUpdate(float deltaTime)
        {
            var current = control;
            if (current == null) return;
            try
            {
                current.Pump();
                role = current.Role;
                pumpFailureLogged = false;
            }
            catch (Exception exception)
            {
                if (!pumpFailureLogged) log.Error("Multiplayer: the provider's pump failed: " + Describe(exception));
                pumpFailureLogged = true;
                return;
            }

            FollowRealm();
            if (role == InternalSessionRole.Solo) return;
            Run(WorldEvents, ref worldFailureLogged, deltaTime);
        }

        /// <summary>Step 4, after the mods' Update: what the frame sends, then the provider's flush. Nothing runs offline.</summary>
        internal void AfterModsUpdate(float deltaTime)
        {
            if (role == InternalSessionRole.Solo) return;
            var current = control;
            if (current == null) return;
            Run(PresenceSend, ref presenceFailureLogged, deltaTime);
            if (role == InternalSessionRole.ListenHost) Run(HostSnapshots, ref snapshotFailureLogged, deltaTime);
            try
            {
                current.Flush();
                flushFailureLogged = false;
            }
            catch (Exception exception)
            {
                if (!flushFailureLogged) log.Error("Multiplayer: the provider's flush failed: " + Describe(exception));
                flushFailureLogged = true;
            }
        }

        /// <summary>LateUpdate, before the mods: replicas move to where the joined player should see them.</summary>
        internal void BeforeModsLateUpdate(GameTimeSample sample)
        {
            if (role != InternalSessionRole.Client) return;
            Run(ReplicaInterpolation, ref replicaFailureLogged, sample.DeltaTime);
        }

        private void Run(IMultiplayerFrameStage? stage, ref bool failureLogged, float deltaTime)
        {
            if (stage == null) return;
            try
            {
                stage.Run(deltaTime);
                failureLogged = false;
            }
            catch (Exception exception)
            {
                if (!failureLogged) log.Error("Multiplayer: a frame stage failed: " + Describe(exception));
                failureLogged = true;
            }
        }

        private static string Describe(Exception exception) =>
            exception.GetType().Name + ": " + MultiplayerDiagnosticRedactor.Redact(exception.Message);
    }
}
