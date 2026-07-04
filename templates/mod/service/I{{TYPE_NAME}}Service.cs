namespace {{ASSEMBLY_NAME}}
{
    /// <summary>
    /// The public API this mod publishes. Consumers depend on {{MOD_ID}} in their manifest (which also exposes
    /// this assembly via apiAssemblies) and resolve it with context.GetService&lt;I{{TYPE_NAME}}Service&gt;().
    /// </summary>
    public interface I{{TYPE_NAME}}Service
    {
        string Ping(string message);
    }
}
