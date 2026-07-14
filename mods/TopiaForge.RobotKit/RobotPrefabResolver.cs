using System;
using System.Collections.Generic;
using System.Reflection;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.RobotKit
{
    // One spawnable robot type discovered in the loaded game.
    internal sealed class RobotPrefabCandidate
    {
        public RobotPrefabCandidate(string id, string displayName, GameObject prefab, int score)
        {
            Id = id;
            DisplayName = displayName;
            Prefab = prefab;
            Score = score;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public GameObject Prefab { get; }
        public int Score { get; }
    }

    // Resolves spawnable robot prefab assets (not live scene instances) from the loaded game, scoring candidates
    // by the robot components they carry. Previously lived inside the Zombies mod; promoted here so every
    // robot-spawning mod shares one authoritative resolver. ResolveAll enumerates every distinct robot type
    // (walkable robots only); Resolve keeps its original meaning — the single best candidate.
    internal sealed class RobotPrefabResolver
    {
        // Types must actually be robots that can walk: body + locomotion (100 + 25).
        private const int TypeEligibilityScore = 125;

        private static readonly string[] RobotComponentNames =
        {
            "RobotBody",
            "LLMAgent",
            "AgentHead",
            "LocomotionController",
            "SegmentedRobotBodyController",
            "SegmentedGenericBodyController"
        };

        private readonly IModLogger logger;
        private bool loggedSource;

        public RobotPrefabResolver(IModLogger logger)
        {
            this.logger = logger;
        }

        // Original single-prefab resolution, semantics unchanged: the best PooledSpawner prefab wins, the
        // loaded-asset scan is only a fallback, and no type-eligibility threshold applies.
        public GameObject? Resolve()
        {
            var fromSpawner = BestOf(EnumerateSpawnerScored());
            if (fromSpawner != null)
            {
                LogSource("using robot prefab from PooledSpawner: " + fromSpawner.name);
                return fromSpawner;
            }

            var fromLoadedAssets = BestOf(EnumerateLoadedScored());
            if (fromLoadedAssets != null)
            {
                LogSource("using loaded robot object: " + fromLoadedAssets.name);
                return fromLoadedAssets;
            }

            return null;
        }

        private static GameObject? BestOf(IEnumerable<(GameObject Prefab, int Score)> scored)
        {
            var bestScore = 0;
            GameObject? best = null;
            foreach (var (prefab, score) in scored)
            {
                if (score > bestScore)
                {
                    bestScore = score;
                    best = prefab;
                }
            }

            return best;
        }

        // Every distinct robot TYPE in the loaded game: the walkable robot prefabs (body + locomotion), deduped
        // by prefab reference then by case-insensitive prefab name (the same prefab can be discoverable via both
        // the PooledSpawner and the loaded-asset scan). The default — exactly Resolve()'s winner — is always
        // index 0 (inserted even when it misses the type threshold), so default spawns are unchanged.
        public IReadOnlyList<RobotPrefabCandidate> ResolveAll()
        {
            var defaultPrefab = Resolve();
            var byName = new Dictionary<string, (GameObject Prefab, int Score)>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<GameObject>();

            foreach (var (prefab, score) in EnumerateAllScored())
            {
                var isDefault = ReferenceEquals(prefab, defaultPrefab);
                if ((score < TypeEligibilityScore && !isDefault) || !seen.Add(prefab))
                {
                    continue;
                }

                if (!byName.TryGetValue(prefab.name, out var existing) || score > existing.Score)
                {
                    byName[prefab.name] = (prefab, score);
                }
            }

            var candidates = new List<RobotPrefabCandidate>(byName.Count);
            foreach (var pair in byName)
            {
                candidates.Add(new RobotPrefabCandidate(
                    Slugify(pair.Value.Prefab.name),
                    Prettify(pair.Value.Prefab.name),
                    pair.Value.Prefab,
                    pair.Value.Score));
            }

            candidates.Sort((a, b) =>
            {
                var aDefault = ReferenceEquals(a.Prefab, defaultPrefab);
                var bDefault = ReferenceEquals(b.Prefab, defaultPrefab);
                if (aDefault != bDefault)
                {
                    return aDefault ? -1 : 1;
                }

                return a.Score != b.Score
                    ? b.Score.CompareTo(a.Score)
                    : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            });

            return candidates;
        }

        private IEnumerable<(GameObject Prefab, int Score)> EnumerateAllScored()
        {
            foreach (var scored in EnumerateSpawnerScored())
            {
                yield return scored;
            }

            foreach (var scored in EnumerateLoadedScored())
            {
                yield return scored;
            }
        }

        private IEnumerable<(GameObject Prefab, int Score)> EnumerateSpawnerScored()
        {
            foreach (var component in UnityEngine.Object.FindObjectsByType<Component>(FindObjectsSortMode.None))
            {
                if (!GameReflection.IsNamed(component, "PooledSpawner"))
                {
                    continue;
                }

                foreach (var prefab in GetPrefabs(component))
                {
                    var score = Score(prefab);
                    if (score > 0)
                    {
                        yield return (prefab!, score);
                    }
                }
            }
        }

        private IEnumerable<(GameObject Prefab, int Score)> EnumerateLoadedScored()
        {
            foreach (var component in Resources.FindObjectsOfTypeAll<Component>())
            {
                if (!GameReflection.IsNamed(component, RobotComponentNames))
                {
                    continue;
                }

                var root = GameReflection.GetRobotBodyRoot(component);
                var score = Score(root);
                if (score > 0)
                {
                    yield return (root!, score);
                }
            }
        }

        private static GameObject[] GetPrefabs(Component spawner)
        {
            try
            {
                var field = spawner.GetType().GetField(
                    "prefabs",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return field?.GetValue(spawner) as GameObject[] ?? Array.Empty<GameObject>();
            }
            catch
            {
                return Array.Empty<GameObject>();
            }
        }

        private static int Score(GameObject? candidate)
        {
            if (candidate == null || GameReflection.HasComponent(candidate, "PlayerController"))
            {
                return 0;
            }

            // Only clone genuine prefab assets. A loaded prefab asset has an invalid/zero scene handle, while a
            // live (or already-spawned) scene instance belongs to a valid loaded scene. Cloning a live instance
            // would snapshot its mutated runtime state and momentarily duplicate it.
            if (candidate.scene.IsValid())
            {
                return 0;
            }

            var score = 0;
            if (GameReflection.HasComponent(candidate, "RobotBody"))
            {
                score += 100;
            }

            if (GameReflection.HasComponent(candidate, "LLMAgent"))
            {
                score += 40;
            }

            if (GameReflection.HasComponent(candidate, "AgentHead"))
            {
                score += 30;
            }

            if (GameReflection.HasComponent(candidate, "LocomotionController"))
            {
                score += 25;
            }

            if (GameReflection.HasComponent(candidate, "Health"))
            {
                score += 5;
            }

            return score;
        }

        // "Worker Robot_v2" -> "worker-robot-v2"
        private static string Slugify(string name)
        {
            var sb = new System.Text.StringBuilder(name.Length);
            var lastWasDash = false;
            foreach (var ch in name.Trim())
            {
                var c = char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-';
                if (c == '-')
                {
                    if (lastWasDash || sb.Length == 0)
                    {
                        continue;
                    }

                    lastWasDash = true;
                }
                else
                {
                    lastWasDash = false;
                }

                sb.Append(c);
            }

            return sb.ToString().TrimEnd('-');
        }

        // "worker_robot-v2" -> "Worker Robot V2"
        private static string Prettify(string name)
        {
            var sb = new System.Text.StringBuilder(name.Length);
            var startOfWord = true;
            foreach (var ch in name.Trim())
            {
                if (ch == '-' || ch == '_' || ch == ' ')
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] != ' ')
                    {
                        sb.Append(' ');
                    }

                    startOfWord = true;
                    continue;
                }

                sb.Append(startOfWord ? char.ToUpperInvariant(ch) : ch);
                startOfWord = false;
            }

            return sb.Length == 0 ? name : sb.ToString();
        }

        private void LogSource(string message)
        {
            if (loggedSource)
            {
                return;
            }

            loggedSource = true;
            logger.Info("RobotKit " + message + ".");
        }
    }
}
