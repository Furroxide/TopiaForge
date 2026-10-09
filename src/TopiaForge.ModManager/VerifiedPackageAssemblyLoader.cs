using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager
{
    /// <summary>Verifies the selected bytes and the CLR's actual result before exposing declaration types.</summary>
    internal sealed class VerifiedPackageAssemblyLoader
    {
        private readonly ModAssemblyResolutionCatalog catalog;
        private readonly Action<Assembly, string> registerOwner;
        private readonly Action<string>? beforeOpen;
        internal VerifiedPackageAssemblyLoader(ModAssemblyResolutionCatalog catalog, Action<Assembly, string> registerOwner, Action<string>? beforeOpen = null)
        { this.catalog = catalog; this.registerOwner = registerOwner; this.beforeOpen = beforeOpen; }

        internal Type LoadType(ModPackage package, ModImplementationBinding binding)
        {
            var manifest = package.Manifest ?? throw new ArgumentException("A verified manifest is required.");
            var relative = binding.Assembly ?? manifest.EntryAssembly;
            string path;
            try
            {
                if (!PortablePackagePath.TryValidate(relative, out _, out _, out var error))
                    throw new InvalidDataException(error);
                path = PathSafety.CombineRelativeChild(package.PackagePath, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!catalog.TryGetOwner(path, out var owner) || !string.Equals(owner, manifest.Id, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The selected implementation file is not owned by this package.");
            }
            catch (Exception error) { throw new BindingVerificationException(RuntimeBindingFailureCode.UnsafeAssemblyPath, error.Message, error); }
            if (!File.Exists(path)) throw new BindingVerificationException(RuntimeBindingFailureCode.AssemblyMissing, "The declared implementation assembly is missing.");
            var suppliedHash = manifest.Hashes.TryGetValue(relative, out var expected);
            if (binding.Assembly != null && !suppliedHash)
                throw new BindingVerificationException(RuntimeBindingFailureCode.AssemblyHashMissing, "An explicit implementation assembly requires its manifest SHA-256.");
            // Keep a read-only sharing lease through LoadFrom on Windows, so ordinary file replacement
            // cannot race the verified bytes. Receipts remain integrity evidence, not a code sandbox.
            beforeOpen?.Invoke(path);
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    throw new BindingVerificationException(RuntimeBindingFailureCode.UnsafeAssemblyPath, "Implementation assemblies must be regular files.");
                // Verify the complete inventory while the selected file's sharing lease is held.
                // Verifying before this open leaves unhashed entry assemblies exposed to a swap race.
                var receiptErrors = PackageInstallReceipt.Verify(package.PackagePath, manifest);
                if (receiptErrors.Count != 0) throw new BindingVerificationException(RuntimeBindingFailureCode.ReceiptInvalid,
                    "Package integrity changed before declaration binding: " + string.Join("; ", receiptErrors));
                if (suppliedHash)
                {
                    using (var sha = SHA256.Create())
                    {
                        var actual = string.Concat(sha.ComputeHash(input).Select(value => value.ToString("x2")));
                        if (!string.Equals(actual, expected, StringComparison.Ordinal))
                            throw new BindingVerificationException(RuntimeBindingFailureCode.AssemblyHashMismatch, "The implementation assembly does not match its declared SHA-256.");
                    }
                }
                var assembly = Assembly.LoadFrom(path);
                if (string.IsNullOrEmpty(assembly.Location) || !PathSafety.AreSame(path, assembly.Location)
                    || !catalog.TryGetOwner(assembly.Location, out var actualOwner)
                    || !string.Equals(actualOwner, manifest.Id, StringComparison.OrdinalIgnoreCase))
                    throw new BindingVerificationException(RuntimeBindingFailureCode.ForeignAssembly, "The CLR returned an assembly from a different physical package location.");
                try { registerOwner(assembly, manifest.Id); }
                catch (Exception error) { throw new BindingVerificationException(RuntimeBindingFailureCode.ForeignAssembly, error.Message, error); }
                var type = assembly.GetType(binding.Type, throwOnError: false);
                if (type == null) throw new BindingVerificationException(RuntimeBindingFailureCode.MissingType, "The declared implementation type was not found.");
                if (!ReferenceEquals(type.Assembly, assembly))
                    throw new BindingVerificationException(RuntimeBindingFailureCode.ForeignAssembly, "Forwarded implementation types must be declared by their own package assembly.");
                return type;
            }
        }
    }
}
