using System;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed class NetAudioService : IAudioService
    {
        private readonly IAudioService inner;
        private readonly RuntimeReplication context;
        internal NetAudioService(IAudioService inner, RuntimeReplication context) { this.inner = inner; this.context = context; }
        public OperationResult<IAudioPlayback> Play(AudioPlayRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            UnityMainThreadGuard.AssertCurrent();
            var presentation = context.Services.Players?.Presentation;
            return presentation != null && PresentationRoutingPolicy.Route(context.Services.Runtime.Realm, context.Profile,
                PresentationKind.AudioCue) == PresentationRoute.BroadcastToEveryone
                ? presentation.Play(context, inner, request) : inner.Play(request);
        }
    }
}
