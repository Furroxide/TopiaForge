using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Compiles the production voice fingerprint (<c>NativeRobotVoice.cs</c>) against signature stand-ins for build
    /// 2640's <c>TTSVoiceAsset</c> and <c>TTSPhonemizerAsset</c>, so the fingerprint's inputs are checked without
    /// Unity or GameCode in the process.
    /// </summary>
    internal static class NativeRobotVoiceFingerprintTests
    {
        internal static void Run()
        {
            var root = Program.FindRepoRoot();
            var source = File.ReadAllText(Path.Combine(root, "mods", "TopiaForge.RobotKit", "NativeRobotVoice.cs"), Encoding.UTF8);
            var abstractions = typeof(TopiaForge.Mods.OperationResult<string>).Assembly.Location;
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(p => !Path.GetFileName(p).StartsWith("UnityEngine", StringComparison.Ordinal)
                    && !Path.GetFileName(p).StartsWith("TopiaForge", StringComparison.Ordinal)
                    && !Path.GetFileName(p).StartsWith("FMOD", StringComparison.Ordinal)
                    && !string.Equals(Path.GetFileName(p), "GameCode.dll", StringComparison.OrdinalIgnoreCase))
                .Append(abstractions)
                .Select(p => MetadataReference.CreateFromFile(p));
            var compilation = CSharpCompilation.Create("NativeRobotVoiceProbe",
                new[] { CSharpSyntaxTree.ParseText(source), CSharpSyntaxTree.ParseText(Contract) }, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var emitted = compilation.Emit(bytes);
            if (!emitted.Success) throw new InvalidOperationException("Voice fingerprint probe compile failure (not a regression red):\n"
                + string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            var probe = Assembly.Load(bytes.ToArray()).GetType("TopiaForge.RobotKit.VoiceProbe", true)!;
            ((Action)probe.GetMethod("Run")!.CreateDelegate(typeof(Action)))();
            Console.WriteLine("Native voice fingerprint: phonemizer, speed and asset inputs distinguish voices; equal voices agree.");
        }

        private const string Contract = """
using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.Mods;
namespace UnityEngine {
 public class Object { }
 public class ScriptableObject : Object { }
 public class TextAsset : Object { public byte[] bytes; public TextAsset(string text) { bytes = System.Text.Encoding.UTF8.GetBytes(text); } }
 public static class Resources {
  public static readonly List<Object> Loaded = new();
  public static T[] FindObjectsOfTypeAll<T>() where T : Object => Loaded.OfType<T>().ToArray();
 }
}
namespace FMOD { public struct GUID { public int Data1, Data2, Data3, Data4; } }
namespace FMODUnity { public struct EventReference { public FMOD.GUID Guid; } }
public class TTSPhonemizerAsset : UnityEngine.ScriptableObject {
 private UnityEngine.TextAsset? modelAsset, tokenizerAsset, dictAsset;
 public TTSPhonemizerAsset(UnityEngine.TextAsset? model, UnityEngine.TextAsset? tokenizer, UnityEngine.TextAsset? dict)
 { modelAsset = model; tokenizerAsset = tokenizer; dictAsset = dict; }
}
public class TTSVoiceAsset : UnityEngine.ScriptableObject {
 private UnityEngine.TextAsset? modelAsset, configAsset;
 private TTSPhonemizerAsset? phonemizer;
 private FMODUnity.EventReference fmodVoiceAsset;
 private float speedMultiplier;
 public FMODUnity.EventReference FmodVoiceAsset => fmodVoiceAsset;
 public TTSVoiceAsset(UnityEngine.TextAsset? model, UnityEngine.TextAsset? config, TTSPhonemizerAsset? phonemizer, int audio, float speed)
 { modelAsset = model; configAsset = config; this.phonemizer = phonemizer; fmodVoiceAsset.Guid.Data1 = audio; speedMultiplier = speed; }
}
public sealed class AgentHead { private TTSVoiceAsset? ActiveVoice { get; } public AgentHead(TTSVoiceAsset? voice) { ActiveVoice = voice; } }
namespace TopiaForge.RobotKit {
 public static class VoiceProbe {
  private static readonly UnityEngine.TextAsset Model = new("model"), Config = new("config"),
   PhonemeModel = new("phoneme-model"), Tokenizer = new("tokenizer"), English = new("english"), German = new("german");
  private static string Id(TTSVoiceAsset voice) {
   var result = NativeRobotVoice.Identity(new AgentHead(voice));
   Check(result.TryGetValue(out var id), "a complete voice must have a fingerprint");
   return id!;
  }
  private static TTSVoiceAsset Voice(TTSPhonemizerAsset? phonemizer, float speed = 1f, int audio = 7) =>
   new TTSVoiceAsset(Model, Config, phonemizer, audio, speed);
  private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
  public static void Run() {
   UnityEngine.Resources.Loaded.Clear(); NativeRobotVoice.Clear();
   var english = Voice(new TTSPhonemizerAsset(PhonemeModel, Tokenizer, English));
   var german = Voice(new TTSPhonemizerAsset(PhonemeModel, Tokenizer, German));
   var bare = Voice(null);
   var noDictionary = Voice(new TTSPhonemizerAsset(PhonemeModel, Tokenizer, null));
   var englishAgain = Voice(new TTSPhonemizerAsset(new("phoneme-model"), new("tokenizer"), new("english")));
   var fast = Voice(new TTSPhonemizerAsset(PhonemeModel, Tokenizer, English), speed: 1.5f);
   var otherEvent = Voice(new TTSPhonemizerAsset(PhonemeModel, Tokenizer, English), audio: 8);

   Check(Id(english) == Id(englishAgain), "voices with equal asset contents agree, whatever the asset instances");
   NativeRobotVoice.Clear();
   Check(Id(english) == Id(englishAgain), "the fingerprint does not depend on the asset hash cache");
   Check(Id(english) != Id(german), "two voices differing only by their phonemizer dictionary must not match");
   Check(Id(english) != Id(bare) && Id(german) != Id(bare), "a voice without a phonemizer differs from one with a phonemizer");
   Check(Id(english) != Id(noDictionary) && Id(bare) != Id(noDictionary),
    "a phonemizer without a dictionary differs from both a full phonemizer and no phonemizer");
   Check(Id(english) != Id(fast) && Id(english) != Id(otherEvent), "speed and FMOD event remain fingerprint inputs");
   Check(Id(english).Length == 64 && Id(english).All(c => char.IsDigit(c) || (c >= 'a' && c <= 'f')),
    "the fingerprint is lowercase SHA-256 hex");

   UnityEngine.Resources.Loaded.AddRange(new UnityEngine.Object[] { german, bare, english });
   var resolved = NativeRobotVoice.Resolve(Id(english));
   Check(ReferenceEquals(resolved, english), "a receiver resolves the sender's phonemizer, not a voice that shares its model");
   Check(ReferenceEquals(NativeRobotVoice.Resolve(Id(german)), german), "each phonemizer variant resolves to itself");
   Check(NativeRobotVoice.Resolve(Id(noDictionary)) == null, "an unloaded variant resolves to nothing");

   Check(!NativeRobotVoice.Identity(new AgentHead(new TTSVoiceAsset(null, Config, null, 7, 1f))).TryGetValue(out _),
    "a voice without its model fails closed");
   Check(!NativeRobotVoice.Identity(new AgentHead(Voice(new TTSPhonemizerAsset(PhonemeModel, new UnityEngine.TextAsset(""), English))))
    .TryGetValue(out _), "an empty phonemizer asset fails closed rather than hashing nothing");
   Check(!NativeRobotVoice.Identity(new AgentHead(null)).TryGetValue(out _), "a head without a voice fails closed");
  }
 }
}
""";
    }
}
