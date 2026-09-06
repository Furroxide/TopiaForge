using System;
using System.Collections.Generic;
using System.Reflection;

namespace TopiaForge.Worlds
{
    /// <summary>Captures the exact native host and shared config before any selection mutation.</summary>
    internal sealed class NativeImportSelection
    {
        private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly List<(object Target, FieldInfo Field, object? Value)> fields = new List<(object, FieldInfo, object?)>();
        private readonly List<(object Target, PropertyInfo Property, object? Value, string Next)> properties = new List<(object, PropertyInfo, object?, string)>();
        internal NativeImportSelection(object host, Type type, RoboWorldImportPlan plan)
        {
            foreach (var name in new[] { "importFolderOverride", "selectedExportFilePath", "selectedSceneId", "runtimeImportFolderOverride" })
            {
                var field = type.GetField(name, Instance);
                if (field == null || field.FieldType != typeof(string)) throw new MissingFieldException(type.FullName, name);
                fields.Add((host, field, field.GetValue(host)));
            }
            var configField = type.GetField("config", Instance) ?? throw new MissingFieldException(type.FullName, "config");
            var config = configField.GetValue(host);
            if (config == null) return;
            foreach (var item in new[] { ("ImportFolderOverride", plan.FolderPath), ("SelectedExportFilePath", plan.FilePath), ("SelectedSceneId", string.Empty) })
            {
                var property = config.GetType().GetProperty(item.Item1, Instance);
                if (property == null || property.PropertyType != typeof(string) || property.GetMethod == null || property.SetMethod == null)
                    throw new MissingMemberException(config.GetType().FullName, item.Item1);
                properties.Add((config, property, property.GetValue(config), item.Item2));
            }
        }
        internal void Apply() { foreach (var item in properties) item.Property.SetValue(item.Target, item.Next); }
        internal void Restore()
        {
            var failures = new List<Exception>();
            foreach (var item in fields) try { item.Field.SetValue(item.Target, item.Value); } catch (Exception error) { failures.Add(error); }
            foreach (var item in properties) try { item.Property.SetValue(item.Target, item.Value); } catch (Exception error) { failures.Add(error); }
            if (failures.Count > 0) throw new AggregateException("Native import selection could not be restored.", failures);
        }
    }
}
