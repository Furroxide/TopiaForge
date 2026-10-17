using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.Seam;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The architecture rule for the realm and the factory seam: nothing in the internal replication SPI, or in the runtime
    /// types behind it, is shaped like a network address or carries a network type, and nothing the realm and the seam
    /// write or answer holds one, whatever addresses the failures they report contain.
    /// </summary>
    internal static class ReplicationSpiPrivacyTests
    {
        private static readonly string[] AddressWords = { "address", "endpoint", "hostname", "socket" };
        private static readonly string[] Secrets = { "203.0.113.21", "2001:db8", "play.example.org" };

        internal static void Run()
        {
            TheSeamCarriesNoAddress();
            TheRealmAndTheSeamWriteNoAddress();
        }

        private static void TheSeamCarriesNoAddress()
        {
            var spi = typeof(IModContext).Assembly.GetTypes()
                .Where(type => type.Namespace == "TopiaForge.Mods.Internal" && !type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
                .ToArray();
            foreach (var expected in new[]
                     {
                         typeof(IInternalReplicationContext), typeof(IRuntimeReplication), typeof(IInternalReplicaFactory), typeof(IInternalMicrophoneLease),
                         typeof(InternalSpawnDescriptor), typeof(InternalCommand), typeof(InternalNetRef), typeof(InternalReplicationLimits)
                     })
            {
                That(spi.Contains(expected), "the scan covers " + expected.Name);
            }

            var runtime = new[]
            {
                typeof(MultiplayerRealmChange), typeof(MultiplayerPromotion), typeof(IMultiplayerPromotionCensus), typeof(MultiplayerSpawnRecord),
                typeof(MultiplayerSpawnDescription), typeof(MultiplayerSpawnLedger), typeof(MultiplayerSpawnKinds), typeof(MultiplayerReplication),
                typeof(IMultiplayerReferenceSource), typeof(RuntimeReplication), typeof(MultiplayerMicrophoneArbiter), typeof(MultiplayerMicrophoneArbiter.Hold),
                typeof(NetAssetService), typeof(NetEntityService), typeof(NetItemService), typeof(NetUiService), typeof(MultiplayerRuntime)
            };
            var offenders = new List<string>();
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var type in spi.Concat(runtime).Concat(runtime.SelectMany(type => type.GetNestedTypes(All))))
            {
                if (IsAddressShaped(type.Name)) offenders.Add(type.Name);
                foreach (var member in type.GetMembers(All))
                {
                    if (member.Name.StartsWith("<", StringComparison.Ordinal)) continue;
                    if (IsAddressShaped(member.Name) || Types(member).Any(IsNetworkType)) offenders.Add(type.Name + "." + member.Name);
                    foreach (var parameter in member is MethodBase method ? method.GetParameters() : Array.Empty<ParameterInfo>())
                    {
                        if (parameter.Name != null && IsAddressShaped(parameter.Name)) offenders.Add(type.Name + "." + member.Name + "(" + parameter.Name + ")");
                    }
                }
            }

            That(offenders.Count == 0, "no member, parameter or type of the seam is shaped like an address (" + string.Join(", ", offenders) + ")");
        }

        private static void TheRealmAndTheSeamWriteNoAddress()
        {
            // The runtime's own answers are fixed texts the redactor leaves alone: they hold nothing address-shaped.
            foreach (var text in new[]
                     {
                         MultiplayerRuntime.ProviderMissing, MultiplayerRuntime.LiveSwitchedOff, MultiplayerRuntime.LeaveGameFirst, MultiplayerRuntime.StopHostingFirst,
                         MultiplayerRuntime.LeaveSessionFirst, MultiplayerRuntime.AlreadyHosting, MultiplayerRuntime.AlreadyJoined, MultiplayerRuntime.SessionBusy,
                         MultiplayerRuntime.StandaloneRefusal(new[] { "Solo", "Story" }, "join")
                     })
            {
                That(MultiplayerDiagnosticRedactor.Redact(text) == text, "'" + text + "' holds nothing address-shaped");
            }

            // Every failure the realm and the seam report, each carrying addresses, as a broken provider or source might.
            var phaseFails = false;
            var (runtime, control, log) = Runtime(() => phaseFails ? throw Failure() : SessionPhase.Running);
            var census = new RecordingCensus { Failure = Failure() };
            runtime.PromotionCensus = census;
            runtime.Connect();
            control.Role = InternalSessionRole.ListenHost;
            Frame(runtime);
            control.LeaveFailure = Failure();
            var answers = new List<string> { runtime.Leave().Result.ErrorMessage };
            control.LeaveFailure = null;
            control.Role = InternalSessionRole.Solo;
            Frame(runtime);
            phaseFails = true;
            answers.Add(runtime.Join(new FakeJoinTarget(), new InternalJoinSettings()).Result.ErrorMessage);
            answers.Add(runtime.OpenToMultiplayer(new InternalHostSettings("Open", 4)).Result.ErrorMessage);
            phaseFails = false;

            var services = new MultiplayerReplication(runtime) { References = new FailingReferences() };
            var seam = services.Bind("io.example.robotkit", new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Process), new OwnerModLifetime());
            seam.TryCreateRef(new SeamEntity(), out _);
            seam.TryResolveRef(new InternalNetRef(InternalNetRefKind.Entity, "5"), out _);
            seam.RecordAssetSpawn(new SeamEntity { ThrowOnId = true }, new AssetSpawnRequest(new SeamPrefab(), TransformState.Identity));
            answers.Add(seam.AcquireMicrophone(InternalMicrophoneUse.NativeRecording, "Talking").ErrorMessage);

            That(log.Lines.Count(line => line.StartsWith("error:", StringComparison.Ordinal)) == 5,
                "the census, the leave, the phase, the reference source and the recording each reported (" + string.Join(" | ", log.Lines) + ")");
            foreach (var line in log.Lines.Concat(answers))
            {
                foreach (var secret in Secrets)
                {
                    That(!line.Contains(secret, StringComparison.OrdinalIgnoreCase), "'" + line + "' holds no address");
                }
            }
        }

        private static Exception Failure() =>
            new InvalidOperationException("peer [2001:db8::21]:27250 via 203.0.113.21:27250 at play.example.org");

        private static bool IsAddressShaped(string name)
        {
            foreach (var word in AddressWords)
            {
                if (name.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return name.EndsWith("Port", StringComparison.Ordinal) || name == "port" || name.EndsWith("Ip", StringComparison.Ordinal) || name == "ip";
        }

        private static IEnumerable<Type> Types(MemberInfo member) => member switch
        {
            PropertyInfo property => new[] { property.PropertyType },
            FieldInfo field => new[] { field.FieldType },
            MethodInfo method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType),
            ConstructorInfo constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType),
            EventInfo handler => handler.EventHandlerType == null ? Array.Empty<Type>() : new[] { handler.EventHandlerType },
            _ => Array.Empty<Type>()
        };

        private static bool IsNetworkType(Type type)
        {
            if (type.HasElementType) return IsNetworkType(type.GetElementType()!);
            if (type.IsGenericType && type.GetGenericArguments().Any(IsNetworkType)) return true;
            return type.Namespace != null && (type.Namespace == "System.Net" || type.Namespace.StartsWith("System.Net.", StringComparison.Ordinal));
        }

        private sealed class FailingReferences : IMultiplayerReferenceSource
        {
            public bool TryCreateRef(IEntity entity, out InternalNetRef reference) => throw Failure();

            public bool TryResolveRef(InternalNetRef reference, out IEntity? entity) => throw Failure();
        }
    }
}
