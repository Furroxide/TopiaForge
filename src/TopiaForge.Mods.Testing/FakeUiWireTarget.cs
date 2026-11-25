using System;
using System.Security.Cryptography;
using System.Text;

namespace TopiaForge.Mods.Testing
{
    internal readonly struct FakeUiWireTarget
    {
        private const string OwnerPackageId = "example.test-mod";
        private static readonly byte[] PackageDigest = CreateDigest();
        private static readonly byte[] KitThemeDigest = new byte[32];

        private readonly string player;
        private readonly ulong generation;
        private readonly ulong token;

        internal FakeUiWireTarget(string player, ulong generation, ulong token)
        {
            this.player = player;
            this.generation = generation;
            this.token = token;
        }

        internal void Measure(FakeUiService owner, ulong revision, UiSurfaceRequest request, string body, bool visible,
            UiNode? content, string epoch, bool retained)
        {
            var descriptor = owner.ThemeForWire(request.Theme);
            UiWireEnvelope? envelope = null;
            if (descriptor != null || UiWireCodec.NeedsEnvelope(content))
            {
                var digest = descriptor != null ? descriptor.Digest.Span : KitThemeDigest.AsSpan();
                envelope = new UiWireEnvelope(OwnerPackageId, PackageDigest, generation, descriptor?.Id, digest);
            }

            UiWireCodec.MeasureTargetedSurface(OwnerPackageId, player, token, revision, request, body,
                visible, content, epoch, retained, envelope);
        }

        private static byte[] CreateDigest()
        {
            using var sha256 = SHA256.Create();
            return sha256.ComputeHash(Encoding.UTF8.GetBytes(OwnerPackageId));
        }
    }
}
