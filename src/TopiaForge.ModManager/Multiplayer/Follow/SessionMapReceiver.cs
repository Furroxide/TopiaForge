using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    // Main-thread state. The store may call ReadAsync from a worker; dispatch before touching the channel.
    internal sealed class SessionMapReceiver
    {
        private readonly Func<IInternalSystemChannel?> channel;
        private readonly IHostDispatcher dispatcher;
        private readonly Action<long> progressed;
        private readonly WorldWireReader reader = new WorldWireReader();
        private readonly WorldWireWriter writer = new WorldWireWriter(256);
        private Pending? pending;
        internal bool IsPending => pending != null;
        internal SessionMapReceiver(Func<IInternalSystemChannel?> channel, IHostDispatcher dispatcher, Action<long> progressed)
        { this.channel = channel; this.dispatcher = dispatcher; this.progressed = progressed; }

        internal async Task<OperationResult<byte[]>> ReadAsync(HostWorldState target, CancellationToken token, IProgress<long>? progress)
        {
            var work = new Pending(target, token, progress);
            await dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested)
                { work.Completion.TrySetResult(Failure(ModErrorCode.Cancelled)); return; }
                Clear();
                if (target.Map == null || target.Map.Size > InternalMapIdentity.MaximumTransferBytes)
                    work.Completion.TrySetResult(Failure(ModErrorCode.Unavailable));
                else pending = work;
            });
            using (token.Register(() => dispatcher.Post(() =>
            {
                if (ReferenceEquals(pending, work)) Clear();
                else work.Completion.TrySetResult(Failure(ModErrorCode.Cancelled));
            }))) return await work.Completion.Task.ConfigureAwait(false);
        }

        internal void Clear()
        {
            var old = pending; pending = null;
            old?.Completion.TrySetResult(Failure(ModErrorCode.Cancelled));
        }

        internal void Pump(float seconds)
        {
            var work = pending;
            if (work == null) return;
            if (work.Token.IsCancellationRequested) { Clear(); return; }
            work.Elapsed += seconds;
            if (work.Elapsed >= InternalReadinessLimits.MaximumSeconds) { Finish(Failure(ModErrorCode.TimedOut)); return; }
            var open = channel();
            if (open == null || !open.IsOpen) return;
            if (work.Generation != open.Generation)
            {
                work.Generation = open.Generation;
                work.RequestId = Guid.NewGuid().ToString("N");
                work.Buffer = null; work.Offset = 0; work.Sent = false;
            }
            if (work.Sent) return;
            writer.Begin(SessionMapHost.Request);
            writer.WriteString(work.Target.SessionId!);
            writer.WriteString(work.RequestId);
            writer.WriteString(work.Target.Map!.Sha256);
            if (open.CanSend(InternalSystemTarget.Host, writer.Length)
                && open.Send(writer.Segment, InternalSystemTarget.Host).TryGetValue(out var sent) && sent == 1) work.Sent = true;
        }

        internal bool Receive(byte[] payload)
        {
            if (payload.Length < 2 || payload[0] != 1 || (payload[1] != SessionMapHost.Chunk && payload[1] != SessionMapHost.Refused)) return false;
            var work = pending;
            if (work == null || !work.Sent || work.Token.IsCancellationRequested || work.Generation != channel()?.Generation) return true;
            reader.Reset(payload);
            reader.TryReadByte(out _); reader.TryReadByte(out var type);
            if (payload.Length > SessionMapHost.ChunkBytes + 256 || !reader.TryReadString(32, out var request))
            { Finish(Failure(ModErrorCode.Conflict)); return true; }
            if (request != work.RequestId) return true;
            if (type == SessionMapHost.Refused)
            { Finish(Failure(reader.AtEnd ? ModErrorCode.Unavailable : ModErrorCode.Conflict)); return true; }
            var size = work.Target.Map!.Size;
            if (!reader.TryReadUInt32((uint)size, out var offset) || offset != work.Offset
                || !reader.TryReadBytes(SessionMapHost.ChunkBytes, out var bytes) || !reader.AtEnd
                || bytes.Length != Math.Min(SessionMapHost.ChunkBytes, size - work.Offset))
            { Finish(Failure(ModErrorCode.Conflict)); return true; }
            work.Buffer ??= new byte[size];
            Buffer.BlockCopy(bytes, 0, work.Buffer, work.Offset, bytes.Length);
            work.Offset += bytes.Length;
            work.Progress?.Report(work.Offset);
            progressed(work.Offset);
            if (work.Offset == size)
                Finish(work.Target.Map.Matches(work.Buffer) ? OperationResult<byte[]>.Success(work.Buffer) : Failure(ModErrorCode.Conflict));
            return true;
        }

        private void Finish(OperationResult<byte[]> result)
        { var work = pending; pending = null; work?.Completion.TrySetResult(result); }
        private static OperationResult<byte[]> Failure(ModErrorCode code) => OperationResult<byte[]>.Failure(code,
            code == ModErrorCode.Cancelled ? "Map loading was cancelled." : code == ModErrorCode.TimedOut ? "Downloading the host's map stopped. Join again."
            : code == ModErrorCode.Conflict ? "The map copy did not match the host's revision. Join again."
            : "The host's map revision is unavailable or too large to send. Ask the host to restart the round.");

        private sealed class Pending
        {
            internal Pending(HostWorldState target, CancellationToken token, IProgress<long>? progress)
            { Target = target; Token = token; Progress = progress; }
            internal readonly HostWorldState Target;
            internal readonly CancellationToken Token;
            internal readonly IProgress<long>? Progress;
            internal readonly TaskCompletionSource<OperationResult<byte[]>> Completion = new TaskCompletionSource<OperationResult<byte[]>>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal string RequestId = Guid.NewGuid().ToString("N");
            internal ulong Generation;
            internal byte[]? Buffer;
            internal int Offset;
            internal float Elapsed;
            internal bool Sent;
        }
    }
}
