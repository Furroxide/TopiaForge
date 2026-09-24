using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed class OwnerWorldRuntimeService : IInternalWorldRuntimeService
    {
        private readonly IModLifetime lifetime;
        private readonly IInternalSceneTransitionService transitions;
        private readonly IWorldRuntimeBackend backend;
        private readonly IWorldRuntimeClock clock;
        private readonly IHostDispatcher dispatcher;
        internal OwnerWorldRuntimeService(IModLifetime lifetime, IInternalSceneTransitionService transitions,
            IWorldRuntimeBackend backend, IWorldRuntimeClock clock, IHostDispatcher dispatcher)
        { this.lifetime = lifetime; this.transitions = transitions; this.backend = backend; this.clock = clock; this.dispatcher = dispatcher; }

        public Task<OperationResult<IReadOnlyList<NativeWorldEntry>>> DiscoverAsync(NativeWorldSource source,
            int maximumResults, CancellationToken token) => dispatcher.InvokeAsync(() =>
        {
            if (token.IsCancellationRequested || lifetime.IsStopping) return Failure<IReadOnlyList<NativeWorldEntry>>(ModErrorCode.Cancelled, "The discovery owner stopped.");
            if (!Enum.IsDefined(typeof(NativeWorldSource), source) || maximumResults < 1 || maximumResults > 4096)
                return Failure<IReadOnlyList<NativeWorldEntry>>(ModErrorCode.InvalidArgument, "Discovery requires a known source and a limit between 1 and 4096.");
            try
            {
                var result = backend.Discover(source, maximumResults);
                if (token.IsCancellationRequested || lifetime.IsStopping) return Failure<IReadOnlyList<NativeWorldEntry>>(ModErrorCode.Cancelled, "The discovery owner stopped.");
                if (!result.TryGetValue(out var values)) return result;
                if (values.Count > maximumResults || values.Any(value => value == null || value.Source != source)
                    || values.Select(value => value.SourceKey).Distinct(StringComparer.Ordinal).Count() != values.Count)
                    return Failure<IReadOnlyList<NativeWorldEntry>>(ModErrorCode.InvalidState, "Native discovery returned invalid or duplicate source records.");
                return OperationResult<IReadOnlyList<NativeWorldEntry>>.Success(Array.AsReadOnly(values.OrderBy(value => value.SourceKey, StringComparer.Ordinal).ToArray()));
            }
            catch (Exception error) { return Failure<IReadOnlyList<NativeWorldEntry>>(ModErrorCode.External, error.Message); }
        });

        public Task<OperationResult<IInternalWorldPreparation>> PrepareAsync(NativeWorldLoadRequest request,
            CancellationToken token) => dispatcher.InvokeCallbackAsync(() => PrepareCoreAsync(request, token));

        private async Task<OperationResult<IInternalWorldPreparation>> PrepareCoreAsync(NativeWorldLoadRequest request, CancellationToken token)
        {
            if (request == null) return Failure<IInternalWorldPreparation>(ModErrorCode.InvalidArgument, "A native world request is required.");
            if (token.IsCancellationRequested || lifetime.IsStopping) return Failure<IInternalWorldPreparation>(ModErrorCode.Cancelled, "The world owner stopped.");
            WorldPreparation? preparation = null;
            OperationResult<IInternalWorldPreparation> result;
            try
            {
                var created = backend.CreateLoad(request);
                if (!created.TryGetValue(out var load)) return Failure<IInternalWorldPreparation>(created.ErrorCode, created.ErrorMessage);
                preparation = new WorldPreparation(load, lifetime.StoppingToken, token, clock, dispatcher);
                lifetime.Track(preparation);
                if (preparation.Token.IsCancellationRequested) throw new OperationCanceledException();
                var admission = transitions.Acquire(load.ExpectedSceneName, false, "world preparation");
                if (!admission.TryGetValue(out var lease))
                {
                    await preparation.CloseAsync();
                    return Failure<IInternalWorldPreparation>(admission.ErrorCode, admission.ErrorMessage);
                }
                preparation.Attach(lease);
                if (preparation.Token.IsCancellationRequested) throw new OperationCanceledException();
                if (load.RequiresDispatch)
                {
                    var dispatched = lease.Transitions.TryDispatch(new NativeSceneRequest(load.ExpectedSceneName, false, "world preparation"), load, preparation.Token);
                    if (!dispatched.TryGetValue(out var operation))
                        result = Failure<IInternalWorldPreparation>(dispatched.ErrorCode, dispatched.ErrorMessage);
                    else
                    {
                        preparation.Attach(operation);
                        var completed = await operation.Completion;
                        await operation.NativeDrained;
                        var terminal = await operation.NativeCompletion;
                        result = !terminal.Succeeded ? Failure<IInternalWorldPreparation>(terminal.ErrorCode,
                            (completed.Succeeded ? string.Empty : "Caller: " + completed.ErrorCode + ": " + completed.ErrorMessage + "; ") + terminal.ErrorMessage)
                            : !completed.Succeeded ? Failure<IInternalWorldPreparation>(completed.ErrorCode, completed.ErrorMessage)
                            : await CompletePreparationAsync(preparation);
                    }
                }
                else result = await CompletePreparationAsync(preparation);
            }
            catch (Exception error) { result = Failure<IInternalWorldPreparation>((error is OperationCanceledException || (error is ObjectDisposedException && (lifetime.IsStopping || token.IsCancellationRequested))) ? ModErrorCode.Cancelled : ModErrorCode.External, error.Message); }
            if (!result.Succeeded && preparation != null)
            {
                try { await preparation.CloseAsync(); }
                catch (Exception cleanup) { result = Failure<IInternalWorldPreparation>(ModErrorCode.External, result.ErrorMessage + "; world cleanup failed: " + cleanup.Message); }
            }
            return result;
        }

        private static async Task<OperationResult<IInternalWorldPreparation>> CompletePreparationAsync(WorldPreparation preparation)
        {
            var ready = await preparation.PrepareAsync();
            return ready.Succeeded ? OperationResult<IInternalWorldPreparation>.Success(preparation)
                : Failure<IInternalWorldPreparation>(ready.ErrorCode, ready.ErrorMessage);
        }
        private static OperationResult<T> Failure<T>(ModErrorCode code, string message) where T : notnull => OperationResult<T>.Failure(code, message);
    }
}
