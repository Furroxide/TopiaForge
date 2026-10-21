using System;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// The participant realm (WS3.9): what of a gamemode runs in this process, by the realm its session started in. See
    /// docs/internal/MultiplayerRuntimeIntegration.md, section 15.
    /// </summary>
    internal sealed partial class GamemodeSessionOrchestrator
    {
        internal const string ParticipantStartFailure = "The gamemode's participant controller failed to start: ";

        /// <summary>
        /// Starts the gamemode for this process, during StartingMode. Where the session's server runs, in single player and
        /// on the host, the factory's server controller starts first. Then, in every realm, a factory that implements
        /// <see cref="IGamemodeParticipantFactory"/> starts this game's own player's participant controller, in a scope of
        /// its own. A joined player starts only the participant, and never even constructs a gamemode without one: session
        /// follow (WS3.8) calls this once the host's world is loaded and synchronized, and the session runs once it returns.
        /// It commits no phase of its own.
        /// </summary>
        private async Task StartGamemodeAsync(SessionRecord record, WorldReadiness ready)
        {
            var serverRuns = RunsServerController(record.Realm);
            if (!serverRuns && !typeof(IGamemodeParticipantFactory).IsAssignableFrom(record.Mode.ImplementationType)) return;
            record.Factory = await dispatcher.InvokeAsync(record.Mode.Create);
            ThrowIfStopping(record);
            if (serverRuns)
            {
                var session = new GamemodeSessionView(this, record.Identity, record.Scopes[record.Mode.Package.Id].Context,
                    record.Cancellation.Token, ready);
                var started = await dispatcher.InvokeCallbackAsync(() => record.Factory.StartAsync(session, record.Cancellation.Token));
                if (started.TryGetValue(out var controller)) record.Controller = controller;
                ThrowIfStopping(record);
                if (!started.Succeeded) throw new StartupFailure(started.ErrorCode, Message(started.ErrorMessage));
            }
            if (record.Factory is IGamemodeParticipantFactory participants)
                await StartParticipantAsync(record, participants, ready);
        }

        /// <summary>
        /// Starts the local player's participant controller in its own scope: the gamemode's package, its execution context
        /// <see cref="ModExecutionContext.Participant"/>, and a lifetime apart from the server controller's. A failure, or
        /// an exception, ends the session as a failed server start does, saying the participant failed; a controller
        /// returned after the session began stopping is disposed at cleanup, before the server controller.
        /// </summary>
        private async Task StartParticipantAsync(SessionRecord record, IGamemodeParticipantFactory factory, WorldReadiness ready)
        {
            var scopeId = ParticipantScopeId(record.Identity.SessionId);
            // The participant borrows no native transition: scene changes belong to the session's world owner.
            var slot = new NativeTransitionAccessSlot(scopeId, record.Identity.SessionId, () => !record.StopRequested);
            record.Slots.Add(scopeId, slot);
            var scope = await record.Snapshot.Contexts[record.Mode.Package.Id].CreateChildScopeAsync(scopeId, record.Cancellation.Token,
                () => RequestBoundStop(record.Identity.SessionId), slot, dispatcher, ModExecutionContext.Participant);
            record.Scopes.Add(scopeId, scope);
            ThrowIfStopping(record);
            var participant = new GamemodeParticipantView(this, record.Identity, scope.Context, record.Cancellation.Token, ready);
            OperationResult<IGamemodeParticipantController> started;
            try
            {
                started = await dispatcher.InvokeCallbackAsync(() => factory.StartParticipantAsync(participant, record.Cancellation.Token));
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                throw new StartupFailure(ModErrorCode.External, Message(ParticipantStartFailure + error.GetBaseException().Message));
            }
            if (started.TryGetValue(out var controller)) record.ParticipantController = controller;
            ThrowIfStopping(record);
            if (started.Succeeded) return;
            var failure = Failure(started);
            throw new StartupFailure(failure.ErrorCode, failure.ErrorCode == ModErrorCode.Cancelled
                ? failure.ErrorMessage : Message(ParticipantStartFailure + failure.ErrorMessage));
        }

        /// <summary>Whether the gamemode's server controller runs in <paramref name="realm"/>: in single player and on the host, never on a joined player.</summary>
        internal static bool RunsServerController(NetRealm realm) => ModExecutionContexts.RunsOn(ModExecutionContext.GamemodeServer, realm);

        /// <summary>The participant scope's identity among its package's scopes; one local player takes part in a session.</summary>
        internal static string ParticipantScopeId(string sessionId) => sessionId + ":participant";

        private static NetRealm RealmOf(ModContext context) => context.NetReplication.Services.Runtime.Realm;
    }
}
