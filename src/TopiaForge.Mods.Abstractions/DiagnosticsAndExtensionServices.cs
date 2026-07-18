using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods
{
    /// <summary>Identifies structured diagnostic severity.</summary>
    public enum DiagnosticSeverity
    {
        /// <summary>Verbose developer information.</summary>
        Debug = 0,

        /// <summary>Ordinary operational information.</summary>
        Info = 1,

        /// <summary>A recoverable problem.</summary>
        Warning = 2,

        /// <summary>An operation failure.</summary>
        Error = 3
    }

    /// <summary>Contains one structured mod diagnostic.</summary>
    public sealed class DiagnosticEntry
    {
        /// <summary>Creates a diagnostic entry.</summary>
        public DiagnosticEntry(
            string code,
            string message,
            DiagnosticSeverity severity = DiagnosticSeverity.Info,
            string detail = "")
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                throw new ArgumentException("A stable diagnostic code is required.", nameof(code));
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                throw new ArgumentException("A diagnostic message is required.", nameof(message));
            }

            if (!Enum.IsDefined(typeof(DiagnosticSeverity), severity))
            {
                throw new ArgumentOutOfRangeException(nameof(severity));
            }

            Code = code;
            Message = message;
            Severity = severity;
            Detail = detail ?? string.Empty;
        }

        /// <summary>Gets the stable machine-readable code.</summary>
        public string Code { get; }

        /// <summary>Gets the short user-readable message.</summary>
        public string Message { get; }

        /// <summary>Gets the severity.</summary>
        public DiagnosticSeverity Severity { get; }

        /// <summary>Gets optional remediation or technical detail.</summary>
        public string Detail { get; }
    }

    /// <summary>Contains a diagnostic captured by the runtime.</summary>
    public sealed class CapturedDiagnostic
    {
        /// <summary>Creates a captured diagnostic.</summary>
        public CapturedDiagnostic(DiagnosticEntry entry, DateTimeOffset timestamp)
        {
            Entry = entry ?? throw new ArgumentNullException(nameof(entry));
            Timestamp = timestamp;
        }

        /// <summary>Gets the diagnostic content.</summary>
        public DiagnosticEntry Entry { get; }

        /// <summary>Gets when the runtime received the diagnostic.</summary>
        public DateTimeOffset Timestamp { get; }
    }

    /// <summary>Captures bounded structured diagnostics and mirrors them to the mod logger.</summary>
    public interface IDiagnosticsService
    {
        /// <summary>Reports one structured diagnostic.</summary>
        void Report(DiagnosticEntry entry);

        /// <summary>Returns a bounded snapshot in capture order.</summary>
        IReadOnlyList<CapturedDiagnostic> GetSnapshot();
    }

    /// <summary>Controls whether an extension contract permits one or multiple providers.</summary>
    public enum ExtensionCardinality
    {
        /// <summary>Exactly one provider may be registered.</summary>
        Singleton = 0,

        /// <summary>Multiple providers may be registered and are selected deterministically.</summary>
        Multiple = 1
    }

    /// <summary>Represents a lifetime-owned extension provider registration.</summary>
    public interface IExtensionRegistration : IDisposable
    {
        /// <summary>Gets whether the provider is still registered.</summary>
        bool IsActive { get; }
    }

    /// <summary>
    /// Publishes typed integration contracts and resolves only providers declared as dependencies of the current mod.
    /// </summary>
    public interface IExtensionService
    {
        /// <summary>Registers a provider owned by the current mod.</summary>
        OperationResult<IExtensionRegistration> Register<T>(
            T provider,
            ExtensionCardinality cardinality = ExtensionCardinality.Singleton) where T : class;

        /// <summary>Tries to resolve the deterministic first dependency-scoped provider.</summary>
        bool TryGet<T>(out T? provider) where T : class;

        /// <summary>Returns every dependency-scoped provider ordered by normalized provider identity.</summary>
        IReadOnlyList<T> GetAll<T>() where T : class;
    }
}
