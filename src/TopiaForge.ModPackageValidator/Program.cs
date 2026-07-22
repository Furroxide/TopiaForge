using TopiaForge.ModManager.Core;

namespace TopiaForge.ModPackageValidator;

internal static class Program
{
    private const string ErrorCode = "TFPKG160";
    private const string ScaffoldErrorCode = "TFSCF170";
    private const string ScaffoldDocsUrl = "https://docs.topiaforge.dev/diagnostics/TFSCF170";

    public static int Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "release-scaffold", StringComparison.Ordinal))
        {
            return ValidateReleaseScaffold(args.Skip(1).ToArray());
        }

        if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            Console.Error.WriteLine(ErrorCode + ": expected one safely extracted package directory.");
            return 2;
        }

        try
        {
            var packageRoot = Path.GetFullPath(args[0]);
            if (!Directory.Exists(packageRoot))
            {
                Console.Error.WriteLine(ErrorCode + ": package directory does not exist: " + packageRoot);
                return 2;
            }

            var manifestPath = Path.Combine(packageRoot, "topiaforge.mod.json");
            if (!File.Exists(manifestPath))
            {
                Console.Error.WriteLine(ErrorCode + ": package manifest does not exist.");
                return 1;
            }

            var manifest = ModManifestJson.LoadFile(manifestPath);
            var errors = ManifestValidator.Validate(manifest).ToList();
            if (errors.Count == 0)
            {
                errors.AddRange(ManifestContentValidator.Validate(packageRoot, manifest));
            }

            if (errors.Count == 0)
            {
                errors.AddRange(ManagedModAssemblyValidator.Validate(packageRoot, manifest));
            }

            if (errors.Count == 0)
            {
                Console.WriteLine("Manifest and managed package metadata validation passed.");
                return 0;
            }

            foreach (var error in errors)
            {
                Console.Error.WriteLine(ErrorCode + ": " + error);
            }

            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(ErrorCode + ": package metadata could not be validated: " + exception.Message);
            return 1;
        }
    }

    private static int ValidateReleaseScaffold(string[] args)
    {
        string? project = null;
        string? package = null;
        string? installedPackages = null;
        var forbidden = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument is "--forbid" or "--package" or "--installed-packages")
            {
                if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                {
                    Console.Error.WriteLine(ScaffoldErrorCode + ": " + argument + " requires a path.");
                    return 2;
                }

                switch (argument)
                {
                    case "--forbid":
                        forbidden.Add(args[index]);
                        break;
                    case "--package":
                        package = args[index];
                        break;
                    case "--installed-packages":
                        installedPackages = args[index];
                        break;
                }

                continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal) || project is not null)
            {
                Console.Error.WriteLine(
                    ScaffoldErrorCode +
                    ": usage: release-scaffold <project> [--forbid path] [--package path --installed-packages path].");
                return 2;
            }

            project = argument;
        }

        if (string.IsNullOrWhiteSpace(project) || (package is null) != (installedPackages is null))
        {
            Console.Error.WriteLine(
                ScaffoldErrorCode +
                ": usage: release-scaffold <project> [--forbid path] [--package path --installed-packages path].");
            return 2;
        }

        try
        {
            var errors = ReleaseScaffoldValidator.Validate(project, forbidden, package, installedPackages);
            if (errors.Count > 0)
            {
                foreach (var error in errors)
                {
                    Console.Error.WriteLine(ScaffoldErrorCode + ": " + error);
                }

                WriteScaffoldRemediation();

                return 1;
            }

            Console.WriteLine(
                "Release scaffold" + (package is null ? string.Empty : " and installed package") +
                " check passed: " + Path.GetFullPath(project));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                ScaffoldErrorCode + ": release scaffold could not be validated: " + exception.Message);
            WriteScaffoldRemediation();
            return 1;
        }
    }

    private static void WriteScaffoldRemediation()
    {
        Console.Error.WriteLine(
            ScaffoldErrorCode +
            ": Remediation: run 'topiaforge restore --project <path>', then correct each reported " +
            "portability, lock, install-receipt, or manager-state mismatch. Docs: " + ScaffoldDocsUrl);
    }
}
