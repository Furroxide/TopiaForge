using System.Text;

namespace TopiaForge.Mods.Multiplayer.Generators
{
    public sealed partial class MultiplayerContractGenerator
    {
        /// <summary>Gets the name of the generated method that submits a command and hands its answer to a callback.</summary>
        private static string AnsweredSubmitName(CommandModel command) => "Submit" + command.Method.Name;

        /// <summary>
        /// Emits <c>Submit{Handler}(request, answered, cancellationToken)</c>: the command proxy authors call. It never
        /// returns a task to poll or wait on; the answer reaches the callback on the main thread, once, and never after
        /// the mod or the caller's scope has stopped.
        /// </summary>
        private static void RenderAnsweredSubmit(StringBuilder builder, CommandModel command)
        {
            var requestType = TypeName(command.RequestType);
            var responseType = TypeName(command.ResponseType);
            builder.AppendLine("    /// <summary>");
            builder.Append("    /// Submits one <c>").Append(Xml(command.Id)).AppendLine("</c> command, and hands the host's answer to <paramref name=\"answered\"/>.");
            builder.AppendLine("    /// </summary>");
            builder.AppendLine("    /// <remarks>");
            builder.AppendLine("    /// The answer runs once, on the main thread: before this returns where this process is the host, solo play");
            builder.AppendLine("    /// included, and otherwise in the frame that brings the host's result. It never runs once the mod stops or");
            builder.AppendLine("    /// <paramref name=\"cancellationToken\"/> is cancelled, so pass a scope's stopping token to tie the answer to that");
            builder.AppendLine("    /// scope. A session that ends or a connection that drops answers with <see cref=\"global::TopiaForge.Mods.ModErrorCode.Cancelled\"/>.");
            builder.AppendLine("    /// </remarks>");
            builder.AppendLine("    /// <param name=\"request\">What the command carries. The host checks every field again.</param>");
            builder.AppendLine("    /// <param name=\"answered\">Receives the host's answer; null submits without waiting for one.</param>");
            builder.AppendLine("    /// <param name=\"cancellationToken\">Withdraws the submission and its answer when cancelled.</param>");
            builder.Append("    public void ").Append(AnsweredSubmitName(command)).Append('(').Append(requestType)
                .Append(" request, global::System.Action<global::TopiaForge.Mods.MultiplayerCommandConfirmation<").Append(responseType)
                .AppendLine(">>? answered = null, global::System.Threading.CancellationToken cancellationToken = default)");
            builder.AppendLine("    {");
            builder.AppendLine("        var binding = __topiaforgeMultiplayerBinding;");
            builder.AppendLine("        var session = __topiaforgeMultiplayerSession;");
            builder.AppendLine("        if (session == null || binding == null) throw new global::System.InvalidOperationException(\"BindMultiplayer must succeed before a generated command proxy is used.\");");
            builder.Append("        global::TopiaForge.Mods.MultiplayerAnswers.Submit<").Append(requestType).Append(", ").Append(responseType)
                .Append(">(session, __topiaforgeCommandType_").Append(PublicName(command.Id))
                .AppendLine(", request, answered, binding.Stopping, cancellationToken);");
            builder.AppendLine("    }");
            builder.AppendLine();
        }

        private static string Xml(string value) =>
            value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
