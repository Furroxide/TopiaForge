using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.SandboxAcceptance.Native
{
    // Transport runs on a worker. Unity and mod services are touched only by Pump on the host update.
    // Timeout retires the pending request before returning, so a later update cannot execute stale work.
    internal sealed class SandboxPipeServer : IDisposable
    {
        private readonly string challenge;
        private readonly Func<SandboxWireRequest, byte[]> execute;
        private readonly CancellationTokenSource stopping = new CancellationTokenSource();
        private readonly object gate = new object();
        private NamedPipeServerStream? stream;
        private Pending? pending;
        private readonly Task worker;
        internal string Failure { get; private set; } = "";
        internal SandboxPipeServer(string challenge, Func<SandboxWireRequest, byte[]> execute)
        {
            this.challenge = challenge; this.execute = execute;
            worker = Task.Run(Serve);
        }
        internal void Pump()
        {
            Pending? current;
            lock (gate) { current = pending; }
            if (current == null || !current.TryStart()) return;
            try { current.Completion.TrySetResult(execute(current.Request)); }
            catch (Exception exception) { current.Completion.TrySetException(exception); }
        }
        private async Task Serve()
        {
            try
            {
                // A challenge is single-use. EOF or protocol failure ends this server permanently;
                // reconnect/replay cannot reset the request sequence or scenario baseline.
                using var pipe = new NamedPipeServerStream("topiaforge-sandbox-" + challenge, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
                lock (gate) { if (stopping.IsCancellationRequested) return; stream = pipe; }
                await pipe.WaitForConnectionAsync(stopping.Token).ConfigureAwait(false);
                for (var sequence = 1; sequence <= 65536 && !stopping.IsCancellationRequested; sequence++)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(15));
                    var request = SandboxWireCodec.Parse(await ReadFrame(pipe, deadline.Token).ConfigureAwait(false), challenge, sequence);
                    var call = new Pending(request);
                    lock (gate) { pending = call; }
                    using var cancel = deadline.Token.Register(() => { call.Cancel(); try { pipe.Dispose(); } catch { } });
                    try
                    {
                        var response = await call.Completion.Task.ConfigureAwait(false);
                        await WriteFrame(pipe, response, deadline.Token).ConfigureAwait(false);
                    }
                    finally { call.Cancel(); lock (gate) { if (ReferenceEquals(pending, call)) pending = null; } }
                }
            }
            catch (OperationCanceledException) { if (!stopping.IsCancellationRequested) Failure = "request-deadline-exceeded"; }
            catch (Exception exception) { if (!stopping.IsCancellationRequested) Failure = "native-transport-stopped:" + exception.GetType().Name; }
            finally { lock (gate) { stream = null; pending?.Cancel(); pending = null; } }
        }
        internal static async Task<byte[]> ReadFrame(Stream source, CancellationToken token)
        {
            var header = new byte[4]; await ReadExactly(source, header, token).ConfigureAwait(false);
            var size = header[0] | header[1] << 8 | header[2] << 16 | header[3] << 24;
            if (size < 1 || size > SandboxWireCodec.MaximumBytes) throw new InvalidDataException("Invalid frame length.");
            var data = new byte[size]; await ReadExactly(source, data, token).ConfigureAwait(false); return data;
        }
        private static async Task ReadExactly(Stream source, byte[] target, CancellationToken token)
        {
            var offset = 0;
            while (offset < target.Length)
            { var count = await source.ReadAsync(target, offset, target.Length - offset, token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("Truncated native frame."); offset += count; }
        }
        internal static async Task WriteFrame(Stream target, byte[] data, CancellationToken token)
        {
            if (data.Length < 1 || data.Length > SandboxWireCodec.MaximumBytes) throw new InvalidDataException("Invalid reply size.");
            var header = new[] { (byte)data.Length, (byte)(data.Length >> 8), (byte)(data.Length >> 16), (byte)(data.Length >> 24) };
            await target.WriteAsync(header, 0, 4, token).ConfigureAwait(false);
            await target.WriteAsync(data, 0, data.Length, token).ConfigureAwait(false); await target.FlushAsync(token).ConfigureAwait(false);
        }
        public void Dispose()
        {
            stopping.Cancel();
            lock (gate) { pending?.Cancel(); stream?.Dispose(); }
            // Never wait on the host thread; the cancellation/disposed pipe lets the worker drain.
        }
        private sealed class Pending
        {
            internal readonly SandboxWireRequest Request;
            internal readonly TaskCompletionSource<byte[]> Completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int state;
            internal Pending(SandboxWireRequest request) { Request = request; }
            internal bool TryStart() => Interlocked.CompareExchange(ref state, 1, 0) == 0;
            internal void Cancel() { Interlocked.Exchange(ref state, 2); Completion.TrySetCanceled(); }
        }
    }
}
