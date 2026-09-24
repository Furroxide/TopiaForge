using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace TopiaForge.Acceptance.Windows;

internal sealed class NativePipe : IAsyncDisposable
{
    private readonly NamedPipeClientStream stream;
    private readonly BrokerRequest request;
    private readonly OwnedGameProbe game;
    private int sequence;
    internal NativePipe(BrokerRequest request, OwnedGameProbe game)
    {
        this.request = request;
        this.game = game;
        stream = new NamedPipeClientStream(".", "topiaforge-sandbox-" + request.Challenge, PipeDirection.InOut, PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Identification);
    }
    internal async Task Connect(CancellationToken cancellation)
    {
        await stream.ConnectAsync(10000, cancellation);
        VerifyPeer();
    }
    private void VerifyPeer()
    {
        game.VerifyIdentity();
        if (!NativeMethods.GetNamedPipeServerProcessId(stream.SafePipeHandle, out var pid) || pid != request.Process.Pid) throw new InvalidOperationException("Native observer pipe belongs to another process.");
    }
    internal async Task<(JsonElement Request, JsonElement Response)> Exchange(string operation, string scenarioId, int cycle, CancellationToken cancellation)
    {
        if (!DriverVocabulary.WireOperations.Contains(operation)) throw new InvalidDataException("Undeclared native operation.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        cancellation = deadline.Token;
        VerifyPeer();
        var body = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, challenge = request.Challenge, sequence = ++sequence, operation, scenarioId, cycle });
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, cancellation);
        await stream.WriteAsync(body, cancellation);
        await stream.FlushAsync(cancellation);
        await stream.ReadExactlyAsync(header, cancellation);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 2 || length > BoundedJson.MaximumBytes) throw new InvalidDataException("Native response frame is out of bounds.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellation);
        var reply = BoundedJson.Parse(bytes);
        BoundedJson.Keys(reply, "schemaVersion", "challenge", "sequence", "operation", "scenarioId", "cycle", "frame", "managerSessionId", "facts", "unavailableReasons");
        if (BoundedJson.Integer(reply, "schemaVersion", 1, 1) != 1 || BoundedJson.Digest(reply, "challenge") != request.Challenge || BoundedJson.Integer(reply, "sequence", 1, 100000) != sequence || BoundedJson.Text(reply, "operation") != operation || BoundedJson.Text(reply, "scenarioId") != scenarioId || BoundedJson.Integer(reply, "cycle", 1, 10) != cycle || BoundedJson.Text(reply, "managerSessionId") != request.ManagerSessionId)
            throw new InvalidDataException("Stale or foreign native response.");
        _ = BoundedJson.Integer(reply, "frame", 0, int.MaxValue);
        VerifyPeer();
        return (BoundedJson.Parse(body), reply);
    }
    public ValueTask DisposeAsync() => stream.DisposeAsync();
}
