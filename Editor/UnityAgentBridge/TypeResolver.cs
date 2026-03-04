using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace UnityAgentBridge
{
    /// <summary>
    /// Utility class for finding types by name across all loaded assemblies.
    /// Particularly useful for finding Unity component types.
    /// </summary>
    public static class TypeResolver
    {
        private static Dictionary<string, Type> _typeCache = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        private static bool _cacheInitialized = false;

        /// <summary>
        /// Find a type by its name. Searches Unity assemblies first, then all loaded assemblies.
        /// </summary>
        /// <param name="typeName">The name of the type (e.g., "Rigidbody", "BoxCollider", "MyCustomScript")</param>
        /// <returns>The Type if found, null otherwise</returns>
        public static Type FindType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return null;

            // Check cache first
            if (_typeCache.TryGetValue(typeName, out Type cachedType))
                return cachedType;

            // Initialize cache if needed
            if (!_cacheInitialized)
                InitializeCache();

            // Try cache again after initialization
            if (_typeCache.TryGetValue(typeName, out cachedType))
                return cachedType;

            // Try to find the type directly
            Type foundType = FindTypeDirectly(typeName);
            if (foundType != null)
            {
                _typeCache[typeName] = foundType;
            }

            return foundType;
        }

        /// <summary>
        /// Find a component type specifically (must derive from Component).
        /// </summary>
        public static Type FindComponentType(string typeName)
        {
            var type = FindType(typeName);
            if (type != null && typeof(Component).IsAssignableFrom(type))
                return type;
            return null;
        }

        /// <summary>
        /// Find a ScriptableObject type specifically.
        /// </summary>
        public static Type FindScriptableObjectType(string typeName)
        {
            var type = FindType(typeName);
            if (type != null && typeof(ScriptableObject).IsAssignableFrom(type))
                return type;
            return null;
        }

        /// <summary>
        /// Get all available component types.
        /// </summary>
        public static IEnumerable<Type> GetAllComponentTypes()
        {
            if (!_cacheInitialized)
                InitializeCache();

            return _typeCache.Values
                .Where(t => typeof(Component).IsAssignableFrom(t))
                .Distinct()
                .OrderBy(t => t.Name);
        }

        /// <summary>
        /// Get all available ScriptableObject types.
        /// </summary>
        public static IEnumerable<Type> GetAllScriptableObjectTypes()
        {
            if (!_cacheInitialized)
                InitializeCache();

            return _typeCache.Values
                .Where(t => typeof(ScriptableObject).IsAssignableFrom(t) && !t.IsAbstract)
                .Distinct()
                .OrderBy(t => t.Name);
        }

        /// <summary>
        /// Search for types matching a pattern.
        /// </summary>
        public static IEnumerable<Type> SearchTypes(string pattern, bool componentsOnly = false)
        {
            if (!_cacheInitialized)
                InitializeCache();

            var query = _typeCache.Values.AsEnumerable();

            if (componentsOnly)
                query = query.Where(t => typeof(Component).IsAssignableFrom(t));

            if (!string.IsNullOrEmpty(pattern))
                query = query.Where(t => t.Name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0);

            return query.Distinct().OrderBy(t => t.Name);
        }

        /// <summary>
        /// Refresh the type cache. Call this after scripts are recompiled.
        /// </summary>
        public static void RefreshCache()
        {
            _typeCache.Clear();
            _cacheInitialized = false;
            InitializeCache();
        }

        private static void InitializeCache()
        {
            if (_cacheInitialized) return;

            // Priority assemblies to search
            var priorityAssemblyNames = new[]
            {
                "UnityEngine",
                "UnityEngine.CoreModule",
                "UnityEngine.PhysicsModule",
                "UnityEngine.Physics2DModule",
                "UnityEngine.UIModule",
                "UnityEngine.UI",
                "UnityEngine.AudioModule",
                "UnityEngine.AnimationModule",
                "UnityEngine.ParticleSystemModule",
                "UnityEngine.AIModule",
                "UnityEngine.TextRenderingModule",
                "Assembly-CSharp",
                "Assembly-CSharp-Editor",
            };

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            // Process priority assemblies first
            foreach (var assemblyName in priorityAssemblyNames)
            {
                var assembly = assemblies.FirstOrDefault(a => a.GetName().Name == assemblyName);
                if (assembly != null)
                {
                    CacheTypesFromAssembly(assembly);
                }
            }

            // Then process remaining assemblies
            foreach (var assembly in assemblies)
            {
                var name = assembly.GetName().Name;
                if (!priorityAssemblyNames.Contains(name) &&
                    (name.StartsWith("Unity") || name.StartsWith("Assembly-")))
                {
                    CacheTypesFromAssembly(assembly);
                }
            }

            _cacheInitialized = true;
        }

        private static void CacheTypesFromAssembly(Assembly assembly)
        {
            try
            {
                foreach (var type in assembly.GetTypes())
                {
                    if (type.IsPublic && !type.IsGenericTypeDefinition)
                    {
                        // Cache by simple name (first one wins)
                        if (!_typeCache.ContainsKey(type.Name))
                        {
                            _typeCache[type.Name] = type;
                        }

                        // Also cache by full name
                        if (!_typeCache.ContainsKey(type.FullName))
                        {
                            _typeCache[type.FullName] = type;
                        }
                    }
                }
            }
            catch (ReflectionTypeLoadException)
            {
                // Some assemblies can't be loaded, skip them
            }
            catch (Exception)
            {
                // Skip problematic assemblies
            }
        }

        private static Type FindTypeDirectly(string typeName)
        {
            // Try fully qualified name first
            var type = Type.GetType(typeName);
            if (type != null) return type;

            // Try common Unity namespaces
            var namespaces = new[]
            {
                "UnityEngine",
                "UnityEngine.UI",
                "UnityEngine.AI",
                "UnityEngine.Audio",
                "UnityEngine.Animations",
                "UnityEngine.Rendering",
                "UnityEngine.EventSystems",
                "UnityEditor",
            };

            foreach (var ns in namespaces)
            {
                type = Type.GetType($"{ns}.{typeName}, {ns}");
                if (type != null) return type;
            }

            // Search all assemblies
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    type = assembly.GetType(typeName);
                    if (type != null) return type;

                    // Try with namespaces
                    foreach (var ns in namespaces)
                    {
                        type = assembly.GetType($"{ns}.{typeName}");
                        if (type != null) return type;
                    }

                    // Search by simple name
                    type = assembly.GetTypes()
                        .FirstOrDefault(t => t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
                    if (type != null) return type;
                }
                catch
                {
                    // Skip problematic assemblies
                }
            }

            return null;
        }
    }
}
