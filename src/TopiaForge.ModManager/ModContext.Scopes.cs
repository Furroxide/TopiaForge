using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed class ScopedContextConstructionException : AggregateException
    {
        internal ScopedContextConstructionException(Exception constructionFailure, Exception cleanupFailure)
            : base("Scoped context construction and cleanup failed.", constructionFailure, cleanupFailure)
        { CleanupFailure = cleanupFailure; }
        internal Exception CleanupFailure { get; }
    }

    internal sealed partial class ModContext
    {
        private bool scopeCreationStopped;
        private readonly object scopeSync = new object();
        private readonly List<ModContextScope> childScopes = new List<ModContextScope>();
        internal int ActiveChildScopeCount { get { lock (scopeSync) return childScopes.Count; } }

        /// <param name="executionContext">
        /// Where the scope's code runs: a session gives its gamemode's package <see cref="ModExecutionContext.GamemodeServer"/>
        /// and its other packages <see cref="ModExecutionContext.WorldOwner"/>. Without one, the scope runs where this context
        /// does, as a content or discovery scope works for its caller. The scope keeps its mod's network class.
        /// </param>
        internal async Task<ModContextScope> CreateChildScopeAsync(string sessionId, CancellationToken sessionStoppingToken,
            Action requestSessionStop, NativeTransitionAccessSlot transitionAccess, IHostDispatcher dispatcher,
            ModExecutionContext? executionContext = null)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("A session identity is required.", nameof(sessionId));
            if (requestSessionStop == null) throw new ArgumentNullException(nameof(requestSessionStop));
            if (transitionAccess == null) throw new ArgumentNullException(nameof(transitionAccess));
            if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
            var scopeContext = executionContext ?? NetProfile.Context;
            if (!ModExecutionContexts.IsDefined(scopeContext)) throw new ArgumentOutOfRangeException(nameof(executionContext));
            if (!dispatcher.IsCurrent) throw new InvalidOperationException("Scopes must be created on the host thread.");
            ModContextScope scope;
            lock (scopeSync)
            {
                if (scopeCreationStopped || Lifetime.IsStopping) throw new ObjectDisposedException(nameof(ModContext));
                foreach (var existing in childScopes)
                    if (string.Equals(existing.SessionId, sessionId, StringComparison.Ordinal))
                        throw new InvalidOperationException("A package can participate in a session through only one scope.");
                scope = new ModContextScope(this, sessionId, sessionStoppingToken, requestSessionStop, dispatcher);
                childScopes.Add(scope);
            }
            try
            {
                scope.Initialize(new ModContext(this, scope, transitionAccess, new ModNetProfile(NetProfile.NetworkClass, scopeContext)));
                return scope;
            }
            catch (Exception constructionFailure)
            {
                try { await scope.CleanupFailedConstructionAsync().ConfigureAwait(false); }
                catch (Exception cleanupFailure)
                { throw new ScopedContextConstructionException(constructionFailure, cleanupFailure); }
                throw;
            }
        }

        private ModContext(ModContext parent, ModContextScope scope, NativeTransitionAccessSlot transitionAccess, ModNetProfile profile)
        {
            scopeParent = parent;
            packagePath = parent.packagePath;
            dataPath = parent.dataPath;
            gameplayFactory = parent.gameplayFactory;
            serviceRegistry = parent.serviceRegistry;
            replicationServices = parent.replicationServices;
            visibleDependencies = parent.visibleDependencies;
            allowUnityInterop = parent.allowUnityInterop;
            Identity = parent.Identity;
            NetProfile = profile;
            Runtime = parent.Runtime;
            Logger = parent.Logger;
            ownerLifetime = scope.OwnerLifetime;
            Lifetime = scope.Lifetime;
            replication = replicationServices.Bind(Identity.Id, NetProfile, Lifetime);
            if (parent.sessionOwner != null) ConfigureSessions(parent.sessionOwner);
            modEvents = new ModEvents(parent.modEvents, Lifetime);
            Events = modEvents;
            Files = new ModFiles(packagePath, dataPath, Lifetime);
            Config = new ScopedConfigService(parent.Config, Lifetime);
            LocalStorage = new ScopedStorageService(parent.LocalStorage, Lifetime);
            var gameplay = gameplayFactory?.Create(Identity.Id, packagePath, dataPath, Lifetime, Logger, NetProfile, transitionAccess)
                ?? GameplayContextServices.Unavailable(Lifetime);
            Input = gameplay.Input;
            Time = gameplay.Time;
            Scheduler = gameplay.Scheduler;
            LocalPlayer = new LifetimePlayerService(gameplay.LocalPlayer, Lifetime);
            Scenes = gameplay.Scenes;
            Entities = new LifetimeEntityService(new NetEntityService(gameplay.Entities, replication), Lifetime);
            Physics = gameplay.Physics;
            Interactions = gameplay.Interactions;
            Items = new NetItemService(gameplay.Items, replication);
            Assets = new NetAssetService(gameplay.Assets, replication);
            if (Assets is IParentAssetScope assetScope) assetScope.AttachParent(parent.Assets);
            Audio = gameplay.Audio;
            Ui = new NetUiService(gameplay.Ui, replication);
            Ui.ApplyAccessibility(parent.Ui.Accessibility);
            SceneTransitions = gameplay.SceneTransitions;
            WorldRuntime = gameplay.WorldRuntime;
            unityInterop = allowUnityInterop ? gameplay.UnityInterop : null;
            Localization = new OwnerLocalizationService(Lifetime, (OwnerLocalizationService)parent.Localization);
            Commands = new OwnerCommandService(Identity.Id, Lifetime, Logger, serviceRegistry);
            Diagnostics = parent.Diagnostics;
            Extensions = new OwnerExtensionService(Identity.Id, visibleDependencies, Lifetime, serviceRegistry, this);
        }

        internal void BeginStopping()
        {
            ModContextScope[] children;
            lock (scopeSync) { scopeCreationStopped = true; children = childScopes.ToArray(); }
            var failures = new List<Exception>();
            foreach (var child in children)
                try { child.BeginStop(); } catch (Exception exception) { failures.Add(exception); }
            foreach (var child in children)
                try { child.RequestSessionStop(); } catch (Exception exception) { failures.Add(exception); }
            try { ownerLifetime.BeginStop(); } catch (Exception exception) { failures.Add(exception); }
            if (failures.Count > 0) throw new AggregateException("Package cancellation failed.", failures);
        }

        internal void ReleaseChildScope(ModContextScope scope)
        {
            lock (scopeSync) childScopes.Remove(scope);
        }
    }
}
