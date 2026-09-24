using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static class ContextBoundExtensionTests
    {
        internal static void Run()
        {
            using var first = new FakeModContext();
            using var second = new FakeModContext();
            var registry = new ModServiceRegistry();
            var registered = registry.RegisterExtension<IContextProbe>(first.Identity.Id, new ContextProvider(), ExtensionCardinality.Singleton);
            Assert(registered.Succeeded, "the fixture provider must register");
            var firstAccess = new OwnerExtensionService(first.Identity.Id, Array.Empty<string>(), first.Lifetime, registry, first);
            var secondAccess = new OwnerExtensionService(second.Identity.Id, Array.Empty<string>(), second.Lifetime, registry, second);
            Assert(firstAccess.TryGet<IContextProbe>(out var a) && ReferenceEquals(a.Context, first),
                "resource-producing extension must receive its exact consuming context");
            Assert(secondAccess.TryGet<IContextProbe>(out var b) && ReferenceEquals(b.Context, second),
                "same package identity must not reuse another scope's facade");
            Assert(!ReferenceEquals(a, b), "independent contexts need independent facades");
            first.Dispose();
            Assert(firstAccess.GetAll<IContextProbe>().Count == 0 && secondAccess.GetAll<IContextProbe>().Count == 1,
                "one stopped scope must not revoke a live sibling facade");
            Console.WriteLine("Context-bound extension tests passed.");
        }
        private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        private interface IContextProbe { IModContext? Context { get; } }
        private sealed class ContextProvider : IContextProbe, IOwnerContextBoundExtensionFactory
        {
            public IModContext? Context => null;
            public object CreateOwnerFacade(Type contract, IModContext context) => new ContextProbe(context);
        }
        private sealed class ContextProbe : IContextProbe
        {
            internal ContextProbe(IModContext context) { Context = context; }
            public IModContext Context { get; }
        }
    }
}
