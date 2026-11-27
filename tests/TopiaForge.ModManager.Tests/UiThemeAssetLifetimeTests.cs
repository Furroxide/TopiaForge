using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static class UiThemeAssetLifetimeTests
    {
        internal static void Run()
        {
            UnityMainThreadGuard.CaptureCurrentThread();
            SiblingsAndWaitersOwnSeparateDemands();
            RedemandWaitsForPhysicalRetirement();
            AllQueuedDemandsCanStop();
            ObservationFailureKeepsTheObserver();
            SharesTexturesWithinThePackageBudget();
            InheritedSelectionIsCheckedBeforePublication();
            Console.WriteLine("UI theme assets: sibling and waiter ownership, retirement, observation failure and distinct residency passed.");
        }

        private static void InheritedSelectionIsCheckedBeforePublication()
        {
            using var lifetime = new FakeModLifetime();
            using var childLifetime = new FakeModLifetime();
            var badges = new UiThemeRef("badge-pack");
            var boxes = new UiThemeRef("box-pack");
            var badgeTheme = Pack(badges.Id, "badge");
            var boxTheme = Pack(boxes.Id, "box");
            var definitions = new Dictionary<string, UiThemeDescriptor>(StringComparer.Ordinal)
            {
                [badgeTheme.Id] = badgeTheme,
                [boxTheme.Id] = boxTheme,
            };
            using var parent = new UiThemeScope(lifetime, new AdmittedUiCatalogue(null, definitions));
            using var child = new UiThemeScope(childLifetime, null, parent);
            Check(parent.UseTheme(badges).Succeeded, "Admit the initial selection.");
            var content = new UiBadge("Stocked", style: new UiStyleRef("status"));
            var preflights = 0;
            var publications = 0;
            child.ValidateLocalSelection = (changed, theme) =>
            {
                preflights++;
                return child.TryDescribe(null, out var candidate, changed, theme) &&
                    UiLayoutDecoration.AdmitsNamedStyles(content, candidate ?? UiThemePackParser.Default)
                    ? OperationResult<bool>.Success(true)
                    : OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "The style targets a different component.");
            };
            parent.PrepareSelection = (UiThemeScope _, UiThemeRef? _, out Action? commit) =>
            {
                publications++;
                commit = null;
                return OperationResult<bool>.Success(true);
            };

            Check(parent.UseTheme(boxes).ErrorCode == ModErrorCode.InvalidArgument && preflights == 1 && publications == 0 &&
                Equals(parent.SelectedTheme, badges), "Rejected inherited styles stop before publication or selection mutation.");
            Check(child.UseTheme(badges).Succeeded && parent.UseTheme(boxes).Succeeded && preflights == 2 && publications == 1,
                "A child with its own selection does not participate in a parent's change.");
            Check(child.UseTheme(null).ErrorCode == ModErrorCode.InvalidArgument && Equals(child.SelectedTheme, badges),
                "Clearing a child selection checks the candidate from its parent.");

            static UiThemeDescriptor Pack(string id, string target) => UiThemePackParser.Parse(Encoding.UTF8.GetBytes(
                "{\"schema\":1,\"id\":\"" + id + "\",\"styles\":[{\"id\":\"status\",\"target\":\"" + target + "\",\"radius\":\"small\"}]}")).Value!;
        }

        private static void SiblingsAndWaitersOwnSeparateDemands()
        {
            using var fixture = new Fixture("harbour");
            using var firstLifetime = new FakeModLifetime();
            using var siblingLifetime = new FakeModLifetime();
            using var first = fixture.Child(firstLifetime);
            using var sibling = fixture.Child(siblingLifetime);
            using var cancellation = new CancellationTokenSource();
            var theme = new UiThemeRef("harbour");
            var firstWaiter = first.PreloadAsync(theme);
            var cancelledWaiter = sibling.PreloadAsync(theme, cancellation.Token);
            var survivingWaiter = sibling.PreloadAsync(theme);
            fixture.Step();
            Check(fixture.Loader.Requests.Count == 1, "Sibling scopes and repeated preloads share one native attempt.");
            var attempt = fixture.Loader.Requests[0];
            firstLifetime.Dispose();
            Task.Run(cancellation.Cancel).GetAwaiter().GetResult();
            fixture.Step();
            Check(firstWaiter.IsCompleted && firstWaiter.Result.ErrorCode == ModErrorCode.Cancelled &&
                cancelledWaiter.IsCompleted && cancelledWaiter.Result.ErrorCode == ModErrorCode.Cancelled &&
                !survivingWaiter.IsCompleted && !attempt.Owner.IsStopping,
                "Stopping the initiating scope or cancelling an earlier call cannot cancel a sibling's live waiter.");

            attempt.Done = true;
            fixture.Until(() => survivingWaiter.IsCompleted);
            Check(survivingWaiter.Result.Succeeded &&
                ReferenceEquals(sibling.Registration(fixture["harbour"])?.Image<NativeResource>("sail"), attempt.Resource),
                "A surviving sibling retains a ready pack, even without a visible surface.");
            Check(ReferenceEquals(sibling.PreloadAsync(theme), sibling.PreloadAsync(theme)) && attempt.Resource.Disposals == 0,
                "Repeated settled calls reuse the scope's retained preload.");
            siblingLifetime.Dispose();
            fixture.Until(() => attempt.Resource.Disposals == 1);
            Check(fixture.Acquisitions.Registration(fixture["harbour"]) == null && attempt.Disposals == 1,
                "The last preload owner releases its registration and native resources exactly once.");
        }

        private static void AllQueuedDemandsCanStop()
        {
            using var fixture = new Fixture("harbour");
            using var cancellation = new CancellationTokenSource();
            using var queuedCancellation = new CancellationTokenSource();
            var theme = new UiThemeRef("harbour");
            var first = fixture.Scope.PreloadAsync(theme, cancellation.Token);
            fixture.Step();
            var retired = fixture.Loader.Requests[0];
            cancellation.Cancel();
            var queued = fixture.Scope.PreloadAsync(theme, queuedCancellation.Token);
            queuedCancellation.Cancel();
            Check(first.IsCompleted && queued.IsCompleted && first.Result.ErrorCode == ModErrorCode.Cancelled &&
                queued.Result.ErrorCode == ModErrorCode.Cancelled, "Both callers detach before native retirement.");

            retired.Done = true;
            fixture.Drain();
            Check(retired.Resource.Disposals == 1 && fixture.Loader.Requests.Count == 1,
                "Retirement must not start another attempt when every queued demand ended.");
        }

        private static void RedemandWaitsForPhysicalRetirement()
        {
            using var fixture = new Fixture("harbour");
            using var firstLifetime = new FakeModLifetime();
            using var queuedLifetime = new FakeModLifetime();
            using var first = fixture.Child(firstLifetime);
            using var queued = fixture.Child(queuedLifetime);
            var theme = new UiThemeRef("harbour");
            var firstWaiter = first.PreloadAsync(theme);
            fixture.Step();
            var old = fixture.Loader.Requests[0];
            firstLifetime.Dispose();
            Check(firstWaiter.IsCompleted && firstWaiter.Result.ErrorCode == ModErrorCode.Cancelled &&
                old.Owner.IsStopping && old.Resource.Disposals == 0,
                "The last demand retires the attempt immediately without implying native work is finished.");

            var changes = 0;
            queued.Changed += () => changes++;
            Check(queued.UseTheme(theme).Succeeded, "A new scope may select while the old attempt drains.");
            var waiter = queued.PreloadAsync(theme);
            fixture.Step();
            Check(fixture.Loader.Requests.Count == 1 && changes == 1 && !waiter.IsCompleted &&
                queued.ReadyDescriptor(fixture["harbour"]) == null,
                "A demand during native drain queues instead of starting or reviving an attempt.");
            old.Done = true;
            fixture.Until(() => fixture.Loader.Requests.Count == 2,
                "Exactly one fresh attempt must start after physical retirement.");
            Check(changes == 1 && old.Disposals == 1 && old.Resource.Disposals == 1 && !waiter.IsCompleted,
                "Retirement disposes the late result exactly once without publishing a layout refresh.");
            fixture.Loader.Requests[1].Done = true;
            fixture.Until(() => waiter.IsCompleted);
            Check(waiter.Result.Succeeded && changes == 2 && ReferenceEquals(queued.ReadyDescriptor(fixture["harbour"]), fixture["harbour"]),
                "Only the fresh generation commits and refreshes the selected scope.");
        }

        private static void ObservationFailureKeepsTheObserver()
        {
            using var fixture = new Fixture("harbour");
            var theme = new UiThemeRef("harbour");
            var waiter = fixture.Scope.PreloadAsync(theme);
            fixture.Step();
            var request = fixture.Loader.Requests[0];
            request.ObservationFails = true;
            fixture.Until(() => waiter.IsCompleted);
            Check(waiter.Result.ErrorCode == ModErrorCode.External && request.Disposals == 0,
                "A poll failure is observable while the native request remains owned.");
            var retry = fixture.Scope.PreloadAsync(theme);
            fixture.Step();
            Check(!retry.IsCompleted && fixture.Loader.Requests.Count == 1,
                "An indeterminate native retirement does not allow speculative native loads.");
            request.ObservationFails = false;
            request.Done = true;
            fixture.Until(() => retry.IsCompleted || fixture.Loader.Requests.Count != 1);
            Check(retry.IsCompleted && retry.Result.ErrorCode == ModErrorCode.Unavailable && fixture.Loader.Requests.Count == 1 &&
                request.Disposals == 1 && request.Resource.Disposals == 1,
                "Observed retirement releases the late result and reports the failed observation without restarting.");
            fixture.ExpectDrainFailure = true;
        }

        private static void SharesTexturesWithinThePackageBudget()
        {
            using var fixture = new Fixture("harbour", "mesa", "heights");
            const long mib = 1024 * 1024;
            fixture.Loader.Textures["harbour"] = new Dictionary<int, long> { [12] = 48 * mib };
            fixture.Loader.Textures["mesa"] = new Dictionary<int, long> { [12] = 48 * mib };
            fixture.Loader.Textures["heights"] = new Dictionary<int, long> { [15] = 17 * mib };
            using var first = fixture.Demand("harbour");
            using var second = fixture.Demand("mesa");
            fixture.Step();
            fixture.Loader.Requests[0].Done = true;
            fixture.Loader.Requests[1].Done = true;
            fixture.Until(() => first.Outcome != null && second.Outcome != null);
            Check(first.Registration != null && second.Registration != null,
                "Two descriptors using the same resident texture count it once.");

            using var excess = fixture.Demand("heights");
            fixture.Step();
            var rejected = fixture.Loader.Requests[2];
            rejected.Done = true;
            fixture.Until(() => excess.Outcome != null && rejected.Resource.Disposals != 0);
            Check(excess.Outcome!.ErrorCode == ModErrorCode.RateLimited && excess.Registration == null &&
                first.Registration != null && second.Registration != null && rejected.Resource.Disposals == 1,
                "A distinct texture over the package budget cannot commit or evict existing registrations.");
            first.Dispose();
            Check(second.Registration != null, "Retiring one descriptor preserves a sibling descriptor's registration.");
            second.Dispose();
            using var retry = fixture.Demand("heights");
            fixture.Until(() => fixture.Loader.Requests.Count == 4);
            fixture.Loader.Requests[3].Done = true;
            fixture.Until(() => retry.Outcome != null);
            Check(retry.Outcome!.Succeeded, "A later deliberate demand can retry after capacity is released.");
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class Fixture : IDisposable
        {
            private readonly HostDispatcher dispatcher = new HostDispatcher();
            private readonly OwnerModLifetime package = new OwnerModLifetime();
            private readonly Dictionary<string, UiThemeDescriptor> descriptors = new Dictionary<string, UiThemeDescriptor>(StringComparer.Ordinal);

            internal Fixture(params string[] names)
            {
                foreach (var name in names)
                {
                    var bytes = Encoding.UTF8.GetBytes($$"""
                        {"schema":1,"id":"{{name}}","images":[{"id":"sail","bundlePath":"ui/sail.bundle",
                        "assetName":"sail","licensePath":"LICENSES/sail.txt"}]}
                        """);
                    descriptors.Add(name, UiThemePackParser.Parse(bytes).Value ?? throw new InvalidOperationException("Fixture descriptor failed."));
                }
                Acquisitions = new UiThemeAcquisitions(dispatcher, package, Loader);
                Scope = new UiThemeScope(package, new AdmittedUiCatalogue(null, descriptors), acquisitions: Acquisitions);
            }

            internal FakeNativeLoader Loader { get; } = new FakeNativeLoader();
            internal UiThemeAcquisitions Acquisitions { get; }
            internal UiThemeScope Scope { get; }
            internal bool ExpectDrainFailure { get; set; }
            internal UiThemeDescriptor this[string name] => descriptors[name];

            internal UiThemeScope Child(FakeModLifetime lifetime) => new UiThemeScope(lifetime, null, Scope);
            internal UiThemeDemand Demand(string name) => Acquisitions.Demand(this[name]) ??
                throw new InvalidOperationException("Demand was refused by the test package.");

            internal void Step()
            {
                dispatcher.Drain();
                AssetNativeRequestPump.Poll();
            }

            internal void Until(Func<bool> condition, string message = "UI asset work did not settle.")
            {
                var timer = Stopwatch.StartNew();
                while (!condition())
                {
                    Step();
                    if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(message);
                    if (!condition()) Thread.Sleep(1);
                }
            }

            internal void Drain()
            {
                var drained = package.DrainNativeWorkAsync();
                Until(() => drained.IsCompleted);
                drained.GetAwaiter().GetResult();
                Step();
            }

            public void Dispose()
            {
                package.BeginStop();
                foreach (var entry in Loader.Requests) { entry.ObservationFails = false; entry.Done = true; }
                var stopped = package.DrainNativeWorkAsync();
                Until(() => stopped.IsCompleted);
                Step();
                Check(stopped.IsFaulted == ExpectDrainFailure, "Package drain reports any preserved native retirement failure.");
                package.Dispose();
                dispatcher.Dispose();
            }
        }

        private sealed class FakeNativeLoader : IUiThemeNativeLoader
        {
            internal readonly List<NativeRequest> Requests = new List<NativeRequest>();
            internal readonly Dictionary<string, IReadOnlyDictionary<int, long>> Textures = new Dictionary<string, IReadOnlyDictionary<int, long>>();

            public async Task<OperationResult<UiThemeRegistration>> LoadAsync(UiThemeDescriptor descriptor, OwnerModLifetime owner)
            {
                var request = new NativeRequest(owner);
                Requests.Add(request);
                var result = await AssetNativeOperation<NativeResource>.Start(owner, "test UI asset", () => request, default);
                if (!result.TryGetValue(out var asset))
                    return OperationResult<UiThemeRegistration>.Failure(result.ErrorCode, result.ErrorMessage);
                return OperationResult<UiThemeRegistration>.Success(new UiThemeRegistration(
                    new Dictionary<string, object> { ["sail"] = asset }, new Dictionary<UiTextStyle, object>(),
                    Textures.TryGetValue(descriptor.Id, out var textures) ? textures : new Dictionary<int, long>()));
            }
        }

        private sealed class NativeRequest : IAssetNativeRequest<NativeResource>
        {
            internal NativeRequest(OwnerModLifetime owner) { Owner = owner; }
            internal readonly OwnerModLifetime Owner;
            internal readonly NativeResource Resource = new NativeResource();
            internal bool Done;
            internal bool ObservationFails;
            internal int Disposals;
            public bool HasStarted { get; private set; }
            public bool IsDone => ObservationFails ? throw new InvalidOperationException("Test native observation failure.") : Done;
            public void Start() => HasStarted = true;
            public OperationResult<NativeResource> ReadResult() => OperationResult<NativeResource>.Success(Resource);
            public void Dispose() => Disposals++;
        }

        private sealed class NativeResource : IDisposable
        {
            internal int Disposals;
            public void Dispose() => Disposals++;
        }
    }
}
