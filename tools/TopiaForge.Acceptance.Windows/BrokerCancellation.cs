using System.Text.Json;

namespace TopiaForge.Acceptance.Windows;

/// The launcher requests graceful cancellation before considering termination
/// of its original broker process. Cancellation always goes through input finally.
internal sealed class BrokerCancellation : IDisposable
{
    private readonly Timer timer;
    internal BrokerCancellation(BrokerRequest request, CancellationTokenSource cancellation)
    {
        var path = BoundedJson.Child(request.OutputRoot, "broker-cancel.json");
        timer = new Timer(_ =>
        {
            try
            {
                if (!File.Exists(path)) return;
                Validate(BoundedJson.Read(path, 4096), request.Challenge);
                cancellation.Cancel();
            }
            catch
            {
                // An invalid cancellation record also retires this immutable run;
                // it never disables cleanup or creates an accepted observation.
                try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
            }
        }, null, 0, 25);
    }
    internal static void Validate(JsonElement value, string challenge)
    {
        BoundedJson.Keys(value, "schemaVersion", "kind", "challenge");
        if (BoundedJson.Integer(value, "schemaVersion", 1, 1) != 1
            || BoundedJson.Text(value, "kind") != "sandbox-broker-cancel-v1"
            || BoundedJson.Digest(value, "challenge") != challenge)
            throw new InvalidDataException("Stale or invalid broker cancellation record.");
    }
    public void Dispose() => timer.Dispose();
}
