using System;
using System.Threading;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// The network seam's interaction service (WS3.4). The gameplay host calls a registered handler when the local player
    /// interacts, so each call runs with that player's interaction intent, attributed to the handler's mod. Everything else
    /// passes through to the service the gameplay host made, a missing handler included, which the host refuses as before.
    /// Authority routing (WS3.7) runs a participant's forwarded interaction under that participant's intent through
    /// <see cref="InteractionIntent.Handler"/>, not through the local player's.
    /// </summary>
    internal sealed class NetInteractionService : IInteractionService
    {
        private readonly IInteractionService inner;

        internal NetInteractionService(IInteractionService inner, RuntimeReplication replication)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            Replication = replication ?? throw new ArgumentNullException(nameof(replication));
        }

        /// <summary>Gets the service the gameplay host made.</summary>
        internal IInteractionService Inner => inner;

        /// <summary>Gets the replication seam of the context this service belongs to.</summary>
        internal RuntimeReplication Replication { get; }

        public OperationResult<IInteractableRegistration> Register(IEntity entity, InteractableDefinition definition, Action<InteractionEvent> handler)
        {
            if (handler == null) return inner.Register(entity, definition, handler!);
            var intent = new InteractionIntent(Replication.Services.Runtime.Intents, Replication.OwnerModId, handler);
            return inner.Register(entity, definition, intent.Handle);
        }

        public bool TryGetFocused(out IInteractableRegistration? interaction) => inner.TryGetFocused(out interaction);
    }

    /// <summary>One registered handler, run with the local player's interaction intent whenever the player interacts.</summary>
    internal sealed class InteractionIntent
    {
        private static readonly ContextCallback Deliver = state => ((Delivery)state!).Run();
        private readonly MultiplayerIntents intents;
        private readonly string modId;
        private IntentRecord? record;

        internal InteractionIntent(MultiplayerIntents intents, string modId, Action<InteractionEvent> handler)
        {
            this.intents = intents;
            this.modId = modId;
            Handler = handler;
        }

        /// <summary>Gets the mod's own handler.</summary>
        internal Action<InteractionEvent> Handler { get; }

        /// <summary>What the gameplay host calls when the local player interacts.</summary>
        internal void Handle(InteractionEvent interaction)
        {
            if (!intents.IsRecording)
            {
                Handler(interaction);
                return;
            }

            intents.RunInteraction(ref record, modId, Deliver, new Delivery(Handler, interaction));
        }

        private sealed class Delivery
        {
            private readonly Action<InteractionEvent> handler;
            private readonly InteractionEvent interaction;

            internal Delivery(Action<InteractionEvent> handler, InteractionEvent interaction)
            {
                this.handler = handler;
                this.interaction = interaction;
            }

            internal void Run() => handler(interaction);
        }
    }
}
