using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TopiaForge.RobotKit
{
    internal sealed partial class RobotAgent : IRobotNativeSpeech
    {
        private Func<string, CancellationToken, Task<OperationResult<bool>>>? speechRoute;
        private readonly CancellationTokenSource speechLifetime = new CancellationTokenSource();
        private readonly SemaphoreSlim speechSerial = new SemaphoreSlim(1, 1);
        private AgentHead? speechHead;
        private bool speechReleased;
        public bool SpeechVisible
        {
            get
            {
                if (!IsAlive || !go.activeInHierarchy || go.scene != SceneManager.GetActiveScene()) return false;
                renderers ??= go.GetComponentsInChildren<Renderer>(true);
                foreach (var renderer in renderers)
                    if (renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy) return true;
                return false;
            }
        }

        internal void RegisterSpeech()
        {
            speechHead = go.GetComponentInChildren<AgentHead>(true);
            if (speechHead != null) NativeRobotSpeech.Register(speechHead, this);
        }
        internal void ReleaseSpeech()
        {
            if (speechReleased) return;
            speechReleased = true;
            // A destroyed Unity head compares equal to null but still owns a managed dictionary entry.
            if (!ReferenceEquals(speechHead, null)) NativeRobotSpeech.Release(speechHead!);
            speechHead = null;
            speechRoute = null;
            try { speechLifetime.Cancel(); }
            finally { speechLifetime.Dispose(); }
        }
        public void BindSpeech(Func<string, CancellationToken, Task<OperationResult<bool>>> route) => speechRoute = route;
        internal Task<OperationResult<bool>> RouteSpeech(string text, CancellationToken token) =>
            speechRoute != null && IsAlive ? speechRoute(text, token)
                : Task.FromResult(OperationResult<bool>.Failure(ModErrorCode.NotAuthoritative, "This robot has no owning host speech context."));
        public OperationResult<string> VoiceIdentity() => SpeechVisible && NativeRobotSpeech.IsInstalled && speechHead != null
            ? NativeRobotVoice.Identity(speechHead)
            : OperationResult<string>.Failure(ModErrorCode.Unavailable, "Native robot speech bindings are unavailable; retry after recovery.");
        public async Task<OperationResult<bool>> PlaySpeechAsync(string voice, string text, CancellationToken token)
        {
            if (!SpeechVisible || !NativeRobotSpeech.IsInstalled || speechHead == null)
                return OperationResult<bool>.Failure(ModErrorCode.Unavailable, "The robot's native voice does not match; retry after its bindings recover.");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, speechLifetime.Token);
            var entered = false;
            try
            {
                // Replacement cancels the old line before waiting here. Never overlap native voice overrides.
                await speechSerial.WaitAsync(linked.Token);
                entered = true;
                linked.Token.ThrowIfCancellationRequested();
                var asset = NativeRobotVoice.Resolve(voice);
                if (asset == null) return OperationResult<bool>.Failure(ModErrorCode.Unavailable, "The requested native voice binding is unavailable; retry after recovery.");
                using (speechHead.OverrideSpeech(speechHead.CurrentSpeakerName, asset))
                    await NativeRobotSpeech.Play(speechHead, text, linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                return OperationResult<bool>.Success(true);
            }
            catch (OperationCanceledException) { return OperationResult<bool>.Failure(ModErrorCode.Cancelled, "Robot speech was cancelled."); }
            catch (Exception) { return OperationResult<bool>.Failure(ModErrorCode.Unavailable, "Native robot speech failed; retry a fresh line after recovery."); }
            finally { if (entered) speechSerial.Release(); }
        }
    }
}
