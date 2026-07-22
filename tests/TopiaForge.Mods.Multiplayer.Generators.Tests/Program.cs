using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TopiaForge.Mods;
using TopiaForge.Mods.Multiplayer.Generators;

namespace TopiaForge.Mods.Multiplayer.Generators.Tests
{
    internal static class Program
    {
        private const string ValidContractSource = """
            #nullable enable
            using TopiaForge.Mods;

            namespace Sample;

            [MultiplayerContract(Id = "sample.contract")]
            public partial class GameContract
            {
                [ReplicatedState("score")]
                private ReplicatedState<ScoreState> score = new ReplicatedState<ScoreState>(new ScoreState());

                [MultiplayerCommand("increment", Prediction = PredictionMode.Owner, MaximumPerSecond = 20, MaximumPayloadBytes = 512)]
                private OperationResult<IncrementResponse> Increment(MultiplayerCommandContext context, IncrementRequest request)
                {
                    if (request.Amount < 0)
                    {
                        return OperationResult<IncrementResponse>.Failure(ModErrorCode.InvalidArgument, "Amount must be non-negative.");
                    }

                    EmitOnSpark(context, new SparkEvent { Message = "accepted" }, MultiplayerAudience.Sender);
                    return OperationResult<IncrementResponse>.Success(new IncrementResponse { Total = request.Amount });
                }

                [PresentationEvent("spark")]
                private void OnSpark(SparkEvent value)
                {
                }

                [ReplicatedObject("robot", Prediction = PredictionMode.Owner, MaximumPerSecond = 12, MaximumPayloadBytes = 256)]
                private OperationResult<ObjectState> ApplyObjectInput(
                    ReplicatedObjectCommandContext context,
                    ObjectState state,
                    ObjectInput input) =>
                    OperationResult<ObjectState>.Success(new ObjectState { X = state.X + input.DeltaX });
            }

            public sealed class ScoreState
            {
                public TinyKind Kind { get; set; }
                public sbyte Mood { get; set; }
                public int Score { get; set; }

                [NetworkBound(16)]
                public string Label { get; set; } = string.Empty;

                public NestedState? Nested { get; set; }

                [NetworkBound(4)]
                public byte[] Samples { get; set; } = System.Array.Empty<byte>();
            }

            public sealed class NestedState
            {
                public char Marker { get; set; }
                public long Sequence { get; set; }
            }

            public enum TinyKind : sbyte
            {
                Calm = -1,
                Alert = 1
            }

            public sealed class IncrementRequest
            {
                public int Amount { get; set; }
            }

            public sealed class IncrementResponse
            {
                public int Total { get; set; }
            }

            public sealed class SparkEvent
            {
                [NetworkBound(32)]
                public string Message { get; set; } = string.Empty;
            }

            public sealed class ObjectState
            {
                public int X { get; set; }
            }

            public sealed class ObjectInput
            {
                public int DeltaX { get; set; }
            }
            """;

        private static readonly MetadataReference[] References = BuildReferences();

        private static int Main()
        {
            try
            {
                GeneratedContractMatchesGoldenSurface();
                GeneratedConsumerCompilesAndCodecRoundTrips();
                GeneratedPresentationRegistrationSupportsHeadlessPeers();
                NestedDtoShapeChangesSchemaDigest();
                EnumSchemaIncludesUnderlyingTypeAndNamedValues();
                WireFormatRevisionChangesSchemaAndContractLockIdentity();
                ReportsNonPartialContract();
                ReportsUnsupportedPayload();
                ReportsPolymorphicPayload();
                ReportsNativePayload();
                ReportsPayloadConstructorSideEffect();
                ReportsPayloadInstanceInitializerSideEffect();
                ReportsPayloadStaticInitializerSideEffect();
                ReportsUnboundedStringsAndCollections();
                ReportsNondeterministicPrediction();
                ReportsPredictedProcessLocalSideEffect();
                ReportsPredictedHelperBypass();
                ReportsPredictedObjectSideEffect();
                ReportsPredictedFieldMutation();
                ReportsPredictedContextAccess();
                ReportsPredictedPropertyGetterBypass();
                ReportsPredictedConstructorBypass();
                ReportsPredictedInstanceInitializerBypass();
                ReportsPredictedStaticInitializerBypass();
                ReportsPredictedFieldReceiverMutation();
                ReportsPredictedRefFieldMutation();
                ReportsPredictedUserDefinedOperator();
                ReportsPredictedUserDefinedConversion();
                ReportsPredictedExternalGetter();
                ReportsPredictedExternalField();
                ReportsUnsafePredictedStateUpdateMethodGroup();
                AllowsPurePayloadInitializers();
                AllowsDeterministicOperationResultInspection();
                AllowsDeterministicPredictedStateUpdateMethodGroup();
                ReportsGeneratedNameCollision();
                ReportsAuthorGeneratedNameCollision();
                ReportsMissingExplicitContractId();
                ReportsCommandPayloadLimitOverflow();
                ReportsStateAndEventTransportLimitOverflow();
                ReplicatedStateCanReconnectAcrossSessions();
                CommandDefinitionEnforcesPayloadLimit();
                StateAndPresentationDefinitionsEnforceTransportLimit();
                ContractDescriptorDefensivelyCopiesAndValidates();
                LowLevelSpiIsHiddenFromOrdinaryIntelliSense();
                GeneratedBindingCleansUpFailedAndThrowingRegistrations();
                Console.WriteLine("All TopiaForge multiplayer generator tests passed.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        private static void GeneratedContractMatchesGoldenSurface()
        {
            var result = Generate(ValidContractSource);
            AssertNoErrors(result);
            var generated = SingleGeneratedSource(result);
            var actual = GoldenSurface(generated.HintName, generated.SourceText.ToString());
            var path = Path.Combine(AppContext.BaseDirectory, "Golden", "ValidContract.surface.txt");
            var expected = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
            Assert(actual == expected, "Generated contract golden surface changed.\nActual:\n" + actual);
        }

        private static void GeneratedConsumerCompilesAndCodecRoundTrips()
        {
            var result = Generate(ValidContractSource);
            AssertNoErrors(result);
            using var assemblyStream = new MemoryStream();
            var emit = result.Compilation.Emit(assemblyStream);
            Assert(emit.Success, "Generated consumer failed to emit:\n" + JoinErrors(emit.Diagnostics));
            assemblyStream.Position = 0;
            var assembly = AssemblyLoadContext.Default.LoadFromStream(assemblyStream);
            var contractType = assembly.GetType("Sample.GameContract", true)!;
            var stateType = assembly.GetType("Sample.ScoreState", true)!;
            var nestedType = assembly.GetType("Sample.NestedState", true)!;
            var enumType = assembly.GetType("Sample.TinyKind", true)!;
            var contract = Activator.CreateInstance(contractType)!;
            Assert(contract is IGeneratedMultiplayerContract, "Generated contracts must expose their descriptor and typed codecs.");
            var codec = contractType.GetMethod("GetCodec")!.MakeGenericMethod(stateType).Invoke(contract, null)!;
            var codecType = codec.GetType();
            var value = Activator.CreateInstance(stateType)!;
            stateType.GetProperty("Score")!.SetValue(value, 42);
            stateType.GetProperty("Mood")!.SetValue(value, (sbyte)-7);
            stateType.GetProperty("Kind")!.SetValue(value, Enum.ToObject(enumType, (sbyte)-1));
            stateType.GetProperty("Label")!.SetValue(value, "robot-π");
            stateType.GetProperty("Samples")!.SetValue(value, new byte[] { 1, 2, 3, 4 });
            var nested = Activator.CreateInstance(nestedType)!;
            nestedType.GetProperty("Marker")!.SetValue(nested, 'λ');
            nestedType.GetProperty("Sequence")!.SetValue(nested, 99L);
            stateType.GetProperty("Nested")!.SetValue(value, nested);

            var encoded = InvokeSuccessfulResult(codecType.GetMethod("Encode")!.Invoke(codec, new[] { value })!);
            var decoded = InvokeSuccessfulResult(codecType.GetMethod("Decode")!.Invoke(codec, new[] { encoded })!);
            Assert((int)stateType.GetProperty("Score")!.GetValue(decoded)! == 42, "Score did not round-trip.");
            Assert((sbyte)stateType.GetProperty("Mood")!.GetValue(decoded)! == -7, "Signed byte did not round-trip.");
            Assert(Convert.ToSByte(stateType.GetProperty("Kind")!.GetValue(decoded)) == -1, "Signed enum did not round-trip.");
            Assert((string)stateType.GetProperty("Label")!.GetValue(decoded)! == "robot-π", "String did not round-trip.");
            Assert(((byte[])stateType.GetProperty("Samples")!.GetValue(decoded)!).SequenceEqual(new byte[] { 1, 2, 3, 4 }),
                "Array did not round-trip.");
            var decodedNested = stateType.GetProperty("Nested")!.GetValue(decoded)!;
            Assert((char)nestedType.GetProperty("Marker")!.GetValue(decodedNested)! == 'λ', "Char did not round-trip exactly.");

            stateType.GetProperty("Label")!.SetValue(value, new string('x', 17));
            var rejected = codecType.GetMethod("Encode")!.Invoke(codec, new[] { value })!;
            Assert(!(bool)rejected.GetType().GetProperty("Succeeded")!.GetValue(rejected)!,
                "Character bounds must reject a long ASCII string even when it fits the UTF-8 byte ceiling.");

            try
            {
                _ = contractType.GetMethod("GetCodec")!.MakeGenericMethod(typeof(Box)).Invoke(contract, null);
                throw new InvalidOperationException("Unknown contract DTOs must not receive reflection-based fallback serialization.");
            }
            catch (TargetInvocationException exception) when (
                exception.InnerException is InvalidOperationException inner &&
                inner.Message.Contains("No generated multiplayer codec exists", StringComparison.Ordinal))
            {
            }

            var generated = SingleGeneratedSource(result).SourceText.ToString();
            Assert(!generated.Contains("System.Reflection", StringComparison.Ordinal) &&
                   !generated.Contains("JsonSerializer", StringComparison.Ordinal),
                "Generated codecs must not fall back to reflection or general-purpose polymorphic serialization.");
        }

        private static void NestedDtoShapeChangesSchemaDigest()
        {
            var original = SingleGeneratedSource(Generate(ValidContractSource)).SourceText.ToString();
            var changedSource = ValidContractSource.Replace(
                "public long Sequence { get; set; }",
                "public long Sequence { get; set; }\n        public int Revision { get; set; }",
                StringComparison.Ordinal);
            var changed = SingleGeneratedSource(Generate(changedSource)).SourceText.ToString();
            Assert(DescriptorHash(original) != DescriptorHash(changed),
                "A nested DTO shape change must change the generated contract schema digest.");
        }

        private static void EnumSchemaIncludesUnderlyingTypeAndNamedValues()
        {
            var original = DescriptorHash(SingleGeneratedSource(Generate(ValidContractSource)).SourceText.ToString());
            var changedUnderlying = DescriptorHash(SingleGeneratedSource(Generate(ValidContractSource.Replace(
                "public enum TinyKind : sbyte",
                "public enum TinyKind : short",
                StringComparison.Ordinal))).SourceText.ToString());
            var changedValue = DescriptorHash(SingleGeneratedSource(Generate(ValidContractSource.Replace(
                "Calm = -1,",
                "Calm = -2,",
                StringComparison.Ordinal))).SourceText.ToString());
            var addedMember = DescriptorHash(SingleGeneratedSource(Generate(ValidContractSource.Replace(
                "Calm = -1,",
                "Calm = -1,\n        Idle = 0,",
                StringComparison.Ordinal))).SourceText.ToString());
            var reordered = DescriptorHash(SingleGeneratedSource(Generate(ValidContractSource.Replace(
                "Calm = -1,\n        Alert = 1",
                "Alert = 1,\n        Calm = -1",
                StringComparison.Ordinal))).SourceText.ToString());

            Assert(original != changedUnderlying,
                "Changing an enum's underlying wire width must change the generated schema digest.");
            Assert(original != changedValue,
                "Changing an enum member's numeric assignment must change the generated schema digest.");
            Assert(original != addedMember,
                "Adding a named enum member must change the generated schema digest.");
            Assert(original == reordered,
                "Enum schema identity must order named members deterministically rather than depend on declaration order.");
        }

        private static void WireFormatRevisionChangesSchemaAndContractLockIdentity()
        {
            const string contractId = "sample.contract";
            var currentDigest = MultiplayerContractGenerator.EmptyContractSchemaDigestForTesting(1, contractId);
            var nextDigest = MultiplayerContractGenerator.EmptyContractSchemaDigestForTesting(2, contractId);
            var currentLock = MultiplayerContractGenerator.EmptyContractLockMarkerForTesting(1, contractId);
            var nextLock = MultiplayerContractGenerator.EmptyContractLockMarkerForTesting(2, contractId);
            Assert(currentDigest != nextDigest,
                "A generator wire-format revision bump must change the schema digest even when the author contract is unchanged.");
            Assert(currentLock != nextLock,
                "A generator wire-format revision bump must change the generated contract-lock identity.");
        }

        private static void GeneratedPresentationRegistrationSupportsHeadlessPeers()
        {
            var result = Generate(ValidContractSource);
            AssertNoErrors(result);
            var generated = SingleGeneratedSource(result).SourceText.ToString();
            Assert(generated.Contains("session.RegisterPresentation", StringComparison.Ordinal) &&
                   generated.Contains("session.Snapshot.HasPresentation ?", StringComparison.Ordinal),
                "Every peer must register the event codec while only presentation-capable peers attach the local handler.");
        }

        private static void ReportsNonPartialContract() => ExpectDiagnostic("TFMP001", """
            using TopiaForge.Mods;
            [MultiplayerContract]
            public sealed class Contract { }
            """);

        private static void ReportsUnsupportedPayload() => ExpectDiagnostic("TFMP003", ContractWithState("object"));

        private static void ReportsPolymorphicPayload() => ExpectDiagnostic("TFMP003", ContractWithState("OpenPayload") + """
            public class OpenPayload
            {
                public int Value { get; set; }
            }
            """);

        private static void ReportsNativePayload() => ExpectDiagnostic("TFMP003", ContractWithState("NativePayload") + """
            public sealed class NativePayload
            {
                public System.IntPtr Handle { get; set; }
            }
            """);

        private static void ReportsPayloadConstructorSideEffect() => ExpectDiagnostic("TFMP003", ContractWithState("ConstructedPayload") + """
            public sealed class ConstructedPayload
            {
                public ConstructedPayload() { System.IO.File.WriteAllText("unsafe", "unsafe"); }
                public int Value { get; set; }
            }
            """);

        private static void ReportsPayloadInstanceInitializerSideEffect() =>
            ExpectDiagnostic("TFMP003", ContractWithState("InitializedPayload") + """
                public sealed class InitializedPayload
                {
                    public int Value { get; set; } = Initialize();

                    private static int Initialize()
                    {
                        System.IO.File.WriteAllText("unsafe", "unsafe");
                        return 0;
                    }
                }
                """);

        private static void ReportsPayloadStaticInitializerSideEffect() =>
            ExpectDiagnostic("TFMP003", ContractWithState("StaticInitializedPayload") + """
                public sealed class StaticInitializedPayload
                {
                    private static readonly string Hidden = System.IO.File.ReadAllText("unsafe");

                    static StaticInitializedPayload() { }

                    public int Value { get; set; }
                }
                """);

        private static void ReportsUnboundedStringsAndCollections()
        {
            var result = Generate(ContractWithState("UnboundedPayload") + """
                public sealed class UnboundedPayload
                {
                    public string Name { get; set; } = string.Empty;
                    public int[] Values { get; set; } = System.Array.Empty<int>();
                }
                """);
            Assert(result.GeneratorDiagnostics.Count(item => item.Id == "TFMP004") == 2,
                "Both an unbounded string and an unbounded collection should report TFMP004.");
        }

        private static void ReportsNondeterministicPrediction() => ExpectDiagnostic("TFMP005", PredictedCommand("""
            _ = System.Guid.NewGuid();
            return OperationResult<Response>.Success(new Response());
            """));

        private static void ReportsPredictedProcessLocalSideEffect() => ExpectDiagnostic("TFMP006", PredictedCommand("""
            System.IO.File.WriteAllText("local.txt", "unsafe");
            return OperationResult<Response>.Success(new Response());
            """));

        private static void ReportsPredictedHelperBypass() => ExpectDiagnostic("TFMP006", """
            using TopiaForge.Mods;
            [MultiplayerContract]
            public partial class Contract
            {
                [MultiplayerCommand("command", Prediction = PredictionMode.Owner)]
                private OperationResult<Response> Handle(MultiplayerCommandContext context, Request request)
                {
                    UnsafeHelper();
                    return OperationResult<Response>.Success(new Response());
                }

                private static void UnsafeHelper()
                {
                    _ = new System.Random().Next();
                    System.IO.File.WriteAllText("unsafe", "unsafe");
                }
            }
            public sealed class Request { public int Value { get; set; } }
            public sealed class Response { public int Value { get; set; } }
            """);

        private static void ReportsPredictedObjectSideEffect() => ExpectDiagnostic("TFMP005", """
            using TopiaForge.Mods;
            [MultiplayerContract]
            public partial class Contract
            {
                [ReplicatedObject("robot", Prediction = PredictionMode.Owner)]
                private OperationResult<State> Move(ReplicatedObjectCommandContext context, State state, Input input)
                {
                    _ = System.Guid.NewGuid();
                    return OperationResult<State>.Success(new State { Value = state.Value + input.Value });
                }
            }
            public sealed class State { public int Value { get; set; } }
            public sealed class Input { public int Value { get; set; } }
            """);

        private static void ReportsPredictedFieldMutation() => ExpectDiagnostic("TFMP006", """
            using TopiaForge.Mods;
            [MultiplayerContract]
            public partial class Contract
            {
                private int executions;

                [MultiplayerCommand("command", Prediction = PredictionMode.Owner)]
                private OperationResult<Response> Handle(MultiplayerCommandContext context, Request request)
                {
                    executions++;
                    return OperationResult<Response>.Success(new Response());
                }
            }
            public sealed class Request { public int Value { get; set; } }
            public sealed class Response { public int Value { get; set; } }
            """);

        private static void ReportsPredictedContextAccess() => ExpectDiagnostic("TFMP006", """
            using TopiaForge.Mods;
            [MultiplayerContract]
            public partial class Contract
            {
                public IModContext Context { get; set; } = null!;
                [MultiplayerCommand("command", Prediction = PredictionMode.Owner)]
                private OperationResult<Response> Handle(MultiplayerCommandContext context, Request request)
                {
                    Context.Logger.Info("unsafe");
                    return OperationResult<Response>.Success(new Response());
                }
            }
            public sealed class Request { public int Value { get; set; } }
            public sealed class Response { public int Value { get; set; } }
            """);

        private static void ReportsPredictedPropertyGetterBypass() => ExpectDiagnostic("TFMP006", """
            using TopiaForge.Mods;
            [MultiplayerContract]
            public partial class Contract
            {
                private int BadProperty
                {
                    get { System.IO.File.WriteAllText("unsafe", "unsafe"); return 1; }
                }
                [MultiplayerCommand("command", Prediction = PredictionMode.Owner)]
                private OperationResult<Response> Handle(MultiplayerCommandContext context, Request request)
                {
                    _ = BadProperty;
                    return OperationResult<Response>.Success(new Response());
                }
            }
            public sealed class Request { public int Value { get; set; } }
            public sealed class Response { public int Value { get; set; } }
            """);

        private static void ReportsPredictedConstructorBypass() => ExpectDiagnostic("TFMP006", """
            using TopiaForge.Mods;
            [MultiplayerContract]
            public partial class Contract
            {
                [MultiplayerCommand("command", Prediction = PredictionMode.Owner)]
                private OperationResult<Response> Handle(MultiplayerCommandContext context, Request request)
                {
                    _ = new UnsafeHelper();
                    return OperationResult<Response>.Success(new Response());
                }
            }
            public sealed class UnsafeHelper
            {
                public UnsafeHelper() { System.IO.File.WriteAllText("unsafe", "unsafe"); }
            }
            public sealed class Request { public int Value { get; set; } }
            public sealed class Response { public int Value { get; set; } }
            """);

        private static void ReportsPredictedInstanceInitializerBypass() =>
            ExpectDiagnostic("TFMP006", PredictedCommand("""
                _ = new UnsafeHelper();
                return OperationResult<Response>.Success(new Response());
                """) + """
                public sealed class UnsafeHelper
                {
                    private readonly string hidden = System.IO.File.ReadAllText("unsafe");
                }
                """);

        private static void ReportsPredictedStaticInitializerBypass() =>
            ExpectDiagnostic("TFMP005", PredictedCommand("""
                _ = new UnsafeHelper();
                return OperationResult<Response>.Success(new Response());
                """) + """
                public sealed class UnsafeHelper
                {
                    private static readonly System.Guid Hidden = System.Guid.NewGuid();

                    static UnsafeHelper() { }
                }
                """);

        private static void ReportsPredictedFieldReceiverMutation() => ExpectDiagnostic("TFMP006", """
            using System.Collections.Generic;
            using TopiaForge.Mods;
            [MultiplayerContract]
            public partial class Contract
            {
                private readonly List<int> values = new List<int>();
                [MultiplayerCommand("command", Prediction = PredictionMode.Owner)]
                private OperationResult<Response> Handle(MultiplayerCommandContext context, Request request)
                {
                    values.Add(1);
                    return OperationResult<Response>.Success(new Response());
                }
            }
            public sealed class Request { public int Value { get; set; } }
            public sealed class Response { public int Value { get; set; } }
            """);

        private static void ReportsPredictedRefFieldMutation() => ExpectDiagnostic("TFMP006", """
            using TopiaForge.Mods;
            [MultiplayerContract]
            public partial class Contract
            {
                private int counter;
                [MultiplayerCommand("command", Prediction = PredictionMode.Owner)]
                private OperationResult<Response> Handle(MultiplayerCommandContext context, Request request)
                {
                    System.Threading.Interlocked.Increment(ref counter);
                    return OperationResult<Response>.Success(new Response());
                }
            }
            public sealed class Request { public int Value { get; set; } }
            public sealed class Response { public int Value { get; set; } }
            """);

        private static void ReportsPredictedUserDefinedOperator() => ExpectDiagnostic("TFMP006", PredictedCommand("""
            _ = new DangerousNumber { Value = request.Value } + new DangerousNumber { Value = 1 };
            return OperationResult<Response>.Success(new Response());
            """) + """
            public sealed class DangerousNumber
            {
                public int Value { get; set; }
                public static DangerousNumber operator +(DangerousNumber left, DangerousNumber right) =>
                    new DangerousNumber { Value = left.Value + right.Value };
            }
            """);

        private static void ReportsPredictedUserDefinedConversion() => ExpectDiagnostic("TFMP006", PredictedCommand("""
            int converted = new DangerousConversion { Value = request.Value };
            return OperationResult<Response>.Success(new Response { Value = converted });
            """) + """
            public sealed class DangerousConversion
            {
                public int Value { get; set; }
                public static implicit operator int(DangerousConversion value) => value.Value;
            }
            """);

        private static void ReportsPredictedExternalGetter() => ExpectDiagnostic("TFMP006", PredictedCommand("""
            _ = System.Globalization.CultureInfo.CurrentCulture.Name;
            return OperationResult<Response>.Success(new Response());
            """));

        private static void ReportsPredictedExternalField() => ExpectDiagnostic("TFMP006", PredictedCommand("""
            _ = System.DBNull.Value;
            return OperationResult<Response>.Success(new Response());
            """));

        private static void ReportsUnsafePredictedStateUpdateMethodGroup() => ExpectDiagnostic("TFMP006", """
            using TopiaForge.Mods;
            [MultiplayerContract(Id = "sample.unsafe-method-group")]
            public partial class Contract
            {
                [ReplicatedState("state")]
                private ReplicatedState<State> state = new ReplicatedState<State>(new State());

                [MultiplayerCommand("command", Prediction = PredictionMode.Owner)]
                private OperationResult<Response> Handle(MultiplayerCommandContext context, Request request)
                {
                    var updated = state.Update(UnsafeUpdate);
                    return OperationResult<Response>.Success(new Response());
                }

                private static OperationResult<State> UnsafeUpdate(State current)
                {
                    System.IO.File.WriteAllText("unsafe", "unsafe");
                    return OperationResult<State>.Success(new State { Value = current.Value + 1 });
                }
            }
            public sealed class State { public int Value { get; set; } }
            public sealed class Request { public int Value { get; set; } }
            public sealed class Response { public int Value { get; set; } }
            """);

        private static void AllowsPurePayloadInitializers()
        {
            var result = Generate("""
                using TopiaForge.Mods;
                [MultiplayerContract(Id = "sample.pure-initializers")]
                public partial class Contract
                {
                    [ReplicatedState("state")]
                    private ReplicatedState<PurePayload> state = new ReplicatedState<PurePayload>(new PurePayload());
                }

                public sealed class PurePayload
                {
                    public int Value { get; set; } = 42;

                    [TopiaForge.Mods.NetworkBound(16)]
                    public string Label { get; set; } = string.Empty;

                    [TopiaForge.Mods.NetworkBound(4)]
                    public byte[] Samples { get; set; } = System.Array.Empty<byte>();
                }
                """);
            AssertNoErrors(result);
        }

        private static void AllowsDeterministicOperationResultInspection()
        {
            var result = Generate("""
                using TopiaForge.Mods;
                [MultiplayerContract(Id = "sample.operation-result")]
                public partial class Contract
                {
                    [ReplicatedState("state")]
                    private ReplicatedState<State> state = new ReplicatedState<State>(new State());

                    [MultiplayerCommand("command", Prediction = PredictionMode.Owner)]
                    private OperationResult<Response> Handle(MultiplayerCommandContext context, Request request)
                    {
                        var updated = state.Update(current => OperationResult<State>.Success(
                            new State { Value = current.Value + request.Value }));
                        if (!updated.TryGetValue(out var accepted))
                        {
                            return OperationResult<Response>.Failure(updated.ErrorCode, updated.ErrorMessage);
                        }

                        return OperationResult<Response>.Success(new Response { Value = accepted.Value });
                    }
                }
                public sealed class State { public int Value { get; set; } }
                public sealed class Request { public int Value { get; set; } }
                public sealed class Response { public int Value { get; set; } }
                """);
            AssertNoErrors(result);
        }

        private static void AllowsDeterministicPredictedStateUpdateMethodGroup()
        {
            var result = Generate("""
                using TopiaForge.Mods;
                [MultiplayerContract(Id = "sample.safe-method-group")]
                public partial class Contract
                {
                    [ReplicatedState("state")]
                    private ReplicatedState<State> state = new ReplicatedState<State>(new State());

                    [MultiplayerCommand("command", Prediction = PredictionMode.Owner)]
                    private OperationResult<Response> Handle(MultiplayerCommandContext context, Request request)
                    {
                        var updated = state.Update(Increment);
                        if (!updated.TryGetValue(out var accepted))
                        {
                            return OperationResult<Response>.Failure(updated.ErrorCode, updated.ErrorMessage);
                        }

                        return OperationResult<Response>.Success(new Response { Value = accepted.Value });
                    }

                    private static OperationResult<State> Increment(State current) =>
                        OperationResult<State>.Success(new State { Value = current.Value + 1 });
                }
                public sealed class State { public int Value { get; set; } }
                public sealed class Request { public int Value { get; set; } }
                public sealed class Response { public int Value { get; set; } }
                """);
            AssertNoErrors(result);
        }

        private static void ReportsGeneratedNameCollision() => ExpectDiagnostic("TFMP012", """
            using TopiaForge.Mods;
            [MultiplayerContract]
            public partial class Contract
            {
                [ReplicatedObject("drone-a")]
                private OperationResult<State> First(ReplicatedObjectCommandContext context, State state, Input input) =>
                    OperationResult<State>.Success(state);

                [ReplicatedObject("drone_a")]
                private OperationResult<State> Second(ReplicatedObjectCommandContext context, State state, Input input) =>
                    OperationResult<State>.Success(state);
            }
            public sealed class State { public int Value { get; set; } }
            public sealed class Input { public int Value { get; set; } }
            """);

        private static void ReportsMissingExplicitContractId() => ExpectDiagnostic("TFMP013", """
            using TopiaForge.Mods;
            [MultiplayerContract]
            public partial class Contract { }
            """);

        private static void ReportsAuthorGeneratedNameCollision() => ExpectDiagnostic("TFMP012", """
            using TopiaForge.Mods;
            [MultiplayerContract(Id = "test.collision")]
            public partial class Contract
            {
                public void BindMultiplayer() { }
            }
            """);

        private static void ReportsCommandPayloadLimitOverflow() => ExpectDiagnostic("TFMP008", """
            using TopiaForge.Mods;
            [MultiplayerContract]
            public partial class Contract
            {
                [MultiplayerCommand("large", MaximumPayloadBytes = 64)]
                private OperationResult<Response> Handle(MultiplayerCommandContext context, Request request) =>
                    OperationResult<Response>.Success(new Response());
            }
            public sealed class Request
            {
                [NetworkBound(64)]
                public string Value { get; set; } = string.Empty;
            }
            public sealed class Response { public int Value { get; set; } }
            """);

        private static void ReportsStateAndEventTransportLimitOverflow()
        {
            const string payload = """
                public sealed class HugePayload
                {
                    [NetworkBound(65536)]
                    public long[] Values { get; set; } = System.Array.Empty<long>();
                    [NetworkBound(65536)]
                    public long[] MoreValues { get; set; } = System.Array.Empty<long>();
                    [NetworkBound(65536)]
                    public long[] FinalValues { get; set; } = System.Array.Empty<long>();
                }
                """;
            ExpectDiagnostic("TFMP011", ContractWithState("HugePayload") + payload);
            ExpectDiagnostic("TFMP011", """
                using TopiaForge.Mods;
                [MultiplayerContract]
                public partial class Contract
                {
                    [PresentationEvent("huge")]
                    private void OnHuge(HugePayload value) { }
                }
                """ + payload);
        }

        private static void ReplicatedStateCanReconnectAcrossSessions()
        {
            var declaredDefault = new Box { Value = 1 };
            var handle = new ReplicatedState<Box>(declaredDefault);
            var codec = new BoxCodec();
            AssertThrows<InvalidOperationException>(() => _ = handle.Value,
                "Reading a generated replicated state before binding should fail clearly.");

            var first = new ThrowingBindingSession(throwOnPresentation: false);
            var firstConnection = handle.Bind(first, "state", codec);
            Assert(firstConnection.TryGetValue(out var firstLease), "First connection should succeed.");
            declaredDefault.Value = 100;
            var connectedView = handle.Value;
            connectedView.Value = 200;
            Assert(handle.Value.Value == 1,
                "The provider default and connected Value should be detached from the constructor DTO and caller copies.");
            first.ReplaceRegisteredStateForTest(new Box { Value = 2 });
            var replacedView = handle.Value;
            replacedView.Value = 300;
            Assert(handle.Value.Value == 2, "Connected provider values should be detached at the author-facing handle.");
            firstLease!.Dispose();
            Assert(first.AllCreatedRegistrationsDisposed,
                "Disposing a generated connection lease should dispose its provider state.");
            var disconnectedView = handle.Value;
            disconnectedView.Value = 400;
            Assert(handle.Value.Value == 1,
                "Disconnect should expose only fresh copies of the frozen new-session default.");

            var second = new ThrowingBindingSession(throwOnPresentation: false);
            var secondConnection = handle.Bind(second, "state", codec);
            Assert(secondConnection.TryGetValue(out var secondLease), "A later session should be able to reconnect the same handle.");
            Assert(handle.Value.Value == 1,
                "A later provider registration should receive the frozen default, not a previously returned DTO.");
            second.ReplaceRegisteredStateForTest(new Box { Value = 3 });
            Assert(handle.Value.Value == 3, "Reconnected provider state should be visible through a detached copy.");
            secondLease!.Dispose();
            handle.Dispose();
        }

        private static void CommandDefinitionEnforcesPayloadLimit()
        {
            var threw = false;
            try
            {
                _ = new MultiplayerCommandDefinition<Box, Box>(
                    new MultiplayerCommandType<Box, Box>("oversized"),
                    new FixedCodec<Box>(65),
                    new FixedCodec<Box>(1),
                    (_, value) => OperationResult<Box>.Success(value),
                    maximumPayloadBytes: 64);
            }
            catch (ArgumentException)
            {
                threw = true;
            }

            Assert(threw, "Runtime command definitions must enforce the same payload ceiling as generated code.");
        }

        private static void StateAndPresentationDefinitionsEnforceTransportLimit()
        {
            var threwState = false;
            try
            {
                _ = new ReplicatedStateDefinition<Box>("state", new Box(), new FixedCodec<Box>((1024 * 1024) + 1));
            }
            catch (ArgumentException)
            {
                threwState = true;
            }

            var threwEvent = false;
            try
            {
                _ = new PresentationEventType<Box>("event", new FixedCodec<Box>((1024 * 1024) + 1));
            }
            catch (ArgumentException)
            {
                threwEvent = true;
            }

            Assert(threwState && threwEvent,
                "State and presentation codecs must enforce the hard 1 MiB transport ceiling at runtime.");
        }

        private static void ContractDescriptorDefensivelyCopiesAndValidates()
        {
            var stateIds = new List<string> { "sample/z", "sample/a" };
            var descriptor = new MultiplayerContractDescriptor(
                "sample",
                1,
                new string('a', 64),
                stateIds,
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>());
            stateIds.Clear();
            Assert(descriptor.StateIds.SequenceEqual(new[] { "sample/a", "sample/z" }),
                "Contract descriptors must own an immutable, ordinal inventory.");
            Assert(descriptor.WireFormatRevision == 1,
                "Contract descriptors must expose the generator-owned wire-format revision.");

            var threw = false;
            try
            {
                _ = new MultiplayerContractDescriptor(
                    "sample",
                    1,
                    new string('g', 64),
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>());
            }
            catch (ArgumentException)
            {
                threw = true;
            }

            Assert(threw, "A descriptor must reject non-hex schema digests.");
        }

        private static void LowLevelSpiIsHiddenFromOrdinaryIntelliSense()
        {
            var hiddenTypes = new[]
            {
                typeof(IGeneratedMultiplayerContract),
                typeof(IMultiplayerCodec<>),
                typeof(ReplicatedStateDefinition<>),
                typeof(IReplicatedState<>),
                typeof(MultiplayerCommandType<,>),
                typeof(MultiplayerCommandDefinition<,>),
                typeof(IMultiplayerCommandRegistration),
                typeof(ReplicatedObjectType<,>),
                typeof(ReplicatedObjectTypeDefinition<,>),
                typeof(IReplicatedObjectTypeRegistration),
                typeof(PresentationEventType<>),
                typeof(PresentationEventDefinition<>),
                typeof(IPresentationEventRegistration),
                typeof(MultiplayerContractDescriptor)
            };
            foreach (var type in hiddenTypes)
            {
                Assert(IsHiddenFromEditor(type), type.FullName + " must remain intentional generator/provider SPI.");
            }

            foreach (var methodName in new[]
                     {
                         "RegisterState", "RegisterCommand", "RegisterObjectType", "SubmitAsync", "SpawnObject",
                         "DespawnObject", "GetObjects", "TryGetObject", "SubscribeObjects", "RegisterPresentation",
                         "PublishPresentation"
                     })
            {
                var method = typeof(IMultiplayerSession).GetMethods().Single(item => item.Name == methodName);
                Assert(IsHiddenFromEditor(method), methodName + " must remain provider/generator SPI.");
            }

            Assert(!IsHiddenFromEditor(typeof(IMultiplayerSession)) &&
                   !IsHiddenFromEditor(typeof(ReplicatedState<>)) &&
                   !IsHiddenFromEditor(typeof(MultiplayerCommandContext)) &&
                   !IsHiddenFromEditor(typeof(IReplicatedObject<,>)),
                "Primary author concepts must remain discoverable.");

            var generated = SingleGeneratedSource(Generate(ValidContractSource)).SourceText.ToString();
            Assert(generated.Contains(
                       "[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]\n    public global::TopiaForge.Mods.MultiplayerContractDescriptor",
                       StringComparison.Ordinal) &&
                   generated.Contains(
                       "[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]\n    public global::TopiaForge.Mods.IMultiplayerCodec<T> GetCodec<T>()",
                       StringComparison.Ordinal),
                "Generated descriptor and codec escape hatches must remain hidden while typed author proxies stay visible.");
        }

        private static bool IsHiddenFromEditor(MemberInfo member) =>
            member.GetCustomAttribute<EditorBrowsableAttribute>()?.State == EditorBrowsableState.Never;

        private static void GeneratedBindingCleansUpFailedAndThrowingRegistrations()
        {
            var result = Generate(ValidContractSource);
            AssertNoErrors(result);
            using var assemblyStream = new MemoryStream();
            var emit = result.Compilation.Emit(assemblyStream);
            Assert(emit.Success, "Generated consumer failed to emit for binding cleanup test.");
            assemblyStream.Position = 0;
            var assembly = AssemblyLoadContext.Default.LoadFromStream(assemblyStream);
            var contractType = assembly.GetType("Sample.GameContract", true)!;
            var stateType = assembly.GetType("Sample.ScoreState", true)!;

            var throwingContract = Activator.CreateInstance(contractType)!;
            var throwingSession = new ThrowingBindingSession(throwOnPresentation: true);
            try
            {
                _ = contractType.GetMethod("BindMultiplayer")!.Invoke(
                    throwingContract,
                    new object[] { throwingSession });
                throw new InvalidOperationException("A provider registration exception should escape generated binding.");
            }
            catch (TargetInvocationException exception) when (exception.InnerException is InvalidOperationException)
            {
            }

            Assert(throwingSession.AllCreatedRegistrationsDisposed,
                "Generated binding must dispose state, command, and object-type registrations when a later provider call throws.");

            var connectFailureContract = Activator.CreateInstance(contractType)!;
            var handle = contractType.GetField("score", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(connectFailureContract)!;
            var generatedCodec = contractType.GetMethod("GetCodec")!.MakeGenericMethod(stateType)
                .Invoke(connectFailureContract, null)!;
            var preconnectedSession = new ThrowingBindingSession(throwOnPresentation: false);
            var connectionResult = handle.GetType().GetMethod("Bind")!.Invoke(
                handle,
                new object[] { preconnectedSession, "sample.contract/score", generatedCodec })!;
            var connection = (IDisposable)InvokeSuccessfulResult(connectionResult);
            var connectFailureSession = new ThrowingBindingSession(throwOnPresentation: false);
            var failed = contractType.GetMethod("BindMultiplayer")!.Invoke(
                connectFailureContract,
                new object[] { connectFailureSession })!;
            Assert(!(bool)failed.GetType().GetProperty("Succeeded")!.GetValue(failed)!,
                "An already-connected author state should return a generated binding failure.");
            Assert(connectFailureSession.CreatedRegistrationCount == 0,
                "An already-connected state must fail before creating another provider registration.");
            connection.Dispose();

            var sequentialContract = Activator.CreateInstance(contractType)!;
            var sequentialSession = new ThrowingBindingSession(throwOnPresentation: false);
            var bindingResult = contractType.GetMethod("BindMultiplayer")!.Invoke(
                sequentialContract,
                new object[] { sequentialSession })!;
            var binding = (IDisposable)InvokeSuccessfulResult(bindingResult);
            var submit = contractType.GetMethod("SubmitIncrementAsync")!;
            var requestType = assembly.GetType("Sample.IncrementRequest", true)!;
            var request = Activator.CreateInstance(requestType)!;
            var sequentialHandle = contractType.GetField("score", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(sequentialContract)!;
            var sessionAState = Activator.CreateInstance(stateType)!;
            stateType.GetProperty("Score")!.SetValue(sessionAState, 73);
            sequentialSession.ReplaceRegisteredStateForTest(sessionAState);
            Assert((int)stateType.GetProperty("Score")!.GetValue(
                       sequentialHandle.GetType().GetProperty("Value")!.GetValue(sequentialHandle)!)! == 73,
                "The generated state handle must expose session A's provider value before replacement.");
            ((System.Threading.Tasks.Task)submit.Invoke(
                sequentialContract,
                new[] { request, default(System.Threading.CancellationToken) })!).GetAwaiter().GetResult();
            var firstToken = sequentialSession.CurrentSessionToken;
            var registrationCount = sequentialSession.CreatedRegistrationCount;
            sequentialSession.ReplaceSession("binding-test-next");
            Assert(firstToken.IsCancellationRequested &&
                   !sequentialSession.CurrentSessionToken.IsCancellationRequested &&
                   sequentialSession.Snapshot.Id.Equals(new MultiplayerSessionId("binding-test-next")),
                "A stable session facade cancels the old token, supplies a live replacement token, and exposes the replacement id.");
            Assert((int)stateType.GetProperty("Score")!.GetValue(
                       sequentialHandle.GetType().GetProperty("Value")!.GetValue(sequentialHandle)!)! == 0,
                "A persistent generated state registration must reset to its declared default for session B.");
            ((System.Threading.Tasks.Task)submit.Invoke(
                sequentialContract,
                new[] { request, default(System.Threading.CancellationToken) })!).GetAwaiter().GetResult();
            Assert(sequentialSession.SubmissionCount == 2 &&
                   sequentialSession.CreatedRegistrationCount == registrationCount,
                "The same generated binding and mod instance submit in two sequential sessions without duplicate registration.");
            binding.Dispose();
        }

        private static GenerationResult Generate(string source)
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(
                source,
                new CSharpParseOptions(LanguageVersion.Latest),
                encoding: Encoding.UTF8);
            var compilation = CSharpCompilation.Create(
                "GeneratorConsumer_" + Guid.NewGuid().ToString("N"),
                new[] { syntaxTree },
                References,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            GeneratorDriver driver = CSharpGeneratorDriver.Create(new MultiplayerContractGenerator().AsSourceGenerator());
            driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);
            var runResult = driver.GetRunResult();
            return new GenerationResult(
                (CSharpCompilation)updated,
                runResult.Results.SelectMany(item => item.GeneratedSources).ToImmutableArray(),
                runResult.Diagnostics);
        }

        private static void ExpectDiagnostic(string id, string source)
        {
            var result = Generate(source);
            Assert(result.GeneratorDiagnostics.Any(item => item.Id == id),
                "Expected " + id + " but received: " + string.Join(", ", result.GeneratorDiagnostics.Select(item => item.Id + ": " + item.GetMessage())));
        }

        private static string ContractWithState(string payloadType) => $$"""
            using TopiaForge.Mods;
            [MultiplayerContract]
            public partial class Contract
            {
                [ReplicatedState("state")]
                private ReplicatedState<{{payloadType}}> state = new ReplicatedState<{{payloadType}}>(new {{payloadType}}());
            }
            """;

        private static string PredictedCommand(string body) => $$"""
            using TopiaForge.Mods;
            [MultiplayerContract]
            public partial class Contract
            {
                [MultiplayerCommand("command", Prediction = PredictionMode.Owner)]
                private OperationResult<Response> Handle(MultiplayerCommandContext context, Request request)
                {
                    {{body}}
                }
            }
            public sealed class Request { public int Value { get; set; } }
            public sealed class Response { public int Value { get; set; } }
            """;

        private static GeneratedSourceResult SingleGeneratedSource(GenerationResult result)
        {
            Assert(result.GeneratedSources.Length == 1,
                "Expected one generated source, received " + result.GeneratedSources.Length + ". Diagnostics: " +
                string.Join(", ", result.GeneratorDiagnostics.Select(item => item.ToString())));
            return result.GeneratedSources[0];
        }

        private static string GoldenSurface(string hintName, string source)
        {
            var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal);
            var relevant = normalized.Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Contains("MultiplayerContractDescriptor", StringComparison.Ordinal) ||
                               line.StartsWith("new string[]", StringComparison.Ordinal) ||
                               line.Contains(".Bind(session,", StringComparison.Ordinal) ||
                               line.Contains("RegisterState", StringComparison.Ordinal) ||
                               line.Contains("RegisterCommand", StringComparison.Ordinal) ||
                               line.Contains("RegisterObjectType", StringComparison.Ordinal) ||
                               line.Contains("RegisterPresentation", StringComparison.Ordinal) ||
                               line.Contains("SpawnRobotObject", StringComparison.Ordinal) ||
                               line.Contains("SubscribeRobotObjects", StringComparison.Ordinal) ||
                               line.Contains("SubmitIncrementAsync", StringComparison.Ordinal) ||
                               line.Contains("EmitOnSpark", StringComparison.Ordinal) ||
                               line.Contains("PublishOnSpark", StringComparison.Ordinal) ||
                               line.Contains("GetCodec<T>", StringComparison.Ordinal) ||
                               line.Contains("MaximumEncodedBytes =>", StringComparison.Ordinal))
                .ToArray();
            return "hint=" + hintName + "\n" +
                   "sourceSha256=" + Sha256(normalized) + "\n" +
                   string.Join("\n", relevant);
        }

        private static string DescriptorHash(string source)
        {
            const string marker = "new global::TopiaForge.Mods.MultiplayerContractDescriptor(\"";
            var start = source.IndexOf(marker, StringComparison.Ordinal);
            Assert(start >= 0, "Generated descriptor was missing.");
            var hashStart = source.IndexOf(", \"", start, StringComparison.Ordinal) + 3;
            return source.Substring(hashStart, 64);
        }

        private static object InvokeSuccessfulResult(object result)
        {
            var type = result.GetType();
            Assert((bool)type.GetProperty("Succeeded")!.GetValue(result)!,
                "Expected codec success: " + type.GetProperty("ErrorMessage")!.GetValue(result));
            return type.GetProperty("Value")!.GetValue(result)!;
        }

        private static void AssertNoErrors(GenerationResult result)
        {
            var errors = result.GeneratorDiagnostics.Concat(result.Compilation.GetDiagnostics())
                .Where(item => item.Severity.ToString() == "Error")
                .ToArray();
            Assert(errors.Length == 0, "Generation or consumer compilation failed:\n" + JoinErrors(errors));
        }

        private static string JoinErrors(IEnumerable<Diagnostic> diagnostics) =>
            string.Join(Environment.NewLine, diagnostics.Where(item => item.Severity.ToString() == "Error"));

        private static MetadataReference[] BuildReferences()
        {
            var paths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
                .Split(Path.PathSeparator)
                .Concat(new[]
                {
                    typeof(MultiplayerContractAttribute).Assembly.Location,
                    typeof(OperationResult<>).Assembly.Location
                })
                .Distinct(StringComparer.OrdinalIgnoreCase);
            return paths.Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        }

        private static string Sha256(string value) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void AssertThrows<TException>(Action action, string message) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException(message);
        }

        private sealed class Box
        {
            public int Value { get; set; }
        }

        private sealed class BoxCodec : IMultiplayerCodec<Box>
        {
            public int MaximumEncodedBytes => sizeof(int);

            public OperationResult<byte[]> Encode(Box value)
            {
                if (value == null) throw new ArgumentNullException(nameof(value));
                return OperationResult<byte[]>.Success(BitConverter.GetBytes(value.Value));
            }

            public OperationResult<Box> Decode(byte[] bytes)
            {
                if (bytes == null || bytes.Length != sizeof(int))
                {
                    return OperationResult<Box>.Failure(ModErrorCode.InvalidArgument, "Invalid box payload.");
                }

                return OperationResult<Box>.Success(new Box { Value = BitConverter.ToInt32(bytes, 0) });
            }
        }

        private interface IResettableTrackingState
        {
            void ReplaceForTest(object value);
            void ResetForNextSession();
        }

        private sealed class TrackingState<T> : IReplicatedState<T>, IResettableTrackingState where T : class
        {
            private readonly T initialValue;
            private T value;

            internal TrackingState(string id, T value)
            {
                Id = id;
                initialValue = value;
                this.value = value;
            }

            public string Id { get; }
            public T Value => value;
            public ulong Version { get; private set; }
            internal bool Disposed { get; private set; }

            public OperationResult<T> Update(Func<T, OperationResult<T>> updater)
            {
                var result = updater(value);
                if (result.TryGetValue(out var updated))
                {
                    value = updated;
                    Version++;
                }

                return result;
            }

            public IDisposable SubscribeChanged(Action<T> handler) => new EmptyDisposable();
            public void Dispose() => Disposed = true;

            public void ReplaceForTest(object replacement)
            {
                value = replacement as T ?? throw new ArgumentException("Replacement state type did not match.", nameof(replacement));
                Version++;
            }

            public void ResetForNextSession()
            {
                value = initialValue;
                Version = 0;
            }
        }

        private sealed class ThrowingBindingSession : IMultiplayerSession
        {
            private readonly bool throwOnPresentation;
            private readonly List<Func<bool>> disposedChecks = new List<Func<bool>>();
            private readonly List<IResettableTrackingState> states = new List<IResettableTrackingState>();
            private System.Threading.CancellationTokenSource currentSession =
                new System.Threading.CancellationTokenSource();

            internal ThrowingBindingSession(bool throwOnPresentation)
            {
                this.throwOnPresentation = throwOnPresentation;
                Snapshot = new MultiplayerSessionSnapshot(
                    new MultiplayerSessionId("binding-test"),
                    MultiplayerSessionState.Ready,
                    MultiplayerProcessKind.Interactive,
                    MultiplayerExecutionSide.Client | MultiplayerExecutionSide.Server,
                    new ParticipantId("host"),
                    new[]
                    {
                        new MultiplayerParticipant(new ParticipantId("host"), "Host", isLocal: true, isConnected: true)
                    },
                    new NetworkTick(0),
                    new SessionSeed(1));
            }

            public MultiplayerSessionSnapshot Snapshot { get; private set; }
            public System.Threading.CancellationToken CurrentSessionToken => currentSession.Token;
            internal bool AllCreatedRegistrationsDisposed => disposedChecks.Count > 0 && disposedChecks.All(check => check());
            internal int CreatedRegistrationCount => disposedChecks.Count;
            internal int SubmissionCount { get; private set; }

            internal void ReplaceRegisteredStateForTest(object value)
            {
                if (states.Count != 1) throw new InvalidOperationException("The test expected exactly one generated state registration.");
                states[0].ReplaceForTest(value);
            }

            internal void ReplaceSession(string id)
            {
                currentSession.Cancel();
                currentSession.Dispose();
                currentSession = new System.Threading.CancellationTokenSource();
                foreach (var state in states) state.ResetForNextSession();
                Snapshot = new MultiplayerSessionSnapshot(
                    new MultiplayerSessionId(id),
                    MultiplayerSessionState.Ready,
                    Snapshot.ProcessKind,
                    Snapshot.ExecutionSides,
                    Snapshot.LocalParticipantId,
                    Snapshot.Participants,
                    new NetworkTick(0),
                    Snapshot.Seed);
            }

            public IDisposable SubscribeChanged(Action<MultiplayerSessionSnapshot> handler) => new EmptyDisposable();

            public OperationResult<IReplicatedState<T>> RegisterState<T>(ReplicatedStateDefinition<T> definition)
                where T : class
            {
                var state = new TrackingState<T>(definition.Id, definition.InitialValue);
                states.Add(state);
                disposedChecks.Add(() => state.Disposed);
                return OperationResult<IReplicatedState<T>>.Success(state);
            }

            public OperationResult<IMultiplayerCommandRegistration> RegisterCommand<TRequest, TResponse>(
                MultiplayerCommandDefinition<TRequest, TResponse> definition)
                where TRequest : class
                where TResponse : class
            {
                var registration = new TrackingCommandRegistration(definition.Id);
                disposedChecks.Add(() => !registration.IsActive);
                return OperationResult<IMultiplayerCommandRegistration>.Success(registration);
            }

            public OperationResult<IReplicatedObjectTypeRegistration> RegisterObjectType<TState, TInput>(
                ReplicatedObjectTypeDefinition<TState, TInput> definition)
                where TState : class
                where TInput : class
            {
                var registration = new TrackingObjectRegistration(definition.TypeId);
                disposedChecks.Add(() => !registration.IsActive);
                return OperationResult<IReplicatedObjectTypeRegistration>.Success(registration);
            }

            public OperationResult<IPresentationEventRegistration> RegisterPresentation<TEvent>(
                PresentationEventDefinition<TEvent> definition) where TEvent : class
            {
                if (throwOnPresentation) throw new InvalidOperationException("Deliberate provider registration failure.");
                var registration = new TrackingPresentationRegistration(definition.Type.Id);
                disposedChecks.Add(() => !registration.IsActive);
                return OperationResult<IPresentationEventRegistration>.Success(registration);
            }

            public System.Threading.Tasks.Task<MultiplayerCommandConfirmation<TResponse>> SubmitAsync<TRequest, TResponse>(
                MultiplayerCommandType<TRequest, TResponse> commandType,
                TRequest request,
                System.Threading.CancellationToken cancellationToken = default)
                where TRequest : class
                where TResponse : class
            {
                SubmissionCount++;
                var response = (TResponse?)Activator.CreateInstance(typeof(TResponse));
                if (response == null) throw new InvalidOperationException("Test response needs a parameterless constructor.");
                var tick = Snapshot.Tick;
                return System.Threading.Tasks.Task.FromResult(
                    new MultiplayerCommandConfirmation<TResponse>(
                        tick,
                        tick,
                        false,
                        OperationResult<TResponse>.Success(response)));
            }

            public OperationResult<IReplicatedObject<TState, TInput>> SpawnObject<TState, TInput>(
                ReplicatedObjectType<TState, TInput> type,
                TState initialState,
                ParticipantId? ownerId = null)
                where TState : class
                where TInput : class => OperationResult<IReplicatedObject<TState, TInput>>.Failure(ModErrorCode.NotFound, "Not used.");

            public OperationResult<bool> DespawnObject(NetworkObjectId id) =>
                OperationResult<bool>.Failure(ModErrorCode.NotFound, "Not used.");

            public IReadOnlyList<IReplicatedObject<TState, TInput>> GetObjects<TState, TInput>(
                ReplicatedObjectType<TState, TInput> type)
                where TState : class
                where TInput : class => Array.Empty<IReplicatedObject<TState, TInput>>();

            public bool TryGetObject<TState, TInput>(
                ReplicatedObjectType<TState, TInput> type,
                NetworkObjectId id,
                out IReplicatedObject<TState, TInput>? replicatedObject)
                where TState : class
                where TInput : class
            {
                replicatedObject = null;
                return false;
            }

            public IDisposable SubscribeObjects<TState, TInput>(
                ReplicatedObjectType<TState, TInput> type,
                Action<ReplicatedObjectChange<TState, TInput>> handler)
                where TState : class
                where TInput : class => new EmptyDisposable();

            public bool TryGetNetworkObjectId(IEntity entity, out NetworkObjectId id)
            {
                id = default;
                return false;
            }

            public OperationResult<bool> PublishPresentation<TEvent>(
                PresentationEventType<TEvent> eventType,
                TEvent value,
                MultiplayerAudience audience = MultiplayerAudience.Everyone)
                where TEvent : class => OperationResult<bool>.Failure(ModErrorCode.NotAuthoritative, "Not used.");
        }

        private sealed class TrackingCommandRegistration : IMultiplayerCommandRegistration
        {
            internal TrackingCommandRegistration(string id) { Id = id; IsActive = true; }
            public string Id { get; }
            public bool IsActive { get; private set; }
            public void Dispose() => IsActive = false;
        }

        private sealed class TrackingObjectRegistration : IReplicatedObjectTypeRegistration
        {
            internal TrackingObjectRegistration(string typeId) { TypeId = typeId; IsActive = true; }
            public string TypeId { get; }
            public bool IsActive { get; private set; }
            public void Dispose() => IsActive = false;
        }

        private sealed class TrackingPresentationRegistration : IPresentationEventRegistration
        {
            internal TrackingPresentationRegistration(string id) { Id = id; IsActive = true; }
            public string Id { get; }
            public bool IsActive { get; private set; }
            public void Dispose() => IsActive = false;
        }

        private sealed class FixedCodec<T> : IMultiplayerCodec<T> where T : class
        {
            internal FixedCodec(int maximumEncodedBytes) => MaximumEncodedBytes = maximumEncodedBytes;
            public int MaximumEncodedBytes { get; }
            public OperationResult<byte[]> Encode(T value) => OperationResult<byte[]>.Success(Array.Empty<byte>());
            public OperationResult<T> Decode(byte[] bytes) => OperationResult<T>.Failure(ModErrorCode.InvalidArgument, "Not used.");
        }

        private sealed class EmptyDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }

        private sealed class GenerationResult
        {
            internal GenerationResult(
                CSharpCompilation compilation,
                ImmutableArray<GeneratedSourceResult> generatedSources,
                ImmutableArray<Diagnostic> generatorDiagnostics)
            {
                Compilation = compilation;
                GeneratedSources = generatedSources;
                GeneratorDiagnostics = generatorDiagnostics;
            }

            internal CSharpCompilation Compilation { get; }
            internal ImmutableArray<GeneratedSourceResult> GeneratedSources { get; }
            internal ImmutableArray<Diagnostic> GeneratorDiagnostics { get; }
        }
    }
}
