using System;
using System.IO;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Interop.Unity;

namespace TopiaForge.ModManager
{
    internal sealed partial class ModContext : IInternalAuthoredMapContext
    {
        private readonly IUnityInteropService? authoredMapInterop;
        OperationResult<IEntity> IInternalAuthoredMapContext.WrapMapEntity(object nativeEntity)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (Lifetime.IsStopping) return OperationResult<IEntity>.Failure(ModErrorCode.Cancelled, "The map owner is stopping.");
            return nativeEntity is UnityEngine.GameObject gameObject && gameObject != null && authoredMapInterop != null
                ? authoredMapInterop.Wrap(gameObject)
                : OperationResult<IEntity>.Failure(ModErrorCode.Unavailable, "The map entity bridge is unavailable.");
        }
        OperationResult<byte[]> IInternalAuthoredMapContext.ReadAnchorFile(string relative)
        {
            if (Lifetime.IsStopping) return OperationResult<byte[]>.Failure(ModErrorCode.Cancelled, "The anchor owner is stopping.");
            if (!AuthoredText.PackagePath(relative)) return Invalid();
            try
            {
                var path = PathSafety.CombineRelativeChild(packagePath, relative);
                for (var current = path; current != null; current = Path.GetDirectoryName(current))
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return Invalid();
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length <= 0 || stream.Length > 256 * 1024) return Invalid();
                var bytes = new byte[(int)stream.Length];
                var count = 0;
                while (count < bytes.Length)
                {
                    if (Lifetime.IsStopping) return Invalid();
                    var read = stream.Read(bytes, count, bytes.Length - count);
                    if (read == 0) return Invalid();
                    count += read;
                }
                return stream.ReadByte() == -1 ? OperationResult<byte[]>.Success(bytes) : Invalid();
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
            { return Invalid(); }
        }
        private static OperationResult<byte[]> Invalid() => OperationResult<byte[]>.Failure(ModErrorCode.InvalidArgument,
            "The anchors file must be a readable ordinary package file of at most 256 KiB.");
    }
}
