using System;
using System.Linq;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using static TopiaForge.ModManager.Tests.Seam;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The SceneFollow channel's wire (WS3.8): each announcement round-trips, and a player refuses whole any message that is
    /// not exactly a valid one: another version or type, a field cut short or left over, an id, a transition, a session id,
    /// a scene name or a spawn the runtime's own rules refuse.
    /// </summary>
    internal static class SessionFollowWireTests
    {
        private const string Session = "0123456789abcdef0123456789abcdef";
        private const string Target = "io.github.furroxide.topiaforge.worlds.freeplay.menu";
        private const string Mode = "io.github.furroxide.topiaforge.worlds.freeplay";
        private const string WorldId = "io.github.furroxide.topiaforge.worlds.open_sandbox";

        internal static void Run()
        {
            RoundTrips();
            RefusesWhatIsNotExactlyAnAnnouncement();
            Console.WriteLine("SessionFollowWireTests passed.");
        }

        private static void RoundTrips()
        {
            var spawn = new TransformState(new Vec3(12.5f, -3f, 900f), new Quat(0f, 0.7071068f, 0f, 0.7071068f), new Vec3(1f, 1f, 1f));
            var states = new[]
            {
                HostWorldState.Menu,
                HostWorldState.Level("Downtown"),
                HostWorldState.World(Session, new LaunchRequest(Target), Mode, WorldId, null, ModTransitions.SceneReplacement, null, null),
                HostWorldState.World(Session, new LaunchRequest(Target, WorldId + ".level-7f", ModTransitions.AdditiveArena), Mode, WorldId + ".level-7f", WorldId,
                    ModTransitions.AdditiveArena, "Downtown", spawn),
                HostWorldState.World(Session, new LaunchRequest(Target), Mode, WorldId, null, ModTransitions.AdditiveArena, "Downtown", null)
            };
            var writer = new WorldWireWriter(SessionFollowWire.MaximumMessageBytes);
            var reader = new WorldWireReader();
            foreach (var state in states)
            {
                SessionFollowWire.Write(writer, state);
                That(writer.Length <= SessionFollowWire.MaximumMessageBytes, state + ": an announcement fits its bound");
                That(SessionFollowWire.TryRead(reader, writer.Segment.ToArray(), out var read) && read != null, state + ": it reads back");
                That(read!.Kind == state.Kind && read.SessionId == state.SessionId && read.Scene == state.Scene && read.GamemodeId == state.GamemodeId
                    && read.WorldId == state.WorldId && read.WorldFamilyId == state.WorldFamilyId && read.Transition == state.Transition
                    && Equals(read.Spawn, state.Spawn) && read.Request?.TargetId == state.Request?.TargetId
                    && read.Request?.WorldOverride == state.Request?.WorldOverride && read.Request?.TransitionOverride == state.Request?.TransitionOverride,
                    state + ": every field survives");
                That(read.SameAs(state) && state.SameAs(read), state + ": and it announces the same world");
            }

            That(!states[2].SameAs(HostWorldState.World("fedcba9876543210fedcba9876543210", new LaunchRequest(Target), Mode, WorldId, null,
                ModTransitions.SceneReplacement, null, null)), "another session is another world, whatever it resolves to");
            That(!HostWorldState.Level("Downtown").SameAs(HostWorldState.Level("Uptown")) && !HostWorldState.Menu.SameAs(null),
                "another level is another announcement");
        }

        private static void RefusesWhatIsNotExactlyAnAnnouncement()
        {
            var reader = new WorldWireReader();
            var writer = new WorldWireWriter(SessionFollowWire.MaximumMessageBytes * 2);
            byte[] Message(Action<WorldWireWriter> body)
            {
                writer.Reset();
                body(writer);
                return writer.Segment.ToArray();
            }

            void WriteWorld(WorldWireWriter w, string session = Session, string target = Target, string mode = Mode, string world = WorldId,
                string transition = ModTransitions.SceneReplacement, string? scene = null, byte spawnFlag = 0, float[]? spawn = null)
            {
                w.WriteByte(SessionFollowWire.Version);
                w.WriteByte(SessionFollowWire.WorldType);
                w.WriteString(session);
                w.WriteString(target);
                w.WriteByte(0);
                w.WriteByte(0);
                w.WriteString(mode);
                w.WriteString(world);
                w.WriteByte(0);
                w.WriteString(transition);
                w.WriteByte(scene == null ? (byte)0 : (byte)1);
                if (scene == null) return;
                w.WriteString(scene);
                w.WriteByte(spawnFlag);
                foreach (var value in spawn ?? Array.Empty<float>()) w.WriteSingle(value);
            }

            var valid = Message(w => WriteWorld(w));
            That(SessionFollowWire.TryRead(reader, valid, out _), "the hand-written world reads");
            var unit = new[] { 1f, 2f, 3f, 0f, 0f, 0f, 1f, 1f, 1f, 1f };
            var hostile = new (string Name, byte[]? Payload)[]
            {
                ("null", null),
                ("empty", Array.Empty<byte>()),
                ("version only", new byte[] { SessionFollowWire.Version }),
                ("another version", new byte[] { 2, SessionFollowWire.MenuType }),
                ("an unknown type", new byte[] { SessionFollowWire.Version, 9 }),
                ("a menu with more", new byte[] { SessionFollowWire.Version, SessionFollowWire.MenuType, 0 }),
                ("cut short", valid.Take(valid.Length - 3).ToArray()),
                ("left over", valid.Concat(new byte[] { 0 }).ToArray()),
                ("too large", new byte[SessionFollowWire.MaximumMessageBytes + 1]),
                ("a short session id", Message(w => WriteWorld(w, session: "0123456789abcdef"))),
                ("an upper-case session id", Message(w => WriteWorld(w, session: "0123456789ABCDEF0123456789ABCDEF"))),
                ("a target that is not an id", Message(w => WriteWorld(w, target: "Not An Id"))),
                ("a gamemode that is not an id", Message(w => WriteWorld(w, mode: "../escape"))),
                ("an unknown transition", Message(w => WriteWorld(w, transition: "teleport"))),
                ("a level on scene replacement", Message(w => WriteWorld(w, scene: "Downtown"))),
                ("a level path", Message(w => WriteWorld(w, transition: ModTransitions.AdditiveArena, scene: "Assets/Levels/Downtown"))),
                ("a menu as the level", Message(w => WriteWorld(w, transition: ModTransitions.AdditiveArena, scene: GameScenes.MainMenuSceneName))),
                ("a control character", Message(w => WriteWorld(w, transition: ModTransitions.AdditiveArena, scene: "Down\ntown"))),
                ("a spawn flag of two", Message(w => WriteWorld(w, transition: ModTransitions.AdditiveArena, scene: "Downtown", spawnFlag: 2))),
                ("a spawn cut short", Message(w => WriteWorld(w, transition: ModTransitions.AdditiveArena, scene: "Downtown", spawnFlag: 1, spawn: unit.Take(9).ToArray()))),
                ("a spawn not a number", Message(w => WriteWorld(w, transition: ModTransitions.AdditiveArena, scene: "Downtown", spawnFlag: 1,
                    spawn: new[] { float.NaN, 0f, 0f, 0f, 0f, 0f, 1f, 1f, 1f, 1f }))),
                ("a spawn out of bounds", Message(w => WriteWorld(w, transition: ModTransitions.AdditiveArena, scene: "Downtown", spawnFlag: 1,
                    spawn: new[] { 2e6f, 0f, 0f, 0f, 0f, 0f, 1f, 1f, 1f, 1f }))),
                ("a zero rotation", Message(w => WriteWorld(w, transition: ModTransitions.AdditiveArena, scene: "Downtown", spawnFlag: 1,
                    spawn: new[] { 0f, 0f, 0f, 0f, 0f, 0f, 0f, 1f, 1f, 1f }))),
                ("a zero scale", Message(w => WriteWorld(w, transition: ModTransitions.AdditiveArena, scene: "Downtown", spawnFlag: 1,
                    spawn: new[] { 0f, 0f, 0f, 0f, 0f, 0f, 1f, 0f, 1f, 1f }))),
                ("a level that is a path", Message(w =>
                {
                    w.WriteByte(SessionFollowWire.Version);
                    w.WriteByte(SessionFollowWire.LevelType);
                    w.WriteString("..\\Downtown");
                })),
                ("a level name too long", Message(w =>
                {
                    w.WriteByte(SessionFollowWire.Version);
                    w.WriteByte(SessionFollowWire.LevelType);
                    w.WriteString(new string('a', SessionFollowWire.MaximumSceneNameLength + 1));
                })),
                ("a level with spaces around it", Message(w =>
                {
                    w.WriteByte(SessionFollowWire.Version);
                    w.WriteByte(SessionFollowWire.LevelType);
                    w.WriteString(" Downtown");
                }))
            };
            foreach (var (name, payload) in hostile)
                That(!SessionFollowWire.TryRead(reader, payload, out var state) && state == null, name + ": the message is refused whole");
            That(SessionFollowWire.TryRead(reader, Message(w => WriteWorld(w, transition: ModTransitions.AdditiveArena, scene: "Downtown", spawnFlag: 1, spawn: unit)), out var arena)
                && arena!.Spawn.HasValue, "and an arena within the bounds reads");
            Throws<ArgumentException>(() => HostWorldState.Level("Assets/Levels/Downtown"), "a level is named, never a path");
            Throws<ArgumentException>(() => HostWorldState.World("not-a-session", new LaunchRequest(Target), Mode, WorldId, null, ModTransitions.SceneReplacement, null, null),
                "a world needs the orchestrator's own session id");
        }
    }
}
