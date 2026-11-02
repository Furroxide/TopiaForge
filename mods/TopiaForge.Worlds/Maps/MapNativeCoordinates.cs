using System;
using System.Reflection;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using UnityEngine;

namespace TopiaForge.Worlds.Maps
{
    // All coordinate conversion uses native value helpers, behind one adapter for the handedness/root-transform pin.
    internal sealed class MapNativeCoordinates
    {
        private readonly Transform root;
        private readonly Type vectorType;
        private readonly Type rotationType;
        private readonly MethodInfo convertPosition;
        private readonly MethodInfo convertRotation;
        internal MapNativeCoordinates(Transform root)
        {
            this.root = root;
            vectorType = Type.GetType("UgcVector3Value, GameCode", false) ?? throw Unavailable();
            rotationType = Type.GetType("UgcRotationValue, GameCode", false) ?? throw Unavailable();
            var helper = Type.GetType("UgcRotationHelper, GameCode", false) ?? throw Unavailable();
            convertPosition = vectorType.GetMethod("ToUnityPosition", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null) ?? throw Unavailable();
            convertRotation = helper.GetMethod("ToUnityQuaternion", BindingFlags.Public | BindingFlags.Static, null, new[] { rotationType }, null) ?? throw Unavailable();
            if (convertPosition.ReturnType != typeof(Vector3) || convertRotation.ReturnType != typeof(Quaternion)) throw Unavailable();
        }
        internal Vec3 Position(Vec3 editor)
        {
            if (root == null) throw Unavailable();
            var value = VectorValue(vectorType, editor);
            var converted = root.TransformPoint((Vector3)convertPosition.Invoke(value, null)!);
            return new Vec3(converted.x, converted.y, converted.z);
        }
        internal TransformState Transform(MapTransform editor)
        {
            if (root == null) throw Unavailable();
            var local = LocalTransform(editor);
            var rotation = root.rotation * new Quaternion(local.Rotation.X, local.Rotation.Y, local.Rotation.Z, local.Rotation.W);
            var scale = root.lossyScale;
            return new TransformState(Position(editor.Position), new Quat(rotation.x, rotation.y, rotation.z, rotation.w),
                new Vec3(scale.x * editor.Scale.X, scale.y * editor.Scale.Y, scale.z * editor.Scale.Z));
        }
        internal IReadOnlyDictionary<string, AuthoredAffine> AuthoredTransforms(AuthoredScene scene)
        {
            if (root == null) throw Unavailable();
            var rotation = root.rotation;
            var affine = new AuthoredAffine(Value(root.position), Value(root.TransformVector(Vector3.right)),
                Value(root.TransformVector(Vector3.up)), Value(root.TransformVector(Vector3.forward)),
                new Quat(rotation.x, rotation.y, rotation.z, rotation.w));
            return AuthoredAffine.Compose(scene.Records, LocalTransform, affine);
        }
        private AuthoredAffine LocalTransform(MapTransform editor)
        {
            var quaternion = editor.QuaternionRotation;
            if (quaternion.HasValue) quaternion = AuthoredAffine.NormalizeRotation(quaternion.Value);
            var value = VectorValue(rotationType, quaternion.HasValue
                ? new Vec3(quaternion.Value.X, quaternion.Value.Y, quaternion.Value.Z) : editor.EulerDegrees!.Value);
            var w = rotationType.GetField("w", BindingFlags.Public | BindingFlags.Instance);
            if (w?.FieldType != typeof(float?)) throw Unavailable();
            w.SetValue(value, quaternion.HasValue ? (object)quaternion.Value.W : null);
            var rotation = (Quaternion)convertRotation.Invoke(null, new[] { value })!;
            var position = (Vector3)convertPosition.Invoke(VectorValue(vectorType, editor.Position), null)!;
            return AuthoredAffine.From(Value(position), new Quat(rotation.x, rotation.y, rotation.z, rotation.w), editor.Scale);
        }
        private static Vec3 Value(Vector3 value) => new Vec3(value.x, value.y, value.z);
        private static object VectorValue(Type type, Vec3 vector)
        {
            var value = Activator.CreateInstance(type) ?? throw Unavailable();
            Set(type, value, "x", vector.X); Set(type, value, "y", vector.Y); Set(type, value, "z", vector.Z);
            return value;
        }
        private static void Set(Type type, object instance, string name, float value)
        {
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field?.FieldType != typeof(float)) throw Unavailable();
            field.SetValue(instance, value);
        }
        private static MapProblem Unavailable() => new MapProblem(ModErrorCode.Unavailable,
            "This game build cannot convert authored map coordinates yet. Update TopiaForge.");
    }
}
