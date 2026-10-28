using System;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.Mods.Multiplayer.Generators.Tests
{
    internal static partial class Program
    {
        private const string PrivateSource = """
            using TopiaForge.Mods;
            [MultiplayerContract(Id = "sample.private")]
            public partial class Contract
            {
                [ReplicatedState("card")]
                private PerPlayerState<Card> card = new PerPlayerState<Card>(new Card());
            }
            public sealed class Card
            {
                [NetworkBound(32)]
                public string Text { get; set; } = "";
            }
            """;

        private static void RunPerPlayerGeneratorTests()
        {
            var generated = Generate(PrivateSource);
            AssertNoErrors(generated);
            var type = LoadGenerated(PrivateSource, "Contract");
            var contract = (IGeneratedMultiplayerBinding)Activator.CreateInstance(type)!;
            Assert(contract.MultiplayerContractDescriptor.StateIds.Single() == "sample.private/card", "Private ids use existing contract namespacing.");
            var oldProvider = new ThrowingBindingSession(throwOnPresentation: false);
            var bound = contract.BindMultiplayer(oldProvider);
            Assert(bound.ErrorCode == ModErrorCode.Unavailable && oldProvider.AllCreatedRegistrationsDisposed,
                "An older provider fails binding without falling back to shared state, and releases the contract registration.");
            var privateHash = DescriptorHash(SingleGeneratedSource(generated).SourceText.ToString());
            var publicHash = DescriptorHash(SingleGeneratedSource(Generate(PrivateSource.Replace("PerPlayerState<", "ReplicatedState<", StringComparison.Ordinal))).SourceText.ToString());
            Assert(privateHash != publicHash, "Changing confidentiality changes the generated contract digest.");
            var huge = Generate(PrivateSource.Replace("NetworkBound(32)", "NetworkBound(16384)", StringComparison.Ordinal));
            Assert(huge.GeneratorDiagnostics.Any(x => x.Id == "TFMP003" && x.GetMessage().Contains("16384", StringComparison.Ordinal)), "Private payload bounds are checked at build time.");
            var immediate = Generate(PrivateSource.Replace("[ReplicatedState(\"card\")]", "[ReplicatedState(\"card\", Delivery = MultiplayerDelivery.Immediate)]", StringComparison.Ordinal));
            Assert(immediate.GeneratorDiagnostics.Any(x => x.Id == "TFMP003"), "Unsupported private delivery settings fail generation.");
            GeneratedContractMatchesGoldenSurface();
            OnlyNonDefaultDeliverySettingsChangeTheDigest();
            Console.WriteLine("Per-player generator: 7 focused regression checks passed.");
        }
    }
}
