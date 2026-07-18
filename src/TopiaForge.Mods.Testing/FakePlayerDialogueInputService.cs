using System;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Controlled fake microphone and speech-to-text service.</summary>
    public sealed class FakePlayerDialogueInputService : IPlayerDialogueInputService
    {
        private readonly FakeModLifetime lifetime;

        /// <summary>Creates a fake player dialogue input service.</summary>
        public FakePlayerDialogueInputService(FakeModLifetime lifetime)
        {
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        }

        /// <inheritdoc />
        public bool IsVoiceAvailable { get; set; } = true;

        /// <summary>Gets or sets text returned by the next stopped capture.</summary>
        public string NextTranscript { get; set; } = string.Empty;

        /// <summary>Gets the number of captures still recording.</summary>
        public int ActiveCaptureCount { get; private set; }

        /// <inheritdoc />
        public OperationResult<IVoiceCapture> BeginVoiceCapture()
        {
            if (!IsVoiceAvailable)
            {
                return OperationResult<IVoiceCapture>.Failure(
                    ModErrorCode.Unavailable,
                    "Fake voice capture is unavailable.");
            }

            var capture = new VoiceCapture(
                () => NextTranscript,
                () => ActiveCaptureCount--,
                lifetime.StoppingToken);
            ActiveCaptureCount++;
            return lifetime.TrackResult<IVoiceCapture>(
                capture,
                capture.AttachLifetimeLease,
                "The fake mod stopped before voice capture could begin.");
        }

        private sealed class VoiceCapture : IVoiceCapture
        {
            private readonly Func<string> transcript;
            private readonly CancellationToken lifetimeToken;
            private Action? release;
            private IDisposable? lifetimeLease;

            public VoiceCapture(
                Func<string> transcript,
                Action release,
                CancellationToken lifetimeToken)
            {
                this.transcript = transcript;
                this.release = release;
                this.lifetimeToken = lifetimeToken;
            }

            public void AttachLifetimeLease(IDisposable lease)
            {
                lifetimeLease = lease ?? throw new ArgumentNullException(nameof(lease));
            }

            public bool IsRecording => release != null;

            public Task<OperationResult<VoiceTranscriptResult>> StopAsync(
                CancellationToken cancellationToken = default)
            {
                if (cancellationToken.IsCancellationRequested || lifetimeToken.IsCancellationRequested)
                {
                    Dispose();
                    return Task.FromResult(OperationResult<VoiceTranscriptResult>.Failure(
                        ModErrorCode.Cancelled,
                        "The fake voice capture was cancelled."));
                }

                if (!IsRecording)
                {
                    return Task.FromResult(OperationResult<VoiceTranscriptResult>.Failure(
                        ModErrorCode.InvalidState,
                        "The fake voice capture has stopped."));
                }

                var text = transcript();
                Dispose();
                return Task.FromResult(string.IsNullOrWhiteSpace(text)
                    ? OperationResult<VoiceTranscriptResult>.Failure(
                        ModErrorCode.NotFound,
                        "No fake transcript was configured.")
                    : OperationResult<VoiceTranscriptResult>.Success(
                        new VoiceTranscriptResult(text)));
            }

            public void Dispose()
            {
                var callback = release;
                release = null;
                try
                {
                    callback?.Invoke();
                }
                finally
                {
                    Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
                }
            }
        }
    }
}
