using System;
using System.Collections.Generic;
using System.Linq;

namespace TopiaForge.ModManager.Core
{
    public sealed class LaunchTargetChoice
    {
        internal LaunchTargetChoice(LaunchRequest request, LaunchPlan plan) { Request = request; WorldId = plan.WorldId; Transition = plan.Transition; }
        public LaunchRequest Request { get; }
        public string WorldId { get; }
        public string Transition { get; }
        public bool IsDeclaredDefault => Request.WorldOverride == null && Request.TransitionOverride == null;
    }
    public sealed class LaunchTargetPreview
    {
        internal LaunchTargetPreview(ModLaunchTargetDeclaration target, LaunchResolution declared, IEnumerable<LaunchTargetChoice> choices)
        { Id = target.Id; Title = target.Title; Description = target.Description; Declared = declared; Choices = Array.AsReadOnly(choices.ToArray()); }
        public string Id { get; }
        public string Title { get; }
        public string? Description { get; }
        public LaunchResolution Declared { get; }
        public IReadOnlyList<LaunchTargetChoice> Choices { get; }
    }
    public static class LaunchTargetPreviewBuilder
    {
        public static IReadOnlyList<LaunchTargetPreview> Build(EffectiveProfile profile, RuntimeObservation? observation = null, RuntimeBindingSnapshot? bindings = null)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            var observed = (observation ?? RuntimeObservation.None).ForProfile(profile);
            var index = new LaunchProfileIndex(profile);
            var worlds = profile.Packages.SelectMany(package => package.Snapshot.Contributions?.Worlds ?? new List<ModWorldDeclaration>())
                .Where(world => world.Content?.Kind != ModWorldContent.DiscoveredKind).Select(world => world.Id)
                .Concat(observed.DiscoveredWorlds.Select(world => world.Id)).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.Ordinal).ToArray();
            var previews = new List<LaunchTargetPreview>();
            foreach (var target in profile.Packages.SelectMany(package =>
                (package.Snapshot.Contributions?.LaunchTargets ?? new List<ModLaunchTargetDeclaration>()).Where(target => index.Owns(package, target.Id)))
                .OrderBy(target => target.Id, StringComparer.Ordinal))
            {
                var declared = LaunchResolver.Resolve(profile, new LaunchRequest(target.Id), observed, bindings);
                var choices = new List<LaunchTargetChoice>();
                var worldOverrides = new List<string?> { null };
                if (target.World?.AllowPlayerOverride == true && target.World.Policy != ModWorldPolicy.FixedPolicy)
                    worldOverrides.AddRange(worlds.Where(id => !string.Equals(id, target.World.Default, StringComparison.OrdinalIgnoreCase)));
                var transitions = new List<string?> { null };
                if (target.Transition == ModLaunchTargetDeclaration.PlayerChoiceTransition)
                    transitions.AddRange(new[] { ModTransitions.SceneReplacement, ModTransitions.AdditiveArena });
                foreach (var world in worldOverrides)
                    foreach (var transition in transitions)
                    {
                        var request = new LaunchRequest(target.Id, world, transition);
                        var resolved = LaunchResolver.Resolve(profile, request, observed, bindings);
                        if (resolved.Plan != null) choices.Add(new LaunchTargetChoice(request, resolved.Plan));
                    }
                previews.Add(new LaunchTargetPreview(target, declared, choices));
            }
            return previews.AsReadOnly();
        }
    }
}
