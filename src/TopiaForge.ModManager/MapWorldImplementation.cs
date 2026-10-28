using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed class MapWorldImplementation : ISessionImplementation<IWorldContentProvider>
    {
        private readonly InternalMapWorldRequest request;
        internal MapWorldImplementation(ModPackage package, PackageIdentity identity, ModWorldDeclaration world)
        {
            Package = identity;
            DeclarationId = world.Id;
            var content = world.Content!;
            if (content.Kind != ModWorldContent.MapKind || (content.Map == null) == (content.File == null))
                throw new ArgumentException("A map world requires exactly one source.");
            Func<CancellationToken, Task<OperationResult<byte[]>>>? read = null;
            if (content.File != null)
            {
                var file = content.File;
                if (!package.Manifest!.Hashes.TryGetValue(file, out var digest))
                    throw new ArgumentException("A packaged map requires its verified hash.");
                read = token => ReadFileAsync(package.PackagePath, file, digest, token);
            }
            request = new InternalMapWorldRequest(content.Map, content.Scene, read, content.File);
        }
        public PackageIdentity Package { get; }
        public string DeclarationId { get; }
        public Type ImplementationType => typeof(Provider);
        public IWorldContentProvider Create() => new Provider(DeclarationId, request);

        private sealed class Provider : IWorldContentProvider
        {
            private readonly string id;
            private readonly InternalMapWorldRequest request;
            internal Provider(string id, InternalMapWorldRequest request) { this.id = id; this.request = request; }
            public Task<OperationResult<IWorldInstance>> LoadAsync(IWorldLoadContext context, CancellationToken cancellationToken)
            {
                if (context.WorldId != id || context.WorldFamilyId != null || context.Transition != WorldLoadTransition.SceneReplacement)
                    return Failure(ModErrorCode.InvalidArgument, "The map selection does not match its declaration.");
                if (!context.Context.Extensions.TryGet<IInternalMapWorldService>(out var maps))
                    return Failure(ModErrorCode.Unavailable, "Map loading needs an enabled TopiaForge Worlds package. Install or enable it and restart.");
                return maps.LoadAsync(context, request, cancellationToken);
            }
            private static Task<OperationResult<IWorldInstance>> Failure(ModErrorCode code, string message) =>
                Task.FromResult(OperationResult<IWorldInstance>.Failure(code, message));
        }

        private static Task<OperationResult<byte[]>> ReadFileAsync(string root, string file, string digest, CancellationToken token) => Task.Run(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var path = PathSafety.CombineRelativeChild(root, file);
                for (var current = path; current != null; current = Path.GetDirectoryName(current))
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        return OperationResult<byte[]>.Failure(ModErrorCode.InvalidArgument, "The packaged map uses a linked path. Repair the mod before playing.");
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                const int maximum = 8 * 1024 * 1024;
                if (input.Length <= 0 || input.Length > maximum)
                    return OperationResult<byte[]>.Failure(ModErrorCode.InvalidArgument, "The packaged map is empty or exceeds the 8 MiB limit.");
                using var output = new MemoryStream();
                var buffer = new byte[16384];
                int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (output.Length + count > maximum) throw new IOException();
                    output.Write(buffer, 0, count);
                }
                var bytes = output.ToArray();
                using var sha = SHA256.Create();
                var actual = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty);
                token.ThrowIfCancellationRequested();
                return string.Equals(actual, digest, StringComparison.OrdinalIgnoreCase) ? OperationResult<byte[]>.Success(bytes)
                    : OperationResult<byte[]>.Failure(ModErrorCode.Conflict, "The packaged map changed after installation. Repair the mod before playing.");
            }
            catch (OperationCanceledException) { return OperationResult<byte[]>.Failure(ModErrorCode.Cancelled, "Map loading was cancelled."); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
            { return OperationResult<byte[]>.Failure(ModErrorCode.NotFound, "The packaged map is missing or unreadable. Repair the mod before playing."); }
        });
    }
}
