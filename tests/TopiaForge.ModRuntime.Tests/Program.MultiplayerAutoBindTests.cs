using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using TopiaForge.ModManager;
using TopiaForge.Mods;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        private static void TestGeneratedMultiplayerAutoBinding()
        {
            var session = DispatchProxy.Create<IMultiplayerSession, UnusedInterfaceProxy>();
            var logger = new AutoBindLogger();
            var lifetime = new AutoBindLifetime();
            var contract = new AutoBoundContract(ModErrorCode.None);
            GeneratedMultiplayerBinder.Bind(contract, () => session, lifetime, logger);
            Assert(ReferenceEquals(contract.BoundSession, session) && lifetime.TrackedCount == 1 && logger.Warnings.Count == 0,
                "the runtime binds a generated contract to its multiplayer provider");
            lifetime.Dispose();
            Assert(contract.LeaseDisposed, "the binding lives exactly as long as the mod lifetime");

            var unscoped = new AutoBoundContract(ModErrorCode.None);
            GeneratedMultiplayerBinder.Bind(unscoped, () => null, new AutoBindLifetime(), logger);
            Assert(unscoped.BoundSession == null &&
                   logger.Warnings.Count == 1 &&
                   logger.Warnings[0] == GeneratedMultiplayerBinder.MissingProviderWarning,
                "a contract without a provider dependency stays unbound with an actionable warning");

            var rejected = new AutoBoundContract(ModErrorCode.Conflict);
            try
            {
                GeneratedMultiplayerBinder.Bind(rejected, () => session, new AutoBindLifetime(), logger);
                throw new InvalidOperationException("Runtime test failed: a rejected binding must fail the mod load.");
            }
            catch (InvalidOperationException exception) when (exception.Message.Contains("Conflict", StringComparison.Ordinal))
            {
            }

            GeneratedMultiplayerBinder.Bind(
                new object(),
                () => throw new InvalidOperationException("Runtime test failed: mods without a contract resolve nothing."),
                new AutoBindLifetime(),
                logger);

            var order = new List<string>();
            var mod = new OrderRecordingMod(order);
            mod.Load(
                DispatchProxy.Create<IModContext, UnusedInterfaceProxy>(),
                _ => order.Add(mod.HasContext ? "bind-with-context" : "bind-without-context"));
            Assert(string.Join(",", order) == "bind-with-context,load",
                "binding runs after the context attaches and before OnLoad, so session hooks can use Context");
        }

        private sealed class AutoBoundContract : IGeneratedMultiplayerBinding
        {
            private readonly ModErrorCode failure;

            internal AutoBoundContract(ModErrorCode failure) => this.failure = failure;

            internal IMultiplayerSession? BoundSession { get; private set; }
            internal bool LeaseDisposed { get; private set; }

            public MultiplayerContractDescriptor MultiplayerContractDescriptor { get; } = new MultiplayerContractDescriptor(
                "runtime.test",
                1,
                new string('a', 64),
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>());

            public IMultiplayerCodec<T> GetCodec<T>() where T : class => throw new NotSupportedException();

            public OperationResult<IDisposable> BindMultiplayer(IMultiplayerSession session)
            {
                if (failure != ModErrorCode.None)
                {
                    return OperationResult<IDisposable>.Failure(failure, "Deliberate binding failure.");
                }

                BoundSession = session;
                return OperationResult<IDisposable>.Success(new CallbackLease(() => LeaseDisposed = true));
            }
        }

        private sealed class OrderRecordingMod : TopiaForgeMod
        {
            private readonly List<string> order;

            internal OrderRecordingMod(List<string> order) => this.order = order;

            internal bool HasContext
            {
                get
                {
                    try
                    {
                        return Context != null;
                    }
                    catch (InvalidOperationException)
                    {
                        return false;
                    }
                }
            }

            protected override void OnLoad() => order.Add("load");
        }

        private sealed class AutoBindLifetime : IModLifetime
        {
            private readonly List<IDisposable> tracked = new List<IDisposable>();
            private readonly CancellationTokenSource stopping = new CancellationTokenSource();

            internal int TrackedCount => tracked.Count;
            public bool IsStopping => stopping.IsCancellationRequested;
            public CancellationToken StoppingToken => stopping.Token;

            public IDisposable Track(IDisposable resource)
            {
                tracked.Add(resource);
                return resource;
            }

            public IDisposable Defer(Action cleanup) => Track(new CallbackLease(cleanup));

            public void Dispose()
            {
                stopping.Cancel();
                for (var index = tracked.Count - 1; index >= 0; index--) tracked[index].Dispose();
                tracked.Clear();
            }
        }

        private sealed class AutoBindLogger : IModLogger
        {
            internal List<string> Warnings { get; } = new List<string>();
            public void Debug(string message) { }
            public void Info(string message) { }
            public void Warn(string message) => Warnings.Add(message);
            public void Error(string message) { }
            public void Error(Exception exception, string message) { }
        }

        private sealed class CallbackLease : IDisposable
        {
            private Action? callback;
            internal CallbackLease(Action callback) => this.callback = callback;

            public void Dispose()
            {
                var captured = callback;
                callback = null;
                captured?.Invoke();
            }
        }

        // Stands in for interfaces the code under test must never touch.
        public class UnusedInterfaceProxy : DispatchProxy
        {
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
                throw new InvalidOperationException("Runtime test failed: unexpected call to " + targetMethod?.Name + ".");
        }
    }
}
