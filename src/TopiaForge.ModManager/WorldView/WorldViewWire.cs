using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal readonly struct ViewTarget : IEquatable<ViewTarget>
    {
        // 1 authored scene/entity, 2 replicated spawn, 3 player. No native instance ids.
        internal readonly byte Kind;
        internal readonly string Scene;
        internal readonly string Id;
        internal ViewTarget(byte kind, string scene, string id) { Kind = kind; Scene = scene; Id = id; }
        public bool Equals(ViewTarget other) => Kind == other.Kind && Scene == other.Scene && Id == other.Id;
        public override bool Equals(object? obj) => obj is ViewTarget other && Equals(other);
        public override int GetHashCode() => (Kind * 397 ^ Scene.GetHashCode()) * 397 ^ Id.GetHashCode();
    }

    internal sealed class ViewSnapshot
    {
        internal readonly string World;
        internal readonly string? Environment;
        internal readonly VisionFog? Fog;
        internal readonly ViewTarget[] Hidden;
        internal ViewSnapshot(string world, string? environment, VisionFog? fog, ViewTarget[] hidden)
        { World = world; Environment = environment; Fog = fog; Hidden = hidden; }
        internal bool Same(ViewSnapshot other) => World == other.World && Environment == other.Environment
            && Nullable.Equals(Fog, other.Fog) && Hidden.SequenceEqual(other.Hidden);
        internal static readonly ViewSnapshot Empty = new ViewSnapshot("", null, null, Array.Empty<ViewTarget>());
    }

    // A complete snapshot is committed atomically. One bounded assembler; no journal or audience on the wire.
    internal sealed class WorldViewWire
    {
        internal const int MaximumMessageBytes = 20 * 1024;
        internal const int TargetsPerChunk = 16;
        internal const int MaximumTargets = 1024;
        private readonly WorldWireReader reader = new WorldWireReader();
        private ViewSnapshot? pending;
        private readonly HashSet<ViewTarget> unique = new HashSet<ViewTarget>();
        private int position;
        internal void Reset() { pending = null; position = 0; unique.Clear(); }
        internal bool Read(byte[] bytes, out ViewSnapshot? complete)
        {
            complete = null;
            if (bytes.Length > MaximumMessageBytes) return false;
            reader.Reset(bytes);
            if (!reader.TryReadHeader(out var kind)) return false;
            if (kind == 1)
            {
                if (!Text(reader, 128, true, out var world) || !Text(reader, 64, true, out var preset)
                    || !reader.TryReadSingle(out var distance) || !reader.TryReadByte(out var tint)
                    || !reader.TryReadInt32(MaximumTargets, out var count) || !reader.AtEnd
                    || tint > 2 || (float.IsNaN(distance) || float.IsInfinity(distance)) || distance != 0 && (distance < 1 || distance > 500)
                    || distance == 0 && tint != 0 || world.Length == 0 && (preset.Length != 0 || distance != 0 || count != 0)) return false;
                // Reliable ordering lets a new world supersede a partial old baseline without an unbounded queue.
                pending = new ViewSnapshot(world, preset.Length == 0 ? null : preset,
                    distance == 0 ? (VisionFog?)null : VisionFog.Radius(distance, (FogTint)tint), new ViewTarget[count]);
                position = 0; unique.Clear(); return true;
            }
            if (pending == null) return false;
            if (kind == 2)
            {
                if (!reader.TryReadInt32(TargetsPerChunk, out var count) || count == 0 || position + count > pending.Hidden.Length) return false;
                for (var i = 0; i < count; i++)
                {
                    if (!reader.TryReadByte(out var targetKind) || targetKind < 1 || targetKind > 3
                        || !Text(reader, 128, true, out var scene) || !Text(reader, 128, false, out var id)
                        || (targetKind == 1) != (scene.Length != 0)) return false;
                    var target = new ViewTarget(targetKind, scene, id);
                    if (!unique.Add(target)) return false;
                    pending.Hidden[position++] = target;
                }
                return reader.AtEnd;
            }
            if (kind != 3 || !reader.AtEnd || position != pending.Hidden.Length) return false;
            complete = pending; Reset(); return true;
        }
        private static bool Text(WorldWireReader reader, int maximum, bool empty, out string value) =>
            reader.TryReadString(maximum * 4, out value) && value.Length <= maximum
            && (empty && value.Length == 0 || value.Length != 0 && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl));

        internal static byte[] Write(ViewSnapshot view, int step)
        {
            var writer = new WorldWireWriter(MaximumMessageBytes);
            if (step == 0)
            {
                writer.Begin(1); writer.WriteString(view.World); writer.WriteString(view.Environment ?? "");
                writer.WriteSingle(view.Fog?.VisibleDistance ?? 0); writer.WriteByte((byte)(view.Fog?.Tint ?? 0));
                writer.WriteVarUInt((ulong)view.Hidden.Length);
            }
            else if (step <= (view.Hidden.Length + TargetsPerChunk - 1) / TargetsPerChunk)
            {
                writer.Begin(2);
                var first = (step - 1) * TargetsPerChunk;
                var count = Math.Min(TargetsPerChunk, view.Hidden.Length - first);
                writer.WriteVarUInt((ulong)count);
                for (var i = first; i < first + count; i++)
                { var target = view.Hidden[i]; writer.WriteByte(target.Kind); writer.WriteString(target.Scene); writer.WriteString(target.Id); }
            }
            else writer.Begin(3);
            var segment = writer.Segment;
            var result = new byte[segment.Count]; Array.Copy(segment.Array!, segment.Offset, result, 0, segment.Count); return result;
        }
        internal static int Steps(ViewSnapshot view) => 2 + (view.Hidden.Length + TargetsPerChunk - 1) / TargetsPerChunk;
    }
}
