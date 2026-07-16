using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods
{
    /// <summary>Contains one immutable localization catalog.</summary>
    public sealed class LocalizationCatalog
    {
        private readonly ReadOnlyDictionary<string, string> entries;

        /// <summary>Creates a localization catalog.</summary>
        public LocalizationCatalog(string locale, IReadOnlyDictionary<string, string> entries)
        {
            if (string.IsNullOrWhiteSpace(locale))
            {
                throw new ArgumentException("A locale is required.", nameof(locale));
            }

            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            var copy = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Key))
                {
                    throw new ArgumentException("Localization keys cannot be empty.", nameof(entries));
                }

                copy.Add(entry.Key, entry.Value ?? string.Empty);
            }

            Locale = locale;
            this.entries = new ReadOnlyDictionary<string, string>(copy);
        }

        /// <summary>Gets the BCP 47-style locale name.</summary>
        public string Locale { get; }

        /// <summary>Gets the immutable key-to-text mapping.</summary>
        public IReadOnlyDictionary<string, string> Entries => entries;
    }

    /// <summary>Represents a lifetime-owned localization catalog.</summary>
    public interface ILocalizationRegistration : IDisposable
    {
        /// <summary>Gets the registered locale.</summary>
        string Locale { get; }
    }

    /// <summary>Provides owner-scoped localization with deterministic fallback.</summary>
    public interface ILocalizationService
    {
        /// <summary>Gets the current UI locale.</summary>
        string CurrentLocale { get; }

        /// <summary>Registers and lifetime-tracks a localization catalog.</summary>
        /// <returns>The registration, or a stable cancellation result when the mod is stopping.</returns>
        OperationResult<ILocalizationRegistration> Register(LocalizationCatalog catalog);

        /// <summary>Tries to resolve a key for the current locale and its language fallback.</summary>
        bool TryGet(string key, out string? text);

        /// <summary>Resolves a key or returns the supplied display-ready fallback.</summary>
        string Get(string key, string fallback);
    }

    /// <summary>Describes a console or developer command.</summary>
    public sealed class CommandDefinition
    {
        /// <summary>Creates a command definition.</summary>
        public CommandDefinition(string name, string description, string usage = "")
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A command name is required.", nameof(name));
            }

            if (name.IndexOfAny(new[] { ' ', '\t', '\r', '\n', ':' }) >= 0)
            {
                throw new ArgumentException("A command name cannot contain whitespace or a colon.", nameof(name));
            }

            Name = name;
            Description = description ?? string.Empty;
            Usage = usage ?? string.Empty;
        }

        /// <summary>Gets the short name unique inside the current mod.</summary>
        public string Name { get; }

        /// <summary>Gets the user-facing description.</summary>
        public string Description { get; }

        /// <summary>Gets optional argument usage text.</summary>
        public string Usage { get; }
    }

    /// <summary>Contains one immutable command invocation.</summary>
    public sealed class CommandInvocation
    {
        private readonly ReadOnlyCollection<string> arguments;

        /// <summary>Creates a command invocation.</summary>
        public CommandInvocation(string name, IReadOnlyList<string> arguments)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A command name is required.", nameof(name));
            }

            if (arguments == null)
            {
                throw new ArgumentNullException(nameof(arguments));
            }

            var copy = new string[arguments.Count];
            for (var index = 0; index < arguments.Count; index++)
            {
                copy[index] = arguments[index] ?? string.Empty;
            }

            Name = name;
            this.arguments = Array.AsReadOnly(copy);
        }

        /// <summary>Gets the qualified command name.</summary>
        public string Name { get; }

        /// <summary>Gets immutable command arguments.</summary>
        public IReadOnlyList<string> Arguments => arguments;
    }

    /// <summary>Represents a lifetime-owned command registration.</summary>
    public interface ICommandRegistration : IDisposable
    {
        /// <summary>Gets the globally qualified command name.</summary>
        string QualifiedName { get; }
    }

    /// <summary>Registers and invokes deterministic owner-scoped commands.</summary>
    public interface ICommandService
    {
        /// <summary>Registers and lifetime-tracks a command.</summary>
        OperationResult<ICommandRegistration> Register(
            CommandDefinition definition,
            Func<CommandInvocation, OperationResult<string>> handler);

        /// <summary>Tries to execute an own short name or a globally qualified command.</summary>
        bool TryExecute(string name, IReadOnlyList<string> arguments, out OperationResult<string>? result);
    }
}
