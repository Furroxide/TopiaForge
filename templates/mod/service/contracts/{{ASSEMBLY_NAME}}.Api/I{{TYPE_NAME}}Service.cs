namespace {{ASSEMBLY_NAME}}
{
    /// <summary>
    /// The public API this mod publishes. Consumers depend on {{MOD_ID}} in their manifest, reference
    /// {{ASSEMBLY_NAME}}.Api, and resolve this service with
    /// <c>context.RequireExtension&lt;I{{TYPE_NAME}}Service&gt;()</c>.
    /// </summary>
    public interface I{{TYPE_NAME}}Service
    {
        /// <summary>Returns a message while demonstrating a typed cross-mod call.</summary>
        /// <param name="message">The message to return.</param>
        /// <returns>The supplied message.</returns>
        string Ping(string message);
    }
}
