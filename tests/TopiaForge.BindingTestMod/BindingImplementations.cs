using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.BindingTestMod
{
    public static class BindingProbe
    {
        public static readonly List<string> Events = new List<string>();
        public static IModContext? PackageContext;
        public static Func<int>? ActiveScopes;
        public static IModLifetime? ConstructorLifetime;
        public static bool ThrowOnFirstDiscoveryDispose;
        public static bool ThrowOnStart;
        public static bool ThrowOnLoad;
        public static Action<IModContext>? OnLoadHook;
        public static bool ThrowOnFactoryConstruction;
        public static bool ThrowOnFirstDiscoveryConstruction;
        public static int DiscoveryConstructions;
        public static Func<IWorldDiscoveryContext, CancellationToken, Task<OperationResult<IReadOnlyList<DiscoveredWorldDescriptor>>>>? DiscoverHandler;
    }
    public sealed class BindingMod : TopiaForgeMod
    {
        protected override void OnLoad()
        {
            BindingProbe.PackageContext = Context;
            BindingProbe.Events.Add("package:load");
            BindingProbe.OnLoadHook?.Invoke(Context);
            if (BindingProbe.ThrowOnLoad) throw new InvalidOperationException("synthetic package load failure");
        }
        protected override void OnUnload() => BindingProbe.Events.Add("package:unload");
    }
    public class GoodFactory : IGamemodeFactory
    {
        public GoodFactory()
        {
            if (BindingProbe.ActiveScopes == null || BindingProbe.ActiveScopes() == 0) throw new InvalidOperationException("factory constructed before child scope");
            BindingProbe.ConstructorLifetime!.Track(new OwnedProbe("factory:constructor-resource"));
            BindingProbe.Events.Add("factory:constructor");
            if (BindingProbe.ThrowOnFactoryConstruction) throw new InvalidOperationException("synthetic constructor failure");
        }
        public Task<OperationResult<IGamemodeController>> StartAsync(IGamemodeSession session, CancellationToken cancellationToken)
        {
            if (ReferenceEquals(session.Context, BindingProbe.PackageContext)) throw new InvalidOperationException("package context leaked into session");
            BindingProbe.Events.Add("factory:start:" + session.GamemodeId);
            session.Lifetime.Track(new OwnedProbe("factory:resource"));
            if (BindingProbe.ThrowOnStart) throw new InvalidOperationException("synthetic startup failure");
            return Task.FromResult(OperationResult<IGamemodeController>.Success(new Controller()));
        }
        private sealed class Controller : IGamemodeController
        { public void Dispose() => BindingProbe.Events.Add("controller:dispose"); }
    }
    public sealed class GoodProvider : IWorldContentProvider
    {
        public GoodProvider()
        {
            if (BindingProbe.ActiveScopes == null || BindingProbe.ActiveScopes() == 0) throw new InvalidOperationException("provider constructed before child scope");
            BindingProbe.Events.Add("provider:constructor");
        }
        public Task<OperationResult<IWorldInstance>> LoadAsync(IWorldLoadContext context, CancellationToken cancellationToken)
        {
            if (ReferenceEquals(context.Context, BindingProbe.PackageContext)) throw new InvalidOperationException("package context leaked into world");
            BindingProbe.Events.Add("provider:load");
            context.Context.Lifetime.Track(new OwnedProbe("provider:resource"));
            return Task.FromResult(OperationResult<IWorldInstance>.Success(new Instance()));
        }
        private sealed class Instance : IWorldInstance
        {
            public WorldReadiness Readiness { get; } = new WorldReadiness(new WorldSceneIdentity(123, "SyntheticScene"), TransformState.Identity);
            public void Dispose() => BindingProbe.Events.Add("world:dispose");
        }
    }
    public sealed class GoodDiscovery : IWorldDiscoverySource, IDisposable
    {
        private readonly int sequence;
        public GoodDiscovery()
        {
            if (BindingProbe.ActiveScopes == null || BindingProbe.ActiveScopes() == 0) throw new InvalidOperationException("discovery constructed before child scope");
            BindingProbe.ConstructorLifetime!.Track(new OwnedProbe("discovery:constructor-resource"));
            BindingProbe.Events.Add("discovery:constructor");
            sequence = ++BindingProbe.DiscoveryConstructions;
            if (sequence == 1 && BindingProbe.ThrowOnFirstDiscoveryConstruction) throw new InvalidOperationException("synthetic discovery constructor failure");
        }
        public Task<OperationResult<IReadOnlyList<DiscoveredWorldDescriptor>>> DiscoverAsync(IWorldDiscoveryContext context, CancellationToken cancellationToken)
        {
            if (ReferenceEquals(context.Context, BindingProbe.PackageContext)) throw new InvalidOperationException("package context leaked into discovery");
            BindingProbe.Events.Add("discovery:call:" + context.FamilyId);
            context.Context.Lifetime.Track(new OwnedProbe("discovery:" + context.FamilyId));
            return BindingProbe.DiscoverHandler?.Invoke(context, cancellationToken) ?? Task.FromResult(
                OperationResult<IReadOnlyList<DiscoveredWorldDescriptor>>.Success(new[] { new DiscoveredWorldDescriptor(context.FamilyId + ".one", context.FamilyId, "One") }));
        }
        public void Dispose()
        {
            BindingProbe.Events.Add("discovery:dispose");
            if (sequence == 1 && BindingProbe.ThrowOnFirstDiscoveryDispose) throw new InvalidOperationException("synthetic discovery cleanup failure");
        }
        public Task<OperationResult<IWorldInstance>> LoadAsync(IWorldLoadContext context, CancellationToken cancellationToken) => new GoodProvider().LoadAsync(context, cancellationToken);
    }
    public abstract class AbstractFactory : GoodFactory { }
    public class OpenFactory<T> : GoodFactory { }
    internal sealed class HiddenFactory : GoodFactory { }
    public sealed class PrivateConstructorFactory : GoodFactory { private PrivateConstructorFactory() { } }
    public sealed class WrongKind { }
    internal sealed class OwnedProbe : IDisposable
    {
        private readonly string name;
        internal OwnedProbe(string name) { this.name = name; }
        public void Dispose() => BindingProbe.Events.Add(name + ":dispose");
    }
}
