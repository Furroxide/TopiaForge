using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class RuntimeReplication
    {
        /// <summary>Gets the intent tracking this seam reads, the composition root's.</summary>
        internal MultiplayerIntents Intents => services.Runtime.Intents;

        /// <summary>Gets the record this context's mod observes now, as Core's flow rules decide; null when it acts without intent.</summary>
        internal IntentRecord? VisibleIntent => services.Runtime.Intents.VisibleTo(OwnerModId);

        public InternalIntentRecord CurrentIntent
        {
            get
            {
                var record = VisibleIntent;
                return record == null ? default : new InternalIntentRecord((InternalIntent)(int)record.Source, record.ParticipantId);
            }
        }

        public InternalRoute RouteCurrentIntent()
        {
            var record = VisibleIntent;
            var source = record == null ? IntentSource.None : record.Source;
            return (InternalRoute)(int)ExecutionRouting.Route(services.Runtime.Realm, Profile, source);
        }
    }
}
