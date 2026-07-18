using System;
using System.Collections.Generic;

namespace TopiaForge.ManagedRefs;

internal enum ManagedRefsSource
{
    Auto,
    Public,
    Bundled,
}

internal sealed record ManagedRefsOptions(
    ManagedRefsSource Source,
    string SourcePlatform,
    string ConfigPath,
    string CacheRoot,
    bool Probe,
    bool CacheKeyOnly,
    bool WriteLocalProps,
    bool RequireLatest,
    bool ShowHelp)
{
    internal const string HelpText = """
Restore the SHA-256-pinned Robotopia managed reference assemblies.

Usage:
  dotnet run --project tools/TopiaForge.ManagedRefs/TopiaForge.ManagedRefs.csproj -- [options]

Options:
  --source <auto|public|bundled>  Select the source (default: auto or ROBOTOPIA_REFS_SOURCE).
  --source-platform <name>       Select the public archive platform.
  --config-path <path>           Override .github/robotopia-game-build.json.
  --cache-root <path>            Override the managed-reference cache.
  --probe                        Check availability without restoring.
  --cache-key-only               Print and export the deterministic cache key.
  --write-local-props            Atomically write Directory.Build.local.props.
  --require-latest               Require both pinned public archives to match the latest manifest.
  --help                         Show this help.

The legacy PowerShell spellings (-Source, -Probe, and so on) are also accepted.
""";

    internal static ManagedRefsOptions Parse(
        IReadOnlyList<string> arguments,
        Func<string, string?> getEnvironmentVariable)
    {
        string? sourceValue = null;
        var sourceWasSpecified = false;
        var sourcePlatform = string.Empty;
        var configPath = string.Empty;
        var cacheRoot = string.Empty;
        var probe = false;
        var cacheKeyOnly = false;
        var writeLocalProps = false;
        var requireLatest = false;
        var showHelp = false;

        for (var index = 0; index < arguments.Count; index++)
        {
            var raw = arguments[index];
            var separator = raw.IndexOf('=');
            var name = separator >= 0 ? raw[..separator] : raw;
            var inlineValue = separator >= 0 ? raw[(separator + 1)..] : null;
            var normalized = NormalizeOption(name);

            switch (normalized)
            {
                case "source":
                    sourceValue = ReadValue(arguments, ref index, name, inlineValue);
                    sourceWasSpecified = true;
                    break;
                case "sourceplatform":
                    sourcePlatform = ReadValue(arguments, ref index, name, inlineValue);
                    break;
                case "configpath":
                    configPath = ReadValue(arguments, ref index, name, inlineValue);
                    break;
                case "cacheroot":
                    cacheRoot = ReadValue(arguments, ref index, name, inlineValue);
                    break;
                case "probe":
                    EnsureNoValue(name, inlineValue);
                    probe = true;
                    break;
                case "cachekeyonly":
                    EnsureNoValue(name, inlineValue);
                    cacheKeyOnly = true;
                    break;
                case "writelocalprops":
                    EnsureNoValue(name, inlineValue);
                    writeLocalProps = true;
                    break;
                case "requirelatest":
                    EnsureNoValue(name, inlineValue);
                    requireLatest = true;
                    break;
                case "help":
                case "h":
                    EnsureNoValue(name, inlineValue);
                    showHelp = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown managed-reference option '{raw}'. Use --help for usage.");
            }
        }

        if (!sourceWasSpecified)
        {
            sourceValue = getEnvironmentVariable("ROBOTOPIA_REFS_SOURCE");
        }

        var source = ParseSource(sourceValue);
        return new ManagedRefsOptions(
            source,
            sourcePlatform.Trim(),
            configPath.Trim(),
            cacheRoot.Trim(),
            probe,
            cacheKeyOnly,
            writeLocalProps,
            requireLatest,
            showHelp);
    }

    private static string NormalizeOption(string value)
    {
        var trimmed = value.TrimStart('-');
        if (trimmed.Length == value.Length || trimmed.Length == 0)
        {
            throw new ArgumentException($"Invalid managed-reference argument '{value}'.");
        }

        return trimmed.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
    }

    private static string ReadValue(
        IReadOnlyList<string> arguments,
        ref int index,
        string option,
        string? inlineValue)
    {
        var value = inlineValue;
        if (value is null)
        {
            if (++index >= arguments.Count)
            {
                throw new ArgumentException($"Option '{option}' requires a value.");
            }

            value = arguments[index];
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Option '{option}' requires a non-empty value.");
        }

        return value;
    }

    private static void EnsureNoValue(string option, string? inlineValue)
    {
        if (inlineValue is not null)
        {
            throw new ArgumentException($"Option '{option}' does not accept a value.");
        }
    }

    private static ManagedRefsSource ParseSource(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "auto" : value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "auto" => ManagedRefsSource.Auto,
            "public" => ManagedRefsSource.Public,
            "bundled" => ManagedRefsSource.Bundled,
            _ => throw new ArgumentException(
                $"Invalid ROBOTOPIA_REFS_SOURCE '{value}'. Expected auto, public, or bundled."),
        };
    }
}
