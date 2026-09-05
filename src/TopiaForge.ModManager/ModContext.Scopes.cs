using System;
using System.Collections.Generic;
using System.Threading;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed partial class ModContext
    {
        private bool scopeCreationStopped;
        private readonly object scopeSync = new object();
        private readonly List<ModContextScope> childScopes = new List<ModContextScope>();
        internal int ActiveChildScopeCount { get { lock (scopeSync) return childScopes.Count; } }

        internal ModContextScope CreateChildScope(string sessionId, CancellationToken sessionStoppingToken,
            Action requestSessionStop, NativeTransitionAccessSlot transitionAccess, IHostDispatcher dispatcher)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("A session identity is required.", nameof(sessionId));
            if (requestSessionStop == null) throw new ArgumentNullException(nameof(requestSessionStop));
            if (transitionAccess == null) throw new ArgumentNullException(nameof(transitionAccess));
            if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
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
                scope.Initialize(new ModContext(this, scope, transitionAccess));
                return scope;
            }
            catch (Exception constructionFailure)
            {
                try { scope.Dispose(); }
                catch (Exception cleanupFailure)
                { throw new AggregateException("Scoped context construction and cleanup failed.", constructionFailure, cleanupFailure); }
                throw;
            }
        }

        private ModContext(ModContext parent, ModContextScope scope, NativeTransitionAccessSlot transitionAccess)
        {
            packagePath = parent.packagePath;
            dataPath = parent.dataPath;
            gameplayFactory = parent.gameplayFactory;
            serviceRegistry = parent.serviceRegistry;
            visibleDependencies = parent.visibleDependencies;
            allowUnityInterop = parent.allowUnityInterop;
            Identity = parent.Identity;
            Runtime = parent.Runtime;
            Logger = parent.Logger;
            ownerLifetime = scope.OwnerLifetime;
            Lifetime = scope.Lifetime;
            modEvents = new ModEvents(parent.modEvents, Lifetime);
            Events = modEvents;
            Files = new ModFiles(packagePath, dataPath, Lifetime);
            Config = new ScopedConfigService(parent.Config, Lifetime);
            LocalStorage = new ScopedStorageService(parent.LocalStorage, Lifetime);
            var gameplay = gameplayFactory?.Create(Identity.Id, packagePath, dataPath, Lifetime, Logger, transitionAccess)
                ?? GameplayContextServices.Unavailable(Lifetime);
            Input = gameplay.Input;
            Time = gameplay.Time;
            Scheduler = gameplay.Scheduler;
            LocalPlayer = new LifetimePlayerService(gameplay.LocalPlayer, Lifetime);
            Scenes = gameplay.Scenes;
            Entities = new LifetimeEntityService(gameplay.Entities, Lifetime);
            Physics = gameplay.Physics;
            Interactions = gameplay.Interactions;
            Items = gameplay.Items;
            Assets = gameplay.Assets;
            if (Assets is IParentAssetScope assetScope) assetScope.AttachParent(parent.Assets);
            Audio = gameplay.Audio;
            Ui = gameplay.Ui;
            Ui.ApplyAccessibility(parent.Ui.Accessibility);
            SceneTransitions = gameplay.SceneTransitions;
            unityInterop = allowUnityInterop ? gameplay.UnityInterop : null;
            Localization = new OwnerLocalizationService(Lifetime, (OwnerLocalizationService)parent.Localization);
            Commands = new OwnerCommandService(Identity.Id, Lifetime, Logger, serviceRegistry);
            Diagnostics = parent.Diagnostics;
            Extensions = new OwnerExtensionService(Identity.Id, visibleDependencies, Lifetime, serviceRegistry);
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
