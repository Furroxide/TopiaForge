using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Voice
{
    /// <summary>How opening the microphone went.</summary>
    internal enum VoiceMicrophoneOpen
    {
        Opened = 0,

        /// <summary>The system has no microphone.</summary>
        NoDevice = 1,

        /// <summary>A microphone is there, but it would not start recording.</summary>
        Failed = 2
    }

    /// <summary>
    /// The microphone as voice chat uses it, over Unity's <c>Microphone</c> in the game and a scripted one in tests. Unity
    /// keys a recording by its device, not by who started it, so ending a device ends whoever records on it. Voice chat
    /// therefore never ends a recording while another use of the microphone holds it (<see cref="Abandon"/>), and ends one it
    /// left only once that use has let go (<see cref="EndAbandoned"/>). Nothing it reads is kept or written: each read hands
    /// its samples to the voice service and the buffer is used again. Main thread only.
    /// </summary>
    internal interface IVoiceMicrophone
    {
        /// <summary>Gets whether voice chat has a recording open that it reads.</summary>
        bool IsOpen { get; }

        /// <summary>Gets the rate of the open recording's samples, in hertz.</summary>
        int SampleRate { get; }

        /// <summary>Lists the microphones the system has now. It allocates, so the settings page asks only when it opens or refreshes.</summary>
        IReadOnlyList<string> ListDevices();

        /// <summary>Starts recording on <paramref name="device"/>, or on the system's default for null or a device no longer there.</summary>
        VoiceMicrophoneOpen Open(string? device);

        /// <summary>
        /// Reads into <paramref name="buffer"/> at most <paramref name="count"/> samples in [-1, 1] recorded since the last
        /// read; zero when none is new. Negative when the recording stopped under voice chat, as when the device was
        /// unplugged: it then counts as closed.
        /// </summary>
        int Read(float[] buffer, int offset, int count);

        /// <summary>Ends voice chat's recording: the device stops.</summary>
        void Close();

        /// <summary>
        /// Lets go of the recording without ending it, because another use of the microphone took the device: what it
        /// records from here on is never read, and ending the device would end that use's recording too.
        /// </summary>
        void Abandon();

        /// <summary>Ends a recording voice chat abandoned, if its device still records; once the other use let go, only voice chat's can.</summary>
        void EndAbandoned();
    }

    /// <summary>
    /// Whether the game's own robot recording holds the microphone: from the push-to-record key going down until the game
    /// has decoded what it recorded and ended its recording. Main thread only.
    /// </summary>
    internal interface IVoiceNativeRecording
    {
        /// <summary>Gets whether the game records now. It throws when the game's state cannot be read, which counts as recording.</summary>
        bool IsRecording { get; }
    }

    /// <summary>The keyboard and mouse as push-to-talk reads them. Main thread only.</summary>
    internal interface IVoiceInput
    {
        /// <summary>Gets whether <paramref name="binding"/>'s key or mouse button is down. It allocates nothing.</summary>
        bool IsHeld(InputBinding binding);

        /// <summary>Gets whether a text field has the keyboard, so keys type rather than act.</summary>
        bool IsTyping { get; }

        /// <summary>
        /// Takes the key or mouse button pressed this frame, for rebinding push-to-talk; Esc comes back as itself, so a
        /// capture can be cancelled. False when nothing was pressed.
        /// </summary>
        bool TryTakePressed(out InputBinding pressed);
    }

    /// <summary>The game's own input bindings, for <see cref="NativeInputCensus"/>. Main thread only.</summary>
    internal interface IVoiceNativeBindings
    {
        /// <summary>Adds the effective path of every binding of the game's player input to <paramref name="into"/>; false when they cannot be read.</summary>
        bool TryRead(List<string> into);
    }

    /// <summary>A point in the world, in metres, for positional voice.</summary>
    internal readonly struct VoicePosition
    {
        internal VoicePosition(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        internal float X { get; }

        internal float Y { get; }

        internal float Z { get; }
    }

    /// <summary>
    /// Where each speaker's voice comes from: their avatar. The seam for positional voice, which WS3.10's avatars fill;
    /// until then no source is set and every voice plays as if beside the listener. Main thread only.
    /// </summary>
    internal interface IVoicePositions
    {
        /// <summary>Gets where <paramref name="speaker"/>'s avatar is now; false when it has none, and the voice then plays non-positionally.</summary>
        bool TryGetPosition(ParticipantId speaker, out VoicePosition position);
    }

    /// <summary>One speaker's voice in the game's audio: an audio source playing a stream. Main thread only.</summary>
    internal interface IVoiceEmitter
    {
        /// <summary>Starts playing <paramref name="stream"/> at <paramref name="volume"/>, from 0 to 1.</summary>
        void Play(IVoicePlaybackStream stream, float volume);

        void SetVolume(float volume);

        /// <summary>Plays the voice from <paramref name="position"/>: full volume within 3 m, fading to nothing at 40 m.</summary>
        void Place(VoicePosition position);

        /// <summary>Plays the voice as if beside the listener, as every voice plays until avatars exist.</summary>
        void Unplace();

        /// <summary>Stops playing and lets go of the stream.</summary>
        void Stop();
    }

    /// <summary>Hands out emitters and takes them back, so a speaker who talks again reuses one. Main thread only.</summary>
    internal interface IVoiceEmitters
    {
        IVoiceEmitter Rent();

        void Return(IVoiceEmitter emitter);
    }
}
