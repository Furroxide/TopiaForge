using TopiaForge.Mods;

namespace {{ASSEMBLY_NAME}}
{
    // Copy this small pattern into a consumer mod after declaring {{MOD_ID}} as a manifest dependency.
    internal sealed class {{TYPE_NAME}}ConsumerExample
    {
        private readonly I{{TYPE_NAME}}Service service;

        public {{TYPE_NAME}}ConsumerExample(IModContext context)
        {
            service = context.RequireExtension<I{{TYPE_NAME}}Service>();
        }

        public string PingProvider(string message) => service.Ping(message);
    }
}
