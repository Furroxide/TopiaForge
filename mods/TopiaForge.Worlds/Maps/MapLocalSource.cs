using System;
using System.IO;
using System.Threading;

namespace TopiaForge.Worlds.Maps
{
    internal static class MapLocalSource
    {
        internal static MapDocument Read(RoboWorldImportPlan plan, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            for (var current = plan.FilePath; current != null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new FormatException("The map export must not use a linked path.");
            using var input = new FileStream(plan.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length <= 0 || input.Length > MapDocument.MaximumPackedBytes)
                throw new FormatException("The map export must be nonempty and at most 8 MiB.");
            var bytes = new byte[(int)input.Length];
            var count = 0;
            while (count < bytes.Length)
            {
                token.ThrowIfCancellationRequested();
                var read = input.Read(bytes, count, bytes.Length - count);
                if (read == 0) throw new FormatException("The map export changed while it was being read.");
                count += read;
            }
            if (input.ReadByte() != -1) throw new FormatException("The map export changed while it was being read.");
            token.ThrowIfCancellationRequested();
            return new MapDocument(bytes);
        }
    }
}
