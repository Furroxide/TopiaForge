using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using TopiaForge.ModManager.Sessions;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Sessions the launcher asks for, and the frame: no launcher channel is read or written on the main thread, and a
    /// launch's questions and connecting overlay show before the player ever opened TopiaForge's overlay.
    /// </summary>
    internal static partial class MultiplayerUiArchitectureTests
    {
        private const BindingFlags DeclaredMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        /// <summary>
        /// Seen in the game: a session the launcher asked for reaches the screens before the player opened TopiaForge's
        /// overlay, and the prompts and the connecting overlay exist only once the overlay is built, so a launched host's
        /// password question never showed and the launch waited for ever. The outside stage builds the overlay, closed, as
        /// soon as a question or a join needs the screen.
        /// </summary>
        private static void LaunchesShowBeforeTheOverlayOpens(string root)
        {
            var overlay = File.ReadAllText(Path.Combine(root, "src", "TopiaForge.ModManager", "Overlay", "ManagerOverlay.cs"));
            var outside = Regex.Match(overlay, @"private void TickMultiplayerOutside\(\)\s*\{(?<body>[^}]*)\}", RegexOptions.CultureInvariant);
            That(outside.Success && Regex.IsMatch(outside.Groups["body"].Value,
                    @"if \(multiplayerUi == null && \(multiplayer\.PendingPrompt != null \|\| multiplayer\.Connecting\.IsVisible\)\) Build\(\);", RegexOptions.CultureInvariant),
                "the overlay's outside stage builds the overlay once a question or a join needs the screen (" + outside.Value.Trim() + ")");
        }

        /// <summary>
        /// The launcher's channels touch the disk, and a refused write or a locked journal waits between tries, so the frame
        /// never calls them. Every call in this game's code is in a lambda that the method making it hands to the background
        /// queue, or in the live-join intake's poll, which only the intake's own timer runs. The check reads the compiled code
        /// of ModManager's own namespaces, every method, lambda and state machine, so no spelling hides a call.
        /// </summary>
        private static void LauncherChannelsAreCalledOffTheFrame()
        {
            var reads = new HashSet<string>(StringComparer.Ordinal) { "get_RequestId", "get_PackageSetDigest", "get_ServiceOrigin" };
            var enqueue = typeof(MultiplayerBackgroundQueue).GetMethod("Enqueue", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var poll = typeof(LiveJoinIntake).GetMethod(nameof(LiveJoinIntake.Poll), BindingFlags.Instance | BindingFlags.NonPublic)!;
            var methods = typeof(MultiplayerSessionsPresenter).Assembly.GetTypes()
                .Where(type => type.Namespace == "TopiaForge.ModManager" || type.Namespace == "TopiaForge.ModManager.Sessions")
                .SelectMany(type => type.GetMethods(DeclaredMembers).Cast<MethodBase>().Concat(type.GetConstructors(DeclaredMembers)))
                .ToArray();
            var calls = 0;
            var offenders = new List<string>();
            var pollers = new List<string>();
            foreach (var method in methods)
            {
                foreach (var called in CalledMethods(method))
                {
                    if (Same(called, poll)) pollers.Add(method.DeclaringType!.Name + "." + method.Name);
                    if (called.DeclaringType != typeof(ILauncherChannels) || reads.Contains(called.Name)) continue;
                    calls++;
                    if (Same(method, poll) || IsQueuedLambda(method, enqueue)) continue;
                    offenders.Add(method.DeclaringType!.Name + "." + method.Name + " calls " + called.Name);
                }
            }

            That(calls >= 7, "the scan reads every call to the launcher's channels (" + calls + ")");
            That(offenders.Count == 0, "the launcher's channels are called only on the background queue or the intake's timer (" + string.Join("; ", offenders) + ")");
            That(pollers.Count == 1 && pollers[0].StartsWith("LiveJoinIntake.<.ctor>", StringComparison.Ordinal),
                "and the intake's poll runs only from the callback its timer is made with (" + string.Join("; ", pollers) + ")");
        }

        /// <summary>Gets whether <paramref name="method"/> is a lambda the method that makes it hands to the background queue.</summary>
        private static bool IsQueuedLambda(MethodBase method, MethodInfo enqueue)
        {
            var name = method.Name;
            var close = name.IndexOf('>');
            if (!name.StartsWith("<", StringComparison.Ordinal) || close < 2 || string.CompareOrdinal(name, close + 1, "b__", 0, 3) != 0) return false;
            var outerName = name.Substring(1, close - 1);

            // A lambda lives in the type of the method that makes it, or in a closure class nested in that type.
            var owner = method.DeclaringType!;
            var outerType = owner.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false) ? owner.DeclaringType! : owner;
            foreach (var outer in outerType.GetMethods(DeclaredMembers).Cast<MethodBase>().Concat(outerType.GetConstructors(DeclaredMembers)))
            {
                if (outer.Name != outerName) continue;
                var called = CalledMethods(outer).ToArray();
                if (called.Any(each => Same(each, method)) && called.Any(each => Same(each, enqueue))) return true;
            }

            return false;
        }

        private static bool Same(MethodBase left, MethodBase right) => left.Module == right.Module && left.MetadataToken == right.MetadataToken;
    }
}
