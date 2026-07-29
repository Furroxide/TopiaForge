using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed class OwnerLocalizationService : ILocalizationService
    {
        private readonly object sync = new object();
        private readonly IModLifetime lifetime;
        private readonly List<CatalogRegistration> catalogs = new List<CatalogRegistration>();

        public OwnerLocalizationService(IModLifetime lifetime)
        {
            this.lifetime = lifetime;
        }

        public string CurrentLocale => CultureInfo.CurrentUICulture.Name;

        public OperationResult<ILocalizationRegistration> Register(LocalizationCatalog catalog)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException(nameof(catalog));
            }

            var registration = new CatalogRegistration(this, catalog);
            lock (sync)
            {
                catalogs.Add(registration);
            }

            try
            {
                lifetime.Track(registration);
                return OperationResult<ILocalizationRegistration>.Success(registration);
            }
            catch (ObjectDisposedException)
            {
                registration.Dispose();
                return OperationResult<ILocalizationRegistration>.Failure(
                    ModErrorCode.Cancelled,
                    "The mod stopped before its localization catalog could be registered.");
            }
        }

        public bool TryGet(string key, out string? text)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                text = null;
                return false;
            }

            CatalogRegistration[] snapshot;
            lock (sync)
            {
                snapshot = catalogs.Where(item => item.IsActive).ToArray();
            }

            foreach (var locale in CandidateLocales(CurrentLocale, snapshot))
            {
                for (var index = snapshot.Length - 1; index >= 0; index--)
                {
                    var registration = snapshot[index];
                    if (string.Equals(registration.Locale, locale, StringComparison.OrdinalIgnoreCase)
                        && registration.Catalog.Entries.TryGetValue(key, out text))
                    {
                        return true;
                    }
                }
            }

            text = null;
            return false;
        }

        public string Get(string key, string fallback)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("A localization key is required.", nameof(key));
            }

            return TryGet(key, out var text) ? text! : fallback ?? string.Empty;
        }

        private void Remove(CatalogRegistration registration)
        {
            lock (sync)
            {
                catalogs.Remove(registration);
            }
        }

        private static IEnumerable<string> CandidateLocales(
            string current,
            IReadOnlyList<CatalogRegistration> registrations)
        {
            if (!string.IsNullOrWhiteSpace(current))
            {
                yield return current;
                var separator = current.IndexOf('-');
                if (separator > 0)
                {
                    yield return current.Substring(0, separator);
                }
            }

            if (!current.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            {
                yield return "en";
            }

            var deterministicFallback = registrations
                .Select(item => item.Locale)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (deterministicFallback != null)
            {
                yield return deterministicFallback;
            }
        }

        private sealed class CatalogRegistration : ILocalizationRegistration
        {
            private OwnerLocalizationService? owner;

            public CatalogRegistration(OwnerLocalizationService owner, LocalizationCatalog catalog)
            {
                this.owner = owner;
                Catalog = catalog;
            }

            public LocalizationCatalog Catalog { get; }
            public string Locale => Catalog.Locale;
            public bool IsActive => owner != null;

            public void Dispose()
            {
                Interlocked.Exchange(ref owner, null)?.Remove(this);
            }
        }
    }

    internal sealed class OwnerCommandService : ICommandService
    {
        private readonly string ownerModId;
        private readonly IModLifetime lifetime;
        private readonly IModLogger logger;
        private readonly ModServiceRegistry registry;

        public OwnerCommandService(
            string ownerModId,
            IModLifetime lifetime,
            IModLogger logger,
            ModServiceRegistry registry)
        {
            this.ownerModId = ownerModId;
            this.lifetime = lifetime;
            this.logger = logger;
            this.registry = registry;
        }

        public OperationResult<ICommandRegistration> Register(
            CommandDefinition definition,
            Func<CommandInvocation, OperationResult<string>> handler)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            if (lifetime.IsStopping)
            {
                return OperationResult<ICommandRegistration>.Failure(
                    ModErrorCode.Cancelled,
                    "The mod is stopping and cannot register commands.");
            }

            var result = registry.RegisterCommand(ownerModId, definition, invocation =>
            {
                try
                {
                    return handler(invocation)
                        ?? OperationResult<string>.Failure(
                            ModErrorCode.Unknown,
                            "The command handler returned no result.");
                }
                catch (Exception exception)
                {
                    logger.Error(exception, "Command '" + definition.Name + "' failed.");
                    return OperationResult<string>.Failure(
                        ModErrorCode.Unknown,
                        "The command failed. See the attributed mod log for details.");
                }
            });

            if (result.TryGetValue(out var registration))
            {
                lifetime.Track(registration);
            }

            return result;
        }

        public bool TryExecute(
            string name,
            IReadOnlyList<string> arguments,
            out OperationResult<string>? result)
        {
            if (arguments == null)
            {
                throw new ArgumentNullException(nameof(arguments));
            }

            return registry.TryExecuteCommand(ownerModId, name, arguments, out result);
        }
    }

    internal sealed class OwnerDiagnosticsService : IDiagnosticsService
    {
        private const int MaximumEntries = 256;
        private readonly object sync = new object();
        private readonly Queue<CapturedDiagnostic> entries = new Queue<CapturedDiagnostic>();
        private readonly IModLogger logger;

        public OwnerDiagnosticsService(IModLogger logger)
        {
            this.logger = logger;
        }

        public void Report(DiagnosticEntry entry)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            lock (sync)
            {
                while (entries.Count >= MaximumEntries)
                {
                    entries.Dequeue();
                }

                entries.Enqueue(new CapturedDiagnostic(entry, DateTimeOffset.UtcNow));
            }

            var text = "[" + entry.Code + "] " + entry.Message;
            if (!string.IsNullOrWhiteSpace(entry.Detail))
            {
                text += " — " + entry.Detail;
            }

            switch (entry.Severity)
            {
                case DiagnosticSeverity.Debug:
                    logger.Debug(text);
                    break;
                case DiagnosticSeverity.Warning:
                    logger.Warn(text);
                    break;
                case DiagnosticSeverity.Error:
                    logger.Error(text);
                    break;
                default:
                    logger.Info(text);
                    break;
            }
        }

        public IReadOnlyList<CapturedDiagnostic> GetSnapshot()
        {
            lock (sync)
            {
                return entries.ToArray();
            }
        }
    }

    internal sealed class OwnerExtensionService : IExtensionService
    {
        private readonly object facadeSync = new object();
        private readonly string ownerModId;
        private readonly IModLifetime lifetime;
        private readonly ModServiceRegistry registry;
        private readonly HashSet<string> accessibleOwnerIds;
        private readonly List<FacadeEntry> facadeCache = new List<FacadeEntry>();

        public OwnerExtensionService(
            string ownerModId,
            IEnumerable<string> dependencyIds,
            IModLifetime lifetime,
            ModServiceRegistry registry)
        {
            this.ownerModId = ownerModId;
            this.lifetime = lifetime;
            this.registry = registry;
            accessibleOwnerIds = new HashSet<string>(dependencyIds, StringComparer.OrdinalIgnoreCase)
            {
                ownerModId
            };
            lifetime.Defer(ClearFacadeCache);
        }

        public OperationResult<IExtensionRegistration> Register<T>(
            T provider,
            ExtensionCardinality cardinality = ExtensionCardinality.Singleton) where T : class
        {
            if (provider == null)
            {
                throw new ArgumentNullException(nameof(provider));
            }

            if (lifetime.IsStopping)
            {
                return OperationResult<IExtensionRegistration>.Failure(
                    ModErrorCode.Cancelled,
                    "The mod is stopping and cannot publish extension providers.");
            }

            var result = registry.RegisterExtension(ownerModId, provider, cardinality);
            if (result.TryGetValue(out var registration))
            {
                lifetime.Track(registration);
            }

            return result;
        }

        public bool TryGet<T>([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? provider) where T : class
        {
            provider = GetAll<T>().FirstOrDefault();
            return provider != null;
        }

        public IReadOnlyList<T> GetAll<T>() where T : class
        {
            var providers = registry.GetExtensions<T>(accessibleOwnerIds);
            if (providers.Count == 0)
            {
                return providers;
            }

            var result = new T[providers.Count];
            for (var index = 0; index < providers.Count; index++)
            {
                result[index] = GetOwnerFacade(providers[index]);
            }

            return result;
        }

        private T GetOwnerFacade<T>(T provider) where T : class
        {
            if (!(provider is IOwnerBoundExtensionFactory factory))
            {
                return provider;
            }

            var contractType = typeof(T);
            lock (facadeSync)
            {
                foreach (var entry in facadeCache)
                {
                    if (entry.ContractType == contractType && ReferenceEquals(entry.Provider, provider))
                    {
                        return (T)entry.Facade;
                    }
                }

                var facade = factory.CreateOwnerFacade(contractType, ownerModId, lifetime);
                if (!(facade is T typedFacade))
                {
                    throw new InvalidOperationException(
                        "Extension provider '" + provider.GetType().FullName
                        + "' returned an owner facade that does not implement '"
                        + contractType.FullName + "'.");
                }

                facadeCache.Add(new FacadeEntry(contractType, provider, typedFacade));
                return typedFacade;
            }
        }

        private void ClearFacadeCache()
        {
            lock (facadeSync)
            {
                facadeCache.Clear();
            }
        }

        private sealed class FacadeEntry
        {
            public FacadeEntry(Type contractType, object provider, object facade)
            {
                ContractType = contractType;
                Provider = provider;
                Facade = facade;
            }

            public Type ContractType { get; }
            public object Provider { get; }
            public object Facade { get; }
        }
    }
}
