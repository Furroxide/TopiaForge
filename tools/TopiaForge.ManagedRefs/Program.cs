using System;
using System.Threading.Tasks;

namespace TopiaForge.ManagedRefs;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var options = ManagedRefsOptions.Parse(args, Environment.GetEnvironmentVariable);
            if (options.ShowHelp)
            {
                Console.WriteLine(ManagedRefsOptions.HelpText);
                return 0;
            }

            using var restore = new ManagedRefsRestore(options);
            await restore.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Managed-reference restore failed: {exception.Message}");
            return 1;
        }
    }
}
