using System.Threading;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed partial class ModContext
    {
        private static readonly ContextCallback UpdateWithIntent = state => ((FrameDispatch)state!).RaiseUpdate();
        private static readonly ContextCallback LateUpdateWithIntent = state => ((FrameDispatch)state!).RaiseLateUpdate();

        // Every context of a mod shares its package context's activity, so an action a session scope registers counts too.
        private readonly ModInputActivity inputActivity;
        private IntentRecord? frameIntent;
        private FrameDispatch? frameDispatch;

        /// <summary>Gets whether one of this mod's input actions, in any of its contexts, has an edge or is held now.</summary>
        internal bool HasActiveInput => inputActivity.IsActive;

        /// <summary>
        /// Raises the mod's Update. Online, in a frame where one of its own input actions has an edge or is held after the
        /// input sample, it runs with the local player's input intent (WS3.4); otherwise, and always offline, as it is.
        /// Offline that costs one field read, and an input-active frame reuses its record, so neither allocates.
        /// </summary>
        private void RaiseFrameUpdate(float deltaTime)
        {
            var intents = replicationServices.Runtime.Intents;
            if (!intents.IsRecording || !inputActivity.IsActive)
            {
                modEvents.RaiseUpdate(deltaTime);
                return;
            }

            var dispatch = frameDispatch ??= new FrameDispatch(modEvents);
            dispatch.DeltaTime = deltaTime;
            intents.RunInputFrame(ref frameIntent, Identity.Id, late: false, UpdateWithIntent, dispatch);
        }

        /// <summary>Raises the mod's LateUpdate, by the same test in the same frame. FixedUpdate never carries the frame's input.</summary>
        private void RaiseFrameLateUpdate(GameTimeSample sample)
        {
            var intents = replicationServices.Runtime.Intents;
            if (!intents.IsRecording || !inputActivity.IsActive)
            {
                modEvents.RaiseLateUpdate(sample);
                return;
            }

            var dispatch = frameDispatch ??= new FrameDispatch(modEvents);
            dispatch.Sample = sample;
            intents.RunInputFrame(ref frameIntent, Identity.Id, late: true, LateUpdateWithIntent, dispatch);
        }

        /// <summary>One context's frame arguments, kept so an input-active frame passes them without allocating.</summary>
        private sealed class FrameDispatch
        {
            private readonly ModEvents events;

            internal FrameDispatch(ModEvents events)
            {
                this.events = events;
            }

            internal float DeltaTime { get; set; }

            internal GameTimeSample Sample { get; set; }

            internal void RaiseUpdate() => events.RaiseUpdate(DeltaTime);

            internal void RaiseLateUpdate() => events.RaiseLateUpdate(Sample);
        }
    }
}
