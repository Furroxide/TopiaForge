using System;
using System.Collections.Generic;
using System.Linq;

namespace TopiaForge.GameCompat
{
    public enum Severity
    {
        Info = 0,
        Warning = 1,
        Error = 2,
    }

    public enum ChangeKind
    {
        Ok = 0,
        MissingType = 1,
        MissingMember = 2,
        SignatureMismatch = 3,
        EnumOrdinalMismatch = 4,
        ValueContractBroken = 5,
        ConstructorUnavailable = 6,

        // Not a break: the binding cannot be checked offline (by mode) — recorded so coverage is honest.
        Unverifiable = 7,

        // The capture environment could not read this symbol (missing referenced assembly). NOT a break.
        Indeterminate = 8,

        // Live-diff only: the mod was already broken vs baseline and the new build restores it.
        Restored = 9,
    }

    public sealed class CompatFinding
    {
        public string ModId { get; set; } = string.Empty;
        public string BindingId { get; set; } = string.Empty;
        public string Feature { get; set; } = string.Empty;
        public string DeclaringType { get; set; } = string.Empty;
        public string Member { get; set; } = string.Empty;
        public MatchMode MatchMode { get; set; }
        public ChangeKind ChangeKind { get; set; }
        public Severity Severity { get; set; }
        public string Detail { get; set; } = string.Empty;

        public JsonObject ToJson() => new JsonObject()
            .Set("modId", ModId)
            .Set("bindingId", BindingId)
            .Set("feature", Feature)
            .Set("declaringType", DeclaringType)
            .Set("member", Member)
            .Set("matchMode", MatchMode.ToString())
            .Set("changeKind", ChangeKind.ToString())
            .Set("severity", Severity.ToString())
            .Set("detail", Detail);
    }

    public sealed class CompatReport
    {
        public string Mode { get; set; } = string.Empty; // "resolve" | "diff"
        public IList<CompatFinding> Findings { get; } = new List<CompatFinding>();

        // Coverage accounting — the antidote to false confidence: how many bindings were actually verifiable.
        public int TotalBindings { get; set; }
        public int VerifiableBindings { get; set; }
        public int UncheckableBindings { get; set; }
        public int IndeterminateBindings { get; set; }

        public int ErrorCount => Findings.Count(f => f.Severity == Severity.Error);
        public int WarningCount => Findings.Count(f => f.Severity == Severity.Warning);
        public bool HasBreakingChanges => ErrorCount > 0;

        public JsonObject ToJson()
        {
            var findings = new JsonArray();
            foreach (var finding in Findings
                .OrderByDescending(f => (int)f.Severity)
                .ThenBy(f => f.ModId, StringComparer.Ordinal)
                .ThenBy(f => f.BindingId, StringComparer.Ordinal))
            {
                findings.Add(finding.ToJson());
            }

            return new JsonObject()
                .Set("mode", Mode)
                .Set("totalBindings", TotalBindings)
                .Set("verifiableBindings", VerifiableBindings)
                .Set("uncheckableBindings", UncheckableBindings)
                .Set("indeterminateBindings", IndeterminateBindings)
                .Set("errorCount", ErrorCount)
                .Set("warningCount", WarningCount)
                .Set("findings", findings);
        }
    }

    // A single binding's resolution against one surface, before severity is applied.
    internal readonly struct Resolution
    {
        public Resolution(ChangeKind kind, string detail)
        {
            Kind = kind;
            Detail = detail;
        }

        public ChangeKind Kind { get; }

        public string Detail { get; }

        public bool IsPresent => Kind == ChangeKind.Ok;

        public static readonly Resolution Ok = new Resolution(ChangeKind.Ok, string.Empty);
    }

    public static class SurfaceDiffer
    {
        // OFFLINE GATE and LIVE CHECK share this: resolve every manifest binding against a full surface snapshot
        // (the checked-in baseline, or a freshly-extracted candidate). Because the surface carries every type's
        // COMPLETE member list — captured independently from the real DLL, not projected from the manifest — a
        // declared-but-absent symbol produces a real MissingMember. That is what makes this non-circular.
        public static CompatReport ResolveManifests(IEnumerable<BindingManifest> manifests, SurfaceSnapshot surface)
        {
            var report = new CompatReport { Mode = "resolve" };
            foreach (var manifest in manifests)
            {
                foreach (var binding in manifest.Bindings)
                {
                    report.TotalBindings++;
                    if (binding.MatchMode == MatchMode.Uncheckable)
                    {
                        report.UncheckableBindings++;
                        continue;
                    }

                    var resolution = Resolve(binding, surface);
                    if (resolution.Kind == ChangeKind.Indeterminate)
                    {
                        report.IndeterminateBindings++;
                        report.Findings.Add(MakeFinding(manifest, binding, resolution, Severity.Info));
                        continue;
                    }

                    report.VerifiableBindings++;
                    if (resolution.Kind != ChangeKind.Ok)
                    {
                        report.Findings.Add(MakeFinding(manifest, binding, resolution, SeverityFor(binding, resolution.Kind)));
                    }
                }
            }

            return report;
        }

        // CHANGE-ORIENTED report for the human-reviewed baseline-refresh artifact: what did a new build change for
        // the mods, relative to the last known-good baseline. Root-cause parenting keeps a removed TYPE from also
        // spamming a MissingMember for every binding on it; reference-set drift downgrades signature-only noise.
        public static CompatReport DiffSurfaces(SurfaceSnapshot baseline, SurfaceSnapshot candidate, IEnumerable<BindingManifest> manifests)
        {
            var report = new CompatReport { Mode = "diff" };
            var referenceSetChanged = !baseline.ReferenceSet.SequenceEqual(candidate.ReferenceSet);
            var removedTypeKeys = new HashSet<string>(StringComparer.Ordinal);

            var all = manifests.SelectMany(m => m.Bindings.Select(b => (m, b))).ToList();

            // First pass: find types the new build removed, so member-level findings on them can be suppressed.
            foreach (var (manifest, binding) in all)
            {
                if (binding.MatchMode == MatchMode.Uncheckable)
                {
                    continue;
                }

                var before = Resolve(binding, baseline);
                var after = Resolve(binding, candidate);
                if (before.IsPresent && after.Kind == ChangeKind.MissingType)
                {
                    removedTypeKeys.Add(binding.TypeKey);
                }
            }

            var reportedTypeRemovals = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (manifest, binding) in all)
            {
                report.TotalBindings++;
                if (binding.MatchMode == MatchMode.Uncheckable)
                {
                    report.UncheckableBindings++;
                    continue;
                }

                var before = Resolve(binding, baseline);
                var after = Resolve(binding, candidate);

                if (after.Kind == ChangeKind.Indeterminate || before.Kind == ChangeKind.Indeterminate)
                {
                    report.IndeterminateBindings++;
                    continue;
                }

                report.VerifiableBindings++;

                if (before.IsPresent && after.IsPresent)
                {
                    continue; // unchanged and healthy
                }

                if (!before.IsPresent && after.IsPresent)
                {
                    report.Findings.Add(MakeFinding(manifest, binding, new Resolution(ChangeKind.Restored,
                        "was broken against the baseline; the new build restores it"), Severity.Info));
                    continue;
                }

                if (!before.IsPresent && !after.IsPresent)
                {
                    continue; // already broken in the baseline; not a regression introduced by this build
                }

                // before present, after not present -> a regression the new build introduced.
                if (after.Kind == ChangeKind.MissingType)
                {
                    if (removedTypeKeys.Contains(binding.TypeKey) && !reportedTypeRemovals.Add(binding.TypeKey))
                    {
                        // Already reported this type's removal once; suppress the duplicate member noise but
                        // still count the impacted feature in the detail of the first finding.
                        continue;
                    }
                }

                var severity = SeverityFor(binding, after.Kind);
                if (after.Kind == ChangeKind.SignatureMismatch && referenceSetChanged)
                {
                    severity = Severity.Info; // could be an environment artifact, not a real game change
                    report.Findings.Add(MakeFinding(manifest, binding, new Resolution(after.Kind,
                        after.Detail + " (reference set differs between baseline and candidate — treat as advisory)"), severity));
                    continue;
                }

                report.Findings.Add(MakeFinding(manifest, binding, after, severity));
            }

            return report;
        }

        // ---- core per-binding resolution ----
        private static Resolution Resolve(GameBinding binding, SurfaceSnapshot surface)
        {
            if (binding.MatchMode == MatchMode.SimpleNameWalk)
            {
                return ResolveSimpleNameWalk(binding, surface);
            }

            var type = surface.FindType(binding.TypeKey);
            if (type == null || type.Status == SurfaceStatus.Absent)
            {
                return new Resolution(ChangeKind.MissingType, "type '" + binding.DeclaringType + "' not found in " + binding.Assembly);
            }

            if (type.Status == SurfaceStatus.Unreadable)
            {
                return new Resolution(ChangeKind.Indeterminate,
                    "type '" + binding.DeclaringType + "' could not be read in this environment (missing referenced assembly)");
            }

            return ResolveOnType(binding, type);
        }

        private static Resolution ResolveSimpleNameWalk(GameBinding binding, SurfaceSnapshot surface)
        {
            var simpleName = Simple(binding.DeclaringType);
            var key = binding.Assembly + "|" + simpleName;
            if (!surface.SimpleNameCounts.TryGetValue(key, out var count))
            {
                return new Resolution(ChangeKind.Indeterminate,
                    "snapshot has no simple-name scan for '" + simpleName + "' in " + binding.Assembly);
            }

            if (count == 0)
            {
                return new Resolution(ChangeKind.MissingType,
                    "no type named '" + simpleName + "' found in " + binding.Assembly + " (matched by simple name)");
            }

            var candidates = surface.Types.Values
                .Where(type => type.Status != SurfaceStatus.Absent &&
                               string.Equals(type.Assembly, binding.Assembly, StringComparison.Ordinal) &&
                               string.Equals(type.SimpleName, simpleName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToList();

            // A type-only walk has no member contract to validate. A positive, complete count remains sufficient.
            if (binding.Kind == BindingKind.Type)
            {
                return count > 0 || candidates.Count > 0
                    ? Resolution.Ok
                    : new Resolution(ChangeKind.Indeterminate,
                        "simple-name scan for '" + simpleName + "' was incomplete");
            }

            var resolutions = candidates.Select(type => ResolveOnType(binding, type)).ToList();
            if (resolutions.Any(resolution => resolution.Kind == ChangeKind.Ok))
            {
                return Resolution.Ok;
            }

            // A negative result is only conclusive when every simple-name candidate was captured. Older snapshots
            // stored the count but not the candidate surfaces; never green-light or red-light a member from that.
            if (count < 0 || candidates.Count < count ||
                resolutions.Any(resolution => resolution.Kind == ChangeKind.Indeterminate))
            {
                return new Resolution(ChangeKind.Indeterminate,
                    "could not inspect every type named '" + simpleName + "' for member '" + binding.Member + "'");
            }

            var mismatch = resolutions.FirstOrDefault(resolution =>
                resolution.Kind == ChangeKind.SignatureMismatch ||
                resolution.Kind == ChangeKind.ConstructorUnavailable ||
                resolution.Kind == ChangeKind.ValueContractBroken);
            if (mismatch.Kind != ChangeKind.Ok)
            {
                return mismatch;
            }

            return new Resolution(ChangeKind.MissingMember,
                "member '" + binding.Member + "' not found on any of the " + candidates.Count +
                " type(s) named '" + simpleName + "' in " + binding.Assembly);
        }

        private static Resolution ResolveOnType(GameBinding binding, TypeSurface type)
        {
            if (type.Status == SurfaceStatus.Unreadable)
            {
                return new Resolution(ChangeKind.Indeterminate,
                    "type '" + type.FullName + "' could not be read in this environment (missing referenced assembly)");
            }

            switch (binding.Kind)
            {
                case BindingKind.Type:
                    return Resolution.Ok;

                case BindingKind.Field:
                    return ResolveField(binding, type);

                case BindingKind.Property:
                    return ResolveProperty(binding, type);

                case BindingKind.Method:
                    return ResolveMethod(binding, type);

                case BindingKind.Constructor:
                    return ResolveConstructor(binding, type);

                case BindingKind.EnumValue:
                    return ResolveEnumValue(binding, type);

                default:
                    return Resolution.Ok;
            }
        }

        private static Resolution ResolveField(GameBinding binding, TypeSurface type)
        {
            // ValueContract on an enum field: verify the literal token is still a valid enum member.
            var field = type.Fields.FirstOrDefault(f => f.Name == binding.Member);
            if (field == null)
            {
                // A ValueContract may target the enum TYPE of the field rather than a field on this type; handled
                // by EnumValue bindings. Here a plain missing field is a real break.
                return new Resolution(ChangeKind.MissingMember, "field '" + binding.Member + "' not found on " + binding.DeclaringType);
            }

            if (binding.ReturnType.Length > 0 && !TypeNameMatches(field.Type, binding.ReturnType))
            {
                return new Resolution(ChangeKind.SignatureMismatch,
                    "field '" + binding.Member + "' type is now '" + field.Type + "', expected '" + binding.ReturnType + "'");
            }

            return Resolution.Ok;
        }

        private static Resolution ResolveProperty(GameBinding binding, TypeSurface type)
        {
            var property = type.Properties.FirstOrDefault(p => p.Name == binding.Member);
            if (property == null)
            {
                return new Resolution(ChangeKind.MissingMember, "property '" + binding.Member + "' not found on " + binding.DeclaringType);
            }

            if (binding.ReturnType.Length > 0 && !TypeNameMatches(property.Type, binding.ReturnType))
            {
                return new Resolution(ChangeKind.SignatureMismatch,
                    "property '" + binding.Member + "' type is now '" + property.Type + "', expected '" + binding.ReturnType + "'");
            }

            return Resolution.Ok;
        }

        private static Resolution ResolveMethod(GameBinding binding, TypeSurface type)
        {
            var overloads = type.Methods.Where(m => m.Name == binding.Member).ToList();
            if (overloads.Count == 0)
            {
                return new Resolution(ChangeKind.MissingMember, "method '" + binding.Member + "' not found on " + binding.DeclaringType);
            }

            // Match the SAME overload the runtime binder selects: correct arity, and every CONSTRAINED position's
            // type matches. An empty parameter declaration remains name-only, so every overload is a candidate.
            // Unconstrained positions are ignored (the runtime predicate ignores them too).
            var matched = binding.Parameters.Count == 0
                ? overloads
                : overloads.Where(overload =>
            {
                if (overload.Parameters.Count != binding.Parameters.Count)
                {
                    return false;
                }

                for (var i = 0; i < binding.Parameters.Count; i++)
                {
                    var spec = binding.Parameters[i];
                    if (spec.Constrained && !TypeNameMatches(overload.Parameters[i], spec.Type))
                    {
                        return false;
                    }
                }

                return true;
            }).ToList();

            if (matched.Count == 0)
            {
                var expected = string.Join(", ", binding.Parameters.Select(p => p.Constrained ? p.Type : "*"));
                var available = string.Join(" | ", overloads.Select(o => "(" + string.Join(", ", o.Parameters) + ")"));
                return new Resolution(ChangeKind.SignatureMismatch,
                    "no '" + binding.Member + "' overload matches [" + expected + "]; available: " + available);
            }

            if (binding.ReturnType.Length > 0 &&
                !matched.Any(overload => TypeNameMatches(overload.ReturnType, binding.ReturnType)))
            {
                var available = string.Join(" | ", matched.Select(overload => overload.ReturnType));
                return new Resolution(ChangeKind.SignatureMismatch,
                    "method '" + binding.Member + "' return type is [" + available +
                    "], expected '" + binding.ReturnType + "'");
            }

            return Resolution.Ok;
        }

        private static Resolution ResolveConstructor(GameBinding binding, TypeSurface type)
        {
            if (type.IsAbstract || type.IsInterface)
            {
                return new Resolution(ChangeKind.ConstructorUnavailable,
                    "type '" + binding.DeclaringType + "' is now " + (type.IsInterface ? "an interface" : "abstract") + "; Activator.CreateInstance will throw");
            }

            var wantParams = binding.Parameters.Count;
            var match = type.Constructors.Any(c =>
            {
                if (c.Parameters.Count != wantParams)
                {
                    return false;
                }

                for (var i = 0; i < wantParams; i++)
                {
                    var spec = binding.Parameters[i];
                    if (spec.Constrained && !TypeNameMatches(c.Parameters[i], spec.Type))
                    {
                        return false;
                    }
                }

                return true;
            });

            if (!match)
            {
                return new Resolution(ChangeKind.ConstructorUnavailable,
                    "no constructor with " + wantParams + " matching parameter(s) on '" + binding.DeclaringType + "'");
            }

            return Resolution.Ok;
        }

        private static Resolution ResolveEnumValue(GameBinding binding, TypeSurface type)
        {
            if (!type.IsEnum)
            {
                return new Resolution(ChangeKind.SignatureMismatch, "'" + binding.DeclaringType + "' is no longer an enum");
            }

            if (!type.EnumMembers.TryGetValue(binding.Member, out var ordinal))
            {
                return new Resolution(ChangeKind.MissingMember, "enum member '" + binding.Member + "' not found on " + binding.DeclaringType);
            }

            // The crux: a reordered enum silently corrupts every `(int)` cast the mod does. Assert the ordinal.
            if (binding.HasExpectedOrdinal && ordinal != binding.ExpectedOrdinal)
            {
                return new Resolution(ChangeKind.EnumOrdinalMismatch,
                    "enum member '" + binding.DeclaringType + "." + binding.Member + "' moved from ordinal " +
                    binding.ExpectedOrdinal + " to " + ordinal + " — every (int) cast against it is now wrong");
            }

            return Resolution.Ok;
        }

        // ---- helpers ----
        private static CompatFinding MakeFinding(BindingManifest manifest, GameBinding binding, Resolution resolution, Severity severity)
        {
            return new CompatFinding
            {
                ModId = manifest.ModId,
                BindingId = binding.Id,
                Feature = binding.Feature,
                DeclaringType = binding.DeclaringType,
                Member = binding.Member,
                MatchMode = binding.MatchMode,
                ChangeKind = resolution.Kind,
                Severity = severity,
                Detail = resolution.Detail,
            };
        }

        private static Severity SeverityFor(GameBinding binding, ChangeKind kind)
        {
            if (kind == ChangeKind.Indeterminate || kind == ChangeKind.Unverifiable || kind == ChangeKind.Restored)
            {
                return Severity.Info;
            }

            // A field/property TYPE change (not disappearance) is advisory — type-name churn is noisy and often
            // semantically compatible. Disappearance keeps the binding's real criticality.
            if (kind == ChangeKind.SignatureMismatch &&
                (binding.Kind == BindingKind.Field || binding.Kind == BindingKind.Property))
            {
                return Severity.Info;
            }

            var baseSeverity = binding.Criticality switch
            {
                Criticality.Critical => Severity.Error,
                Criticality.Degraded => Severity.Warning,
                _ => Severity.Info,
            };

            // DynamicInstance bindings resolve members off a RUNTIME instance whose concrete type the manifest can
            // only INFER. So a "missing" here may just mean the inferred declaring type is imperfect, not that the
            // game broke. Treat these as a soft signal — never let one hard-fail the gate (cap at Warning).
            if (binding.MatchMode == MatchMode.DynamicInstance && baseSeverity == Severity.Error)
            {
                return Severity.Warning;
            }

            return baseSeverity;
        }

        // Match an actual (usually fully-qualified) type name against an expected name that may be simple or full.
        private static bool TypeNameMatches(string actual, string expected)
        {
            if (string.IsNullOrEmpty(expected))
            {
                return true;
            }

            if (string.Equals(actual, expected, StringComparison.Ordinal))
            {
                return true;
            }

            return string.Equals(
                ComparableTypeShape(actual),
                ComparableTypeShape(expected),
                StringComparison.OrdinalIgnoreCase);
        }

        // Preserve the complete type shape while allowing manifests to use either qualified or simple names.
        // The old Simple() fallback discarded everything after '<', so UniTask<Expected> incorrectly matched
        // UniTask<Unrelated>. Accept both reflection-style Func`2[A,B] and normalized Func<A,B> spellings.
        private static string ComparableTypeShape(string typeName)
        {
            var result = new System.Text.StringBuilder(typeName.Length);
            var brackets = new Stack<bool>(); // true = legacy generic bracket, false = array/other bracket

            for (var index = 0; index < typeName.Length;)
            {
                var character = typeName[index];
                if (char.IsWhiteSpace(character))
                {
                    index++;
                    continue;
                }

                if (char.IsLetterOrDigit(character) || character == '_' || character == '.' || character == '+')
                {
                    var start = index;
                    while (index < typeName.Length)
                    {
                        character = typeName[index];
                        if (!char.IsLetterOrDigit(character) && character != '_' && character != '.' && character != '+')
                        {
                            break;
                        }

                        index++;
                    }

                    var token = typeName.Substring(start, index - start);
                    var separator = Math.Max(token.LastIndexOf('.'), token.LastIndexOf('+'));
                    result.Append(separator >= 0 ? token.Substring(separator + 1) : token);

                    if (index < typeName.Length && typeName[index] == '`')
                    {
                        index++;
                        while (index < typeName.Length && char.IsDigit(typeName[index]))
                        {
                            index++;
                        }

                        while (index < typeName.Length && char.IsWhiteSpace(typeName[index]))
                        {
                            index++;
                        }

                        if (index < typeName.Length && typeName[index] == '[')
                        {
                            result.Append('<');
                            brackets.Push(true);
                            index++;
                        }
                    }

                    continue;
                }

                if (character == '[')
                {
                    result.Append(character);
                    brackets.Push(false);
                    index++;
                    continue;
                }

                if (character == ']')
                {
                    result.Append(brackets.Count > 0 && brackets.Pop() ? '>' : ']');
                    index++;
                    continue;
                }

                result.Append(character);
                index++;
            }

            return result.ToString();
        }

        private static string Simple(string fullName)
        {
            if (string.IsNullOrEmpty(fullName))
            {
                return string.Empty;
            }

            var text = fullName;
            var comma = text.IndexOf(',');
            if (comma >= 0)
            {
                text = text.Substring(0, comma);
            }

            var generic = text.IndexOf('<');
            if (generic >= 0)
            {
                text = text.Substring(0, generic);
            }

            var dot = text.LastIndexOf('.');
            if (dot >= 0)
            {
                text = text.Substring(dot + 1);
            }

            var nested = text.LastIndexOf('+');
            if (nested >= 0)
            {
                text = text.Substring(nested + 1);
            }

            return text.Trim();
        }
    }
}
