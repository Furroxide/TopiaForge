using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace TopiaForge.Mods.Analyzers
{
    // Operation callbacks reuse Roslyn's semantic model; no second compilation-wide binding pass.
    internal sealed class ParticipantViewAnalysis
    {
        private readonly ConcurrentDictionary<ISymbol, ConcurrentBag<ISymbol>> calls = new ConcurrentDictionary<ISymbol, ConcurrentBag<ISymbol>>(SymbolEqualityComparer.Default);
        private readonly ConcurrentDictionary<ISymbol, byte> roots = new ConcurrentDictionary<ISymbol, byte>(SymbolEqualityComparer.Default);
        private readonly ConcurrentBag<(ISymbol Caller, Location Location)> changes = new ConcurrentBag<(ISymbol, Location)>();
        internal void Record(OperationAnalysisContext context)
        {
            var caller = Canonical(context.ContainingSymbol);
            if (IsParticipant(caller)) roots.TryAdd(caller, 0);
            var target = context.Operation is IInvocationOperation call ? call.TargetMethod
                : (context.Operation as IObjectCreationOperation)?.Constructor;
            if (target == null) return;
            if (SymbolEqualityComparer.Default.Equals(target.ContainingAssembly, context.Compilation.Assembly))
                calls.GetOrAdd(caller, _ => new ConcurrentBag<ISymbol>()).Add(Canonical(target));
            if (context.Operation is IInvocationOperation && (TopiaForgeModAnalyzer.IsViewCall(target)
                || target.Name == "AddBlocker" && TopiaForgeModAnalyzer.Implements(target.ContainingType, "TopiaForge.Mods.IPhysicsService")))
                changes.Add((caller, context.Operation.Syntax.GetLocation()));
        }
        internal IEnumerable<Location> Diagnostics(System.Threading.CancellationToken token)
        {
            var pending = new Queue<ISymbol>(roots.Keys);
            var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var item = pending.Dequeue();
                if (!seen.Add(item) || !calls.TryGetValue(item, out var targets)) continue;
                foreach (var target in targets) pending.Enqueue(target);
            }
            foreach (var item in changes)
                if (seen.Contains(item.Caller)) yield return item.Location;
        }
        private static ISymbol Canonical(ISymbol symbol)
        {
            // Lambdas/local functions retain the participant entry point that contains them.
            while (symbol is IMethodSymbol method && (method.MethodKind == MethodKind.AnonymousFunction || method.MethodKind == MethodKind.LocalFunction))
                symbol = symbol.ContainingSymbol;
            return symbol is IMethodSymbol value ? value.OriginalDefinition : symbol;
        }
        private static bool IsParticipant(ISymbol symbol)
        {
            if (symbol.ContainingType != null && TopiaForgeModAnalyzer.Implements(symbol.ContainingType, "TopiaForge.Mods.IGamemodeParticipantController")) return true;
            if (symbol is IMethodSymbol method)
                foreach (var parameter in method.Parameters)
                    if (parameter.Type.ToDisplayString() == "TopiaForge.Mods.IGamemodeParticipantSession") return true;
            return false;
        }
    }
}
