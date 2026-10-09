using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed class WorldPreparation : IInternalWorldPreparation
    {
        private readonly IWorldNativeLoad native;
        private readonly IWorldRuntimeClock clock;
        private readonly IHostDispatcher dispatcher;
        private readonly CancellationTokenSource stopping;
        private readonly CancellationToken token;
        private IInternalSceneTransitionLease? admission;
        private IInternalNativeSceneOperation? operation;
        private WorldSceneIdentity? scene;
        private Task? cleanup;
        private bool finishing;
        internal WorldPreparation(IWorldNativeLoad native, CancellationToken ownerToken, CancellationToken callerToken,
            IWorldRuntimeClock clock, IHostDispatcher dispatcher)
        {
            this.native = native; this.clock = clock; this.dispatcher = dispatcher;
            stopping = CancellationTokenSource.CreateLinkedTokenSource(ownerToken, callerToken);
            token = stopping.Token;
        }
        internal CancellationToken Token => token;
        public WorldSceneIdentity Scene => scene ?? throw new InvalidOperationException("The native scene has not been prepared.");
        public TransformState? NativeDefaultSpawn { get; private set; }
        internal void Attach(IInternalNativeSceneOperation value) { operation = value; }
        internal void Attach(IInternalSceneTransitionLease value) { admission = value; }

        internal async Task<OperationResult<bool>> PrepareAsync()
        {
            Token.ThrowIfCancellationRequested();
            var captured = native.CaptureScene();
            if (!captured.TryGetValue(out var value)) return Failure<bool>(captured.ErrorCode, captured.ErrorMessage);
            scene = value;
            var spawn = await WaitAsync(native.ReadDefaultSpawn, Token);
            if (!spawn.TryGetValue(out var transform)) return Failure<bool>(spawn.ErrorCode, spawn.ErrorMessage);
            if (!Valid(transform)) return Failure<bool>(ModErrorCode.InvalidState, "The native default spawn is not a valid transform.");
            NativeDefaultSpawn = transform;
            return OperationResult<bool>.Success(true);
        }

        public Task<OperationResult<WorldReadiness>> FinishAsync(WorldSpawnPolicy policy, IEntity? contentRoot,
            TransformState? providerSpawn, CancellationToken token) => dispatcher.InvokeCallbackAsync(async () =>
        {
            if (cleanup != null || Token.IsCancellationRequested || token.IsCancellationRequested)
                return Failure<WorldReadiness>(ModErrorCode.Cancelled, "The prepared world stopped.");
            if (finishing || scene == null) return Failure<WorldReadiness>(ModErrorCode.InvalidState, "World readiness can be completed once after preparation.");
            finishing = true;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(Token, token);
            try
            {
                if (policy == null) return Failure<WorldReadiness>(ModErrorCode.InvalidArgument, "A world spawn policy is required.");
                var content = native.ValidateContent(contentRoot);
                if (!content.Succeeded) return Failure<WorldReadiness>(content.ErrorCode, content.ErrorMessage);
                TransformState selected;
                if (policy.Kind == WorldSpawnKind.AuthoredMarker)
                {
                    var markers = native.FindSpawnMarkers(contentRoot, policy.MarkerName!);
                    if (!markers.TryGetValue(out var values)) return Failure<WorldReadiness>(markers.ErrorCode, markers.ErrorMessage);
                    if (values.Count != 1) return Failure<WorldReadiness>(values.Count == 0 ? ModErrorCode.NotFound : ModErrorCode.Conflict,
                        "The selected content must contain exactly one authored spawn marker '" + policy.MarkerName + "'.");
                    selected = values[0];
                }
                else if (providerSpawn.HasValue || NativeDefaultSpawn.HasValue) selected = providerSpawn ?? NativeDefaultSpawn!.Value;
                else return Failure<WorldReadiness>(ModErrorCode.Unavailable, "The world has no resolved default spawn.");
                if (!Valid(selected)) return Failure<WorldReadiness>(ModErrorCode.InvalidArgument, "The resolved spawn is not a valid transform.");
                var applied = await WaitAsync(() => native.ApplySpawn(selected), linked.Token);
                if (!applied.TryGetValue(out var actual)) return Failure<WorldReadiness>(applied.ErrorCode, applied.ErrorMessage);
                if (!Valid(actual)) return Failure<WorldReadiness>(ModErrorCode.InvalidState, "The applied spawn is not a valid transform.");
                linked.Token.ThrowIfCancellationRequested();
                var validScene = native.ValidateContent(contentRoot);
                return validScene.Succeeded ? OperationResult<WorldReadiness>.Success(new WorldReadiness(Scene, actual))
                    : Failure<WorldReadiness>(validScene.ErrorCode, validScene.ErrorMessage);
            }
            catch (Exception error) { return Failure<WorldReadiness>(error is OperationCanceledException ? ModErrorCode.Cancelled : ModErrorCode.External, error.Message); }
            finally { ReleaseAdmission(); }
        });

        private async Task<OperationResult<TransformState>> WaitAsync(Func<OperationResult<TransformState>?> probe, CancellationToken token)
        {
            var deadline = clock.UtcNow + TimeSpan.FromSeconds(30);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var valid = native.ValidateScene();
                if (!valid.Succeeded) return Failure<TransformState>(valid.ErrorCode, valid.ErrorMessage);
                var result = probe();
                token.ThrowIfCancellationRequested();
                if (result != null) return result;
                if (clock.UtcNow >= deadline) return Failure<TransformState>(ModErrorCode.TimedOut, "World scene/player/spawn readiness did not complete within 30 seconds.");
                var frame = await clock.NextFrameAsync(token);
                if (!frame.Succeeded) return Failure<TransformState>(frame.ErrorCode, frame.ErrorMessage);
            }
        }
        private static bool Valid(TransformState value) => value.Position.IsFinite && value.Rotation.IsFinite
            && value.Rotation.LengthSquared > 0.000001f && value.Scale.IsFinite
            && value.Scale.X != 0 && value.Scale.Y != 0 && value.Scale.Z != 0;

        public void Dispose()
        {
            if (!dispatcher.IsCurrent) { dispatcher.Post(Dispose); return; }
            var closed = CloseAsync();
            if (closed.IsCompleted) closed.GetAwaiter().GetResult();
        }
        internal Task CloseAsync()
        {
            if (cleanup != null) return cleanup;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            cleanup = completion.Task; // Publish before cancellation callbacks can re-enter disposal.
            _ = CompleteCloseAsync(completion);
            return cleanup;
        }
        private async Task CompleteCloseAsync(TaskCompletionSource<bool> completion)
        {
            try { await CloseCoreAsync(); completion.TrySetResult(true); }
            catch (Exception error) { completion.TrySetException(error); }
        }
        private void ReleaseAdmission() { var held = admission; admission = null; held?.Dispose(); }
        private async Task CloseCoreAsync()
        {
            var failures = new List<Exception>();
            try { stopping.Cancel(); } catch (Exception error) { failures.Add(error); }
            if (operation != null)
            {
                var drained = false;
                try { await operation.NativeDrained; drained = true; } catch (Exception error) { failures.Add(error); }
                if (drained || operation.NativeCompletion.IsCompleted)
                {
                    try
                    {
                        var terminal = await operation.NativeCompletion;
                        var caller = operation.Completion.IsCompleted ? await operation.Completion : null;
                        if (!terminal.Succeeded && (caller == null || caller.ErrorCode != terminal.ErrorCode || caller.ErrorMessage != terminal.ErrorMessage))
                            failures.Add(new InvalidOperationException("Native work failed after the caller outcome: " + terminal.ErrorCode + ": " + terminal.ErrorMessage));
                    }
                    catch (Exception error) { failures.Add(error); }
                }
            }
            try { ReleaseAdmission(); } catch (Exception error) { failures.Add(error); }
            try { await dispatcher.InvokeAsync(native.Dispose); } catch (Exception error) { failures.Add(error); }
            try { stopping.Dispose(); } catch (Exception error) { failures.Add(error); }
            if (failures.Count == 1) throw failures[0];
            if (failures.Count > 1) throw new AggregateException("World preparation cleanup failed.", failures);
        }
        private static OperationResult<T> Failure<T>(ModErrorCode code, string message) where T : notnull => OperationResult<T>.Failure(code, message);
    }
}
