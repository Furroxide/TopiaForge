using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class MultiplayerPresentation
    {
        private PresentationBroadcastLimiter effects = new PresentationBroadcastLimiter(0);
        private PresentationBroadcastLimiter receivedEffects = new PresentationBroadcastLimiter(0);
        private readonly List<SharedAudio> loops = new List<SharedAudio>();
        private readonly List<SharedAudio> oneShots = new List<SharedAudio>();
        private readonly Dictionary<ulong, RemoteAudio> remoteAudio = new Dictionary<ulong, RemoteAudio>();
        private ulong effectSequence, lastEffect;

        internal OperationResult<bool> Toast(RuntimeReplication context, IUiService local, string message, UiTone tone)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (context.Lifetime.IsStopping) return Stopped<bool>();
            if (string.IsNullOrWhiteSpace(message) || message.Length > 512 || tone < UiTone.Neutral || tone > UiTone.Danger)
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "A shared toast needs 1 to 512 characters and a valid tone.");
            var decision = effects.Offer(PresentationKind.Toast, Hash(message, (ulong)tone), Now);
            if (decision == BroadcastDecision.Duplicate) return OperationResult<bool>.Success(false);
            if (decision == BroadcastDecision.RateLimited) return Limit<bool>();
            var result = local.ShowToast(message, tone);
            if (result.Succeeded && Hosting && !context.Lifetime.IsStopping)
            {
                writer.Begin(31); writer.WriteString(""); writer.WriteVarUInt(++effectSequence);
                writer.WriteString(message); writer.WriteByte((byte)tone); Broadcast();
            }
            return result;
        }

        internal OperationResult<IAudioPlayback> Play(RuntimeReplication context, IAudioService local, AudioPlayRequest request)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (context.Lifetime.IsStopping) return Stopped<IAudioPlayback>();
            if (!ValidAudio(request)) return OperationResult<IAudioPlayback>.Failure(ModErrorCode.InvalidArgument,
                "A shared cue needs a short cue id, finite position and a volume between zero and one.");
            var token = Token();
            var hashWriter = new WorldWireWriter(1024); WriteAudio(hashWriter, request);
            var decision = effects.Offer(PresentationKind.AudioCue, Hash(hashWriter.Segment), Now);
            if (decision != BroadcastDecision.Send || (request.Loop ? loops.Count >= 8 : oneShots.Count >= 32)) return Limit<IAudioPlayback>();
            var result = local.Play(request);
            if (!result.TryGetValue(out var playback)) return result;
            var shared = new SharedAudio(this, context, token, request, playback);
            if (request.Loop) { loops.Add(shared); version++; }
            else
            {
                oneShots.Add(shared);
                writer.Begin(32); writer.WriteString(""); writer.WriteVarUInt(++effectSequence);
                writer.WriteVarUInt(token); WriteAudio(writer, request); Broadcast();
            }
            shared.Tracking = context.Lifetime.Track(shared);
            return OperationResult<IAudioPlayback>.Success(shared);
        }

        private void Broadcast() => players.Channel?.Send(writer.Segment, InternalSystemTarget.Everyone);

        private void Stop(SharedAudio audio)
        {
            if (loops.Remove(audio)) { version++; return; }
            oneShots.Remove(audio);
            if (!Hosting) return;
            writer.Begin(33); writer.WriteString(""); writer.WriteVarUInt(++effectSequence); writer.WriteVarUInt(audio.Token); Broadcast();
        }

        private void ReconcileAudio()
        {
            for (var i = loops.Count - 1; i >= 0; i--)
                if (!loops[i].IsPlaying || loops[i].Context.Lifetime.IsStopping) loops[i].Dispose();
            for (var i = oneShots.Count - 1; i >= 0; i--)
                if (!oneShots[i].IsPlaying || oneShots[i].Context.Lifetime.IsStopping || Now >= oneShots[i].Expires) oneShots[i].Dispose();
        }
        internal void ReconcileReceivedAudio()
        {
            ReconcileAudio();
            removed.Clear();
            foreach (var pair in remoteAudio)
                if (!pair.Value.Playback.IsPlaying || (!pair.Value.Loop && Now >= pair.Value.Expires)) removed.Add(pair.Key);
            for (var i = 0; i < removed.Count; i++) { remoteAudio[removed[i]].Playback.Dispose(); remoteAudio.Remove(removed[i]); }
        }
        private void WriteLoops(WorldWireWriter w)
        {
            w.WriteVarUInt((ulong)loops.Count);
            for (var i = 0; i < loops.Count; i++) { w.WriteVarUInt(loops[i].Token); WriteAudio(w, loops[i].Request); }
        }
        private static Dictionary<ulong, AudioPlayRequest> ReadLoops(WorldWireReader r)
        {
            if (!r.TryReadInt32(8, out var count)) throw new ArgumentException("Too many shared audio loops.");
            var result = new Dictionary<ulong, AudioPlayRequest>();
            for (var i = 0; i < count; i++)
            {
                var token = UiNodeWireCodec.Id(r); var request = ReadAudio(r);
                if (token == 0 || !request.Loop || result.ContainsKey(token)) throw new ArgumentException("Invalid shared audio loop.");
                result.Add(token, request);
            }
            return result;
        }
        private void ApplyLoops(Dictionary<ulong, AudioPlayRequest> values)
        {
            removed.Clear();
            foreach (var entry in remoteAudio) if (entry.Value.Loop && !values.ContainsKey(entry.Key)) removed.Add(entry.Key);
            for (var i = 0; i < removed.Count; i++) { remoteAudio[removed[i]].Playback.Dispose(); remoteAudio.Remove(removed[i]); }
            foreach (var entry in values) StartRemote(entry.Key, entry.Value);
        }
        private bool ReceiveEffect(byte kind, WorldWireReader r)
        {
            if (kind < 31 || kind > 33) return false;
            var sequence = UiNodeWireCodec.Id(r);
            if (sequence == 0 || sequence <= lastEffect) return false;
            if (kind == 31)
            {
                var text = UiNodeWireCodec.Text(r, 512); var tone = (UiTone)UiNodeWireCodec.Byte(r, 3); UiNodeWireCodec.End(r);
                if (string.IsNullOrWhiteSpace(text)) return false;
                lastEffect = sequence;
                if (receivedEffects.Offer(PresentationKind.Toast, Hash(text, (ulong)tone), Now) != BroadcastDecision.Send) return false;
                if (Receiver.Ui is IRemoteUiReceiver plain) plain.RemoteToast(text, tone); else Receiver.Ui.ShowToast(text, tone);
            }
            else
            {
                var token = UiNodeWireCodec.Id(r);
                if (token == 0) return false;
                if (kind == 33)
                {
                    UiNodeWireCodec.End(r); lastEffect = sequence;
                    if (remoteAudio.TryGetValue(token, out var value)) { remoteAudio.Remove(token); value.Playback.Dispose(); }
                }
                else
                {
                    var request = ReadAudio(r); UiNodeWireCodec.End(r);
                    if (request.Loop) return false;
                    lastEffect = sequence;
                    var encoded = new WorldWireWriter(1024); WriteAudio(encoded, request);
                    if (receivedEffects.Offer(PresentationKind.AudioCue, Hash(encoded.Segment), Now) != BroadcastDecision.Send) return false;
                    StartRemote(token, request);
                }
            }
            return true;
        }
        private void StartRemote(ulong token, AudioPlayRequest request)
        {
            if (remoteAudio.ContainsKey(token) || remoteAudio.Count >= 40) return;
            var result = Receiver.Audio.Play(request);
            if (result.TryGetValue(out var playback)) remoteAudio.Add(token, new RemoteAudio(playback, request.Loop, Now + 2_000_000));
        }
        private void ResetEffects()
        {
            foreach (var value in remoteAudio.Values) value.Playback.Dispose();
            remoteAudio.Clear(); lastEffect = 0;
            effects = new PresentationBroadcastLimiter(Now); receivedEffects = new PresentationBroadcastLimiter(Now);
        }
        private static bool ValidAudio(AudioPlayRequest r) => r.CueId.Length <= 128 &&
            MultiplayerPlayers.Finite(r.Volume) && r.Volume >= 0 && r.Volume <= 1 &&
            (!r.Position.HasValue || (MultiplayerPlayers.Finite(r.Position.Value.X) && MultiplayerPlayers.Finite(r.Position.Value.Y) && MultiplayerPlayers.Finite(r.Position.Value.Z)));
        private static void WriteAudio(WorldWireWriter w, AudioPlayRequest r)
        {
            w.WriteString(r.CueId); w.WriteSingle(r.Volume); w.WriteByte(r.Loop ? (byte)1 : (byte)0);
            w.WriteByte(r.Position.HasValue ? (byte)1 : (byte)0);
            if (r.Position.HasValue) { w.WriteSingle(r.Position.Value.X); w.WriteSingle(r.Position.Value.Y); w.WriteSingle(r.Position.Value.Z); }
        }
        private static AudioPlayRequest ReadAudio(WorldWireReader r)
        {
            var cue = UiNodeWireCodec.Text(r, 128); var volume = UiNodeWireCodec.Number(r); var loop = UiNodeWireCodec.Byte(r, 1) != 0;
            Vec3? position = UiNodeWireCodec.Byte(r, 1) != 0 ? new Vec3(UiNodeWireCodec.Number(r), UiNodeWireCodec.Number(r), UiNodeWireCodec.Number(r)) : (Vec3?)null;
            return new AudioPlayRequest(cue, volume, loop, position);
        }
        private static ulong Hash(string value, ulong seed)
        { unchecked { var hash = 14695981039346656037UL ^ seed; for (var i = 0; i < value.Length; i++) hash = (hash ^ value[i]) * 1099511628211UL; return hash; } }
        private static ulong Hash(ArraySegment<byte> value)
        { unchecked { var hash = 14695981039346656037UL; for (var i = 0; i < value.Count; i++) hash = (hash ^ value.Array![value.Offset + i]) * 1099511628211UL; return hash; } }
        private static OperationResult<T> Stopped<T>() where T : notnull => OperationResult<T>.Failure(ModErrorCode.Cancelled, "The context is stopping.");
        private sealed class RemoteAudio
        {
            internal readonly IAudioPlayback Playback;
            internal readonly bool Loop;
            internal readonly long Expires;
            internal RemoteAudio(IAudioPlayback playback, bool loop, long expires) { Playback = playback; Loop = loop; Expires = expires; }
        }
        private sealed class SharedAudio : IAudioPlayback
        {
            private readonly MultiplayerPresentation owner;
            internal readonly RuntimeReplication Context;
            internal readonly ulong Token;
            internal readonly AudioPlayRequest Request;
            private readonly IAudioPlayback local;
            internal IDisposable? Tracking;
            internal readonly long Expires;
            private bool disposed;
            internal SharedAudio(MultiplayerPresentation owner, RuntimeReplication context, ulong token, AudioPlayRequest request, IAudioPlayback local)
            { this.owner = owner; Context = context; Token = token; Request = request; this.local = local; Expires = owner.Now + 2_000_000; }
            public bool IsPlaying => !disposed && local.IsPlaying;
            public void Stop() => Dispose();
            public void Dispose()
            {
                UnityMainThreadGuard.AssertCurrent();
                if (disposed) return;
                disposed = true; owner.Stop(this); local.Dispose(); Tracking?.Dispose(); Tracking = null;
            }
        }
    }
}
