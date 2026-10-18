using System.Collections.Concurrent;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace TopiaForge.Mods.Analyzers
{
    public sealed partial class TopiaForgeModAnalyzer
    {
        private static readonly DiagnosticDescriptor InputEdgeInFixedUpdate = new DiagnosticDescriptor(
            "TF1009",
            "Input edge read in FixedUpdate",
            "'{0}' reads an input edge in a FixedUpdate handler. FixedUpdate runs zero or more times a frame, before the frame's input is sampled, so it misses or repeats presses, and in multiplayer it never counts as the player's own action; read the edge in an Update or LateUpdate handler and keep what it means for FixedUpdate.",
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "WasPressed and WasReleased hold for the one frame the input service sampled them in, and only a mod's Update and LateUpdate carry the player's input as the player's own action.",
            helpLinkUri: DocsRoot + "TF1009",
            customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

        /// <summary>
        /// Registers TF1009 for one compilation that references the SDK's input and event contracts: every
        /// <c>WasPressed</c> or <c>WasReleased</c> read in a handler passed to <c>SubscribeFixedUpdate</c>, whether a lambda
        /// or a method of this compilation, wherever that method is declared.
        /// </summary>
        private static void RegisterFixedUpdateInput(CompilationStartAnalysisContext context)
        {
            var events = context.Compilation.GetTypeByMetadataName("TopiaForge.Mods.IModEvents");
            var input = context.Compilation.GetTypeByMetadataName("TopiaForge.Mods.IInputAction");
            if (events == null || input == null)
            {
                return;
            }

            var scan = new FixedUpdateInputScan(events, input);
            context.RegisterOperationAction(scan.AnalyzeSubscription, OperationKind.Invocation);
            context.RegisterOperationAction(scan.AnalyzeEdgeRead, OperationKind.PropertyReference);
            context.RegisterCompilationEndAction(scan.Report);
        }

        /// <summary>One compilation's FixedUpdate handlers and the input edges each method reads, matched at its end.</summary>
        private sealed class FixedUpdateInputScan
        {
            private readonly INamedTypeSymbol events;
            private readonly INamedTypeSymbol input;
            private readonly ConcurrentDictionary<ISymbol, bool> handlers = new ConcurrentDictionary<ISymbol, bool>(SymbolEqualityComparer.Default);
            private readonly ConcurrentBag<(ISymbol Method, Location Location, string Text)> methodReads = new ConcurrentBag<(ISymbol, Location, string)>();
            private readonly ConcurrentDictionary<Location, string> lambdaReads = new ConcurrentDictionary<Location, string>();

            internal FixedUpdateInputScan(INamedTypeSymbol events, INamedTypeSymbol input)
            {
                this.events = events;
                this.input = input;
            }

            internal void AnalyzeSubscription(OperationAnalysisContext context)
            {
                var invocation = (IInvocationOperation)context.Operation;
                if (!IsFixedUpdateSubscription(invocation.TargetMethod))
                {
                    return;
                }

                foreach (var argument in invocation.Arguments)
                {
                    var value = argument.Value;
                    while (value is IConversionOperation conversion)
                    {
                        value = conversion.Operand;
                    }

                    if (!(value is IDelegateCreationOperation creation))
                    {
                        continue;
                    }

                    switch (creation.Target)
                    {
                        case IAnonymousFunctionOperation handler:
                            foreach (var read in handler.Body.Descendants().OfType<IPropertyReferenceOperation>())
                            {
                                if (IsEdge(read.Property))
                                {
                                    lambdaReads.TryAdd(read.Syntax.GetLocation(), read.Syntax.ToString());
                                }
                            }

                            break;
                        case IMethodReferenceOperation method:
                            handlers.TryAdd(method.Method.OriginalDefinition, true);
                            break;
                    }
                }
            }

            internal void AnalyzeEdgeRead(OperationAnalysisContext context)
            {
                var read = (IPropertyReferenceOperation)context.Operation;
                if (!IsEdge(read.Property))
                {
                    return;
                }

                // A local function is a handler of its own; a lambda's body belongs to the method around it.
                ISymbol method = context.ContainingSymbol;
                for (var parent = read.Parent; parent != null; parent = parent.Parent)
                {
                    if (parent is ILocalFunctionOperation local)
                    {
                        method = local.Symbol;
                        break;
                    }
                }

                methodReads.Add((method.OriginalDefinition, read.Syntax.GetLocation(), read.Syntax.ToString()));
            }

            internal void Report(CompilationAnalysisContext context)
            {
                var reported = new ConcurrentDictionary<Location, bool>();
                foreach (var pair in lambdaReads)
                {
                    if (reported.TryAdd(pair.Key, true))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(InputEdgeInFixedUpdate, pair.Key, pair.Value));
                    }
                }

                foreach (var (method, location, text) in methodReads)
                {
                    if (handlers.ContainsKey(method) && reported.TryAdd(location, true))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(InputEdgeInFixedUpdate, location, text));
                    }
                }
            }

            private bool IsFixedUpdateSubscription(IMethodSymbol method)
            {
                if (method.Name != "SubscribeFixedUpdate")
                {
                    return false;
                }

                var type = method.ContainingType;
                return SymbolEqualityComparer.Default.Equals(type, events)
                    || type.AllInterfaces.Any(item => SymbolEqualityComparer.Default.Equals(item, events));
            }

            private bool IsEdge(IPropertySymbol property)
            {
                if (property.Name != "WasPressed" && property.Name != "WasReleased")
                {
                    return false;
                }

                var type = property.ContainingType;
                return SymbolEqualityComparer.Default.Equals(type, input)
                    || type.AllInterfaces.Any(item => SymbolEqualityComparer.Default.Equals(item, input));
            }
        }
    }
}
