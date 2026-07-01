using System;
using System.Collections.Generic;

namespace Robotopia.GameCompat
{
    // How badly a broken binding hurts, mirroring the runtime inventory's classification.
    //   Critical = the feature hard-fails / throws if the symbol is gone.
    //   Degraded = the feature silently stops working (the common case: a guarded null that no-ops).
    //   Optional = a best-effort nicety; losing it is cosmetic.
    public enum Criticality
    {
        Optional = 0,
        Degraded = 1,
        Critical = 2,
    }

    public enum BindingKind
    {
        Type = 0,
        Method = 1,
        Constructor = 2,
        Field = 3,
        Property = 4,
        EnumValue = 5,
    }

    // How the mod actually resolves the symbol at runtime — this decides what the offline check can honestly
    // prove. The adversarial design review established that a single "does full-name X exist" model green-washes
    // most real bindings, so every binding declares its match mode and the differ reports coverage per mode.
    public enum MatchMode
    {
        // Declaring type comes from a `Type.GetType("X, GameCode")` literal; member resolved by name/signature.
        // Fully verifiable offline.
        StaticFullName = 0,

        // Component matched by SIMPLE name anywhere in the inheritance chain, case-insensitive (IsNamed/
        // FindComponent). Verifiable as "some type still has this simple name", NOT as "this full name exists".
        SimpleNameWalk = 1,

        // Method chosen by a GetMethods() predicate: name + arity + a few pinned parameter positions, with the
        // remaining positions intentionally unconstrained. Verified by matching an overload, not by frozen signature.
        PredicateOverload = 2,

        // A magic string written into a game field/enum (e.g. Mode = "SelectedFile"). If the field is an enum we
        // can check the token is still a valid member name; for a plain string discriminator it is uncheckable.
        ValueContract = 3,

        // Member resolved off a runtime instance's concrete type (target.GetType()). The author declares the
        // intended declaring type; membership is checked with inherited semantics and treated as a soft signal.
        DynamicInstance = 4,

        // Structurally impossible to verify offline (dynamically-built type strings, invoke-only contracts).
        // Recorded so it is COUNTED as unverified rather than silently omitted.
        Uncheckable = 5,
    }

    // One position in a method/constructor parameter list. Constrained=true means the runtime binder pins this
    // position's type (it must match a candidate overload); Constrained=false means only arity matters there.
    public sealed class ParameterSpec
    {
        public ParameterSpec(string type, bool constrained)
        {
            Type = type ?? string.Empty;
            Constrained = constrained;
        }

        public string Type { get; }

        public bool Constrained { get; }
    }

    public sealed class GameBinding
    {
        public string Id { get; set; } = string.Empty;

        public BindingKind Kind { get; set; } = BindingKind.Type;

        // Owning assembly of the declaring type. Most are GameCode, but the runtime surface also reaches into
        // HDRP, Sentry and UnityEngine — those must resolve against their own assembly, not GameCode.dll.
        public string Assembly { get; set; } = "GameCode";

        // Declaring type WITHOUT the assembly suffix, e.g. "Health" or
        // "UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData".
        public string DeclaringType { get; set; } = string.Empty;

        public string Member { get; set; } = string.Empty;

        public MatchMode MatchMode { get; set; } = MatchMode.StaticFullName;

        public Criticality Criticality { get; set; } = Criticality.Degraded;

        public string Feature { get; set; } = string.Empty;

        public string SourceRef { get; set; } = string.Empty;

        // Method/constructor parameter discriminator (ordered). Empty for non-callable kinds.
        public IList<ParameterSpec> Parameters { get; } = new List<ParameterSpec>();

        // Return / field / property type (best-known), for a ChangedSignature signal. Empty when name-only.
        public string ReturnType { get; set; } = string.Empty;

        // EnumValue: the expected integer ordinal the mod depends on. This is the crux of the ordinal-drift
        // class — a reordered game enum silently breaks `(int)` casts, so we pin the name<->ordinal mapping and
        // the differ asserts the game enum still has this member at this ordinal.
        public bool HasExpectedOrdinal { get; set; }

        public long ExpectedOrdinal { get; set; }

        // ValueContract: the literal string the mod writes (e.g. "SelectedFile"); checked against enum members
        // when the target is an enum, otherwise recorded as an uncheckable contract.
        public string ContractValue { get; set; } = string.Empty;

        public bool IsOfflineVerifiable => MatchMode != MatchMode.Uncheckable;

        // The type key used to look this binding up in a surface snapshot: "<Assembly>|<DeclaringType>".
        public string TypeKey => Assembly + "|" + DeclaringType;

        public JsonObject ToJson()
        {
            var json = new JsonObject()
                .Set("id", Id)
                .Set("kind", Kind.ToString())
                .Set("assembly", Assembly)
                .Set("declaringType", DeclaringType)
                .Set("matchMode", MatchMode.ToString())
                .Set("criticality", Criticality.ToString())
                .Set("feature", Feature)
                .Set("sourceRef", SourceRef);

            if (Member.Length > 0)
            {
                json.Set("member", Member);
            }

            if (ReturnType.Length > 0)
            {
                json.Set("returnType", ReturnType);
            }

            if (Parameters.Count > 0)
            {
                var array = new JsonArray();
                foreach (var parameter in Parameters)
                {
                    array.Add(new JsonObject().Set("type", parameter.Type).Set("constrained", parameter.Constrained));
                }

                json.Set("parameters", array);
            }

            if (HasExpectedOrdinal)
            {
                json.Set("expectedOrdinal", ExpectedOrdinal);
            }

            if (ContractValue.Length > 0)
            {
                json.Set("contractValue", ContractValue);
            }

            return json;
        }

        public static GameBinding FromJson(JsonObject json)
        {
            var binding = new GameBinding
            {
                Id = json.GetString("id"),
                Kind = ParseEnum(json.GetString("kind"), BindingKind.Type),
                Assembly = json.GetString("assembly", "GameCode"),
                DeclaringType = json.GetString("declaringType"),
                Member = json.GetString("member"),
                MatchMode = ParseEnum(json.GetString("matchMode"), MatchMode.StaticFullName),
                Criticality = ParseEnum(json.GetString("criticality"), Criticality.Degraded),
                Feature = json.GetString("feature"),
                SourceRef = json.GetString("sourceRef"),
                ReturnType = json.GetString("returnType"),
                ContractValue = json.GetString("contractValue"),
            };

            if (json.Has("expectedOrdinal"))
            {
                binding.HasExpectedOrdinal = true;
                binding.ExpectedOrdinal = json.GetLong("expectedOrdinal");
            }

            foreach (var item in json.GetArray("parameters").Items)
            {
                var parameter = item.AsObject();
                binding.Parameters.Add(new ParameterSpec(parameter.GetString("type"), parameter.GetBool("constrained", true)));
            }

            return binding;
        }

        private static TEnum ParseEnum<TEnum>(string value, TEnum fallback) where TEnum : struct
        {
            return Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed : fallback;
        }
    }

    // The per-mod declaration of every game symbol it depends on. One file per mod:
    // bindings/<mod-id>.gamebindings.json.
    public sealed class BindingManifest
    {
        public int SchemaVersion { get; set; } = 1;

        public string ModId { get; set; } = string.Empty;

        public string ModName { get; set; } = string.Empty;

        public IList<GameBinding> Bindings { get; } = new List<GameBinding>();

        public string ToCanonicalJson() => ToJson().ToCanonical();

        public JsonObject ToJson()
        {
            var array = new JsonArray();
            // Sort by id so the on-disk order is deterministic regardless of authoring order.
            var sorted = new List<GameBinding>(Bindings);
            sorted.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            foreach (var binding in sorted)
            {
                array.Add(binding.ToJson());
            }

            return new JsonObject()
                .Set("schemaVersion", SchemaVersion)
                .Set("modId", ModId)
                .Set("modName", ModName)
                .Set("bindings", array);
        }

        public static BindingManifest Parse(string json)
        {
            var root = JsonValue.Parse(json).AsObject();
            var manifest = new BindingManifest
            {
                SchemaVersion = (int)root.GetLong("schemaVersion", 1),
                ModId = root.GetString("modId"),
                ModName = root.GetString("modName"),
            };

            foreach (var item in root.GetArray("bindings").Items)
            {
                manifest.Bindings.Add(GameBinding.FromJson(item.AsObject()));
            }

            return manifest;
        }

        // Validation independent of any game DLL — used by the offline CI gate to catch a malformed manifest
        // before it ever reaches the extractor.
        public IEnumerable<string> Validate()
        {
            var problems = new List<string>();
            if (ModId.Length == 0)
            {
                problems.Add("manifest has no modId");
            }

            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in Bindings)
            {
                if (binding.Id.Length == 0)
                {
                    problems.Add("a binding has no id");
                }
                else if (!seenIds.Add(binding.Id))
                {
                    problems.Add("duplicate binding id '" + binding.Id + "'");
                }

                // Uncheckable bindings record a dependency the scanner/extractor cannot pin (e.g. a dynamically
                // built type string), so they are allowed to omit the declaringType.
                if (binding.DeclaringType.Length == 0 && binding.MatchMode != MatchMode.Uncheckable)
                {
                    problems.Add("binding '" + binding.Id + "' has no declaringType");
                }

                // Members that are looked up by name need one; a Type binding names no member, and a Constructor is
                // identified by its declaring type + parameters, not a member name.
                var needsMember = binding.Kind == BindingKind.Method || binding.Kind == BindingKind.Field ||
                    binding.Kind == BindingKind.Property || binding.Kind == BindingKind.EnumValue;
                if (needsMember && binding.Member.Length == 0)
                {
                    problems.Add("binding '" + binding.Id + "' is a " + binding.Kind + " but names no member");
                }

                // An enumValue binding MAY pin an expectedOrdinal (for `(int)`-cast/Enum.ToObject mappings, where
                // ordinal drift is the bug) or omit it (for Enum.Parse-by-name lookups, where only the name matters).
                // Both are valid; the resolver checks the ordinal only when one is declared.
            }

            return problems;
        }
    }
}
