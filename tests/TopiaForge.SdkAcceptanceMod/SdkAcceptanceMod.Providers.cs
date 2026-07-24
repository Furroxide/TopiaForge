using System;
using TopiaForge.Mods;

namespace TopiaForge.SdkAcceptance
{
    public sealed partial class SdkAcceptanceMod
    {
        private bool RunProviderContractChecks()
        {
            try
            {
                var timeProviders = Context.Extensions.GetAll<ITimeControlService>();
                var creatorContentProviders = Context.Extensions.GetAll<ICreatorContentService>();
                var promptProviders = Context.Extensions.GetAll<IPromptOverrideRegistry>();
                var robotAgentProviders = Context.Extensions.GetAll<IRobotAgentService>();
                var robotObjectiveProviders = Context.Extensions.GetAll<IRobotObjectiveService>();
                var robotBrainProviders = Context.Extensions.GetAll<IRobotBrainQueryService>();
                var robotConversationProviders = Context.Extensions.GetAll<IRobotConversationService>();
                var dialogueProviders = Context.Extensions.GetAll<IPlayerDialogueInputService>();
                var worldProviders = Context.Extensions.GetAll<IWorldGamemodeService>();
                var ugcProviders = Context.Extensions.GetAll<IUgcLiveSyncService>();
                if (timeProviders.Count != 1
                    || creatorContentProviders.Count != 1
                    || promptProviders.Count != 1
                    || robotAgentProviders.Count != 1
                    || robotObjectiveProviders.Count != 1
                    || robotBrainProviders.Count != 1
                    || robotConversationProviders.Count != 1
                    || dialogueProviders.Count != 1
                    || worldProviders.Count != 1
                    || ugcProviders.Count != 1)
                {
                    Fail(
                        "integration.provider-scope",
                        "declared required providers and the installed optional UGC provider must each resolve exactly once");
                    return false;
                }

                timeControl = timeProviders[0];
                creatorContent = creatorContentProviders[0];
                promptOverrides = promptProviders[0];
                robotAgents = robotAgentProviders[0];
                robotObjectives = robotObjectiveProviders[0];
                robotBrain = robotBrainProviders[0];
                robotConversations = robotConversationProviders[0];
                dialogueInput = dialogueProviders[0];
                worlds = worldProviders[0];
                ugcLiveSync = ugcProviders[0];

                var versions = Context.Runtime.ProviderVersions;
                if (!versions.ContainsKey("io.github.furroxide.topiaforge.chronos")
                    || !versions.ContainsKey("io.github.furroxide.topiaforge.creatorcontent")
                    || !versions.ContainsKey("io.github.furroxide.topiaforge.prompts")
                    || !versions.ContainsKey("io.github.furroxide.topiaforge.robotkit")
                    || !versions.ContainsKey("io.github.furroxide.topiaforge.ugc.livesync")
                    || !versions.ContainsKey(WorldsModule.Id))
                {
                    Fail("integration.provider-scope", "a resolved module is missing provider version metadata");
                    return false;
                }

                if (versions.ContainsKey(MissingOptionalProviderId)
                    || Context.Extensions.GetAll<IMissingOptionalProvider>().Count != 0
                    || Context.TryGetExtension<IMissingOptionalProvider>(out _))
                {
                    Fail(
                        "integration.provider-scope",
                        "an absent optional dependency unexpectedly published a visible provider");
                    return false;
                }

                if (!Context.TryGetExtension<ITimeControlService>(out var selectedTime)
                    || !ReferenceEquals(selectedTime, timeControl)
                    || !Context.TryGetExtension<IUgcLiveSyncService>(out var selectedUgc)
                    || !ReferenceEquals(selectedUgc, ugcLiveSync))
                {
                    Fail("integration.provider-scope", "deterministic first-provider selection did not match GetAll");
                    return false;
                }

                IExtensionRegistration? singletonRegistration = null;
                IExtensionRegistration? firstMultipleRegistration = null;
                IExtensionRegistration? secondMultipleRegistration = null;
                try
                {
                    var singletonProvider = new AcceptanceProbeProvider("singleton");
                    var singleton = Context.Extensions.Register<IAcceptanceProbeProvider>(singletonProvider);
                    if (!singleton.TryGetValue(out singletonRegistration)
                        || !singletonRegistration.IsActive)
                    {
                        Fail("integration.provider-scope", "the first singleton provider registration failed");
                        return false;
                    }

                    var duplicate = Context.Extensions.Register<IAcceptanceProbeProvider>(
                        new AcceptanceProbeProvider("duplicate"));
                    var singletonProviders = Context.Extensions.GetAll<IAcceptanceProbeProvider>();
                    if (duplicate.Succeeded
                        || duplicate.ErrorCode != ModErrorCode.Conflict
                        || singletonProviders.Count != 1
                        || !ReferenceEquals(singletonProviders[0], singletonProvider)
                        || !Context.TryGetExtension<IAcceptanceProbeProvider>(out var selectedSingleton)
                        || !ReferenceEquals(selectedSingleton, singletonProvider))
                    {
                        Fail("integration.provider-scope", "singleton conflict or selection behavior was incorrect");
                        return false;
                    }

                    singletonRegistration.Dispose();
                    singletonRegistration.Dispose();
                    if (singletonRegistration.IsActive
                        || Context.Extensions.GetAll<IAcceptanceProbeProvider>().Count != 0)
                    {
                        Fail("integration.provider-scope", "early singleton release did not remove the provider");
                        return false;
                    }

                    var firstProvider = new AcceptanceProbeProvider("multiple-first");
                    var secondProvider = new AcceptanceProbeProvider("multiple-second");
                    var firstMultiple = Context.Extensions.Register<IAcceptanceProbeProvider>(
                        firstProvider,
                        ExtensionCardinality.Multiple);
                    var secondMultiple = Context.Extensions.Register<IAcceptanceProbeProvider>(
                        secondProvider,
                        ExtensionCardinality.Multiple);
                    if (!firstMultiple.TryGetValue(out firstMultipleRegistration)
                        || !secondMultiple.TryGetValue(out secondMultipleRegistration)
                        || !firstMultipleRegistration.IsActive
                        || !secondMultipleRegistration.IsActive)
                    {
                        Fail("integration.provider-scope", "multiple-provider registration failed");
                        return false;
                    }

                    var providers = Context.Extensions.GetAll<IAcceptanceProbeProvider>();
                    if (providers.Count != 2
                        || !ReferenceEquals(providers[0], firstProvider)
                        || !ReferenceEquals(providers[1], secondProvider)
                        || !Context.TryGetExtension<IAcceptanceProbeProvider>(out var selectedMultiple)
                        || !ReferenceEquals(selectedMultiple, firstProvider))
                    {
                        Fail("integration.provider-scope", "multiple providers were not returned in deterministic order");
                        return false;
                    }

                    firstMultipleRegistration.Dispose();
                    firstMultipleRegistration.Dispose();
                    providers = Context.Extensions.GetAll<IAcceptanceProbeProvider>();
                    if (firstMultipleRegistration.IsActive
                        || providers.Count != 1
                        || !ReferenceEquals(providers[0], secondProvider)
                        || !Context.TryGetExtension<IAcceptanceProbeProvider>(out selectedMultiple)
                        || !ReferenceEquals(selectedMultiple, secondProvider))
                    {
                        Fail("integration.provider-scope", "early release did not deterministically advance selection");
                        return false;
                    }

                    secondMultipleRegistration.Dispose();
                    secondMultipleRegistration.Dispose();
                    if (secondMultipleRegistration.IsActive
                        || Context.Extensions.GetAll<IAcceptanceProbeProvider>().Count != 0)
                    {
                        Fail("integration.provider-scope", "multiple-provider cleanup leaked a registration");
                        return false;
                    }
                }
                finally
                {
                    secondMultipleRegistration?.Dispose();
                    firstMultipleRegistration?.Dispose();
                    singletonRegistration?.Dispose();
                }

                Pass(
                    "integration.provider-scope",
                    "required-singletons=9;optional-present=ugc;optional-absent="
                    + MissingOptionalProviderId
                    + ";singleton-conflict=Conflict;multiple-order=first,second;early-release=clean");
                return true;
            }
            catch (Exception exception)
            {
                Fail("integration.provider-scope", exception.Message);
                return false;
            }
        }

        private interface IAcceptanceProbeProvider
        {
            string Name { get; }
        }

        private interface IMissingOptionalProvider
        {
        }

        private sealed class AcceptanceProbeProvider : IAcceptanceProbeProvider
        {
            public AcceptanceProbeProvider(string name)
            {
                Name = name;
            }

            public string Name { get; }
        }
    }
}
