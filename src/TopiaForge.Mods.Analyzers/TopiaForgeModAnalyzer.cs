using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace TopiaForge.Mods.Analyzers
{
    /// <summary>Enforces the safe V1 authoring boundary and keeps source dependencies aligned with the manifest.</summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class TopiaForgeModAnalyzer : DiagnosticAnalyzer
    {
        private const string Category = "TopiaForge";
        private const string DocsRoot = "https://docs.topiaforge.dev/diagnostics/";

        private static readonly DiagnosticDescriptor UnsafeNativeApi = new DiagnosticDescriptor(
            "TF1001",
            "Native game API used by a safe mod",
            "'{0}' bypasses the safe TopiaForge SDK. Use the corresponding IModContext service, or explicitly opt into the unstable Interop.Unity package and 'unsafe-native' capability.",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "Ordinary mods must not depend directly on Unity, GameCode, or Harmony.",
            helpLinkUri: DocsRoot + "TF1001");

        private static readonly DiagnosticDescriptor UnsupportedTargetFramework = new DiagnosticDescriptor(
            "TF1002",
            "Unsupported mod target framework",
            "Target framework '{0}' is unsupported for safe TopiaForge V1 mods; target netstandard2.1",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: DocsRoot + "TF1002",
            customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

        private static readonly DiagnosticDescriptor MissingModuleDependency = new DiagnosticDescriptor(
            "TF1004",
            "Module contract is not declared in the manifest",
            "'{0}' is the {1} contract assembly, but its runtime dependency is missing; run 'topiaforge mod add {2}' to add both the exact SDK PackageReference and manifest dependency",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: DocsRoot + "TF1004",
            customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

        private static readonly DiagnosticDescriptor ObsoletePreV1Api = new DiagnosticDescriptor(
            "TF1005",
            "Pre-V1 TopiaForge API is no longer supported",
            "'{0}' is a retired pre-V1 API. {1}.",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: DocsRoot + "TF1005");

        private static readonly DiagnosticDescriptor MissingRequiredCapability = new DiagnosticDescriptor(
            "TF1006",
            "Required mod capability is not declared",
            "'{0}' requires the root manifest capability '{1}'; add it to capabilities and review the full-process-trust implications",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: DocsRoot + "TF1006",
            customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

        private static readonly DiagnosticDescriptor LoaderOwnedUiReference = new DiagnosticDescriptor(
            "TF1007",
            "Loader-owned UI renderer referenced by a safe mod",
            "'{0}' is a loader-owned implementation, not a mod SDK contract; remove the reference and build UI through Context.Ui",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "Safe mods consume the Unity-free IUiService contract and cannot reference the internal Unity renderer.",
            helpLinkUri: DocsRoot + "TF1007",
            customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

        private static readonly ImmutableDictionary<string, ModuleRequirement> ModuleAssemblies =
            new Dictionary<string, ModuleRequirement>(StringComparer.Ordinal)
            {
                ["TopiaForge.Mods.Chronos"] = new ModuleRequirement("Chronos", "chronos", "io.github.furroxide.topiaforge.chronos"),
                ["TopiaForge.Mods.Prompts"] = new ModuleRequirement("Prompts", "prompts", "io.github.furroxide.topiaforge.prompts"),
                ["TopiaForge.Mods.RobotKit"] = new ModuleRequirement("RobotKit", "robotkit", "io.github.furroxide.topiaforge.robotkit"),
                ["TopiaForge.Mods.Worlds"] = new ModuleRequirement("Worlds", "worlds", "io.github.furroxide.topiaforge.worlds"),
                ["TopiaForge.Mods.Ugc"] = new ModuleRequirement("UGC", "ugc", "io.github.furroxide.topiaforge.ugc.livesync")
            }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

        private static readonly ImmutableDictionary<string, string> RetiredApis =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ITopiaForgeMod"] = "Derive from TopiaForgeMod and override protected OnLoad/OnUnload",
                ["IModServiceRegistry"] = "Publish or consume a typed provider through Context.Extensions",
                ["ModPaths"] = "Use Context.Files, Config, or Storage; filesystem paths are intentionally hidden",
                ["IModFileService"] = "Use the content-based Context.Files API",
                ["LoadConfig"] = "Define ConfigDefinition<T> and call Context.Config.Load(definition)",
                ["SaveConfig"] = "Call Context.Config.Save(definition, value)",
                ["RequireService"] = "Use Context.RequireExtension<T>() for a declared dependency",
                ["TryGetService"] = "Use Context.TryGetExtension<T>(out provider)",
                ["GetService"] = "Use Context.Extensions.TryGet<T>(out provider)"
            }.ToImmutableDictionary(StringComparer.Ordinal);

        /// <inheritdoc/>
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(
                UnsafeNativeApi,
                UnsupportedTargetFramework,
                MissingModuleDependency,
                ObsoletePreV1Api,
                MissingRequiredCapability,
                LoaderOwnedUiReference);

        /// <inheritdoc/>
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(StartCompilation);
        }

        private static void StartCompilation(CompilationStartAnalysisContext context)
        {
            if (HasBooleanBuildProperty(
                    context.Options.AnalyzerConfigOptionsProvider,
                    "TopiaForgeSafeProject",
                    expected: false)
                || HasBooleanBuildProperty(
                    context.Options.AnalyzerConfigOptionsProvider,
                    "IsTestProject",
                    expected: true))
            {
                return;
            }

            var manifest = ManifestSnapshot.Read(context.Options.AdditionalFiles, context.CancellationToken);
            var unsafeNative = manifest.HasCapability("unsafe-native");

            context.RegisterCompilationEndAction(end => AnalyzeManifestContracts(end, manifest));

            if (context.Options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(
                    "build_property.TargetFramework",
                    out var targetFramework)
                && !string.IsNullOrWhiteSpace(targetFramework)
                && !string.Equals(targetFramework, "netstandard2.1", StringComparison.OrdinalIgnoreCase))
            {
                context.RegisterCompilationEndAction(end => end.ReportDiagnostic(Diagnostic.Create(
                    UnsupportedTargetFramework,
                    Location.None,
                    targetFramework)));
            }

            context.RegisterSyntaxNodeAction(
                node => AnalyzeUsing(node, unsafeNative),
                SyntaxKind.UsingDirective);
            context.RegisterSyntaxNodeAction(
                node => AnalyzeIdentifier(node, unsafeNative),
                SyntaxKind.IdentifierName);
        }

        private static void AnalyzeManifestContracts(
            CompilationAnalysisContext context,
            ManifestSnapshot manifest)
        {
            var reportedModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var interopReferenced = false;
            var loaderOwnedUiReferenced = false;
            foreach (var reference in context.Compilation.References)
            {
                if (!(context.Compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly))
                {
                    continue;
                }

                var assemblyName = assembly.Identity.Name;
                if (ModuleAssemblies.TryGetValue(assemblyName, out var module)
                    && reportedModules.Add(assemblyName)
                    && !manifest.IsPackage(module.ManifestId)
                    && !manifest.HasDependency(module.ManifestId))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        MissingModuleDependency,
                        Location.None,
                        assemblyName,
                        module.DisplayName,
                        module.CliName));
                }

                if (string.Equals(
                        assemblyName,
                        "TopiaForge.Mods.Interop.Unity",
                        StringComparison.OrdinalIgnoreCase))
                {
                    interopReferenced = true;
                }

                if (string.Equals(
                        assemblyName,
                        "TopiaForge.Mods.UnityUi",
                        StringComparison.OrdinalIgnoreCase))
                {
                    loaderOwnedUiReferenced = true;
                }
            }

            if (interopReferenced && !manifest.HasCapability("unsafe-native"))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    MissingRequiredCapability,
                    Location.None,
                    "TopiaForge.Mods.Interop.Unity",
                    "unsafe-native"));
            }

            if (loaderOwnedUiReferenced)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    LoaderOwnedUiReference,
                    Location.None,
                    "TopiaForge.Mods.UnityUi"));
            }
        }

        private static bool HasBooleanBuildProperty(
            AnalyzerConfigOptionsProvider optionsProvider,
            string propertyName,
            bool expected)
        {
            return optionsProvider.GlobalOptions.TryGetValue(
                    "build_property." + propertyName,
                    out var value)
                && bool.TryParse(value, out var parsed)
                && parsed == expected;
        }

        private static void AnalyzeUsing(SyntaxNodeAnalysisContext context, bool unsafeNative)
        {
            if (unsafeNative || !(context.Node is UsingDirectiveSyntax directive) || directive.Name == null)
            {
                return;
            }

            var name = directive.Name.ToString();
            if (IsNativeNamespace(name))
            {
                context.ReportDiagnostic(Diagnostic.Create(UnsafeNativeApi, directive.Name.GetLocation(), name));
            }
        }

        private static void AnalyzeIdentifier(
            SyntaxNodeAnalysisContext context,
            bool unsafeNative)
        {
            var identifier = ((IdentifierNameSyntax)context.Node).Identifier.ValueText;
            if (RetiredApis.TryGetValue(identifier, out var replacement))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    ObsoletePreV1Api,
                    context.Node.GetLocation(),
                    identifier,
                    replacement));
                return;
            }

            if (unsafeNative)
            {
                return;
            }

            var symbol = context.SemanticModel.GetSymbolInfo(context.Node, context.CancellationToken).Symbol;
            var namespaceName = symbol?.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            var assemblyName = symbol?.ContainingAssembly?.Name ?? string.Empty;
            if (IsNativeNamespace(namespaceName) || IsNativeAssembly(assemblyName))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    UnsafeNativeApi,
                    context.Node.GetLocation(),
                    symbol?.ToDisplayString() ?? identifier));
            }
        }

        private static bool IsNativeNamespace(string value)
        {
            return value.Equals("UnityEngine", StringComparison.Ordinal)
                || value.StartsWith("UnityEngine.", StringComparison.Ordinal)
                || value.Equals("HarmonyLib", StringComparison.Ordinal)
                || value.StartsWith("HarmonyLib.", StringComparison.Ordinal)
                || value.Equals("GameCode", StringComparison.Ordinal)
                || value.StartsWith("GameCode.", StringComparison.Ordinal);
        }

        private static bool IsNativeAssembly(string value)
        {
            return value.Equals("GameCode", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Assembly-CSharp", StringComparison.OrdinalIgnoreCase)
                || value.Equals("0Harmony", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase);
        }

        private readonly struct ModuleRequirement
        {
            public ModuleRequirement(string displayName, string cliName, string manifestId)
            {
                DisplayName = displayName;
                CliName = cliName;
                ManifestId = manifestId;
            }

            public string DisplayName { get; }
            public string CliName { get; }
            public string ManifestId { get; }
        }

    }
}
