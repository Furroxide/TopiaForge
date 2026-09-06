using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using TopiaForge.CreatorContent;

namespace TopiaForge.ModManager.Tests
{
    internal static class OwnerFacadeStoppingTests
    {
        internal static void Run(string root)
        {
            var registry = new ModServiceRegistry();
            var lifetime = new OwnerModLifetime();
            var commands = new OwnerCommandService("scope.mod", lifetime, new Logger(), registry);
            var invoked = 0;
            registry.RegisterCommand("scope.mod", new CommandDefinition("parent", "Parent command"),
                _ => { invoked++; return OperationResult<string>.Success("called"); });
            registry.RegisterExtension<ITestProvider>("scope.mod", new TestProvider(), ExtensionCardinality.Singleton);
            var extensions = new OwnerExtensionService("scope.mod", Array.Empty<string>(), lifetime, registry);
            lifetime.Dispose();
            commands.TryExecute("parent", Array.Empty<string>(), out var result);
            Assert(invoked == 0 && result?.ErrorCode == ModErrorCode.Cancelled,
                "a stopped context must not execute a surviving parent/sibling command");
            Assert(extensions.GetAll<ITestProvider>().Count == 0,
                "a stopped context must not expose a surviving parent/sibling extension");
            using var context = new FakeModContext();
            using var router = new CreatorToolHostRouter("provider", context.Input, context.Scenes, context.Logger);
            var liveHost = new Host();
            router.RegisterHost(new CreatorToolHostRegistrationRequest("current", "Current", 1, liveHost));
            router.Toggle();
            var stale = (ICreatorToolHostRouter)((IOwnerBoundExtensionFactory)router)
                .CreateOwnerFacade(typeof(ICreatorToolHostRouter), "scope.mod", lifetime);
            Assert(stale.CloseActive().ErrorCode == ModErrorCode.Cancelled && liveHost.IsOpen,
                "a stale creator facade cannot close a newer session's active host");
            Console.WriteLine("OwnerFacadeStoppingTests passed.");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        private sealed class Host : ICreatorToolHost
        {
            public bool IsOpen { get; private set; }
            public bool CanOpen(CreatorToolOpenContext context) => true;
            public OperationResult<bool> Open(CreatorToolOpenContext context) { IsOpen = true; return OperationResult<bool>.Success(true); }
            public OperationResult<bool> Close(CreatorToolCloseReason reason) { IsOpen = false; return OperationResult<bool>.Success(true); }
        }
        private interface ITestProvider { }
        private sealed class TestProvider : ITestProvider { }
        internal sealed class Logger : IModLogger
        {
            public void Debug(string message) { }
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message) { }
            public void Error(Exception exception, string message) { }
        }
    }
}
