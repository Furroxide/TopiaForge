using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal enum RuntimeBindingFailureCode
    {
        PackageNotLoaded, PackageLoadFailed, OwnerStopping, OwnerRemoved,
        UnsafeAssemblyPath, ReceiptInvalid, AssemblyHashMissing, AssemblyHashMismatch,
        AssemblyMissing, ForeignAssembly, MissingType, InvalidType, VerificationFailed, AmbiguousPackage
    }

    internal sealed class RuntimeBindingFailure
    {
        internal RuntimeBindingFailure(PackageIdentity package, string kind, string declarationId,
            RuntimeBindingFailureCode code, string message, string? assembly = null, string? type = null)
        {
            Package = new PackageIdentity(package.Id, package.Version);
            Kind = kind; DeclarationId = declarationId; Code = code; Message = message;
            Assembly = assembly; Type = type;
        }
        internal PackageIdentity Package { get; }
        internal string Kind { get; }
        internal string DeclarationId { get; }
        internal RuntimeBindingFailureCode Code { get; }
        internal string Message { get; }
        internal string? Assembly { get; }
        internal string? Type { get; }
        internal DeclarationAvailability ToAvailability() => new DeclarationAvailability(Kind, DeclarationId,
            new[] { new LaunchBlock(Kind == "world" ? LaunchBlockCode.WorldUnbound : LaunchBlockCode.GamemodeUnbound, DeclarationId, Package.Version) });
    }

    internal sealed class PackageBindingBatch
    {
        internal PackageBindingBatch(PackageIdentity package,
            IEnumerable<ISessionImplementation<IGamemodeFactory>> gamemodes,
            IEnumerable<ISessionImplementation<IWorldContentProvider>> worlds,
            IEnumerable<ISessionImplementation<IWorldDiscoverySource>> discoverySources,
            IEnumerable<RuntimeBindingFailure> failures)
        {
            Package = new PackageIdentity(package.Id, package.Version);
            Gamemodes = Array.AsReadOnly(gamemodes.ToArray());
            Worlds = Array.AsReadOnly(worlds.ToArray());
            DiscoverySources = Array.AsReadOnly(discoverySources.ToArray());
            Failures = Array.AsReadOnly(failures.ToArray());
        }
        internal PackageIdentity Package { get; }
        internal IReadOnlyList<ISessionImplementation<IGamemodeFactory>> Gamemodes { get; }
        internal IReadOnlyList<ISessionImplementation<IWorldContentProvider>> Worlds { get; }
        internal IReadOnlyList<ISessionImplementation<IWorldDiscoverySource>> DiscoverySources { get; }
        internal IReadOnlyList<RuntimeBindingFailure> Failures { get; }
    }

    internal sealed class BindingVerificationException : Exception
    {
        internal BindingVerificationException(RuntimeBindingFailureCode code, string message, Exception? inner = null) : base(message, inner) { Code = code; }
        internal RuntimeBindingFailureCode Code { get; }
    }
}
