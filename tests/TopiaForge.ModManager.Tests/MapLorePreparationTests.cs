using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Worlds.Maps;
using static TopiaForge.ModManager.Tests.MapDocumentTests;

namespace Cysharp.Threading.Tasks
{
    // Carries the full name the adapter checks for build 2640's plain UniTask. It is a test stand-in only.
    internal sealed class UniTask
    {
        internal readonly LoreAwaiter Awaiter = new LoreAwaiter();
        public LoreAwaiter GetAwaiter() => Awaiter;
    }

    internal sealed class LoreAwaiter
    {
        private Action? continuation;
        internal int Reads; internal Exception? Failure;
        public bool IsCompleted { get; private set; }
        public void GetResult() { Reads++; if (Failure != null) throw Failure; }
        public void OnCompleted(Action value) { continuation = value; }
        internal void Finish() { IsCompleted = true; continuation?.Invoke(); }
    }
}

namespace TopiaForge.ModManager.Tests
{
    internal static class MapLorePreparationTests
    {
        private const string Accepted = "https://catalog.test/";

        internal static async Task RunAsync()
        {
            await Drains();
            await LaunchRequestFirst();
            await AssetConfigFallback();
            await NoAddress();
            await RefusedShapes();
            await FailuresStayPrivate();
            await Cancellation();
            Console.WriteLine("Map lore preparation passed: build 2640 shape, address order, game validation, problem summary, drain, failure privacy and cancellation.");
        }

        private static async Task Drains()
        {
            var pending = new UniTask();
            var drain = MapLorePreparation.DrainAsync(pending);
            Assert(!drain.IsCompleted, "native lore operation must drain before ownership can end");
            pending.Awaiter.Finish(); await drain;
            Assert(pending.Awaiter.Reads == 1, "native awaiter result is consumed once");

            Reset(launch: Accepted + "link", config: null); Loader.CompleteAtStart = true;
            var run = Prepare(new Logger());
            Assert(run.IsCompleted && !run.IsFaulted && !run.IsCanceled && Loader.Pending!.Awaiter.Reads == 1,
                "an operation that completed synchronously is read at once");
        }

        private static async Task LaunchRequestFirst()
        {
            Reset(launch: Accepted + "link", config: Accepted + "base");
            var logger = new Logger();
            var run = Prepare(logger);
            Assert(!run.IsCompleted, "preparation waits for the native lore operation");
            Loader.Pending!.Awaiter.Finish(); await run;
            Assert(Loader.Address == Accepted + "link/" && Loader.Constructed == 1,
                "the launch request's address wins and the loader receives the validator's result");
            Assert(logger.Info.Contains(MapLorePreparation.FromLaunchRequest) && !string.Join("|", logger.Info).Contains("catalog.test"),
                "the log names the address source, never the address");
            Assert(Loader.LastReport is Report report && report.Calls == 1, "each preparation passes one fresh problem report");
        }

        private static async Task AssetConfigFallback()
        {
            foreach (var launch in new[] { null, "", "   ", "https://elsewhere.test/link" })
            {
                Reset(launch, config: Accepted + "base");
                var logger = new Logger();
                var run = Prepare(logger); Loader.Pending!.Awaiter.Finish(); await run;
                Assert(Loader.Address == Accepted + "base/", "a blank or refused launch address falls back to the asset configuration");
                Assert(logger.Info.Contains(MapLorePreparation.FromAssetConfig), "the fallback source is logged");
            }
            Reset(launch: null, config: Accepted + "base"); LaunchRequest.Current = null;
            var none = Prepare(new Logger()); Loader.Pending!.Awaiter.Finish(); await none;
            Assert(Loader.Address == Accepted + "base/", "no launch request falls back to the asset configuration");
        }

        private static async Task NoAddress()
        {
            foreach (var config in new[] { null, "", "https://elsewhere.test/base" })
            {
                Reset(launch: "https://elsewhere.test/link", config);
                var error = await Refused(Prepare(new Logger()));
                Assert(error.Code == ModErrorCode.Unavailable && error.Message.Contains("catalog address") && !error.Message.Contains("test/"),
                    "without an address the game accepts, preparation fails closed with fixed text");
                Assert(Loader.Constructed == 0, "no loader is built without an accepted address");
            }
        }

        private static async Task RefusedShapes()
        {
            Reset(launch: null, config: Accepted + "base");
            foreach (var (loader, report) in new[] { (typeof(OldLoader), typeof(Report)), (typeof(Loader), (Type?)null),
                (typeof(GenericLoader), typeof(Report)), (typeof(UnvalidatedLoader), typeof(Report)) })
            {
                var error = await Refused(MapLorePreparation.PrepareAsync(new Project(), new Config(), new Logger(), loader, report,
                    typeof(LaunchRequest), default));
                Assert(error.Code == ModErrorCode.Unavailable, loader.Name + " is refused before any native call");
            }
            Assert(Loader.Constructed == 0 && OldLoader.Constructed == 0, "a refused shape constructs nothing");
        }

        private static async Task FailuresStayPrivate()
        {
            Reset(launch: Accepted + "link", config: null);
            Loader.Problems = 2;
            var logger = new Logger();
            var run = Prepare(logger);
            Loader.Pending!.Awaiter.Failure = new Exception("private C:/Users/someone/lore.json");
            Loader.Pending.Awaiter.Finish();
            var error = await Refused(run);
            Assert(error.Code == ModErrorCode.Unavailable && !error.Message.Contains("private"), "lore failures are fixed and private");
            var warnings = string.Join("|", logger.Warn);
            Assert(warnings.Contains("2 problems") && !warnings.Contains("someone"), "the report's problems are counted, never quoted");

            Reset(launch: Accepted + "link", config: null); Loader.ThrowOnStart = true;
            error = await Refused(Prepare(new Logger()));
            Assert(error.Code == ModErrorCode.Unavailable && !error.Message.Contains("private"), "a synchronous native failure is fixed and private");
        }

        private static async Task Cancellation()
        {
            Reset(launch: Accepted + "link", config: null);
            using var source = new CancellationTokenSource();
            var run = MapLorePreparation.PrepareAsync(new Project(), new Config(), new Logger(), typeof(Loader), typeof(Report),
                typeof(LaunchRequest), source.Token);
            source.Cancel();
            await Task.Yield();
            Assert(!run.IsCompleted, "a cancelled preparation still drains the native lore operation first");
            Loader.Pending!.Awaiter.Failure = new OperationCanceledException(source.Token);
            Loader.Pending.Awaiter.Finish();
            try { await run; throw new InvalidOperationException("cancelled lore preparation completed"); }
            catch (OperationCanceledException) { }

            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Reset(launch: Accepted + "link", config: null);
            try
            {
                await MapLorePreparation.PrepareAsync(new Project(), new Config(), new Logger(), typeof(Loader), typeof(Report),
                    typeof(LaunchRequest), cancelled.Token);
                throw new InvalidOperationException("a cancelled token reached the loader");
            }
            catch (OperationCanceledException) { Assert(Loader.Constructed == 0, "a cancelled token never reaches the loader"); }

            Reset(launch: Accepted + "link", config: null);
            run = Prepare(new Logger());
            Loader.Pending!.Awaiter.Failure = new OperationCanceledException();
            Loader.Pending.Awaiter.Finish();
            var error = await Refused(run);
            Assert(error.Code == ModErrorCode.Unavailable, "a native cancellation nobody requested is a failure, not a cancel");
        }

        private static Task Prepare(Logger logger) => MapLorePreparation.PrepareAsync(new Project(), new Config(), logger,
            typeof(Loader), typeof(Report), typeof(LaunchRequest), default);

        private static async Task<MapProblem> Refused(Task run)
        {
            try { await run; }
            catch (MapProblem error) { return error; }
            throw new InvalidOperationException("lore preparation succeeded where it must fail");
        }

        private static void Reset(string? launch, string? config)
        {
            LaunchRequest.Current = new LaunchRequest { CatalogUrl = launch };
            Config.Next = config;
            Loader.Constructed = 0; Loader.Address = null; Loader.Pending = null; Loader.LastReport = null;
            Loader.Problems = 0; Loader.ThrowOnStart = false; Loader.CompleteAtStart = false; OldLoader.Constructed = 0;
        }

        private static string Validate(string url) => url.StartsWith(Accepted, StringComparison.Ordinal)
            ? url.TrimEnd('/') + "/" : throw new ArgumentException("refused " + url);

        internal sealed class Project { }
        internal sealed class Config
        {
            internal static string? Next;
            public string? CatalogBaseUrl { get; } = Next;
        }
        internal sealed class Report
        {
            private readonly List<string> problems = new List<string>();
            internal int Calls;
            public IReadOnlyList<string> Problems => problems;
            internal void Add(string value) => problems.Add(value);
        }
        internal sealed class LaunchRequest
        {
            public static LaunchRequest? Current { get; set; }
            public string? CatalogUrl { get; set; }
        }
        internal sealed class Loader
        {
            internal static int Constructed, Problems; internal static string? Address; internal static UniTask? Pending;
            internal static object? LastReport; internal static bool ThrowOnStart, CompleteAtStart;
            public Loader(Config config, string linkCatalogUrl) { Constructed++; Address = linkCatalogUrl; }
            public UniTask AddMissingLoreAsync(Project project, Report report, CancellationToken token)
            {
                LastReport = report; report.Calls++;
                for (var problem = 0; problem < Problems; problem++) report.Add("C:/Users/someone/missing-" + problem);
                if (ThrowOnStart) throw new InvalidOperationException("private C:/Users/someone/lore.json");
                Pending = new UniTask();
                if (CompleteAtStart) Pending.Awaiter.Finish();
                return Pending;
            }
            public static string ValidateLinkCatalogUrl(string url) => Validate(url);
        }
        // Build 2597's shape: one constructor argument and an exception returned as a value.
        internal sealed class OldLoader
        {
            internal static int Constructed;
            public OldLoader(Config config) { Constructed++; }
            public Task<Exception?> AddMissingLoreAsync(Project project, CancellationToken token) => Task.FromResult<Exception?>(null);
            public static string ValidateLinkCatalogUrl(string url) => Validate(url);
        }
        internal sealed class GenericLoader
        {
            public GenericLoader(Config config, string linkCatalogUrl) { }
            public NotUniTask AddMissingLoreAsync(Project project, Report report, CancellationToken token) => new NotUniTask();
            public static string ValidateLinkCatalogUrl(string url) => Validate(url);
        }
        // The awaiter shape of build 2640's UniTask, already complete, under another name: only the name refuses it.
        internal sealed class NotUniTask
        {
            private readonly LoreAwaiter awaiter = new LoreAwaiter();
            internal NotUniTask() => awaiter.Finish();
            public LoreAwaiter GetAwaiter() => awaiter;
        }
        internal sealed class UnvalidatedLoader
        {
            public UnvalidatedLoader(Config config, string linkCatalogUrl) { }
            public UniTask AddMissingLoreAsync(Project project, Report report, CancellationToken token) => new UniTask();
        }
        private sealed class Logger : IModLogger
        {
            internal readonly List<string> Info = new List<string>();
            internal readonly List<string> Warn = new List<string>();
            public void Debug(string message) { }
            void IModLogger.Info(string message) => Info.Add(message);
            void IModLogger.Warn(string message) => Warn.Add(message);
            public void Error(string message) { }
            public void Error(Exception exception, string message) { }
        }
    }
}
