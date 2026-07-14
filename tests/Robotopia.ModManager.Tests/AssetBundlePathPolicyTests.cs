using System;
using System.IO;
using Robotopia.Assets;

namespace Robotopia.ModManager.Tests
{
    internal static class AssetBundlePathPolicyTests
    {
        public static void Run(string root)
        {
            AcceptsOrdinaryPackageChild(root);
            RejectsPortablePathHazards(root);
            RejectsCaseChangedSiblingOnCaseSensitiveFileSystems(root);
            RejectsSymlinkEscape(root);
            Console.WriteLine("AssetBundlePathPolicyTests passed.");
        }

        private static void AcceptsOrdinaryPackageChild(string root)
        {
            var package = Path.Combine(root, "asset-path-ordinary");
            Directory.CreateDirectory(Path.Combine(package, "bundles"));

            var accepted = AssetBundlePathPolicy.TryResolve(
                package,
                Path.Combine("bundles", "world.bundle"),
                out var resolved,
                out var error);

            Assert(accepted, "ordinary package child should be accepted: " + error);
            Assert(resolved == Path.Combine(package, "bundles", "world.bundle"), "ordinary path should resolve deterministically");
        }

        private static void RejectsPortablePathHazards(string root)
        {
            var package = Path.Combine(root, "asset-path-portable");
            Directory.CreateDirectory(package);

            AssertRejected(package, "../escape.bundle", "parent traversal");
            AssertRejected(package, "bundles//world.bundle", "empty segment");
            AssertRejected(package, "bundles/world.bundle:stream", "alternate data stream");
            AssertRejected(package, "CON.bundle", "reserved device name");
            AssertRejected(package, "bundles/world.bundle.", "trailing dot");
        }

        private static void RejectsCaseChangedSiblingOnCaseSensitiveFileSystems(string root)
        {
            var parent = Path.Combine(root, "asset-path-case");
            var package = Path.Combine(parent, "package");
            Directory.CreateDirectory(package);
            var probe = Path.Combine(package, "probe");
            File.WriteAllText(probe, "probe");
            if (File.Exists(Path.Combine(parent, "PACKAGE", "probe")))
            {
                return;
            }

            var caseChangedSibling = Path.Combine(parent, "PACKAGE");
            Directory.CreateDirectory(caseChangedSibling);
            var accepted = AssetBundlePathPolicy.TryResolve(
                package,
                Path.Combine("..", "PACKAGE", "escape.bundle"),
                out _,
                out _);
            Assert(!accepted, "case-changed sibling traversal must be rejected on case-sensitive filesystems");
        }

        private static void RejectsSymlinkEscape(string root)
        {
            var package = Path.Combine(root, "asset-path-link-package");
            var outside = Path.Combine(root, "asset-path-link-outside");
            var link = Path.Combine(package, "linked");
            Directory.CreateDirectory(package);
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "world.bundle"), "bundle");

            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException ||
                exception is PlatformNotSupportedException ||
                exception is IOException)
            {
                return;
            }

            var accepted = AssetBundlePathPolicy.TryResolve(
                package,
                Path.Combine("linked", "world.bundle"),
                out _,
                out _);
            Assert(!accepted, "a package-relative symlink must not redirect an AssetBundle load outside the package");
        }

        private static void AssertRejected(string package, string relativePath, string scenario)
        {
            var accepted = AssetBundlePathPolicy.TryResolve(package, relativePath, out _, out _);
            Assert(!accepted, scenario + " should be rejected");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Assertion failed: " + message);
            }
        }
    }
}
