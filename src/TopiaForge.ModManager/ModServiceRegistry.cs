using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed class ModServiceRegistry
    {
        private static readonly IReadOnlyDictionary<string, string> ReservedModuleOwners =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TopiaForge.Mods.Chronos"] = "io.github.furroxide.topiaforge.chronos",
                ["TopiaForge.Mods.Prompts"] = "io.github.furroxide.topiaforge.prompts",
                ["TopiaForge.Mods.RobotKit"] = "io.github.furroxide.topiaforge.robotkit",
                ["TopiaForge.Mods.Ugc"] = "io.github.furroxide.topiaforge.ugc.livesync",
                ["TopiaForge.Mods.Worlds"] = "io.github.furroxide.topiaforge.worlds"
            };

        private readonly object sync = new object();
        private readonly List<ExtensionEntry> extensions = new List<ExtensionEntry>();
        private readonly List<CommandEntry> commands = new List<CommandEntry>();
        private long nextExtensionSequence;

        internal void UnregisterOwner(string ownerModId)
        {
            List<ExtensionRegistration>? extensionLeases = null;
            List<CommandRegistration>? commandLeases = null;
            lock (sync)
            {
                for (var index = extensions.Count - 1; index >= 0; index--)
                {
                    if (string.Equals(extensions[index].OwnerModId, ownerModId, StringComparison.OrdinalIgnoreCase))
                    {
                        extensionLeases ??= new List<ExtensionRegistration>();
                        extensionLeases.Add(extensions[index].Registration);
                        extensions.RemoveAt(index);
                    }
                }

                for (var index = commands.Count - 1; index >= 0; index--)
                {
                    if (string.Equals(commands[index].OwnerModId, ownerModId, StringComparison.OrdinalIgnoreCase))
                    {
                        commandLeases ??= new List<CommandRegistration>();
                        commandLeases.Add(commands[index].Registration);
                        commands.RemoveAt(index);
                    }
                }
            }

            extensionLeases?.ForEach(item => item.DeactivateFromRegistry());
            commandLeases?.ForEach(item => item.DeactivateFromRegistry());
        }

        internal T? Get<T>() where T : class
        {
            lock (sync)
            {
                return extensions
                    .Where(item => item.ContractType == typeof(T))
                    .OrderBy(item => item.OwnerModId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.Sequence)
                    .Select(item => item.Provider)
                    .OfType<T>()
                    .FirstOrDefault();
            }
        }

        internal OperationResult<IExtensionRegistration> RegisterExtension<T>(
            string ownerModId,
            T provider,
            ExtensionCardinality cardinality) where T : class
        {
            ValidateRegistration(ownerModId, provider);
            if (!Enum.IsDefined(typeof(ExtensionCardinality), cardinality))
            {
                return OperationResult<IExtensionRegistration>.Failure(
                    ModErrorCode.InvalidArgument,
                    "The extension cardinality is not recognized.");
            }

            var contractType = typeof(T);
            if (contractType.Assembly == typeof(IModContext).Assembly)
            {
                return OperationResult<IExtensionRegistration>.Failure(
                    ModErrorCode.InvalidArgument,
                    "TopiaForge core and bundled module contracts are reserved and cannot be replaced as extensions.");
            }

            var contractAssembly = contractType.Assembly.GetName().Name ?? string.Empty;
            if (ReservedModuleOwners.TryGetValue(contractAssembly, out var requiredOwner)
                && !string.Equals(ownerModId, requiredOwner, StringComparison.OrdinalIgnoreCase))
            {
                return OperationResult<IExtensionRegistration>.Failure(
                    ModErrorCode.InvalidArgument,
                    "The " + contractAssembly + " service contracts are reserved for provider '"
                    + requiredOwner + "'.");
            }

            lock (sync)
            {
                var existing = extensions.Where(item => item.ContractType == contractType).ToArray();
                if (existing.Any(item => item.Cardinality != cardinality))
                {
                    return OperationResult<IExtensionRegistration>.Failure(
                        ModErrorCode.Conflict,
                        "The extension contract is already registered with different cardinality.");
                }

                if (cardinality == ExtensionCardinality.Singleton && existing.Length > 0)
                {
                    return OperationResult<IExtensionRegistration>.Failure(
                        ModErrorCode.Conflict,
                        "The singleton extension contract already has a provider.");
                }

                var entry = new ExtensionEntry(
                    ownerModId,
                    contractType,
                    provider,
                    cardinality,
                    nextExtensionSequence++);
                var registration = new ExtensionRegistration(this, entry);
                entry.Registration = registration;
                extensions.Add(entry);
                return OperationResult<IExtensionRegistration>.Success(registration);
            }
        }

        internal IReadOnlyList<T> GetExtensions<T>(ISet<string> accessibleOwnerIds) where T : class
        {
            lock (sync)
            {
                return extensions
                    .Where(item => item.ContractType == typeof(T) && accessibleOwnerIds.Contains(item.OwnerModId))
                    .OrderBy(item => item.OwnerModId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.Sequence)
                    .Select(item => item.Provider)
                    .OfType<T>()
                    .ToArray();
            }
        }

        internal OperationResult<ICommandRegistration> RegisterCommand(
            string ownerModId,
            CommandDefinition definition,
            Func<CommandInvocation, OperationResult<string>> handler)
        {
            if (string.IsNullOrWhiteSpace(ownerModId))
            {
                throw new ArgumentException("Owner mod id is required.", nameof(ownerModId));
            }

            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            var qualifiedName = ownerModId + ":" + definition.Name;
            lock (sync)
            {
                if (commands.Any(item => string.Equals(
                    item.QualifiedName,
                    qualifiedName,
                    StringComparison.OrdinalIgnoreCase)))
                {
                    return OperationResult<ICommandRegistration>.Failure(
                        ModErrorCode.Conflict,
                        "Command '" + qualifiedName + "' is already registered.");
                }

                var entry = new CommandEntry(ownerModId, qualifiedName, handler);
                var registration = new CommandRegistration(this, entry);
                entry.Registration = registration;
                commands.Add(entry);
                return OperationResult<ICommandRegistration>.Success(registration);
            }
        }

        internal bool TryExecuteCommand(
            string requestingOwnerModId,
            string name,
            IReadOnlyList<string> arguments,
            out OperationResult<string>? result)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                result = null;
                return false;
            }

            var qualifiedName = name.IndexOf(':') >= 0 ? name : requestingOwnerModId + ":" + name;
            Func<CommandInvocation, OperationResult<string>>? handler;
            lock (sync)
            {
                handler = commands.FirstOrDefault(item => string.Equals(
                    item.QualifiedName,
                    qualifiedName,
                    StringComparison.OrdinalIgnoreCase))?.Handler;
            }

            if (handler == null)
            {
                result = null;
                return false;
            }

            result = handler(new CommandInvocation(qualifiedName, arguments));
            return true;
        }

        private void RemoveExtension(ExtensionEntry entry, ExtensionRegistration registration)
        {
            lock (sync)
            {
                if (ReferenceEquals(entry.Registration, registration))
                {
                    extensions.Remove(entry);
                }
            }
        }

        private void RemoveCommand(CommandEntry entry, CommandRegistration registration)
        {
            lock (sync)
            {
                if (ReferenceEquals(entry.Registration, registration))
                {
                    commands.Remove(entry);
                }
            }
        }

        private static void ValidateRegistration<T>(string ownerModId, T service) where T : class
        {
            if (string.IsNullOrWhiteSpace(ownerModId))
            {
                throw new ArgumentException("Owner mod id is required.", nameof(ownerModId));
            }

            if (service == null)
            {
                throw new ArgumentNullException(nameof(service));
            }
        }

        private sealed class ExtensionEntry
        {
            public ExtensionEntry(
                string ownerModId,
                Type contractType,
                object provider,
                ExtensionCardinality cardinality,
                long sequence)
            {
                OwnerModId = ownerModId;
                ContractType = contractType;
                Provider = provider;
                Cardinality = cardinality;
                Sequence = sequence;
                Registration = null!;
            }

            public string OwnerModId { get; }
            public Type ContractType { get; }
            public object Provider { get; }
            public ExtensionCardinality Cardinality { get; }
            public long Sequence { get; }
            public ExtensionRegistration Registration { get; set; }
        }

        private sealed class ExtensionRegistration : IExtensionRegistration
        {
            private ModServiceRegistry? owner;
            private ExtensionEntry? entry;

            public ExtensionRegistration(ModServiceRegistry owner, ExtensionEntry entry)
            {
                this.owner = owner;
                this.entry = entry;
            }

            public bool IsActive => owner != null;

            public void Dispose()
            {
                var registry = System.Threading.Interlocked.Exchange(ref owner, null);
                var current = System.Threading.Interlocked.Exchange(ref entry, null);
                if (registry != null && current != null)
                {
                    registry.RemoveExtension(current, this);
                }
            }

            public void DeactivateFromRegistry()
            {
                System.Threading.Interlocked.Exchange(ref owner, null);
                System.Threading.Interlocked.Exchange(ref entry, null);
            }
        }

        private sealed class CommandEntry
        {
            public CommandEntry(
                string ownerModId,
                string qualifiedName,
                Func<CommandInvocation, OperationResult<string>> handler)
            {
                OwnerModId = ownerModId;
                QualifiedName = qualifiedName;
                Handler = handler;
                Registration = null!;
            }

            public string OwnerModId { get; }
            public string QualifiedName { get; }
            public Func<CommandInvocation, OperationResult<string>> Handler { get; }
            public CommandRegistration Registration { get; set; }
        }

        private sealed class CommandRegistration : ICommandRegistration
        {
            private ModServiceRegistry? owner;
            private CommandEntry? entry;

            public CommandRegistration(ModServiceRegistry owner, CommandEntry entry)
            {
                this.owner = owner;
                this.entry = entry;
                QualifiedName = entry.QualifiedName;
            }

            public string QualifiedName { get; }

            public void Dispose()
            {
                var registry = System.Threading.Interlocked.Exchange(ref owner, null);
                var current = System.Threading.Interlocked.Exchange(ref entry, null);
                if (registry != null && current != null)
                {
                    registry.RemoveCommand(current, this);
                }
            }

            public void DeactivateFromRegistry()
            {
                System.Threading.Interlocked.Exchange(ref owner, null);
                System.Threading.Interlocked.Exchange(ref entry, null);
            }
        }
    }
}
