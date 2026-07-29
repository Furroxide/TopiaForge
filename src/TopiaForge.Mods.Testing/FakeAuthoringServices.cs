using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    /// <summary>In-memory localization catalogs with deterministic locale fallback.</summary>
    public sealed class FakeLocalizationService : ILocalizationService
    {
        private readonly FakeModLifetime lifetime;
        private readonly List<CatalogRegistration> catalogs = new List<CatalogRegistration>();

        /// <summary>Creates a fake localization service.</summary>
        public FakeLocalizationService(FakeModLifetime lifetime)
        {
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        }

        /// <inheritdoc/>
        public string CurrentLocale { get; set; } = "en-US";

        /// <summary>Gets the number of active localization catalogs.</summary>
        public int ActiveCatalogCount => catalogs.Count;

        /// <inheritdoc/>
        public OperationResult<ILocalizationRegistration> Register(LocalizationCatalog catalog)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException(nameof(catalog));
            }

            var registration = new CatalogRegistration(catalog, value => catalogs.Remove(value));
            catalogs.Add(registration);
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
                    "The fake mod stopped before its localization catalog could be registered.");
            }
        }

        /// <inheritdoc/>
        public bool TryGet(string key, out string? text)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("A localization key is required.", nameof(key));
            }

            if (TryGetFrom(CurrentLocale, key, out text))
            {
                return true;
            }

            var separator = CurrentLocale.IndexOf('-');
            if (separator > 0 && TryGetFrom(CurrentLocale.Substring(0, separator), key, out text))
            {
                return true;
            }

            text = null;
            return false;
        }

        /// <inheritdoc/>
        public string Get(string key, string fallback) => TryGet(key, out var text) ? text! : fallback ?? string.Empty;

        private bool TryGetFrom(string locale, string key, out string? text)
        {
            for (var index = catalogs.Count - 1; index >= 0; index--)
            {
                var registration = catalogs[index];
                if (string.Equals(registration.Locale, locale, StringComparison.OrdinalIgnoreCase) &&
                    registration.Catalog.Entries.TryGetValue(key, out var value))
                {
                    text = value;
                    return true;
                }
            }

            text = null;
            return false;
        }

        private sealed class CatalogRegistration : ILocalizationRegistration
        {
            private Action<CatalogRegistration>? release;

            public CatalogRegistration(LocalizationCatalog catalog, Action<CatalogRegistration> release)
            {
                Catalog = catalog;
                this.release = release;
            }

            public LocalizationCatalog Catalog { get; }
            public string Locale => Catalog.Locale;

            public void Dispose()
            {
                var callback = release;
                release = null;
                callback?.Invoke(this);
            }
        }
    }

    /// <summary>Deterministic in-memory command registry.</summary>
    public sealed class FakeCommandService : ICommandService
    {
        private readonly string ownerId;
        private readonly FakeModLifetime lifetime;
        private readonly Dictionary<string, Registration> commands =
            new Dictionary<string, Registration>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Creates an owner-scoped fake command service for a fake context.</summary>
        internal FakeCommandService(ModIdentity identity, FakeModLifetime lifetime)
        {
            if (identity == null)
            {
                throw new ArgumentNullException(nameof(identity));
            }

            ownerId = identity.Id;
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        }

        /// <summary>Gets the number of active commands.</summary>
        public int ActiveCommandCount => commands.Count;

        /// <inheritdoc/>
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

            if (commands.ContainsKey(definition.Name))
            {
                return OperationResult<ICommandRegistration>.Failure(
                    ModErrorCode.Conflict,
                    "A command is already registered as '" + definition.Name + "'.");
            }

            var registration = new Registration(
                ownerId + ":" + definition.Name,
                definition,
                handler,
                value => commands.Remove(value.Definition.Name));
            commands.Add(definition.Name, registration);
            return lifetime.TrackResult<ICommandRegistration>(
                registration,
                registration.AttachLifetimeLease,
                "The fake mod stopped before its command could be registered.");
        }

        /// <inheritdoc/>
        public bool TryExecute(
            string name,
            IReadOnlyList<string> arguments,
            out OperationResult<string>? result)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A command name is required.", nameof(name));
            }

            if (arguments == null)
            {
                throw new ArgumentNullException(nameof(arguments));
            }

            var shortName = name.StartsWith(ownerId + ":", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(ownerId.Length + 1)
                : name;
            if (!commands.TryGetValue(shortName, out var registration))
            {
                result = null;
                return false;
            }

            result = registration.Handler(new CommandInvocation(registration.QualifiedName, arguments));
            return true;
        }

        private sealed class Registration : ICommandRegistration
        {
            private Action<Registration>? release;
            private IDisposable? lifetimeLease;

            public Registration(
                string qualifiedName,
                CommandDefinition definition,
                Func<CommandInvocation, OperationResult<string>> handler,
                Action<Registration> release)
            {
                QualifiedName = qualifiedName;
                Definition = definition;
                Handler = handler;
                this.release = release;
            }

            public string QualifiedName { get; }
            public CommandDefinition Definition { get; }
            public Func<CommandInvocation, OperationResult<string>> Handler { get; }

            public void AttachLifetimeLease(IDisposable lease)
            {
                lifetimeLease = lease ?? throw new ArgumentNullException(nameof(lease));
            }

            public void Dispose()
            {
                var callback = release;
                release = null;
                try
                {
                    callback?.Invoke(this);
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
                }
            }
        }
    }

    /// <summary>Bounded deterministic structured diagnostic capture.</summary>
    public sealed class FakeDiagnosticsService : IDiagnosticsService
    {
        private readonly List<CapturedDiagnostic> entries = new List<CapturedDiagnostic>();

        /// <summary>Gets or sets the timestamp used for the next report.</summary>
        public DateTimeOffset CurrentTimestamp { get; set; } = DateTimeOffset.UnixEpoch;

        /// <summary>Gets or sets the maximum retained entry count.</summary>
        public int Capacity { get; set; } = 256;

        /// <inheritdoc/>
        public void Report(DiagnosticEntry entry)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            if (Capacity <= 0)
            {
                throw new InvalidOperationException("Diagnostic capacity must be positive.");
            }

            entries.Add(new CapturedDiagnostic(entry, CurrentTimestamp));
            while (entries.Count > Capacity)
            {
                entries.RemoveAt(0);
            }
        }

        /// <inheritdoc/>
        public IReadOnlyList<CapturedDiagnostic> GetSnapshot() =>
            new List<CapturedDiagnostic>(entries).AsReadOnly();

        /// <summary>Removes every captured diagnostic.</summary>
        public void Clear() => entries.Clear();
    }

    /// <summary>In-memory typed extension provider registry.</summary>
    public sealed class FakeExtensionService : IExtensionService
    {
        private readonly FakeModLifetime lifetime;
        private readonly Dictionary<Type, ProviderSet> providers = new Dictionary<Type, ProviderSet>();

        /// <summary>Creates a fake extension service.</summary>
        public FakeExtensionService(FakeModLifetime lifetime)
        {
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        }

        /// <summary>Gets the total number of active provider registrations.</summary>
        public int ActiveProviderCount
        {
            get
            {
                var count = 0;
                foreach (var set in providers.Values)
                {
                    count += set.Registrations.Count;
                }

                return count;
            }
        }

        /// <inheritdoc/>
        public OperationResult<IExtensionRegistration> Register<T>(
            T provider,
            ExtensionCardinality cardinality = ExtensionCardinality.Singleton) where T : class
        {
            if (provider == null)
            {
                throw new ArgumentNullException(nameof(provider));
            }

            if (!providers.TryGetValue(typeof(T), out var set))
            {
                set = new ProviderSet(cardinality);
                providers.Add(typeof(T), set);
            }
            else if (set.Cardinality != cardinality || cardinality == ExtensionCardinality.Singleton)
            {
                return OperationResult<IExtensionRegistration>.Failure(
                    ModErrorCode.Conflict,
                    "The extension cardinality conflicts with an existing provider.");
            }

            var registration = new ExtensionRegistration(provider, value =>
            {
                set.Registrations.Remove(value);
                if (set.Registrations.Count == 0)
                {
                    providers.Remove(typeof(T));
                }
            });
            set.Registrations.Add(registration);
            return lifetime.TrackResult<IExtensionRegistration>(
                registration,
                registration.AttachLifetimeLease,
                "The fake mod stopped before its extension provider could be registered.");
        }

        /// <inheritdoc/>
        public bool TryGet<T>([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? provider) where T : class
        {
            var values = GetAll<T>();
            provider = values.Count == 0 ? null : values[0];
            return provider != null;
        }

        /// <inheritdoc/>
        public IReadOnlyList<T> GetAll<T>() where T : class
        {
            var result = new List<T>();
            if (providers.TryGetValue(typeof(T), out var set))
            {
                foreach (var registration in set.Registrations)
                {
                    if (registration.IsActive)
                    {
                        result.Add((T)registration.Provider);
                    }
                }
            }

            return result.AsReadOnly();
        }

        private sealed class ProviderSet
        {
            public ProviderSet(ExtensionCardinality cardinality)
            {
                Cardinality = cardinality;
            }

            public ExtensionCardinality Cardinality { get; }
            public List<ExtensionRegistration> Registrations { get; } = new List<ExtensionRegistration>();
        }

        private sealed class ExtensionRegistration : IExtensionRegistration
        {
            private Action<ExtensionRegistration>? release;
            private IDisposable? lifetimeLease;

            public ExtensionRegistration(object provider, Action<ExtensionRegistration> release)
            {
                Provider = provider;
                this.release = release;
            }

            public object Provider { get; }
            public bool IsActive => release != null;

            public void AttachLifetimeLease(IDisposable lease)
            {
                lifetimeLease = lease ?? throw new ArgumentNullException(nameof(lease));
            }

            public void Dispose()
            {
                var callback = release;
                release = null;
                try
                {
                    callback?.Invoke(this);
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
                }
            }
        }
    }
}
