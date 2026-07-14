using TopiaForge.Mods;

namespace {{ASSEMBLY_NAME}}
{
    internal sealed class {{TYPE_NAME}}Service : I{{TYPE_NAME}}Service
    {
        private readonly IModLogger logger;

        public {{TYPE_NAME}}Service(IModLogger logger)
        {
            this.logger = logger;
        }

        public string Ping(string message)
        {
            logger.Debug("{{DISPLAY_NAME}} ping: " + message);
            return message;
        }
    }
}
