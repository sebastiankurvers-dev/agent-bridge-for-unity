using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Profiling;
using Unity.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Audio;
using TMPro;

namespace UnityAgentBridge
{
    public static partial class UnityCommands
    {
        #region Data Classes

        [Serializable]
        public class HierarchyResponse
        {
            public string sceneName;
            public string scenePath;
            public List<HierarchyNode> rootObjects;
        }

        [Serializable]
        public class HierarchyNode
        {
            public string name;
            public int instanceId;
            public bool active;
            public string layer;
            public string tag;
            public List<string> components;
            public List<HierarchyNode> children;
            public int childCount;
        }

        [Serializable]
        public class GameObjectDetails
        {
            public string name;
            public int instanceId;
            public bool active;
            public bool activeInHierarchy;
            public bool isStatic;
            public int layer;
            public string layerName;
            public string tag;
            public TransformData transform;
            public List<ComponentInfo> components;
        }

        [Serializable]
        public class TransformData
        {
            public Vector3Data position;
            public Vector3Data localPosition;
            public Vector3Data rotation;
            public Vector3Data localRotation;
            public Vector3Data localScale;
        }

        [Serializable]
        public class Vector3Data
        {
            public float x, y, z;

            public Vector3Data() { }

            public Vector3Data(Vector3 v)
            {
                x = v.x;
                y = v.y;
                z = v.z;
            }

            public Vector3 ToVector3() => new Vector3(x, y, z);
        }

        [Serializable]
        public class ComponentInfo
        {
            public string type;
            public string fullType;
            public bool enabled;
            public List<PropertyInfo> properties;
        }

        [Serializable]
        public class PropertyInfo
        {
            public string name;
            public string type;
            public string value;
            public string path;
            public bool isArray;
            public int arraySize;
            // SerializeReference fields
            public bool isManagedReference;
            public string managedReferenceTypeName;
            public string managedReferenceAssembly;
            public List<PropertyInfo> nestedProperties;
        }

        [Serializable]
        public class ModifyRequest
        {
            public string name;
            public int active = -1;  // -1 = not set, 0 = false, 1 = true
            public int layer = -1;   // -1 = not set
            public string tag;
            public ModifyTransform transform;
        }

        [Serializable]
        public class ModifyTransform
        {
            public Vector3Data position;
            public Vector3Data localPosition;
            public Vector3Data rotation;
            public Vector3Data localRotation;
            public Vector3Data localScale;
        }

        [Serializable]
        public class SpawnRequest
        {
            public string prefabPath;
            public string name;
            public Vector3Data position;
            public Vector3Data rotation;
            public Vector3Data scale;
            public int parentId = -1;  // -1 = not set
        }

        [Serializable]
        public class SpawnBatchRequest
        {
            public SpawnBatchEntry[] entries;
        }

        [Serializable]
        public class SpawnBatchEntry
        {
            public string prefabPath;
            public string name;
            public Vector3Data position;
            public Vector3Data rotation;
            public Vector3Data scale;
            public int parentId = -1;  // -1 = not set
        }

        [Serializable]
        public class LogEntry
        {
            public string message;
            public string stackTrace;
            public string type;
            public string timestamp;
        }

        [Serializable]
        public class LogResponse
        {
            public List<LogEntry> logs;
        }

        [Serializable]
        public class PlayModeRequest
        {
            public string action;
        }

        [Serializable]
        public class SceneInfo
        {
            public string name;
            public string path;
            public int buildIndex;
            public bool isLoaded;
            public bool isDirty;
        }

        [Serializable]
        public class LoadSceneRequest
        {
            public string scenePath;
            public string sceneName;
            public bool saveCurrentScene;
        }

        [Serializable]
        public class ExecuteRequest
        {
            public string code;
        }

        [Serializable]
        public class RegisterHelpersRequest
        {
            public string name;
            public string code;
        }

        [Serializable]
        public class ClearHelpersRequest
        {
            public string name;
        }

        [Serializable]
        public class PrefabInfo
        {
            public string name;
            public string path;
        }

        [Serializable]
        public class ComponentDetails
        {
            public string type;
            public string fullType;
            public List<FieldData> fields;
        }

        [Serializable]
        public class FieldData
        {
            public string name;
            public string type;
            public string value;
            public bool isProperty;
        }

        // New data classes for extended functionality

        [Serializable]
        public class ScriptRequest
        {
            public string path;
            public string content;
            public string className;
            public string namespaceName;
            public string baseClass;
        }

        [Serializable]
        public class ScriptResponse
        {
            public bool success;
            public string path;
            public string content;
            public int lineCount;
            public string className;
            public string namespaceName;
            public string baseClass;
        }

        [Serializable]
        public class ComponentRequest
        {
            public int instanceId;
            public string componentType;
            public string properties;
        }

        [Serializable]
        public class SerializedPropertyPatchRequest
        {
            public int instanceId;
            public string componentType;
            public SerializedPropertyPatch[] patches;
        }

        [Serializable]
        public class SerializedPropertyPatch
        {
            public string propertyPath;
            public string valueJson;
            public string value; // alias for valueJson — accepts raw JSON value
            public string objectRefAssetPath;
            public int objectRefInstanceId;
        }

        [Serializable]
        public class RendererMaterialsRequest
        {
            public int instanceId;
            public string componentType; // Optional, defaults to first Renderer on GameObject
            public string[] materialPaths;
            public int[] slotIndices;    // Optional; if omitted, replaces full materials array
        }

        [Serializable]
        public class ReparentRequest
        {
            public int instanceId;
            public int newParentId = 0;        // 0 = unparent (move to root)
            public int siblingIndex = -1;
            public int worldPositionStays = -1; // -1 = not set (default true), 0 = false, 1 = true
        }

        [Serializable]
        public class ScriptableObjectRequest
        {
            public string typeName;
            public string savePath;
            public string properties;
        }

        [Serializable]
        public class MaterialRequest
        {
            public string name;
            public string path;
            public string savePath;
            public string shaderName;
            public float[] color;
            public float[] baseColor;             // alias for color (takes precedence if both set)
            public string mainTexturePath;
            public int renderQueue = -1;  // -1 = not set
            public float[] emissionColor;        // [r, g, b] or [r, g, b, a]
            public float emissionIntensity = -1f; // HDR multiplier, sentinel -1 = not set
            public float metallic = -1f;          // sentinel -1 = not set
            public float smoothness = -1f;        // sentinel -1 = not set

            /// <summary>Resolve color: prefers baseColor over color if both set.</summary>
            public float[] ResolvedColor => (baseColor != null && baseColor.Length >= 3) ? baseColor : color;

            /// <summary>Resolve path: prefers path over savePath if both set.</summary>
            public string ResolvedPath => !string.IsNullOrEmpty(path) ? path : savePath;
        }

        [Serializable]
        public class MaterialInfo
        {
            public string name;
            public string path;
            public string shaderName;
        }

        [Serializable]
        public class PrefabRequest
        {
            public int instanceId;
            public string savePath;
        }

        [Serializable]
        public class PrefabModifyRequest
        {
            public string prefabPath;
            public string name;
            public string[] addComponents;
            public string[] removeComponents;
        }

        [Serializable]
        public class PrefabVariantRequest
        {
            public string basePrefabPath;
            public string savePath;
        }

        [Serializable]
        public class ImportFbxToPrefabRequest
        {
            public string fbxPath;
            public string prefabPath;
            public float scaleFactor = 1f;
            public int generateColliders = -1;  // -1=default, 0=none, 1=mesh, 2=box
            public int importMaterials = 1;     // 1=yes, 0=no
            public string materialLocation;     // "InPrefab" or "External"
        }

        [Serializable]
        public class LayerData
        {
            public int index;
            public string name;
            public bool isBuiltIn;
        }

        // UI Request Classes

        [Serializable]
        public class CanvasRequest
        {
            public string name;
            public string renderMode;  // ScreenSpaceOverlay, ScreenSpaceCamera, WorldSpace
            public float[] referenceResolution;  // [1920, 1080]
            public bool skipEventSystem;
        }

        [Serializable]
        public class UIElementRequest
        {
            public int parentId;
            public string elementType;  // Panel, Button, Image, Text, InputField, Slider, Toggle, Dropdown, ScrollView
            public string name;
            public float[] color;
            public string sprite;  // Asset path for Image/Button
            public string text;    // For Button/Text/Toggle
            public string placeholder;  // For InputField
            public float fontSize = -1f;      // -1 = not set
            public string alignment;
            public string contentType;  // For InputField
            public int characterLimit = -1;   // -1 = not set
            public float minValue = -999f;    // sentinel for Slider
            public float maxValue = -999f;    // sentinel for Slider
            public float value = -999f;       // sentinel for Slider
            public int isOn = -1;             // -1 = not set, 0 = false, 1 = true (Toggle)
            public string[] options;  // For Dropdown
            public int horizontal = -1;       // -1 = not set, 0 = false, 1 = true (ScrollView)
            public int vertical = -1;         // -1 = not set, 0 = false, 1 = true (ScrollView)
        }

        [Serializable]
        public class RectTransformRequest
        {
            public Vector2Data anchoredPosition;
            public Vector2Data sizeDelta;
            public Vector2Data anchorMin;
            public Vector2Data anchorMax;
            public Vector2Data pivot;
            public Vector2Data offsetMin;
            public Vector2Data offsetMax;
            public string anchorPreset;  // TopLeft, Center, Stretch, etc.
        }

        [Serializable]
        public class TMPTextRequest
        {
            public int parentId;
            public string name;
            public string text;
            public float fontSize = -1f;       // -1 = not set
            public string alignment;  // TopLeft, Center, etc.
            public float[] color;
            public string fontAsset;
            public int richText = -1;          // -1 = not set, 0 = false, 1 = true
            public int wordWrapping = -1;      // -1 = not set, 0 = false, 1 = true
        }

        [Serializable]
        public class ColorRequest
        {
            public float[] color;
        }

        [Serializable]
        public class Vector2Data
        {
            public float x, y;

            public Vector2Data() { }

            public Vector2Data(Vector2 v)
            {
                x = v.x;
                y = v.y;
            }

            public Vector2 ToVector2() => new Vector2(x, y);
        }

        // Shader Request Classes

        [Serializable]
        public class ShaderRequest
        {
            public string path;
            public string content;
            public string name;
            public string shaderType;  // Unlit, Surface, URP, UnlitTransparent, etc.
        }

        [Serializable]
        public class ShaderResponse
        {
            public bool success;
            public string path;
            public string content;
            public int lineCount;
            public string name;
            public int propertyCount;
            public bool isSupported;
        }

        [Serializable]
        public class ShaderInfo
        {
            public string name;
            public string path;
            public bool isSupported;
            public int propertyCount;
        }

        [Serializable]
        public class ShaderKeywordRequest
        {
            public string materialPath;
            public string keyword;
            public bool enabled;
        }

        [Serializable]
        public class ShaderPropertyInfo
        {
            public string name;
            public string description;
            public string type;
            public string flags;
        }

        // Compilation Classes

        [Serializable]
        public class CompilationStatusResponse
        {
            public bool isCompiling;
            public string lastCompilationTime;
            public int errorCount;
        }

        [Serializable]
        public class CompilationErrorsResponse
        {
            public List<UnityAgentBridgeServer.CompilationErrorInfo> errors;
        }

        // Serialization Classes

        [Serializable]
        public class ManagedReferenceRequest
        {
            public string propertyPath;
            public string typeName;
            public string data;
        }

        [Serializable]
        public class DerivedTypeInfo
        {
            public string name;
            public string fullName;
            public string assembly;
        }

        #endregion

        #region Scene Builder Classes

        [Serializable]
        public class SceneDescriptor
        {
            public string name;
            public List<ObjectDescriptor> objects = new List<ObjectDescriptor>();
        }

        [Serializable]
        public class MaterialOverrideDescriptor
        {
            public float[] color;
            public float[] emissionColor;
            public float emissionIntensity = -1f;
            public float metallic = -1f;
            public float smoothness = -1f;
            public string materialOverrideMode; // "instanced_copy" (default) or "property_block"
        }

        [Serializable]
        public class ObjectDescriptor
        {
            public string name;
            public string prefab;           // Prefab path or empty for empty GO
            public string primitiveType;    // Cube, Sphere, Plane, Cylinder, Capsule, Quad
            public string type;             // Alias for primitiveType (AI-friendly)
            public string primitive;        // Alias for primitiveType (AI-friendly)
            public float[] position;        // [x, y, z]
            public float[] rotation;        // [x, y, z] euler
            public float[] scale;           // [x, y, z]
            public string materialPath;     // Optional material to apply
            public MaterialOverrideDescriptor materialOverrides; // Inline material property overrides
            public string tag;              // e.g. "Player", "Enemy"
            public string layer;            // layer name or index as string
            public bool active = true;      // default true
            public bool isStatic;           // static flag
            public LightDescriptor light;   // embedded light config
            public List<ComponentDescriptor> components = new List<ComponentDescriptor>();
            public List<ObjectDescriptor> children = new List<ObjectDescriptor>();
        }

        [Serializable]
        public class LightDescriptor
        {
            public string type;         // Directional, Point, Spot, Area
            public float[] color;       // [r,g,b] or [r,g,b,a]
            public float intensity = -1f;  // sentinel -1 = not set
            public float range = -1f;
            public float spotAngle = -1f;
            public string shadows;      // None, Hard, Soft
        }

        [Serializable]
        public class ComponentDescriptor
        {
            public string type;
            public string propertiesJson;
        }

        [Serializable]
        public class SceneCreateResponse
        {
            public bool success;
            public int createdCount;
            public List<int> instanceIds = new List<int>();
            public List<string> warnings = new List<string>();
            public string error;
        }

        [Serializable]
        public class PrefabPreviewRequest
        {
            public string path;
            public int size;
        }

        [Serializable]
        public class PrefabGeometryRequest
        {
            public string path;
            public int includeSockets = 1;        // -1 not used, 0 false, 1 true
            public int includeChildren = 0;        // 0 false, 1 true
            public string[] socketPrefixes;
            public int includeAccurateBounds = 0;  // 0 false, 1 true — temp-instantiates to get real renderer bounds
        }

        [Serializable]
        public class PrefabFootprint2DRequest
        {
            public string path;
            public string source = "hybrid";       // hybrid | mesh | collider | rendererBounds
            public float targetMinEdgeGap = 0.04f; // world units
            public int maxPoints = 6000;
            public int includeHull = 1;            // 0 false, 1 true
            public int includeSamplePoints = 0;    // 0 false, 1 true
        }

        [Serializable]
        public class PrefabPreviewResponse
        {
            public bool success;
            public string prefabPath;
            public string prefabName;
            public int width;
            public int height;
            public string base64;
            public string error;
        }

        [Serializable]
        public class PrefabPreviewsResponse
        {
            public List<PrefabPreviewInfo> prefabs = new List<PrefabPreviewInfo>();
        }

        [Serializable]
        public class PrefabPreviewInfo
        {
            public string name;
            public string path;
            public string thumbnail;
        }

        [Serializable]
        public class MaterialPreviewResponse
        {
            public bool success;
            public string materialPath;
            public string materialName;
            public string shaderName;
            public int width;
            public int height;
            public string base64;
            public string error;
        }

        [Serializable]
        public class MaterialPreviewsResponse
        {
            public List<MaterialPreviewInfo> materials = new List<MaterialPreviewInfo>();
        }

        [Serializable]
        public class MaterialPreviewInfo
        {
            public string name;
            public string path;
            public string shaderName;
            public string mainColor;
            public string thumbnail;
        }

        [Serializable]
        public class SceneExportRequest
        {
            public int[] instanceIds;
        }

        [Serializable]
        public class SpawnPrimitiveRequest
        {
            public string primitiveType;    // Cube, Sphere, Cylinder, Capsule, Plane, Quad
            public string name;
            public Vector3Data position;
            public Vector3Data rotation;
            public Vector3Data scale;
            public float[] color;           // [r, g, b, a] 0-1 range
            public float metallic = -1f;    // -1 = not set
            public float smoothness = -1f;  // -1 = not set
            public int parentId = -1;       // -1 = no parent
            public bool mode2D;             // If true, swap 3D collider for BoxCollider2D
        }

        // Asset catalog, scene, and rendering DTOs moved to:
        // - UnityCommands.DataClasses.Scene.cs
        // - UnityCommands.DataClasses.Rendering.cs

        [Serializable]
        public class GroupObjectsRequest
        {
            public string name;           // name for the parent empty
            public int[] instanceIds;     // objects to group
            public int parentId = -1;     // optional parent for the group
            public int centerOnChildren = 1; // 1 = position group at children center
        }

        [Serializable]
        public class ScatterObjectsRequest
        {
            public int sourceInstanceId;   // object to duplicate
            public string prefabPath;      // or prefab to spawn (alternative to sourceInstanceId)
            public int count = 5;
            public float[] boundsCenter;   // [x,y,z]
            public float[] boundsSize;     // [x,y,z]
            public float[] rotationMin;    // [x,y,z] euler min
            public float[] rotationMax;    // [x,y,z] euler max
            public float scaleMin = 1f;
            public float scaleMax = 1f;
            public int uniformScale = 1;   // 1 = same scale on XYZ
            public int seed = 0;
            public int parentId = -1;
            public string name;            // base name (appended with _N)
            public float[] color;
        }

        [Serializable]
        public class ProceduralSkyboxRequest
        {
            public string name;
            public string path;            // save path for material
            public float sunSize = 0.04f;
            public int sunSizeConvergence = 5;
            public float atmosphereThickness = 1f;
            public float[] skyTint;        // [r,g,b]
            public float[] groundColor;    // [r,g,b]
            public float exposure = 1.3f;
            public int applySkybox = 1;    // 1 = assign to RenderSettings
        }

        [Serializable]
        public class ParticleTemplateRequest
        {
            public string template;       // fire, smoke, rain, sparks, snow, dust, fountain, fireflies
            public string name;           // GameObject name
            public float[] position;      // [x,y,z]
            public float[] rotation;      // [x,y,z] euler
            public float[] color;         // [r,g,b,a] override start color
            public int parentId = -1;
            public float scale = 1f;      // uniform scale multiplier
            public float intensity = 1f;  // multiplier for emission rate and particle count
        }

        [Serializable]
        public class RawMeshRequest
        {
            public string name;
            public float[] vertices;       // flat [x,y,z, x,y,z, ...] — 3 floats per vertex
            public int[] triangles;        // index triplets
            public float[] normals;        // flat [x,y,z, ...] — optional, auto-calculated if omitted
            public float[] uvs;            // flat [u,v, u,v, ...] — optional
            public float[] position;       // [x,y,z]
            public float[] rotation;       // [x,y,z] euler
            public float[] scale;          // [x,y,z]
            public float[] color;          // [r,g,b] or [r,g,b,a]
            public int parentId = -1;
            public float metallic = -1f;
            public float smoothness = -1f;
            public string saveMeshPath;    // optional: save mesh as .asset for reuse
        }

        // ─── Terrain ───

        [Serializable]
        public class CreateTerrainRequest
        {
            public string name;
            public float[] position;           // [x,y,z]
            public float terrainWidth = 100f;
            public float terrainLength = 100f;
            public float terrainHeight = 50f;
            public int heightmapResolution = 257; // must be 2^n+1 (33, 65, 129, 257, 513, 1025)
            public int parentId = -1;
        }

        [Serializable]
        public class SetTerrainHeightsRequest
        {
            public int instanceId;
            public float[] heights;            // flat row-major heightmap (0-1 values)
            public string mode;                // "raw", "noise", "flat", "slope", "plateau"
            // Noise params
            public float noiseScale = 0.03f;
            public float noiseAmplitude = 0.3f;
            public int noiseSeed = 0;
            public int noiseOctaves = 3;
            public float noisePersistence = 0.5f;
            // Flat/slope params
            public float flatHeight = 0.1f;
            public float slopeFrom = 0f;
            public float slopeTo = 0.5f;
            public string slopeDirection = "z";  // "x" or "z"
            // Plateau params
            public float plateauHeight = 0.3f;
            public float plateauRadius = 0.3f;   // 0-1 normalized
            public float plateauFalloff = 0.15f;  // transition width
        }

        [Serializable]
        public class AddTerrainLayerRequest
        {
            public int instanceId;
            public string diffusePath;         // texture asset path
            public string normalPath;          // optional normal map
            public float tileSizeX = 10f;
            public float tileSizeY = 10f;
            public float[] tint;               // [r,g,b,a] optional tint
            public float metallic = 0f;
            public float smoothness = 0f;
        }

        [Serializable]
        public class PaintTerrainRequest
        {
            public int instanceId;
            public int layerIndex;             // which terrain layer to paint
            public float centerX = 0.5f;       // normalized 0-1 center on alphamap
            public float centerY = 0.5f;
            public float radius = 0.1f;        // normalized radius
            public float opacity = 1f;         // paint strength 0-1
            public string shape = "circle";    // "circle" or "square"
            public int fill = 0;               // 1 = fill entire terrain with this layer
        }

        [Serializable]
        public class PlaceTerrainTreesRequest
        {
            public int instanceId;
            public string prefabPath;          // tree prefab asset path
            public int count = 10;
            public float minHeight = 0.8f;     // scale range
            public float maxHeight = 1.2f;
            public float minWidth = 0.8f;
            public float maxWidth = 1.2f;
            public float[] color;              // [r,g,b,a] tint
            public int seed = 0;
            public float density = 1f;         // multiplier for placement density
            public float minSlope = 0f;        // degrees — only place on slopes >= this
            public float maxSlope = 90f;       // degrees — only place on slopes <= this
            public float minAltitude = 0f;     // normalized 0-1
            public float maxAltitude = 1f;     // normalized 0-1
        }

        #endregion
    }
}
