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
        private static readonly string[] settings = { "speedMultiplier", "ellipsisBreakTime", "periodBreakTime", "colonBreakTime",
            "exclamationBreakTime", "questionBreakTime", "semicolonBreakTime", "commaBreakTime", "dashBreakTime", "singleDashBreakTime", "newlineBreakTime" };
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal static void Clear() => assets = new ConditionalWeakTable<TextAsset, Digest>();
        internal static OperationResult<string> Identity(AgentHead head)
        {
            try
            {
                var voice = typeof(AgentHead).GetProperty("ActiveVoice", Instance)?.GetValue(head) as OvertoneVoiceAsset;
                return voice == null ? Missing() : Identity(voice);
            }
            catch (Exception) { return Missing(); }
        }
        internal static OvertoneVoiceAsset? Resolve(string identity)
        {
            // Only locally loaded native assets are candidates. A descriptor never requests an arbitrary asset or file.
            var voices = Resources.FindObjectsOfTypeAll<OvertoneVoiceAsset>();
            if (voices.Length > 128) return null;
            foreach (var voice in voices)
                if (voice != null && Identity(voice).TryGetValue(out var key) && key == identity) return voice;
            return null;
        }
        private static OperationResult<string> Identity(OvertoneVoiceAsset voice)
        {
            try
            {
                var type = typeof(OvertoneVoiceAsset);
                var model = type.GetField("modelAsset", Instance)?.GetValue(voice) as TextAsset;
                var config = type.GetField("configAsset", Instance)?.GetValue(voice) as TextAsset;
                if (model == null || config == null) return Missing();
                using var data = new MemoryStream();
                using var writer = new BinaryWriter(data, Encoding.UTF8, true);
                writer.Write((byte)1);
                writer.Write(assets.GetValue(model, Hash).Bytes); writer.Write(assets.GetValue(config, Hash).Bytes);
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
