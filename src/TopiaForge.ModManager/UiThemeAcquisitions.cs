using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>Serializes the registration and retirement of each descriptor within an admitted package.</summary>
    internal sealed class UiThemeAcquisitions
    {
        internal static readonly OperationResult<bool> Ready = OperationResult<bool>.Success(true);
        internal static readonly OperationResult<bool> Cancelled =
            OperationResult<bool>.Failure(ModErrorCode.Cancelled, "$.ui: The UI resource owner is stopping.");
        internal static readonly OperationResult<bool> Unavailable =
            OperationResult<bool>.Failure(ModErrorCode.Unavailable, "$.ui: UI bundle acquisition is unavailable.");
        private static readonly OperationResult<bool> BudgetExceeded =
            OperationResult<bool>.Failure(ModErrorCode.RateLimited, "$.ui: The package exceeds its resident UI resource budget.");

        private readonly IHostDispatcher dispatcher;
        private readonly IModLifetime package;
        private readonly IUiThemeNativeLoader loader;
        private readonly Dictionary<UiThemeDescriptor, Entry> entries = new Dictionary<UiThemeDescriptor, Entry>();
        private bool stopped;

        internal UiThemeAcquisitions(IHostDispatcher dispatcher, IModLifetime package, IUiThemeNativeLoader loader)
        {
            this.dispatcher = dispatcher;
            this.package = package;
            this.loader = loader;
            package.StoppingToken.Register(() => OnMain(Stop));
        }

        internal UiThemeDemand? Demand(UiThemeDescriptor descriptor, Action<OperationResult<bool>>? settled = null,
            Action<UiThemeDemand>? detached = null, bool retryFailed = true)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (stopped || package.IsStopping || !HasNativeResources(descriptor)) return null;
            if (!entries.TryGetValue(descriptor, out var entry))
                entries.Add(descriptor, entry = new Entry(descriptor));

            var demand = new UiThemeDemand(this, descriptor, settled, detached);
            entry.Demands.Add(demand);
            switch (entry.State)
            {
                case State.Idle:
                case State.Failed when retryFailed:
                    Start(entry);
                    break;
                case State.Draining when entry.Failure == null || retryFailed:
                    entry.QueuedRetry = true;
                    break;
            }
            return demand;
        }

        internal static bool HasNativeResources(UiThemeDescriptor descriptor) =>
            descriptor.Images.Count != 0 || descriptor.Fonts.Count != 0;

        internal UiThemeRegistration? Registration(UiThemeDescriptor descriptor) =>
            !stopped && !package.IsStopping && entries.TryGetValue(descriptor, out var entry) ? entry.Registration : null;

        internal OperationResult<bool>? Outcome(UiThemeDescriptor descriptor)
        {
            if (stopped || package.IsStopping) return Cancelled;
            if (!entries.TryGetValue(descriptor, out var entry)) return Unavailable;
            if (entry.Registration != null) return Ready;
            return entry.QueuedRetry && !entry.RetirementFailed ? null : entry.Failure;
        }

        internal void Release(UiThemeDemand demand)
        {
            UnityMainThreadGuard.AssertCurrent();
            var entry = entries[demand.Descriptor];
            entry.Demands.Remove(demand);
            if (entry.Demands.Count != 0) return;
            entry.QueuedRetry = false;
            if (entry.State == State.Ready || entry.State == State.Loading)
                BeginRetirement(entry, entry.Current!);
        }

        internal void OnMain(Action action)
        {
            if (dispatcher.IsCurrent) action();
            else dispatcher.Post(action);
        }

        private void Start(Entry entry)
        {
            if (stopped || package.IsStopping || entry.Demands.Count == 0) return;
            var failure = Unavailable;
            if (package is IInternalNativeWorkLifetime native)
            {
                try
                {
                    var attempt = new Attempt(native.RegisterNativeWork("UI theme registration"));
                    entry.Current = attempt;
                    entry.State = State.Loading;
                    entry.Failure = null;
                    // Registration can resolve inline for cached resources. Publish only after the caller installed its lease.
                    dispatcher.Post(() => _ = LoadAsync(entry, attempt));
                    return;
                }
                catch (ObjectDisposedException) when (package.IsStopping)
                {
                    failure = Cancelled;
                }
            }
            entry.Failure = failure;
            entry.State = State.Failed;
            dispatcher.Post(() =>
            {
                if (ReferenceEquals(entry.Failure, failure)) Notify(entry, failure);
            });
        }

        private async Task LoadAsync(Entry entry, Attempt attempt)
        {
            OperationResult<UiThemeRegistration> result;
            try
            {
                result = attempt.Owner.IsStopping
                    ? OperationResult<UiThemeRegistration>.Failure(Cancelled.ErrorCode, Cancelled.ErrorMessage)
                    : await loader.LoadAsync(entry.Descriptor, attempt.Owner).ConfigureAwait(false);
            }
            catch (Exception)
            {
                result = OperationResult<UiThemeRegistration>.Failure(ModErrorCode.External, "$.ui: The native UI resource load failed.");
            }

            await dispatcher.InvokeAsync(() => FinishLoad(entry, attempt, result)).ConfigureAwait(false);
        }

        private void FinishLoad(Entry entry, Attempt attempt, OperationResult<UiThemeRegistration> result)
        {
            if (!ReferenceEquals(entry.Current, attempt)) return;
            attempt.LoadFinished = true;
            if (entry.State == State.Draining || stopped || package.IsStopping || entry.Demands.Count == 0)
            {
                BeginRetirement(entry, attempt);
                return;
            }
            if (result.TryGetValue(out var registration) && FitsBudget(entry.Descriptor, registration))
            {
                entry.Registration = registration;
                entry.State = State.Ready;
                registration.Commit();
                Notify(entry, Ready);
                return;
            }

            var failure = result.Succeeded ? BudgetExceeded
                : OperationResult<bool>.Failure(result.ErrorCode, result.ErrorMessage);
            entry.Failure = failure;
            entry.State = State.Draining;
            Notify(entry, failure);
            BeginRetirement(entry, attempt);
        }

        private void BeginRetirement(Entry entry, Attempt attempt)
        {
            entry.Registration = null;
            entry.State = State.Draining;
            attempt.Owner.BeginStop();
            if (!attempt.LoadFinished || attempt.RetirementStarted) return;
            attempt.RetirementStarted = true;
            _ = DrainAsync(entry, attempt);
        }

        private async Task DrainAsync(Entry entry, Attempt attempt)
        {
            Exception? failure = null;
            try { await attempt.Owner.DrainNativeWorkAsync().ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
            await dispatcher.InvokeAsync(() => FinishRetirement(entry, attempt, failure)).ConfigureAwait(false);
        }

        private void FinishRetirement(Entry entry, Attempt attempt, Exception? failure)
        {
            try { attempt.Owner.Dispose(); }
            catch (Exception error) { failure = failure == null ? error : new AggregateException(failure, error); }

            if (failure != null)
            {
                // A failed observer/cleanup cannot certify that it is safe to start another generation.
                entry.RetirementFailed = true;
                entry.Failure = OperationResult<bool>.Failure(ModErrorCode.Unavailable, "$.ui: UI resource retirement failed.");
                attempt.PackageTicket.Complete(failure);
                if (!stopped && entry.Demands.Count != 0) Notify(entry, entry.Failure);
                return;
            }

            attempt.PackageTicket.Complete();
            entry.Current = null;
            entry.State = entry.Failure == null ? State.Idle : State.Failed;
            var retry = entry.QueuedRetry;
            entry.QueuedRetry = false;
            if (retry) Start(entry);
        }

        private void Stop()
        {
            if (stopped) return;
            stopped = true;
            foreach (var entry in entries.Values)
            {
                entry.QueuedRetry = false;
                Notify(entry, Cancelled);
                if (entry.Current != null) BeginRetirement(entry, entry.Current);
            }
        }

        private static void Notify(Entry entry, OperationResult<bool> outcome)
        {
            foreach (var demand in entry.Demands.ToArray())
            {
                // A renderer failure cannot keep other callers waiting or strand the attempt's owner.
                try { demand.Notify(outcome); }
                catch (Exception) { }
            }
        }

        private bool FitsBudget(UiThemeDescriptor descriptor, UiThemeRegistration registration)
        {
            const long maximumBytes = 64L * 1024 * 1024;
            var textures = new Dictionary<int, long>();
            var fonts = new HashSet<(string, string)>();
            var images = descriptor.Images.Count;
            long bytes = 0;
            AddFonts(descriptor);
            if (!AddTextures(registration)) return false;
            foreach (var entry in entries.Values)
            {
                if (entry.Registration == null) continue;
                images += entry.Descriptor.Images.Count;
                AddFonts(entry.Descriptor);
                if (!AddTextures(entry.Registration)) return false;
            }
            return images <= 256 && fonts.Count <= 16;

            void AddFonts(UiThemeDescriptor theme)
            {
                foreach (var font in theme.Fonts) fonts.Add((font.BundlePath, font.AssetName));
            }

            bool AddTextures(UiThemeRegistration resources)
            {
                foreach (var texture in resources.Textures)
                {
                    if (texture.Value <= 0) return false;
                    if (textures.TryGetValue(texture.Key, out var known))
                    {
                        if (texture.Value <= known) continue;
                        var added = texture.Value - known;
                        if (added > maximumBytes - bytes) return false;
                        textures[texture.Key] = texture.Value;
                        bytes += added;
                        continue;
                    }
                    if (textures.Count >= 4 || texture.Value > maximumBytes - bytes) return false;
                    textures.Add(texture.Key, texture.Value);
                    bytes += texture.Value;
                }
                return true;
            }
        }

        private enum State { Idle, Loading, Ready, Failed, Draining }

        private sealed class Entry
        {
            internal Entry(UiThemeDescriptor descriptor) { Descriptor = descriptor; }
            internal readonly UiThemeDescriptor Descriptor;
            internal readonly List<UiThemeDemand> Demands = new List<UiThemeDemand>();
            internal State State;
            internal Attempt? Current;
            internal UiThemeRegistration? Registration;
            internal OperationResult<bool>? Failure;
            internal bool QueuedRetry;
            internal bool RetirementFailed;
        }

        // Attempt identity is the generation token. The parent ticket lasts through native retirement and resource release.
        private sealed class Attempt
        {
            internal Attempt(AssetNativeWorkTicket packageTicket) { PackageTicket = packageTicket; }
            internal readonly OwnerModLifetime Owner = new OwnerModLifetime();
            internal readonly AssetNativeWorkTicket PackageTicket;
            internal bool LoadFinished;
            internal bool RetirementStarted;
        }
    }
}
