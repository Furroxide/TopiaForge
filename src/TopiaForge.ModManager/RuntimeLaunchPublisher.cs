using System;
using System.Collections.Generic;
using System.Diagnostics;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager
{
    /// <summary>Publishes one owned request's evidence without changing session admission or outcomes.</summary>
    internal sealed class RuntimeLaunchPublisher : IDisposable
    {
        private readonly object gate = new object();
        private readonly GamemodeSessionOrchestrator sessions;
        private readonly LaunchStagingStore store;
        private readonly string? requestId;
        private readonly LaunchSessionIdentity? session;
        private readonly Action<Exception> report;
        private readonly Stopwatch retryClock = Stopwatch.StartNew();
        private readonly Func<long> milliseconds;
        private readonly Dictionary<string, int> observations = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<string> outcomes = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, LaunchOutcome> pendingOutcomes = new Dictionary<string, LaunchOutcome>(StringComparer.Ordinal);
        private LaunchProgress? pendingProgress;
        private int progressSequence = -1;
        private long retryAfter;
        private RuntimeSessionSnapshot? publishedSnapshot;
        private RuntimeBindingRegistry? lastBindings;
        private bool disposed;
        /// <param name="session">
        /// The session a V5 request launches. Its records are then published as progress and outcomes V2: see
        /// <see cref="LaunchSessionPublication"/>. Null for every other launch, whose records stay version 1.
        /// </param>
        /// <param name="sessionRefusal">
        /// Why this process will not host or join <paramref name="session"/>, as in crash recovery. Nothing would report the
        /// session then, so its launch outcome fails at once with this reason instead of leaving the launcher unconfirmed.
        /// </param>
        internal RuntimeLaunchPublisher(GamemodeSessionOrchestrator sessions, LaunchStagingStore store, string? requestId, Action<Exception> report,
            Func<long>? milliseconds = null, LaunchSessionIdentity? session = null, string? sessionRefusal = null)
        {
            this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.report = report ?? throw new ArgumentNullException(nameof(report));
            this.milliseconds = milliseconds ?? (() => retryClock.ElapsedMilliseconds);
            if (requestId != null) _ = LaunchStorageKeys.Request(requestId);
            if (session != null && session.RequestId != requestId) throw new ArgumentException("A session launch publishes under its own request.", nameof(session));
            this.requestId = requestId;
            this.session = session;
            if (sessionRefusal != null)
            {
                if (session == null) throw new ArgumentException("Only a session launch is refused as one.", nameof(sessionRefusal));
                pendingOutcomes.Add("launch", new LaunchOutcome("launch", session.RequestId, 0, "idle", "failed", Array.Empty<LaunchBlock>(),
                    command: session.Command, error: new LaunchExecutionError("unavailable", sessionRefusal),
                    multiplayer: new LaunchOutcomeMultiplayer(session.Role, session.IntentId)));
                WritePendingOutcome("launch");
            }

            sessions.Progress += Progress;
            sessions.Outcome += Outcome;
        }
        private void Progress(LaunchProgress progress)
        {
            lock (gate)
            {
                if (disposed || requestId == null || progress.RequestId != requestId || progress.Sequence <= progressSequence
                    || (pendingProgress != null && progress.Sequence <= pendingProgress.Sequence)) return;
                pendingProgress = session == null ? progress : LaunchSessionPublication.ToSessionProgress(progress, session);
                WritePendingProgress();
            }
        }
        private void Outcome(LaunchOutcome outcome)
        {
            lock (gate)
            {
                if (disposed || requestId == null || outcome.RequestId != requestId || outcomes.Contains(outcome.Kind)
                    || pendingOutcomes.ContainsKey(outcome.Kind)) return;
                if (session != null)
                {
                    var published = LaunchSessionPublication.ToSessionOutcome(outcome, session);
                    if (published == null) return;
                    outcome = published;
                }

                pendingOutcomes.Add(outcome.Kind, outcome);
                WritePendingOutcome(outcome.Kind);
            }
        }
        private void WritePendingProgress()
        {
            if (pendingProgress == null) return;
            try { store.WriteProgress(pendingProgress); progressSequence = pendingProgress.Sequence; pendingProgress = null; }
            catch (Exception error) { PublicationFailed(error); }
        }
        private void WritePendingOutcome(string kind)
        {
            if (!pendingOutcomes.TryGetValue(kind, out var outcome)) return;
            try { store.WriteOutcome(outcome); outcomes.Add(kind); pendingOutcomes.Remove(kind); }
            catch (Exception error) { PublicationFailed(error); }
        }
        private void FlushPending()
        {
            // Three bounded records: latest progress, immutable launch result, immutable terminal result.
            WritePendingProgress();
            WritePendingOutcome("launch");
            WritePendingOutcome("session");
        }
        internal void PublishObservations(RuntimeBindingRegistry bindings)
        {
            lock (gate)
            {
                if (disposed) return;
                lastBindings = bindings;
                if (milliseconds() < retryAfter) return;
                FlushPending();
                PublishSnapshot(bindings);
            }
        }
        private void PublishSnapshot(RuntimeBindingRegistry bindings)
        {
            var snapshot = bindings.Capture();
            if (ReferenceEquals(snapshot, publishedSnapshot)) return;
            if (PublishObservations(bindings.CaptureObservations())) publishedSnapshot = snapshot;
        }
        internal bool PublishObservations(IEnumerable<RuntimeObservationEnvelope> values)
        {
            lock (gate)
            {
                if (disposed) return false;
                var completed = true;
                foreach (var observation in values)
                {
                    var key = LaunchStorageKeys.Observation(observation);
                    if (observations.TryGetValue(key, out var previous) && observation.ObservationRevision <= previous) continue;
                    try { store.WriteObservation(observation); observations[key] = observation.ObservationRevision; }
                    catch (Exception error) { completed = false; PublicationFailed(error); }
                }
                return completed;
            }
        }
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                // Update has stopped during teardown. Attempt each pending file once, without waiting or emitting events.
                FlushPending();
                if (lastBindings != null)
                    try { PublishSnapshot(lastBindings); } catch (Exception error) { Report(error); }
                disposed = true;
                sessions.Progress -= Progress;
                sessions.Outcome -= Outcome;
                pendingProgress = null;
                pendingOutcomes.Clear();
                lastBindings = null;
            }
        }
        private void PublicationFailed(Exception error)
        {
            retryAfter = milliseconds() + 250;
            Report(error);
        }
        private void Report(Exception error) { try { report(error); } catch { } }
    }
}
