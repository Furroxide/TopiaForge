using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.RegularExpressions;
using TopiaForge.ModManager.Sessions;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The in-game sessions interface's architecture: its presenters and screens carry no address and no network type, the
    /// one exception being CONNECT's typed address; its views use the TopiaForgeUi kit and nothing else, so the UI Gallery
    /// compiles them; none of it logs; and every file stays within the 400-line UI cap.
    /// </summary>
    internal static class MultiplayerUiArchitectureTests
    {
        internal const int UiFileCap = 400;

        private static readonly string[] AddressWords = { "address", "endpoint", "hostname", "socket" };

        /// <summary>The typed address in CONNECT: the one place an address may be held, as the player's own input.</summary>
        private static readonly HashSet<string> TypedAddress = new HashSet<string>(StringComparer.Ordinal)
        {
            "IConnectScreen.Address", "IConnectScreen.AddressError", "IConnectScreen.SetAddress", "IConnectScreen.CanJoinAddress", "IConnectScreen.JoinAddress",
            "ConnectPresenter.address", "ConnectPresenter.addressError", "ConnectPresenter.Address", "ConnectPresenter.AddressError",
            "ConnectPresenter.SetAddress", "ConnectPresenter.AddressRule", "ConnectPresenter.CanJoinAddress", "ConnectPresenter.JoinAddress"
        };

        internal static void Run()
        {
            var root = Program.FindRepoRoot();
            PresentersCarryNoAddress();
            ViewsUseTheKitOnly(root);
            NothingLogs(root);
            UiFilesStayWithinTheirCap(root);
            Console.WriteLine("MultiplayerUiArchitectureTests passed.");
        }

        /// <summary>The folders whose sources make up the in-game sessions interface.</summary>
        internal static IEnumerable<string> SourceFolders(string root)
        {
            yield return Path.Combine(root, "src", "TopiaForge.ModManager", "Multiplayer", "Screens");
            yield return Path.Combine(root, "src", "TopiaForge.ModManager", "Multiplayer", "Sessions");
            yield return Path.Combine(root, "src", "TopiaForge.ModManager", "Overlay", "Multiplayer");
        }

        /// <summary>Every source file of the interface: its folders, its overlay adapters and its gallery page.</summary>
        internal static IReadOnlyList<string> SourceFiles(string root)
        {
            var files = new List<string>();
            foreach (var folder in SourceFolders(root)) files.AddRange(Directory.EnumerateFiles(folder, "*.cs", SearchOption.TopDirectoryOnly));
            var overlay = Path.Combine(root, "src", "TopiaForge.ModManager", "Overlay");
            files.Add(Path.Combine(overlay, "MultiplayerTab.cs"));
            files.Add(Path.Combine(overlay, "PluginMultiplayerEnvironment.cs"));
            files.Add(Path.Combine(overlay, "ManagerTabLookup.cs"));
            files.AddRange(Directory.EnumerateFiles(Path.Combine(root, "mods", "TopiaForge.UiGallery", "Pages"), "SessionsPage*.cs"));
            That(files.Count >= 30 && files.All(File.Exists), "the scan covers the whole interface (" + files.Count + " files)");
            return files;
        }

        private static void PresentersCarryNoAddress()
        {
            var types = typeof(MultiplayerSessionsPresenter).Assembly.GetTypes()
                .Where(type => type.Namespace == "TopiaForge.ModManager.Sessions" && !type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
                .ToArray();
            That(types.Length >= 25 && types.Contains(typeof(ConnectPresenter)) && types.Contains(typeof(IMultiplayerScreens)) && types.Contains(typeof(HostText)),
                "the scan covers the presenters and screens (" + types.Length + " types)");
            var offenders = new List<string>();
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var type in types)
            {
                var owner = type.DeclaringType == null ? type.Name : type.DeclaringType.Name;
                if (IsAddressShaped(type.Name)) offenders.Add(type.Name);
                foreach (var member in type.GetMembers(All))
                {
                    if (member.Name.StartsWith("<", StringComparison.Ordinal)) continue;
                    var name = type.Name + "." + Accessed(member.Name);
                    if (IsAddressShaped(member.Name) && !TypedAddress.Contains(name) && !TypedAddress.Contains(owner + "." + Accessed(member.Name))) offenders.Add(name);
                    if (TypesOf(member).Any(IsNetworkType)) offenders.Add(name + " (network type)");
                    if (member is MethodBase method)
                    {
                        foreach (var parameter in method.GetParameters())
                        {
                            if (parameter.Name != null && IsAddressShaped(parameter.Name) && !TypedAddress.Contains(name)) offenders.Add(name + "(" + parameter.Name + ")");
                            if (IsNetworkType(parameter.ParameterType)) offenders.Add(name + "(" + parameter.Name + ": network type)");
                        }
                    }
                }
            }

            That(offenders.Count == 0, "no presenter or screen member, parameter or type is shaped like an address beyond the typed one (" + string.Join(", ", offenders) + ")");
        }

        private static void ViewsUseTheKitOnly(string root)
        {
            var views = Directory.EnumerateFiles(Path.Combine(root, "src", "TopiaForge.ModManager", "Overlay", "Multiplayer"), "*.cs").ToArray();
            That(views.Length >= 8, "the scan covers the views (" + views.Length + " files)");
            var forbidden = new[]
            {
                new Regex(@"using\s+UnityEngine", RegexOptions.CultureInvariant),
                new Regex(@"\bUnityEngine\.", RegexOptions.CultureInvariant),
                new Regex(@"AddComponent\s*<", RegexOptions.CultureInvariant),
                new Regex(@"new\s+GameObject", RegexOptions.CultureInvariant),
                new Regex(@"\.sortingOrder\b", RegexOptions.CultureInvariant),
                new Regex(@"#[0-9A-Fa-f]{6}\b", RegexOptions.CultureInvariant),
                new Regex(@"\bnew\s+Color(?:32)?\s*\(", RegexOptions.CultureInvariant),
                new Regex(@"\.Rect\b", RegexOptions.CultureInvariant)
            };
            foreach (var file in views)
            {
                var source = File.ReadAllText(file);
                foreach (var pattern in forbidden)
                {
                    That(!pattern.IsMatch(source), Path.GetFileName(file) + " builds its UI through the kit only, never " + pattern);
                }
            }

            foreach (var folder in new[] { "Screens", "Sessions" })
            {
                foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "TopiaForge.ModManager", "Multiplayer", folder), "*.cs"))
                {
                    var source = File.ReadAllText(file);
                    That(!forbidden[0].IsMatch(source) && !forbidden[1].IsMatch(source), Path.GetFileName(file) + " stays free of Unity");
                    if (folder == "Screens") That(source.IndexOf("TopiaForge.Mods.Internal", StringComparison.Ordinal) < 0, Path.GetFileName(file) + " names no multiplayer SPI type, so the gallery compiles it");
                }
            }
        }

        /// <summary>Nothing in the interface writes a log: host text and typed addresses can never reach one from here.</summary>
        private static void NothingLogs(string root)
        {
            var logging = new Regex(@"\b(?:Debug\.Log\w*|Console\.Write\w*|TopiaForgeLog\.\w+|[Ll]ogger\.\w+|ReportDiagnostic|LogInfo|LogWarn|LogError)\b", RegexOptions.CultureInvariant);
            foreach (var file in SourceFiles(root))
            {
                var match = logging.Match(File.ReadAllText(file));
                That(!match.Success, Path.GetFileName(file) + " writes no log (" + match.Value + ")");
            }
        }

        private static void UiFilesStayWithinTheirCap(string root)
        {
            foreach (var file in SourceFiles(root))
            {
                var lines = File.ReadLines(file).Count();
                That(lines <= UiFileCap, Path.GetFileName(file) + " stays within the " + UiFileCap + "-line UI cap (" + lines + " lines)");
            }

            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "TopiaForge.ModManager", "Overlay"), "*.cs", SearchOption.AllDirectories))
            {
                var lines = File.ReadLines(file).Count();
                That(lines <= UiFileCap, "overlay file " + Path.GetFileName(file) + " stays within the " + UiFileCap + "-line UI cap (" + lines + " lines)");
            }
        }

        /// <summary>Gets the property a compiler-made accessor belongs to, so <c>get_Address</c> reads as <c>Address</c>.</summary>
        private static string Accessed(string name) =>
            name.StartsWith("get_", StringComparison.Ordinal) || name.StartsWith("set_", StringComparison.Ordinal) ? name.Substring(4) : name;

        private static bool IsAddressShaped(string name)
        {
            foreach (var word in AddressWords)
            {
                if (name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }

            return false;
        }

        private static IEnumerable<Type> TypesOf(MemberInfo member)
        {
            switch (member)
            {
                case FieldInfo field:
                    yield return field.FieldType;
                    break;
                case PropertyInfo property:
                    yield return property.PropertyType;
                    break;
                case MethodInfo method:
                    yield return method.ReturnType;
                    break;
                case EventInfo @event when @event.EventHandlerType != null:
                    yield return @event.EventHandlerType;
                    break;
            }
        }

        private static bool IsNetworkType(Type type)
        {
            var element = type.IsArray || type.IsByRef ? type.GetElementType()! : type;
            return element == typeof(IPAddress) || typeof(EndPoint).IsAssignableFrom(element) || element == typeof(Socket) ||
                   element.Namespace == "System.Net.Sockets";
        }
    }
}
