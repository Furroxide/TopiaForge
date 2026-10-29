using System;
using System.Collections;
using System.Reflection;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.RobotKit
{
    // Metadata-bound, clean-room cosmetic composition. Never spawns an agent, changes health or starts native death.
    internal static class RobotAvatarAppearance
    {
        private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static readonly Type? Composer = Type.GetType("RobotBodyComposer, GameCode", false);
        private static readonly Type? PartType = Type.GetType("ComposerPartType, GameCode", false);
        private static readonly MethodInfo? IsMetal = Composer?.GetMethod("IsExteriorMetal", Members, null, new[] { typeof(Material) }, null);
        private static readonly MethodInfo? Parts = PartType == null ? null : Composer?.GetMethod("IterPartsFor", Members, null, new[] { PartType }, null);
        private static readonly MethodInfo? Select = PartType == null ? null : Composer?.GetMethod("SelectPartPrefab", Members, null, new[] { PartType, typeof(GameObject) }, null);
        private static readonly MethodInfo? Build = Composer?.GetMethod("BuildBodyFromParts", Members, null, Type.EmptyTypes, null);
        private static readonly PropertyInfo? None = Composer?.GetProperty("NoneOption", Members);
        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int Emissive = Shader.PropertyToID("_EmissiveColor");

        internal static void Apply(GameObject clone, AvatarAppearance appearance)
        {
            var composer = Composer == null ? null : clone.GetComponentInChildren(Composer, true);
            if (appearance.Accessory != null) ApplyAccessory(composer, appearance);
            if (IsMetal == null || !IsMetal.IsStatic && composer == null)
                throw new InvalidOperationException("The avatar material binding is unavailable.");
            var renderers = clone.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length > 256) throw new InvalidOperationException("The cosmetic body has too many renderers.");
            var block = new MaterialPropertyBlock();
            var arguments = new object[1];
            for (var r = 0; r < renderers.Length; r++)
            {
                var renderer = renderers[r];
                if (renderer == null) continue;
                var materials = renderer.sharedMaterials;
                if (materials.Length > 32) throw new InvalidOperationException("The cosmetic body has too many material slots.");
                for (var slot = 0; slot < materials.Length; slot++)
                {
                    if (materials[slot] == null) continue;
                    arguments[0] = materials[slot];
                    var metal = (bool)IsMetal.Invoke(IsMetal.IsStatic ? null : composer, arguments);
                    var tint = metal ? appearance.Metal : appearance.Plastic;
                    var color = new Color(tint.Red, tint.Green, tint.Blue, 1f);
                    block.Clear(); renderer.GetPropertyBlock(block, slot);
                    block.SetColor(BaseColor, color); block.SetColor(ColorId, color);
                    block.SetColor(Emissive, color * 0.35f);
                    renderer.SetPropertyBlock(block, slot);
                }
            }
        }

        private static void ApplyAccessory(Component? composer, AvatarAppearance appearance)
        {
            if (composer == null || PartType == null || Parts == null || Select == null || Build == null || None == null)
                throw new InvalidOperationException("The avatar accessory binding is unavailable.");
            var slot = Enum.Parse(PartType, RobotAvatarAccessorySlots.NativeName(appearance.AccessorySlot), false);
            GameObject? selected = null;
            if (appearance.Accessory!.Length == 0) selected = None.GetValue(null) as GameObject;
            else
            {
                var candidates = Parts.Invoke(composer, new[] { slot }) as IEnumerable;
                if (candidates == null) throw new InvalidOperationException("The avatar accessory catalog is unavailable.");
                var count = 0;
                foreach (var item in candidates)
                {
                    if (++count > 256) throw new InvalidOperationException("The avatar accessory catalog is too large.");
                    if (item is not GameObject prefab || prefab == null || prefab.name != appearance.Accessory) continue;
                    if (selected != null) throw new InvalidOperationException("The avatar accessory name is ambiguous.");
                    selected = prefab;
                }
            }
            if (selected == null) throw new InvalidOperationException("The avatar accessory is unavailable.");
            Select.Invoke(composer, new object[] { slot, selected });
            Build.Invoke(composer, null);
        }
    }
}
