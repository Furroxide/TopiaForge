using System;
using System.Collections.Concurrent;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace TopiaForge.Mods.Analyzers
{
    public sealed partial class TopiaForgeModAnalyzer
    {
        private static readonly DiagnosticDescriptor InputEdgeInFixedUpdate = new DiagnosticDescriptor(
            "TF1009",
            "Input edge read in FixedUpdate",
            "'{0}' reads an input edge in a FixedUpdate handler. FixedUpdate runs zero or more times a frame, before the frame's input is sampled, so it misses or repeats presses, and in multiplayer it never counts as the player's own action; read the edge in an Update or LateUpdate handler and set a field there that FixedUpdate reads.",
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "WasPressed and WasReleased hold for the one frame the input service sampled them in, and only a mod's Update and LateUpdate carry the player's input as the player's own action.",
            helpLinkUri: DocsRoot + "TF1009",
            customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

        /// <summary>
        /// Registers TF1009 for one compilation that references the SDK's input and event contracts: every
        /// <c>WasPressed</c> or <c>WasReleased</c> read in a handler passed to <c>SubscribeFixedUpdate</c>, whether a lambda,
        /// a method of this compilation wherever it is declared, or either kept in a local first. Code the handler hands to
        /// the SDK to run later, an Update subscription or a scheduled callback, runs on the frame's own clock and is not
        /// the handler's.
        /// </summary>
        private static void RegisterFixedUpdateInput(CompilationStartAnalysisContext context)
        {
            var events = context.Compilation.GetTypeByMetadataName("TopiaForge.Mods.IModEvents");
            var input = context.Compilation.GetTypeByMetadataName("TopiaForge.Mods.IInputAction");
            if (events == null || input == null)
            {
                return;
            }

            var scheduler = context.Compilation.GetTypeByMetadataName("TopiaForge.Mods.IModScheduler");
            var scan = new FixedUpdateInputScan(events, input, scheduler);
            context.RegisterOperationAction(scan.AnalyzeSubscription, OperationKind.Invocation);
            context.RegisterOperationAction(scan.AnalyzeEdgeRead, OperationKind.PropertyReference);
            context.RegisterCompilationEndAction(scan.Report);
        }

        /// <summary>One compilation's FixedUpdate handlers and the input edges each method reads, matched at its end.</summary>
        private sealed class FixedUpdateInputScan
        {
            private readonly INamedTypeSymbol events;
            private readonly INamedTypeSymbol input;
            private readonly INamedTypeSymbol? scheduler;
            private readonly ConcurrentDictionary<ISymbol, bool> handlers = new ConcurrentDictionary<ISymbol, bool>(SymbolEqualityComparer.Default);
            private readonly ConcurrentBag<(ISymbol Method, Location Location, string Text)> methodReads = new ConcurrentBag<(ISymbol, Location, string)>();
            private readonly ConcurrentDictionary<Location, string> lambdaReads = new ConcurrentDictionary<Location, string>();

            internal FixedUpdateInputScan(INamedTypeSymbol events, INamedTypeSymbol input, INamedTypeSymbol? scheduler)
            {
                this.events = events;
                this.input = input;
                this.scheduler = scheduler;
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
                    switch (HandlerOf(argument.Value)?.Target)
                    {
                        case IAnonymousFunctionOperation handler:
                            foreach (var read in handler.Body.Descendants().OfType<IPropertyReferenceOperation>())
                            {
                                if (IsEdgeRead(read) && !RunsLater(read, handler))
                                {
                                    lambdaReads.TryAdd(read.Syntax.GetLocation(), Text(read));
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
                if (!IsEdgeRead(read) || RunsLater(read, null))
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

                methodReads.Add((method.OriginalDefinition, read.Syntax.GetLocation(), Text(read)));
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

            /// <summary>
            /// The delegate an argument hands over: made right there, through any conversion, or kept in a local that the
            /// same body declares with one.
            /// </summary>
            private static IDelegateCreationOperation? HandlerOf(IOperation value)
            {
                value = Unwrap(value);
                if (value is ILocalReferenceOperation local)
                {
                    var root = value;
                    while (root.Parent != null)
                    {
                        root = root.Parent;
                    }

                    var declarator = root.Descendants()
                        .OfType<IVariableDeclaratorOperation>()
                        .FirstOrDefault(item => SymbolEqualityComparer.Default.Equals(item.Symbol, local.Local));
                    var initial = declarator?.GetVariableInitializer()?.Value;
                    if (initial != null)
                    {
                        value = Unwrap(initial);
                    }
                }

                return value as IDelegateCreationOperation;
            }

            private static IOperation Unwrap(IOperation value)
            {
                while (value is IConversionOperation conversion)
                {
                    value = conversion.Operand;
                }

                return value;
            }

            /// <summary>
            /// Whether <paramref name="read"/> sits in a lambda that the handler hands to the SDK to run later: an event
            /// subscription of its own, or a scheduled callback. Its reads are not the handler's: an Update subscription or a
            /// scheduled callback runs on the frame's own clock, and a FixedUpdate subscription is a handler that its own
            /// subscription reports. The walk stops at <paramref name="handler"/> and at a local function, whose reads count
            /// where it is a handler itself.
            /// </summary>
            private bool RunsLater(IOperation read, IOperation? handler)
            {
                for (var node = read.Parent; node != null && !ReferenceEquals(node, handler); node = node.Parent)
                {
                    if (node is ILocalFunctionOperation)
                    {
                        return false;
                    }

                    if (node is IAnonymousFunctionOperation && HandedToTheSdkForLater(node))
                    {
                        return true;
                    }
                }

                return false;
            }

            private bool HandedToTheSdkForLater(IOperation function)
            {
                var value = function.Parent;
                if (!(value is IDelegateCreationOperation))
                {
                    return false;
                }

                while (value.Parent is IConversionOperation)
                {
                    value = value.Parent;
                }

                if (!(value.Parent is IArgumentOperation argument) || !(argument.Parent is IInvocationOperation call))
                {
                    return false;
                }

                var owner = call.TargetMethod.ContainingType;
                return Is(owner, events) || (scheduler != null && Is(owner, scheduler));
            }

            private bool IsFixedUpdateSubscription(IMethodSymbol method) =>
                method.Name == "SubscribeFixedUpdate" && Is(method.ContainingType, events);

            /// <summary>Whether <paramref name="read"/> reads an SDK input edge; setting a mod's own property is no read.</summary>
            private bool IsEdgeRead(IPropertyReferenceOperation read)
            {
                var property = read.Property;
                if (property.Name != "WasPressed" && property.Name != "WasReleased")
                {
                    return false;
                }

                if (read.Parent is ISimpleAssignmentOperation assignment && ReferenceEquals(assignment.Target, read))
                {
                    return false;
                }

                return Is(property.ContainingType, input);
            }

            private static bool Is(INamedTypeSymbol? type, INamedTypeSymbol contract) =>
                type != null && (SymbolEqualityComparer.Default.Equals(type, contract)
                    || type.AllInterfaces.Any(item => SymbolEqualityComparer.Default.Equals(item, contract)));

            /// <summary>The read as the author wrote it: inside a conditional access, with its receiver.</summary>
            private static string Text(IOperation read)
            {
                var text = read.Syntax.ToString();
                if (text.StartsWith(".", StringComparison.Ordinal))
                {
                    var access = read.Syntax.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>();
                    if (access != null)
                    {
                        text = access.ToString();
                    }
                }

                return text;
            }
        }
    }
}
