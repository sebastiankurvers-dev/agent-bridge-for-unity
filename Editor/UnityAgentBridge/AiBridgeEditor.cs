using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge
{
    /// <summary>
    /// Attribute to register a custom AI-facing editor for a component type.
    /// Discovered via TypeCache at startup — no manual registration needed.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class AiBridgeEditorAttribute : Attribute
    {
        public Type TargetType { get; }
        public AiBridgeEditorAttribute(Type targetType) => TargetType = targetType;
    }

    /// <summary>
    /// Describes a single AI-visible property.
    /// </summary>
    public struct AiPropertyDef
    {
        public string name;
        public string type;
        public string description;
        public bool readOnly;
    }

    /// <summary>
    /// Base class for AI-facing component editors. Subclass this and add
    /// [AiBridgeEditor(typeof(YourComponent))] to register.
    ///
    /// Inspired by UTCP Code Mode's AiAgentEditor pattern — components declare
    /// how AI sees them instead of relying on raw SerializedProperty iteration.
    /// </summary>
    public abstract class AiBridgeEditor
    {
        protected SerializedObject serializedObject;
        protected Component target;

        private readonly Dictionary<string, Func<object>> _getters = new();
        private readonly Dictionary<string, Action<object>> _setters = new();
        private readonly List<AiPropertyDef> _definitions = new();

        /// <summary>
        /// Called by registry after construction. Binds to the target component.
        /// </summary>
        internal void Bind(Component component)
        {
            target = component;
            serializedObject = new SerializedObject(component);
            OnEnable();
        }

        /// <summary>
        /// Override to bind serialized properties and register handlers via AddProperty.
        /// </summary>
        protected abstract void OnEnable();

        /// <summary>
        /// Register a named property with get/set handlers.
        /// </summary>
        protected void AddProperty(string name, string typeName, string description,
            Func<object> getter, Action<object> setter = null)
        {
            _getters[name] = getter;
            if (setter != null) _setters[name] = setter;
            _definitions.Add(new AiPropertyDef
            {
                name = name,
                type = typeName,
                description = description,
                readOnly = setter == null
            });
        }

        /// <summary>
        /// Returns an AI-readable schema of all properties.
        /// </summary>
        public List<Dictionary<string, object>> GetDefinition()
        {
            return _definitions.Select(d => new Dictionary<string, object>
            {
                { "name", d.name },
                { "type", d.type },
                { "description", d.description },
                { "readOnly", d.readOnly }
            }).ToList();
        }

        /// <summary>
        /// Dumps current state of all properties as a clean dictionary.
        /// </summary>
        public Dictionary<string, object> Dump()
        {
            serializedObject.Update();
            var result = new Dictionary<string, object>();
            foreach (var kvp in _getters)
            {
                try { result[kvp.Key] = kvp.Value(); }
                catch (Exception ex) { result[kvp.Key] = $"<error: {ex.Message}>"; }
            }
            return result;
        }

        /// <summary>
        /// Apply property changes from AI. Returns applied/failed lists.
        /// </summary>
        public (List<string> applied, List<string> errors) Apply(Dictionary<string, object> values)
        {
            serializedObject.Update();
            Undo.RecordObject(target, "AI Bridge Editor Apply");
            var applied = new List<string>();
            var errors = new List<string>();

            foreach (var kvp in values)
            {
                if (!_setters.TryGetValue(kvp.Key, out var setter))
                {
                    errors.Add(kvp.Key + ": unknown or read-only property");
                    continue;
                }
                try
                {
                    setter(kvp.Value);
                    applied.Add(kvp.Key);
                }
                catch (Exception ex)
                {
                    errors.Add(kvp.Key + ": " + ex.Message);
                }
            }

            serializedObject.ApplyModifiedProperties();
            EditorUtility.SetDirty(target);
            return (applied, errors);
        }

        // ─── Helpers for common property types ───

        protected static Vector3 ParseVector3(object value)
        {
            if (value is Dictionary<string, object> dict)
            {
                return new Vector3(
                    Convert.ToSingle(dict.GetValueOrDefault("x", 0f)),
                    Convert.ToSingle(dict.GetValueOrDefault("y", 0f)),
                    Convert.ToSingle(dict.GetValueOrDefault("z", 0f)));
            }
            if (value is IList<object> list && list.Count >= 3)
            {
                return new Vector3(
                    Convert.ToSingle(list[0]),
                    Convert.ToSingle(list[1]),
                    Convert.ToSingle(list[2]));
            }
            throw new ArgumentException($"Cannot parse Vector3 from {value?.GetType().Name ?? "null"}");
        }

        protected static Dictionary<string, object> SerializeVector3(Vector3 v)
        {
            return new Dictionary<string, object> { { "x", v.x }, { "y", v.y }, { "z", v.z } };
        }

        protected static Vector4 ParseVector4(object value)
        {
            if (value is Dictionary<string, object> dict)
            {
                return new Vector4(
                    Convert.ToSingle(dict.GetValueOrDefault("x", 0f)),
                    Convert.ToSingle(dict.GetValueOrDefault("y", 0f)),
                    Convert.ToSingle(dict.GetValueOrDefault("z", 0f)),
                    Convert.ToSingle(dict.GetValueOrDefault("w", 0f)));
            }
            throw new ArgumentException($"Cannot parse Vector4 from {value?.GetType().Name ?? "null"}");
        }

        protected static Dictionary<string, object> SerializeVector4(Vector4 v)
        {
            return new Dictionary<string, object> { { "x", v.x }, { "y", v.y }, { "z", v.z }, { "w", v.w } };
        }

        protected static Color ParseColor(object value)
        {
            if (value is Dictionary<string, object> dict)
            {
                return new Color(
                    Convert.ToSingle(dict.GetValueOrDefault("r", 0f)),
                    Convert.ToSingle(dict.GetValueOrDefault("g", 0f)),
                    Convert.ToSingle(dict.GetValueOrDefault("b", 0f)),
                    Convert.ToSingle(dict.GetValueOrDefault("a", 1f)));
            }
            if (value is string hex)
            {
                if (ColorUtility.TryParseHtmlString(hex, out Color c)) return c;
            }
            throw new ArgumentException($"Cannot parse Color from {value?.GetType().Name ?? "null"}");
        }

        protected static Dictionary<string, object> SerializeColor(Color c)
        {
            return new Dictionary<string, object> { { "r", c.r }, { "g", c.g }, { "b", c.b }, { "a", c.a } };
        }

        protected static float ParseFloat(object value) => Convert.ToSingle(value);
        protected static int ParseInt(object value) => Convert.ToInt32(value);
        protected static bool ParseBool(object value) => Convert.ToBoolean(value);
    }

    /// <summary>
    /// Registry that discovers and creates AiBridgeEditor instances.
    /// Uses TypeCache for zero-cost startup discovery.
    /// </summary>
    public static class AiBridgeEditorRegistry
    {
        private static Dictionary<Type, Type> _editors;

        static AiBridgeEditorRegistry()
        {
            Rebuild();
        }

        public static void Rebuild()
        {
            _editors = new Dictionary<Type, Type>();
            foreach (var editorType in TypeCache.GetTypesWithAttribute<AiBridgeEditorAttribute>())
            {
                var attr = (AiBridgeEditorAttribute)Attribute.GetCustomAttribute(
                    editorType, typeof(AiBridgeEditorAttribute));
                if (attr != null)
                    _editors[attr.TargetType] = editorType;
            }
        }

        /// <summary>
        /// Returns true if a custom AI editor exists for this component type (or a base type).
        /// </summary>
        public static bool HasEditor(Type componentType)
        {
            return FindEditorType(componentType) != null;
        }

        /// <summary>
        /// Creates and binds an editor for the given component. Returns null if none registered.
        /// </summary>
        public static AiBridgeEditor CreateEditor(Component component)
        {
            var editorType = FindEditorType(component.GetType());
            if (editorType == null) return null;

            var editor = (AiBridgeEditor)Activator.CreateInstance(editorType);
            editor.Bind(component);
            return editor;
        }

        /// <summary>
        /// Lists all registered editor mappings.
        /// </summary>
        public static Dictionary<string, string> GetRegisteredEditors()
        {
            var result = new Dictionary<string, string>();
            foreach (var kvp in _editors)
                result[kvp.Key.Name] = kvp.Value.Name;
            return result;
        }

        private static Type FindEditorType(Type componentType)
        {
            // Exact match first
            if (_editors.TryGetValue(componentType, out var editorType))
                return editorType;

            // Walk up inheritance chain
            var baseType = componentType.BaseType;
            while (baseType != null && baseType != typeof(Component) && baseType != typeof(object))
            {
                if (_editors.TryGetValue(baseType, out editorType))
                    return editorType;
                baseType = baseType.BaseType;
            }

            return null;
        }
    }
}
