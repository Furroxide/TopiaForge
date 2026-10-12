using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TopiaForge.Mods.Multiplayer.Generators
{
    public sealed partial class MultiplayerContractGenerator
    {
        private const string SessionStartedHook = "OnMultiplayerSessionStarted";
        private const string SessionEndedHook = "OnMultiplayerSessionEnded";
        private const string SnapshotType = "TopiaForge.Mods.MultiplayerSessionSnapshot";

        /// <summary>
        /// Finds the optional session hooks an author implemented. The session tracker is generated only when at
        /// least one exists, so contracts without hooks pay nothing for it.
        /// </summary>
        private static SessionHooksModel FindSessionHooks(INamedTypeSymbol contract) =>
            new SessionHooksModel(FindSessionHook(contract, SessionStartedHook), FindSessionHook(contract, SessionEndedHook));

        private static SessionHookModel? FindSessionHook(INamedTypeSymbol contract, string name)
        {
            foreach (var method in contract.GetMembers(name).OfType<IMethodSymbol>())
            {
                if (method.IsStatic || method.IsGenericMethod || !method.ReturnsVoid || method.Parameters.Length != 1 ||
                    method.Parameters[0].RefKind != RefKind.None ||
                    method.Parameters[0].Type.ToDisplayString() != SnapshotType)
                {
                    continue;
                }

                // A defining declaration with no implementation compiles to nothing, so there is nothing to call.
                if (method.IsPartialDefinition && method.PartialImplementationPart == null) continue;
                var implementation = method.PartialImplementationPart ?? method;
                var declaration = implementation.DeclaringSyntaxReferences
                    .Select(reference => reference.GetSyntax())
                    .OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault();
                if (declaration == null || (declaration.Body == null && declaration.ExpressionBody == null)) continue;

                var needsDefinition = declaration.Modifiers.Any(SyntaxKind.PartialKeyword) &&
                                      method.PartialDefinitionPart == null &&
                                      !method.IsPartialDefinition;
                var accessibility = string.Join(" ", declaration.Modifiers
                    .Where(modifier =>
                        modifier.IsKind(SyntaxKind.PublicKeyword) || modifier.IsKind(SyntaxKind.PrivateKeyword) ||
                        modifier.IsKind(SyntaxKind.ProtectedKeyword) || modifier.IsKind(SyntaxKind.InternalKeyword))
                    .Select(modifier => modifier.Text));
                return new SessionHookModel(name, needsDefinition, accessibility);
            }

            return null;
        }

        private static void RenderSessionHooks(StringBuilder builder, SessionHooksModel hooks)
        {
            if (!hooks.Any) return;
            foreach (var hook in new[] { hooks.Started, hooks.Ended })
            {
                if (hook == null || !hook.NeedsDefinition) continue;
                builder.Append("    ");
                if (hook.Accessibility.Length != 0) builder.Append(hook.Accessibility).Append(' ');
                builder.Append("partial void ").Append(hook.Name)
                    .AppendLine("(global::TopiaForge.Mods.MultiplayerSessionSnapshot snapshot);");
                builder.AppendLine();
            }

            builder.AppendLine("    private global::TopiaForge.Mods.MultiplayerSessionId? __topiaforgeStartedSession;");
            builder.AppendLine("    private global::TopiaForge.Mods.MultiplayerSessionSnapshot? __topiaforgeStartedSnapshot;");
            builder.AppendLine();
            builder.AppendLine("    // Calls the author's hooks once per session: started on the first Ready snapshot of a session id,");
            builder.AppendLine("    // ended with the last snapshot observed for it when it ends or another session replaces it.");
            builder.AppendLine("    private void __TopiaForgeTrackSession(global::TopiaForge.Mods.MultiplayerSessionSnapshot snapshot)");
            builder.AppendLine("    {");
            builder.AppendLine("        if (__topiaforgeStartedSession.HasValue)");
            builder.AppendLine("        {");
            builder.AppendLine("            if (__topiaforgeStartedSession.Value.Equals(snapshot.Id) && snapshot.State != global::TopiaForge.Mods.MultiplayerSessionState.Ended)");
            builder.AppendLine("            {");
            builder.AppendLine("                __topiaforgeStartedSnapshot = snapshot;");
            builder.AppendLine("                return;");
            builder.AppendLine("            }");
            builder.AppendLine();
            builder.AppendLine("            var ended = __topiaforgeStartedSession.Value.Equals(snapshot.Id) ? snapshot : __topiaforgeStartedSnapshot!;");
            builder.AppendLine("            __topiaforgeStartedSession = null;");
            builder.AppendLine("            __topiaforgeStartedSnapshot = null;");
            if (hooks.Ended != null)
            {
                builder.Append("            this.").Append(hooks.Ended.Name).AppendLine("(ended);");
            }
            else
            {
                builder.AppendLine("            _ = ended;");
            }

            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine("        if (snapshot.State != global::TopiaForge.Mods.MultiplayerSessionState.Ready) return;");
            builder.AppendLine("        __topiaforgeStartedSession = snapshot.Id;");
            builder.AppendLine("        __topiaforgeStartedSnapshot = snapshot;");
            if (hooks.Started != null)
            {
                builder.Append("        this.").Append(hooks.Started.Name).AppendLine("(snapshot);");
            }

            builder.AppendLine("    }");
            builder.AppendLine();
        }

        private sealed class SessionHooksModel
        {
            public SessionHooksModel(SessionHookModel? started, SessionHookModel? ended)
            {
                Started = started;
                Ended = ended;
            }

            public SessionHookModel? Started { get; }
            public SessionHookModel? Ended { get; }
            public bool Any => Started != null || Ended != null;
        }

        private sealed class SessionHookModel
        {
            public SessionHookModel(string name, bool needsDefinition, string accessibility)
            {
                Name = name;
                NeedsDefinition = needsDefinition;
                Accessibility = accessibility;
            }

            public string Name { get; }
            public bool NeedsDefinition { get; }
            public string Accessibility { get; }
        }
    }
}
