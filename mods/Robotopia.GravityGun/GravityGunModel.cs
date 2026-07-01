using System.Collections.Generic;
using UnityEngine;

namespace Robotopia.GravityGun
{
    internal sealed class GravityGunModel
    {
        private readonly GameObject root;
        private readonly List<Renderer> glowRenderers = new List<Renderer>();
        private readonly Material bodyMaterial;
        private readonly Material metalMaterial;
        private readonly Material glowMaterial;
        private Transform? parentCamera;
        private float pulse;

        public GravityGunModel()
        {
            bodyMaterial = CreateMaterial("RobotopiaGravityGunBody", new Color(0.055f, 0.06f, 0.065f, 1f), false);
            metalMaterial = CreateMaterial("RobotopiaGravityGunMetal", new Color(0.24f, 0.27f, 0.29f, 1f), false);
            glowMaterial = CreateMaterial("RobotopiaGravityGunGlow", new Color(0.1f, 0.82f, 1f, 1f), true);

            root = new GameObject("RobotopiaGravityGunModel");
            Object.DontDestroyOnLoad(root);
            root.SetActive(false);

            CreatePart("Body", PrimitiveType.Cube, new Vector3(0f, 0f, 0f), new Vector3(0.22f, 0.16f, 0.34f), Quaternion.identity, bodyMaterial);
            CreatePart("RearBlock", PrimitiveType.Cube, new Vector3(0f, 0.015f, -0.22f), new Vector3(0.27f, 0.14f, 0.18f), Quaternion.identity, bodyMaterial);
            CreatePart("Grip", PrimitiveType.Cube, new Vector3(0.035f, -0.2f, -0.11f), new Vector3(0.11f, 0.32f, 0.11f), Quaternion.Euler(12f, 0f, -8f), bodyMaterial);
            CreatePart("Barrel", PrimitiveType.Cylinder, new Vector3(0f, 0.025f, 0.3f), new Vector3(0.055f, 0.38f, 0.055f), Quaternion.Euler(90f, 0f, 0f), metalMaterial);
            CreatePart("Muzzle", PrimitiveType.Cylinder, new Vector3(0f, 0.025f, 0.7f), new Vector3(0.09f, 0.075f, 0.09f), Quaternion.Euler(90f, 0f, 0f), metalMaterial);
            CreatePart("Core", PrimitiveType.Sphere, new Vector3(0f, 0.035f, 0.13f), new Vector3(0.13f, 0.13f, 0.13f), Quaternion.identity, glowMaterial, true);

            for (var i = 0; i < 6; i++)
            {
                var angle = i * Mathf.PI * 2f / 6f;
                var position = new Vector3(Mathf.Cos(angle) * 0.095f, 0.025f + Mathf.Sin(angle) * 0.095f, 0.42f);
                CreatePart("CoilNode" + i, PrimitiveType.Sphere, position, new Vector3(0.045f, 0.045f, 0.045f), Quaternion.identity, glowMaterial, true);
            }
        }

        public void Update(Camera? camera, bool visible, bool active, float deltaTime)
        {
            if (root == null)
            {
                return;
            }

            if (camera == null || !visible)
            {
                root.SetActive(false);
                return;
            }

            root.SetActive(true);
            if (parentCamera != camera.transform)
            {
                parentCamera = camera.transform;
                root.transform.SetParent(parentCamera, worldPositionStays: false);
            }

            root.transform.localPosition = new Vector3(0.34f, -0.31f, 0.58f);
            root.transform.localRotation = Quaternion.Euler(-4f, -8f, 1f);

            pulse += Mathf.Clamp(deltaTime, 0.001f, 0.05f) * (active ? 10f : 3f);
            var glow = active ? 1.45f + Mathf.Sin(pulse) * 0.35f : 0.55f + Mathf.Sin(pulse) * 0.08f;
            SetGlow(new Color(0.08f, 0.72f, 1f, 1f) * glow);
        }

        public void Dispose()
        {
            if (root != null)
            {
                Object.Destroy(root);
            }

            Object.Destroy(bodyMaterial);
            Object.Destroy(metalMaterial);
            Object.Destroy(glowMaterial);
        }

        private void CreatePart(
            string name,
            PrimitiveType primitive,
            Vector3 localPosition,
            Vector3 localScale,
            Quaternion localRotation,
            Material material,
            bool isGlow = false)
        {
            var part = GameObject.CreatePrimitive(primitive);
            part.name = name;
            part.transform.SetParent(root.transform, worldPositionStays: false);
            part.transform.localPosition = localPosition;
            part.transform.localScale = localScale;
            part.transform.localRotation = localRotation;

            var collider = part.GetComponent<Collider>();
            if (collider != null)
            {
                Object.Destroy(collider);
            }

            var renderer = part.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material = material;
                if (isGlow)
                {
                    glowRenderers.Add(renderer);
                }
            }
        }

        private void SetGlow(Color color)
        {
            glowMaterial.color = color;
            glowMaterial.SetColor("_EmissionColor", color);
            for (var i = 0; i < glowRenderers.Count; i++)
            {
                if (glowRenderers[i] != null)
                {
                    glowRenderers[i].material.color = color;
                }
            }
        }

        private static Material CreateMaterial(string name, Color color, bool emission)
        {
            var shader =
                Shader.Find("HDRP/Lit") ??
                Shader.Find("Standard") ??
                Shader.Find("Universal Render Pipeline/Lit") ??
                Shader.Find("Sprites/Default");
            var material = new Material(shader) { name = name, color = color };
            if (emission)
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", color);
            }

            return material;
        }
    }
}
