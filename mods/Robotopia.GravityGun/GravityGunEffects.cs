using UnityEngine;

namespace Robotopia.GravityGun
{
    internal sealed class GravityGunEffects
    {
        private readonly GameObject root;
        private readonly ParticleSystem muzzleSystem;
        private readonly ParticleSystem targetSystem;
        private readonly Material? particleMaterial;
        private readonly Material? beamMaterial;
        private readonly LineRenderer holdBeam;
        private float muzzleAccumulator;
        private float targetAccumulator;
        private float beamPulse;

        public GravityGunEffects()
        {
            root = new GameObject("RobotopiaGravityGunEffects");
            Object.DontDestroyOnLoad(root);

            particleMaterial = CreateParticleMaterial();
            beamMaterial = CreateBeamMaterial();
            muzzleSystem = CreateSystem("MuzzleSparks", 0.24f, 0.045f, new Color(0.35f, 0.95f, 1f, 0.9f));
            targetSystem = CreateSystem("TargetSwirl", 0.32f, 0.06f, new Color(1f, 0.75f, 0.22f, 0.9f));
            holdBeam = CreateBeam();
        }

        public void UpdateHold(Camera camera, Vector3 targetPosition, float intensity, float deltaTime)
        {
            if (camera == null)
            {
                return;
            }

            var clampedIntensity = Mathf.Clamp(intensity, 0f, 5f);
            if (clampedIntensity <= 0f)
            {
                EndHold();
                return;
            }

            var muzzle = GetMuzzlePosition(camera);
            UpdateBeam(camera, muzzle, targetPosition, clampedIntensity, deltaTime);
            EmitMuzzle(camera, muzzle, clampedIntensity, deltaTime);
            EmitTarget(camera, targetPosition, clampedIntensity, deltaTime);
        }

        public void EndHold()
        {
            holdBeam.gameObject.SetActive(false);
        }

        public void Burst(Camera? camera, Vector3 position, Color color, int baseCount)
        {
            var count = Mathf.Clamp(baseCount, 1, 80);
            for (var i = 0; i < count; i++)
            {
                var random = Random.onUnitSphere;
                Emit(targetSystem, position, random * Random.Range(0.4f, 2.5f), 0.25f, Random.Range(0.04f, 0.11f), color);
            }

            if (camera != null)
            {
                var muzzle = GetMuzzlePosition(camera);
                for (var i = 0; i < Mathf.Max(2, count / 3); i++)
                {
                    Emit(muzzleSystem, muzzle, camera.transform.forward * Random.Range(0.6f, 2.6f) + Random.insideUnitSphere, 0.18f, 0.05f, color);
                }
            }
        }

        public void Dispose()
        {
            if (root != null)
            {
                Object.Destroy(root);
            }

            if (particleMaterial != null)
            {
                Object.Destroy(particleMaterial);
            }

            if (beamMaterial != null)
            {
                Object.Destroy(beamMaterial);
            }
        }

        private void EmitMuzzle(Camera camera, Vector3 muzzle, float intensity, float deltaTime)
        {
            muzzleAccumulator += 80f * intensity * deltaTime;
            var count = DrainAccumulator(ref muzzleAccumulator, 10);
            for (var i = 0; i < count; i++)
            {
                var velocity = camera.transform.forward * Random.Range(1f, 3f) + Random.insideUnitSphere * 0.65f;
                Emit(muzzleSystem, muzzle + Random.insideUnitSphere * 0.035f, velocity, 0.16f, Random.Range(0.025f, 0.055f), new Color(0.3f, 0.95f, 1f, 0.9f));
            }
        }

        private void UpdateBeam(Camera camera, Vector3 muzzle, Vector3 targetPosition, float intensity, float deltaTime)
        {
            var direction = targetPosition - muzzle;
            var distance = direction.magnitude;
            if (distance <= 0.05f)
            {
                EndHold();
                return;
            }

            holdBeam.gameObject.SetActive(true);
            beamPulse += Mathf.Clamp(deltaTime, 0.001f, 0.05f) * (12f + intensity * 4f);

            var right = camera.transform.right;
            var up = camera.transform.up;
            var wobble = Mathf.Min(0.16f, distance * 0.035f) * Mathf.Clamp01(intensity);
            const int segments = 7;
            holdBeam.positionCount = segments;
            for (var i = 0; i < segments; i++)
            {
                var t = i / (float)(segments - 1);
                var waveA = Mathf.Sin(beamPulse + t * Mathf.PI * 4f) * wobble;
                var waveB = Mathf.Cos(beamPulse * 0.72f + t * Mathf.PI * 3f) * wobble * 0.55f;
                var offset = i == 0 || i == segments - 1 ? Vector3.zero : right * waveA + up * waveB;
                holdBeam.SetPosition(i, Vector3.Lerp(muzzle, targetPosition, t) + offset);
            }

            var width = Mathf.Lerp(0.035f, 0.09f, Mathf.Clamp01(intensity / 2f));
            var pulseWidth = width * (1f + Mathf.Sin(beamPulse * 1.5f) * 0.18f);
            holdBeam.startWidth = pulseWidth;
            holdBeam.endWidth = pulseWidth * 0.72f;
            holdBeam.startColor = new Color(0.28f, 0.92f, 1f, 0.95f);
            holdBeam.endColor = new Color(1f, 0.72f, 0.2f, 0.9f);
        }

        private void EmitTarget(Camera camera, Vector3 targetPosition, float intensity, float deltaTime)
        {
            targetAccumulator += 90f * intensity * deltaTime;
            var count = DrainAccumulator(ref targetAccumulator, 14);
            var toCamera = (camera.transform.position - targetPosition).normalized;
            for (var i = 0; i < count; i++)
            {
                var tangent = Vector3.Cross(toCamera, Random.onUnitSphere).normalized;
                if (tangent == Vector3.zero)
                {
                    tangent = camera.transform.right;
                }

                var position = targetPosition + Random.onUnitSphere * Random.Range(0.12f, 0.42f);
                var velocity = tangent * Random.Range(0.7f, 2.2f) + toCamera * Random.Range(0.1f, 0.45f);
                Emit(targetSystem, position, velocity, 0.24f, Random.Range(0.035f, 0.075f), new Color(1f, 0.72f, 0.2f, 0.85f));
            }
        }

        private ParticleSystem CreateSystem(string name, float lifetime, float size, Color color)
        {
            var gameObject = new GameObject(name);
            gameObject.transform.SetParent(root.transform, worldPositionStays: false);
            var system = gameObject.AddComponent<ParticleSystem>();

            var main = system.main;
            main.loop = false;
            main.playOnAwake = false;
            main.startLifetime = lifetime;
            main.startSpeed = 0f;
            main.startSize = size;
            main.startColor = color;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 700;

            var emission = system.emission;
            emission.enabled = false;

            var shape = system.shape;
            shape.enabled = false;

            var renderer = system.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            if (particleMaterial != null)
            {
                renderer.material = particleMaterial;
            }

            system.Play();
            return system;
        }

        private LineRenderer CreateBeam()
        {
            var gameObject = new GameObject("HoldBeam");
            gameObject.transform.SetParent(root.transform, worldPositionStays: false);
            var renderer = gameObject.AddComponent<LineRenderer>();
            renderer.useWorldSpace = true;
            renderer.positionCount = 0;
            renderer.numCapVertices = 8;
            renderer.numCornerVertices = 4;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.startColor = new Color(0.28f, 0.92f, 1f, 0.95f);
            renderer.endColor = new Color(1f, 0.72f, 0.2f, 0.9f);
            renderer.startWidth = 0.05f;
            renderer.endWidth = 0.035f;
            if (beamMaterial != null)
            {
                renderer.material = beamMaterial;
            }

            gameObject.SetActive(false);
            return renderer;
        }

        private static void Emit(ParticleSystem system, Vector3 position, Vector3 velocity, float lifetime, float size, Color color)
        {
            var emit = new ParticleSystem.EmitParams
            {
                position = position,
                velocity = velocity,
                startLifetime = lifetime,
                startSize = size,
                startColor = color
            };
            system.Emit(emit, 1);
        }

        private static int DrainAccumulator(ref float accumulator, int maxPerFrame)
        {
            var count = Mathf.Min(Mathf.FloorToInt(accumulator), maxPerFrame);
            accumulator -= count;
            return count;
        }

        private static Vector3 GetMuzzlePosition(Camera camera)
        {
            var transform = camera.transform;
            return transform.position + transform.forward * 0.45f + transform.right * 0.22f - transform.up * 0.18f;
        }

        private static Material? CreateParticleMaterial()
        {
            var shader =
                Shader.Find("Sprites/Default") ??
                Shader.Find("Particles/Standard Unlit") ??
                Shader.Find("Legacy Shaders/Particles/Alpha Blended");

            return shader == null ? null : new Material(shader) { name = "RobotopiaGravityGunParticleMaterial" };
        }

        private static Material? CreateBeamMaterial()
        {
            var shader =
                Shader.Find("Sprites/Default") ??
                Shader.Find("Legacy Shaders/Particles/Alpha Blended") ??
                Shader.Find("Particles/Standard Unlit");

            return shader == null ? null : new Material(shader) { name = "RobotopiaGravityGunBeamMaterial" };
        }
    }
}
