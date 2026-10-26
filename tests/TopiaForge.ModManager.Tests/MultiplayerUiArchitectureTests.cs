using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The in-game sessions interface's architecture: its presenters and screens carry no address and no network type, the
    /// one exception being CONNECT's typed address; its views use the TopiaForgeUi kit and nothing else, so the UI Gallery
    /// compiles them; none of it logs; and every file stays within the 400-line UI cap.
    /// </summary>
    internal static partial class MultiplayerUiArchitectureTests
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
            OtherPlayersTextRendersPlain(root);
            ChatUsesPlainTextAndReleasesFocus(root);
            ModalsSurviveSceneLoadsAndRowsKeepTheirHeight(root);
            PresentersGoOnlineThroughTheRealm(root);
            LauncherChannelsAreCalledOffTheFrame();
            LaunchesShowBeforeTheOverlayOpens(root);
            TheOverlayFrameRunsThroughTheGuard(root);
            TheOverlayShowsTheCurrentFailure(root);
            TheOverlayOffersCancelOnlyWhileItStopsSomething(root);
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

        private static void ChatUsesPlainTextAndReleasesFocus(string root)
        {
            string Read(string path) => File.ReadAllText(Path.Combine(root, path));
            var chat = Read("src/TopiaForge.ModManager/Overlay/Multiplayer/ChatOverlay.cs");
            var chrome = Read("src/TopiaForge.Mods.UnityUi/Widgets/TopiaForgeChatLog.Chrome.cs");
            var field = Read("src/TopiaForge.Mods.UnityUi/Widgets/TopiaForgeInputField.cs");
            var widget = Read("src/TopiaForge.Mods.UnityUi/Widgets/TopiaForgeWidget.cs");
            That(chrome.Contains("text.richText = false;", StringComparison.Ordinal), "chat messages render literally, never markup");
            That(field.Contains("textComponent.richText = false;", StringComparison.Ordinal) && chat.Contains(".PlainText()", StringComparison.Ordinal), "the draft is plain too");
            That(widget.Contains("TopiaForgeFocus.Release(Go);", StringComparison.Ordinal), "kit widgets release only their own selection");
            var adapter = Read("src/TopiaForge.ModManager/Overlay/InSessionOverlay.cs");
            That(!chat.Contains("PlayerInput", StringComparison.Ordinal) && !adapter.Contains("PlayerInput", StringComparison.Ordinal), "chat never disables or restores native input");
            That(adapter.Contains("IsLocalPlayerTyping = screen.IsTyping", StringComparison.Ordinal), "presence reads the named typing seam");
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
            files.Add(Path.Combine(overlay, "InSessionOverlay.cs"));
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
                new Regex(@"\.Rect\b", RegexOptions.CultureInvariant),
                // A widget's GameObject is a Unity type, and the UI Gallery compiles these views with no Unity reference.
                new Regex(@"\.Go\b", RegexOptions.CultureInvariant)
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

        /// <summary>
        /// A host writes its server's name and its kick messages, and a player chooses their name, and the screens show those
        /// words. Rich text would let them restyle or hide what this game says, so every label the views make with text that
        /// is not a literal renders plain, toasts show plain, and destructive confirmations, which can name a player, too.
        /// </summary>
        private static void OtherPlayersTextRendersPlain(string root)
        {
            var label = new Regex(@"\.Label\(\s*(?<text>[^,]+),[^;]*;", RegexOptions.CultureInvariant);
            var labels = 0;
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "TopiaForge.ModManager", "Overlay", "Multiplayer"), "*.cs"))
            {
                var source = File.ReadAllText(file);
                foreach (Match match in label.Matches(source))
                {
                    labels++;
                    var text = match.Groups["text"].Value.Trim();
                    if (text.StartsWith("\"", StringComparison.Ordinal)) continue;
                    That(match.Value.Contains(".PlainText()", StringComparison.Ordinal), Path.GetFileName(file) + " renders a label of screen text as plain text: " + match.Value.Trim());
                }

                That(!source.Contains("TopiaForgeToasts.Show(", StringComparison.Ordinal), Path.GetFileName(file) + " shows toasts as plain text, never with markup");
                var destructive = Regex.Matches(source, @"Modal\.Destructive\([^;]*;", RegexOptions.CultureInvariant | RegexOptions.Singleline);
                foreach (Match call in destructive)
                {
                    That(call.Value.Contains("plainText: true", StringComparison.Ordinal), Path.GetFileName(file) + " shows destructive confirmations as plain text");
                }
            }

            That(labels >= 25, "the scan covers the views' labels (" + labels + ")");
        }

        /// <summary>
        /// Seen in the game: "Leave current game?" returns to the main menu with the connecting overlay open, and a modal
        /// that is not persistent is destroyed with the scene it opened in, so the join went on with no overlay, its
        /// failure never showed, and the view threw from the plugin's frame. Every modal the views open persists. Also seen:
        /// a kit spacer flexes both ways, so a row holding one took a share of the screen's height and squeezed HOST's form
        /// into a sliver at the largest UI scale; the views' spacers flex across only.
        /// </summary>
        private static void ModalsSurviveSceneLoadsAndRowsKeepTheirHeight(string root)
        {
            var modals = 0;
            var spacers = 0;
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "TopiaForge.ModManager", "Overlay", "Multiplayer"), "*.cs"))
            {
                var source = File.ReadAllText(file);
                foreach (Match call in Regex.Matches(source, @"Modal\.(?:Custom|Destructive|Confirm|ConfirmHud)\([^;]*;", RegexOptions.CultureInvariant | RegexOptions.Singleline))
                {
                    modals++;
                    That(call.Value.Contains("persistent: true", StringComparison.Ordinal), Path.GetFileName(file) + " opens a modal that survives scene loads: " + call.Value.Trim());
                }

                foreach (Match spacer in Regex.Matches(source, @"\.Spacer\([^)]*\)[^;]*;", RegexOptions.CultureInvariant))
                {
                    spacers++;
                    That(spacer.Value.Contains(".Flex(1f, 0f)", StringComparison.Ordinal), Path.GetFileName(file) + " keeps a spacer from stretching its row's height: " + spacer.Value.Trim());
                }
            }

            That(modals >= 3 && spacers >= 6, "the scan covers the views' modals and spacers (" + modals + ", " + spacers + ")");
        }

        /// <summary>
        /// The presenters host, join and leave only through the runtime's realm, whose rules read the orchestrator's
        /// RealmPhase; a call straight to the control would skip them, as a join during a main-menu load once did. The
        /// check reads the presenters' compiled code, every method, lambda and state machine of their namespace, so no
        /// spelling hides such a call: a field, a local alias or a null-forgiving dereference all compile to the same call.
        /// The views name no multiplayer SPI type at all, so they cannot reach the control either.
        /// </summary>
        private static void PresentersGoOnlineThroughTheRealm(string root)
        {
            var forbidden = new HashSet<string>(StringComparer.Ordinal) { "StartHosting", "StopHosting", "Join", "Leave", "ReplaceSession" };
            var types = typeof(MultiplayerSessionsPresenter).Assembly.GetTypes().Where(type => type.Namespace == "TopiaForge.ModManager.Sessions").ToArray();
            var calls = 0;
            var controlCalls = 0;
            var offenders = new List<string>();
            const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var type in types)
            {
                foreach (var method in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
                {
                    foreach (var called in CalledMethods(method))
                    {
                        calls++;
                        if (called.DeclaringType != typeof(IInternalMultiplayerControl)) continue;
                        controlCalls++;
                        if (forbidden.Contains(called.Name)) offenders.Add(type.Name + "." + method.Name + " calls the control's " + called.Name);
                    }
                }
            }

            That(calls > 1000 && controlCalls >= 8, "the scan reads the presenters' compiled calls (" + calls + ", " + controlCalls + " to the control)");
            That(offenders.Count == 0, "the presenters go online and leave through the runtime's realm, never the control (" + string.Join("; ", offenders) + ")");
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "TopiaForge.ModManager", "Overlay", "Multiplayer"), "*.cs"))
            {
                That(File.ReadAllText(file).IndexOf("TopiaForge.Mods.Internal", StringComparison.Ordinal) < 0, Path.GetFileName(file) + " names no multiplayer SPI type");
            }
        }

        /// <summary>
        /// Nothing the interface throws may reach Unity's frame, where the game's crash reporter takes it: the overlay runs
        /// its multiplayer stages through the frame (<see cref="MultiplayerOverlayFrame"/>), the tab builds and hides through
        /// the guard, and the game's later answers to a launch or a return to the menu reach the screens through it too. Besides
        /// those stages the frame only counts the status line's feedback down (ManagerStatus, arithmetic that cannot throw) and
        /// puts the normal status back on the overlay's own label when it expires.
        /// </summary>
        private static void TheOverlayFrameRunsThroughTheGuard(string root)
        {
            var overlay = File.ReadAllText(Path.Combine(root, "src", "TopiaForge.ModManager", "Overlay", "ManagerOverlay.cs"));
            var tick = Regex.Match(overlay, @"public void Tick\(\)\s*\{(?<body>[^}]*)\}", RegexOptions.CultureInvariant);
            That(tick.Success && Regex.Replace(Regex.Replace(tick.Groups["body"].Value, @"//[^\n]*", string.Empty), @"\s+", string.Empty)
                    == "multiplayerFrame.Tick(Time.unscaledDeltaTime);if(status.Tick(Time.unscaledDeltaTime))statusLabel?.SetText(status.Text);",
                "the overlay's frame runs the multiplayer stages only through the guarded frame, and otherwise only counts its status down ("
                + tick.Value.Trim() + ")");
            That(Regex.IsMatch(overlay, @"multiplayerGuard\.Run\(MultiplayerOverlayFrame\.StartStage, multiplayer\.Start\)", RegexOptions.CultureInvariant) &&
                 Regex.IsMatch(overlay, @"multiplayerGuard\.Run\(MultiplayerOverlayFrame\.TeardownStage,", RegexOptions.CultureInvariant),
                "the overlay starts and closes the multiplayer interface through the guard");
            var tab = File.ReadAllText(Path.Combine(root, "src", "TopiaForge.ModManager", "Overlay", "MultiplayerTab.cs"));
            That(tab.Contains("guard.Run(MultiplayerOverlayFrame.BuildStage,", StringComparison.Ordinal) &&
                 tab.Contains("guard.Run(MultiplayerOverlayFrame.HiddenStage, screens.OnHidden)", StringComparison.Ordinal),
                "the MULTIPLAYER tab builds and hides through the guard");
            var environment = File.ReadAllText(Path.Combine(root, "src", "TopiaForge.ModManager", "Overlay", "PluginMultiplayerEnvironment.cs"));
            var answers = Regex.Matches(environment, @"[^\n]*\bdone\(ok, message\)[^\n]*", RegexOptions.CultureInvariant).Cast<Match>().ToArray();
            That(answers.Length == 2 && answers.All(answer => answer.Value.Contains("guard.Run(", StringComparison.Ordinal)),
                "the game's answers to a launch and to a return to the menu reach the screens through the guard (" + answers.Length + ")");
        }

        /// <summary>The methods <paramref name="method"/>'s IL calls, takes the address of or constructs, read opcode by opcode.</summary>
        private static IEnumerable<MethodBase> CalledMethods(MethodBase method)
        {
            var il = method.GetMethodBody()?.GetILAsByteArray();
            if (il == null) yield break;
            var typeArguments = method.DeclaringType != null && method.DeclaringType.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
            var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
            var position = 0;
            while (position < il.Length)
            {
                int value = il[position++];
                if (value == 0xFE) value = 0xFE00 | il[position++];
                if (!OpCodesByValue.TryGetValue((short)value, out var code)) throw new InvalidOperationException("Assertion failed: an unknown opcode in " + method.Name);
                var operand = position;
                position += OperandSize(code, il, position);
                if (code.OperandType != OperandType.InlineMethod) continue;
                yield return method.Module.ResolveMethod(BitConverter.ToInt32(il, operand), typeArguments, methodArguments)!;
            }
        }

        private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => field.GetValue(null)).OfType<OpCode>().ToDictionary(code => code.Value);

        private static int OperandSize(OpCode code, byte[] il, int position)
        {
            switch (code.OperandType)
            {
                case OperandType.InlineNone:
                    return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    return 1;
                case OperandType.InlineVar:
                    return 2;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    return 8;
                case OperandType.InlineSwitch:
                    return 4 + (4 * BitConverter.ToInt32(il, position));
                default:
                    return 4;
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
