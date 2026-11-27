using System;
using System.Threading;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Voice
{
    /// <summary>
    /// What one emitter's audio callback does on the audio thread: it fills Unity's buffer from the stream the main thread
    /// attached, or with silence when none is attached. It takes no lock, never blocks and allocates nothing, so a game
    /// frame that stalls never stalls the audio: the stream's own <see cref="IVoicePlaybackStream.Read"/> is wait-free, and
    /// handing a stream over is one volatile write. The main thread attaches and detaches; only the audio thread reads.
    /// </summary>
    internal sealed class VoiceEmitterReader
    {
        private IVoicePlaybackStream? stream;

        /// <summary>Gets the stream attached now, for the main thread.</summary>
        internal IVoicePlaybackStream? Stream => Volatile.Read(ref stream);

        /// <summary>Attaches <paramref name="next"/>, or silence for null. Main thread.</summary>
        internal void Attach(IVoicePlaybackStream? next) => Volatile.Write(ref stream, next);

        /// <summary>Fills all of <paramref name="data"/>: Unity's audio callback. Audio thread.</summary>
        internal void Read(float[] data)
        {
            if (data == null || data.Length == 0) return;
            var current = Volatile.Read(ref stream);
            if (current == null)
            {
                Array.Clear(data, 0, data.Length);
                return;
            }

            current.Read(data, 0, data.Length);
        }
    }
}
