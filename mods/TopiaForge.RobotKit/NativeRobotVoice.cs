using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.RobotKit
{
    // Identity is derived from the actual bound model/configuration and native synthesis settings, never a catalog id.
    internal static class NativeRobotVoice
    {
        private static ConditionalWeakTable<TextAsset, Digest> assets = new ConditionalWeakTable<TextAsset, Digest>();
        // Build 2640 replaced OvertoneVoiceAsset with TTSVoiceAsset and dropped its ten punctuation break times. Its
        // synthesis inputs are the model, the configuration, the FMOD event, the speed multiplier and the phonemizer,
        // which turns text into phonemes before synthesis. Each is part of the fingerprint.
        private static readonly string[] settings = { "speedMultiplier" };
        private static readonly string[] phonemizerAssets = { "modelAsset", "tokenizerAsset", "dictAsset" };
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal static void Clear() => assets = new ConditionalWeakTable<TextAsset, Digest>();
        internal static OperationResult<string> Identity(AgentHead head)
        {
            try
            {
                var voice = typeof(AgentHead).GetProperty("ActiveVoice", Instance)?.GetValue(head) as TTSVoiceAsset;
                return voice == null ? Missing() : Identity(voice);
            }
            catch (Exception) { return Missing(); }
        }
        internal static TTSVoiceAsset? Resolve(string identity)
        {
            // Only locally loaded native assets are candidates. A descriptor never requests an arbitrary asset or file.
            var voices = Resources.FindObjectsOfTypeAll<TTSVoiceAsset>();
            if (voices.Length > 128) return null;
            foreach (var voice in voices)
                if (voice != null && Identity(voice).TryGetValue(out var key) && key == identity) return voice;
            return null;
        }
        private static OperationResult<string> Identity(TTSVoiceAsset voice)
        {
            try
            {
                var type = typeof(TTSVoiceAsset);
                var model = type.GetField("modelAsset", Instance)?.GetValue(voice) as TextAsset;
                var config = type.GetField("configAsset", Instance)?.GetValue(voice) as TextAsset;
                if (model == null || config == null) return Missing();
                using var data = new MemoryStream();
                using var writer = new BinaryWriter(data, Encoding.UTF8, true);
                // Version 2: the settings list changed shape and the phonemizer joined, so no version 2 fingerprint
                // equals a version 1 one. Every field is fixed length or behind a presence byte, so the encoding is
                // unambiguous.
                writer.Write((byte)2);
                writer.Write(assets.GetValue(model, Hash).Bytes); writer.Write(assets.GetValue(config, Hash).Bytes);
                if (!WritePhonemizer(writer, type, voice)) return Missing();
                var audio = voice.FmodVoiceAsset.Guid;
                writer.Write(audio.Data1); writer.Write(audio.Data2); writer.Write(audio.Data3); writer.Write(audio.Data4);
                foreach (var name in settings)
                {
                    if (!(type.GetField(name, Instance)?.GetValue(voice) is float value) || float.IsNaN(value) || float.IsInfinity(value)) return Missing();
                    writer.Write(value);
                }
                using var sha = SHA256.Create();
                return OperationResult<string>.Success(BitConverter.ToString(sha.ComputeHash(data.ToArray())).Replace("-", "").ToLowerInvariant());
            }
            catch (Exception) { return Missing(); }
        }
        // The phonemizer is identified like the voice's own assets: by the contents of its model, tokenizer and
        // dictionary. A voice without a phonemizer, or a phonemizer without one of those assets, is written as absent
        // rather than refused; a missing field means the binding changed, which fails closed.
        private static bool WritePhonemizer(BinaryWriter writer, Type voiceType, TTSVoiceAsset voice)
        {
            var field = voiceType.GetField("phonemizer", Instance);
            if (field == null || field.FieldType != typeof(TTSPhonemizerAsset)) return false;
            var phonemizer = field.GetValue(voice) as TTSPhonemizerAsset;
            if (phonemizer == null)
            {
                writer.Write((byte)0);
                return true;
            }
            writer.Write((byte)1);
            foreach (var name in phonemizerAssets)
            {
                var assetField = typeof(TTSPhonemizerAsset).GetField(name, Instance);
                if (assetField == null || assetField.FieldType != typeof(TextAsset)) return false;
                var asset = assetField.GetValue(phonemizer) as TextAsset;
                if (asset == null) { writer.Write((byte)0); continue; }
                writer.Write((byte)1);
                writer.Write(assets.GetValue(asset, Hash).Bytes);
            }
            return true;
        }
        private static Digest Hash(TextAsset asset)
        {
            var bytes = asset.bytes;
            if (bytes == null || bytes.Length == 0) throw new InvalidOperationException();
            using var sha = SHA256.Create();
            return new Digest(sha.ComputeHash(bytes));
        }
        private static OperationResult<string> Missing() => OperationResult<string>.Failure(ModErrorCode.Unavailable,
            "Native model, voice configuration or synthesis settings are unavailable; retry after the voice bindings recover.");
        private sealed class Digest
        {
            internal readonly byte[] Bytes;
            internal Digest(byte[] bytes) { Bytes = bytes; }
        }
    }
}
