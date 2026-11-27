extern alias provider;

using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using Provider = provider::TopiaForge.Multiplayer.MultiplayerProvider;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static class PerPlayerProviderTests
{
    internal static void SoloProviderAndScopedFacadeUsePrivateState()
    {
        using var solo = new Provider("private.provider", clockMicros: () => 0);
        var local = solo.Snapshot.LocalParticipantId!.Value;
        var contract = new PrivateContract();
        using var binding = contract.BindMultiplayer(solo).Value!;
        That(binding != null && contract.Cards.Set(local, new PrivateCard { Text = "provider local" }).Succeeded,
            "the production solo provider binds generated private contracts");
        using var lifetime = new FakeModLifetime();
        var session = (IMultiplayerSession)solo.CreateOwnerFacade(typeof(IMultiplayerSession), "private.consumer", lifetime);
        var scoped = new PrivateContract();
        using var scopedBinding = scoped.BindMultiplayer(session).Value!;
        That(scopedBinding != null && scoped.Cards.Get(local).Value!.Text == "", "same field id in another mod is isolated");
        scoped.Cards.Set(local, new PrivateCard { Text = "scoped local" });
        That(contract.Cards.Get(local).Value!.Text == "provider local", "consumer writes cannot overwrite another mod's private field");
        lifetime.Dispose();
        That(!scoped.Cards.Get(local).Succeeded, "the real provider retires private fields with the mod scope");
    }
}
