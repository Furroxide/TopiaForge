using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static partial class RuntimeAdmissionTests
{
    private sealed record StingerReceipt(string Owner, ulong ScopeToken, UiStingerRequest Request, UiThemeDescriptor? Theme,
        AdmittedUiCatalogue? Catalogue, bool HasDeclaredTheme, UiStingerFontFit FontFit, UiWireEventEnvelope? Envelope);

    // The provider harness records the actual decoded request at the native service boundary.
    private sealed partial class ThemeProbe
    {
        private readonly UiStingerQueue<UiStingerRequest> localStingerQueue = new();
        private readonly Dictionary<(string Owner, ulong Token), UiStingerQueue<double>> remoteStingerQueues = new();
        internal readonly List<UiStingerRequest> LocalStingers = new();
        internal readonly List<StingerReceipt> RemoteStingers = new();
        internal readonly List<(string Owner, ulong Token)> RetiredStingerScopes = new();
        internal int StingerPreflights, StingerResets;
        internal bool RejectStingerFit;
        internal UiStingerFontFit StingerFontFit;
        internal bool StandardStingerExit;
        internal Func<double>? StingerTimeOverride;
        internal int ActiveRemoteStingerScopes => remoteStingerQueues.Count;
        internal bool HasStingerScope(string owner, ulong token) => remoteStingerQueues.ContainsKey((owner, token));

        public OperationResult<bool> ShowStinger(UiStingerRequest request) => ShowStinger(request, out _);

        public OperationResult<bool> ShowStinger(UiStingerRequest request, out bool acceptedNew)
        {
            var decision = localStingerQueue.Enqueue(request, request, ClockTime, out _);
            acceptedNew = decision is UiStingerQueueResult.Started or UiStingerQueueResult.Queued;
            if (acceptedNew) LocalStingers.Add(request);
            return Result(decision);
        }

        public OperationResult<bool> ValidateStingerForDelivery(UiStingerRequest request, UiThemeDescriptor? declared,
            out UiStingerFontFit fontFit)
        {
            fontFit = StingerFontFit;
            StingerPreflights++;
            return RejectStingerFit
                ? OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "The stinger title cannot fit in two lines.")
                : UiPresentationTheme.Allowed;
        }

        public OperationResult<bool> ShowRemoteStinger(string ownerPackageId, ulong scopeToken, UiStingerRequest request, UiThemeDescriptor? declared,
            AdmittedUiCatalogue? catalogue, bool hasDeclaredTheme, UiStingerFontFit fontFit,
            UiWireEventEnvelope? authenticatedEnvelope = null)
        {
            var key = (ownerPackageId, scopeToken);
            if (!remoteStingerQueues.TryGetValue(key, out var queue))
                remoteStingerQueues.Add(key, queue = new UiStingerQueue<double>());
            var now = StingerTimeOverride?.Invoke() ?? ClockTime;
            if (queue.Active is { } active && now >= queue.ShownAt + active.Request.DurationSeconds + active.Presentation)
                queue.Advance(now, out _);
            var exit = StandardStingerExit ? (declared ?? UiThemePackParser.Default).Motion.SlowMs / 1000d : 0d;
            var decision = queue.EnqueueRetained(request, exit, now);
            if (decision is UiStingerQueueResult.Started or UiStingerQueueResult.Queued)
                RemoteStingers.Add(new StingerReceipt(ownerPackageId, scopeToken, request, declared, catalogue, hasDeclaredTheme, fontFit, authenticatedEnvelope));
            return Result(decision);
        }

        public bool RetireRemoteStingers(string ownerPackageId, ulong scopeToken)
        {
            var key = (ownerPackageId, scopeToken);
            if (!remoteStingerQueues.Remove(key, out var queue)) return false;
            while (queue.TryDrain(out _)) { }
            RetiredStingerScopes.Add(key);
            return true;
        }

        public void ResetRemoteStingers()
        {
            remoteStingerQueues.Clear();
            StingerResets++;
        }

        private static OperationResult<bool> Result(UiStingerQueueResult result) => result == UiStingerQueueResult.RateLimited
            ? OperationResult<bool>.Failure(ModErrorCode.RateLimited, "The native stinger queue is full.")
            : UiPresentationTheme.Allowed;
    }
}
