using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Captures audio playback requests and owns their fake handles.</summary>
    public sealed class FakeAudioService : IAudioService
    {
        private readonly FakeModLifetime lifetime;
        private readonly List<FakeAudioPlayback> playbacks = new List<FakeAudioPlayback>();

        /// <summary>Creates a fake audio service.</summary>
        public FakeAudioService(FakeModLifetime lifetime)
        {
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        }

        /// <summary>Gets or sets a stable error used to reject playback.</summary>
        public ModErrorCode PlayErrorCode { get; set; }

        /// <summary>Gets currently active playback handles.</summary>
        public IReadOnlyList<FakeAudioPlayback> ActivePlaybacks => playbacks.AsReadOnly();

        /// <inheritdoc/>
        public OperationResult<IAudioPlayback> Play(AudioPlayRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (PlayErrorCode != ModErrorCode.None)
            {
                return OperationResult<IAudioPlayback>.Failure(
                    PlayErrorCode,
                    "Audio playback was rejected by the fake service.");
            }

            var playback = new FakeAudioPlayback(request, value => playbacks.Remove(value));
            playbacks.Add(playback);
            return lifetime.TrackResult<IAudioPlayback>(
                playback,
                playback.AttachLifetimeLease,
                "The fake mod stopped before audio playback could start.");
        }
    }

    /// <summary>Inspectable fake audio playback.</summary>
    public sealed class FakeAudioPlayback : IAudioPlayback
    {
        private Action<FakeAudioPlayback>? release;
        private IDisposable? lifetimeLease;

        internal FakeAudioPlayback(AudioPlayRequest request, Action<FakeAudioPlayback> release)
        {
            Request = request;
            this.release = release;
        }

        internal void AttachLifetimeLease(IDisposable lease)
        {
            lifetimeLease = lease ?? throw new ArgumentNullException(nameof(lease));
        }

        /// <summary>Gets the captured playback request.</summary>
        public AudioPlayRequest Request { get; }

        /// <inheritdoc/>
        public bool IsPlaying => release != null;

        /// <inheritdoc/>
        public void Stop() => Dispose();

        /// <inheritdoc/>
        public void Dispose()
        {
            var callback = release;
            release = null;
            try
            {
                callback?.Invoke(this);
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
            }
        }
    }
}
