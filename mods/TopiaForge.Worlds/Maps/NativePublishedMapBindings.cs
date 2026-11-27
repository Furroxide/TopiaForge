using System;
using System.Linq;
using System.Reflection;
using TopiaForge.Mods;

namespace TopiaForge.Worlds.Maps
{
    // The legacy direct project and current manifest protocol share only the owned transport policy.
    internal sealed class NativePublishedMapBindings
    {
        private const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;
        private readonly MethodInfo resolve;
        private readonly MethodInfo? readProject;
        internal Uri PublicBase { get; }
        internal bool UsesManifest => readProject != null;
        internal NativePublishedMapBindings(Type loader)
        {
            var byRef = typeof(string).MakeByRefType();
            var parameters = new[] { typeof(string), byRef, byRef, byRef, byRef };
            var legacyBase = loader.GetField("DefaultPublicBaseUrl", PublicStatic);
            var legacyResolve = loader.GetMethod("TryResolveProjectUrl", PublicStatic, null, parameters, null);
            object? value;
            if (legacyBase?.FieldType == typeof(string) && legacyResolve?.ReturnType == typeof(bool) && !legacyResolve.IsGenericMethod)
            {
                value = legacyBase.GetValue(null);
                resolve = legacyResolve!;
            }
            else
            {
                var production = loader.GetField("ProductionMapsBaseUrl", BindingFlags.NonPublic | BindingFlags.Static);
                resolve = loader.GetMethod("TryResolveManifestUrl", PublicStatic, null, parameters, null) ?? throw Unavailable();
                readProject = loader.GetMethod("ReadProjectUrl", PublicStatic, null, new[] { typeof(string), typeof(string) }, null);
                var file = loader.GetField("ManifestFileName", BindingFlags.NonPublic | BindingFlags.Static);
                if (production?.FieldType != typeof(string) || !production.IsLiteral || resolve.ReturnType != typeof(bool) || resolve.IsGenericMethod
                    || readProject?.ReturnType != typeof(string) || readProject.IsGenericMethod || file?.FieldType != typeof(string) || !file.IsLiteral
                    || !string.Equals(file.GetRawConstantValue() as string, "manifest.json", StringComparison.Ordinal)) throw Unavailable();
                value = production.GetRawConstantValue();
            }
            if (!(value is string text) || !Uri.TryCreate(text, UriKind.Absolute, out var origin)) throw Unavailable();
            new MapEndpointPolicy(origin);
            PublicBase = origin;
        }
        internal Uri Resolve(string id)
        {
            if (!MapDocument.IsId(id, 64)) throw new MapProblem(ModErrorCode.InvalidArgument, "Choose a valid published map id.");
            try
            {
                var arguments = new object?[] { id, null, null, null, null };
                if (!(resolve.Invoke(null, arguments) is bool success) || !success)
                    throw new MapProblem(ModErrorCode.InvalidArgument, "The game could not resolve this published map. Check its id or update TopiaForge.");
                var policy = new MapEndpointPolicy(PublicBase);
                if (UsesManifest)
                {
                    if (!(arguments[1] is string text) || !(arguments[2] is string resolvedId) || resolvedId != id
                        || !Uri.TryCreate(text, UriKind.Absolute, out var manifest)
                        || !manifest.AbsolutePath.EndsWith("/manifest.json", StringComparison.Ordinal)) throw Unavailable();
                    policy.Require(manifest);
                    return manifest;
                }
                foreach (var value in arguments.Skip(1).OfType<string>())
                    if (Uri.TryCreate(value, UriKind.Absolute, out var project) && project.AbsolutePath.EndsWith("/project.roboworld", StringComparison.Ordinal))
                    { policy.Require(project); return project; }
                throw Unavailable();
            }
            catch (MapProblem) { throw; }
            catch (Exception) { throw Unavailable(); }
        }
        internal Uri ReadProject(Uri manifest, string json)
        {
            try
            {
                var policy = new MapEndpointPolicy(PublicBase);
                policy.Require(manifest);
                var value = readProject?.Invoke(null, new object[] { manifest.AbsoluteUri, json });
                if (!(value is string text) || !Uri.TryCreate(text, UriKind.Absolute, out var project)) throw Unavailable();
                policy.Require(project);
                return project;
            }
            catch (Exception) { throw Unavailable(); }
        }
        private static MapProblem Unavailable() => new MapProblem(ModErrorCode.Unavailable,
            "This game build cannot resolve maps through TopiaForge yet. Update TopiaForge.");
    }
}
