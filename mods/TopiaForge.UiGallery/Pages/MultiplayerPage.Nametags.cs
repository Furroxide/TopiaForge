using System;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.UiGallery.Pages
{
    internal static partial class MultiplayerPage
    {
        private const float OrbitRadius = 6f;
        private const float OrbitSpeed = 0.6f;

        private static TopiaForgeHudLayer? tagHud;
        private static TopiaForgeNametagLayer? tags;
        private static TopiaForgeWorldPointAnchor? orbitAnchor;
        private static TopiaForgeNametag? orbitTag;
        private static Vec3 orbitCenter;
        private static float orbitAngle;
        private static float orbitTalkTimer;

        private static void BuildNametags(TopiaForgeContainer page, ILocalPlayerService player)
        {
            page.SectionHeader("NAME TAGS");
            page.Label(
                "Spawns persistent tags at world points around the camera: near and mid range, one fading at 33 m, and one orbiting you. "
                + "Three stay hidden by design: beyond the 40 m range, behind you, and inside the near limit. Close the gallery to see them over gameplay.",
                TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);
            var status = page.Label(tags == null ? "No tags spawned." : "Tags are spawned.", TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);
            var row = page.Row(TopiaForgeGap.Sm);
            row.Button("SPAWN AROUND CAMERA", () => status.SetText(SpawnNametags(page.Host, player)), TopiaForgeButtonStyle.Outline);
            row.Button("DESPAWN", () =>
            {
                DestroyNametags();
                status.SetText("No tags spawned.");
            }, TopiaForgeButtonStyle.Ghost);
        }

        private static string SpawnNametags(UiHost host, ILocalPlayerService player)
        {
            if (!player.TryGetSnapshot(out var snapshot) || snapshot == null)
            {
                return "No camera is available yet; load a world first.";
            }

            DestroyNametags();
            var origin = snapshot.AimRay.Origin;
            var forward = new Vec3(snapshot.AimRay.Direction.X, 0f, snapshot.AimRay.Direction.Z);
            forward = forward.LengthSquared > 0.0001f ? forward.Normalized : new Vec3(0f, 0f, 1f);
            var right = Vec3.Cross(new Vec3(0f, 1f, 0f), forward);
            var height = origin.Y - 0.45f;

            Vec3 At(float ahead, float side)
            {
                return new Vec3(
                    origin.X + (forward.X * ahead) + (right.X * side),
                    height,
                    origin.Z + (forward.Z * ahead) + (right.Z * side));
            }

            tagHud = host.HudLayer("gallery-nametags");
            tags = tagHud.Nametags(12);

            var ada = Track(At(4f, -1.4f), "Ada");
            ada?.SetBadge("HOST");
            ada?.SetTalkState(TopiaForgeTalkState.Talking);
            Track(At(9f, 1.6f), "Bo")?.SetTalkState(TopiaForgeTalkState.MutedByHost);
            var cy = Track(At(18f, -3f), "Cy");
            cy?.SetSecondary("Reconnecting...");
            cy?.SetTone(TopiaForgeTone.Muted);
            Track(At(33f, 2f), "Di (fading)");
            Track(At(12f, 5f), "Professor Vex Thunderbolt the Unreasonably Long");
            Track(At(7f, -5f), "<size=300>Mal</size>")?.SetSecondary("markup stays literal");
            Track(At(48f, 0f), "Ed (beyond range)");
            Track(At(-5f, 0f), "Fi (behind you)");
            Track(At(0.4f, 0f), "Gil (too close)");

            orbitCenter = new Vec3(origin.X, height, origin.Z);
            orbitAngle = 0f;
            orbitAnchor = new TopiaForgeWorldPointAnchor(OrbitPoint());
            orbitTag = tags.Track(orbitAnchor, "Orbiter");
            orbitTag?.SetSecondary("moving anchor");
            return "Spawned " + tags.Count + " tags; Ed, Fi and Gil stay hidden until you move.";
        }

        private static TopiaForgeNametag? Track(Vec3 position, string name)
        {
            return tags?.Track(new TopiaForgeWorldPointAnchor(position), name);
        }

        private static void TickNametags(float deltaTime)
        {
            if (orbitAnchor == null || orbitTag == null || !orbitTag.IsTracked)
            {
                return;
            }

            orbitAngle += OrbitSpeed * deltaTime;
            if (orbitAngle > Math.PI * 2.0)
            {
                orbitAngle -= (float)(Math.PI * 2.0);
            }

            orbitAnchor.Position = OrbitPoint();
            orbitTalkTimer += deltaTime;
            if (orbitTalkTimer >= 3f)
            {
                orbitTalkTimer = 0f;
            }

            orbitTag.SetTalkState(orbitTalkTimer < 1.5f ? TopiaForgeTalkState.Talking : TopiaForgeTalkState.Idle);
        }

        private static Vec3 OrbitPoint()
        {
            return new Vec3(
                orbitCenter.X + ((float)Math.Cos(orbitAngle) * OrbitRadius),
                orbitCenter.Y,
                orbitCenter.Z + ((float)Math.Sin(orbitAngle) * OrbitRadius));
        }

        private static void DestroyNametags()
        {
            tagHud?.Destroy();
            tagHud = null;
            tags = null;
            orbitAnchor = null;
            orbitTag = null;
        }
    }
}
