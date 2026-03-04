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
        #region Lighting Data Classes

        [Serializable]
        public class CreateLightRequest
        {
            public string name;
            public string type;         // Directional, Point, Spot, Area
            public float[] color;       // [r,g,b] or [r,g,b,a]
            public float intensity = -1f;
            public float range = -1f;
            public float spotAngle = -1f;
            public string shadows;      // None, Hard, Soft
            public float[] position;
            public float[] rotation;
            public int parentId = 0;
        }

        [Serializable]
        public class ModifyLightRequest
        {
            public int instanceId;
            public string type;
            public float[] color;
            public float intensity = -1f;
            public float range = -1f;
            public float spotAngle = -1f;
            public string shadows;
        }

        [Serializable]
        public class RenderSettingsResponse
        {
            public bool success;
            public string ambientMode;
            public float[] ambientLight;
            public float[] ambientSkyColor;
            public float[] ambientEquatorColor;
            public float[] ambientGroundColor;
            public bool fog;
            public string fogMode;
            public float[] fogColor;
            public float fogDensity;
            public float fogStartDistance;
            public float fogEndDistance;
            public string skyboxMaterial;
            public float reflectionIntensity;
            public string error;
        }

        [Serializable]
        public class SetRenderSettingsRequest
        {
            public string ambientMode;
            public float[] ambientLight;
            public float[] ambientSkyColor;
            public float[] ambientEquatorColor;
            public float[] ambientGroundColor;
            public int fog = -1; // -1 = not set, 0 = false, 1 = true
            public string fogMode;
            public float[] fogColor;
            public float fogDensity = -1f;
            public float fogStartDistance = -1f;
            public float fogEndDistance = -1f;
            public string skyboxMaterialPath;
            public float reflectionIntensity = -1f;
        }

        [Serializable]
        public class GetVolumeProfileRequest
        {
            public string profilePath;
            public int volumeInstanceId;
            public int includeRenderHooks = 1;
        }

        [Serializable]
        public class GetCameraRenderingRequest
        {
            public int instanceId;
            public string cameraName;
        }

        [Serializable]
        public class SetCameraRenderingRequest
        {
            public int instanceId;
            public string cameraName;
            public string renderType;
            public int renderPostProcessing = -1;
            public string antialiasing;
            public string antialiasingQuality;
            public int stopNaN = -1;
            public int dithering = -1;
            public int allowXRRendering = -1;
            public int renderShadows = -1;
            public string requiresColorOption;
            public string requiresDepthOption;
            public int volumeLayerMask = -1;
            public int volumeTriggerInstanceId = int.MinValue;
            public string clearFlags;
            public float[] backgroundColor;
            public float fieldOfView = -1f;
            public float nearClipPlane = -1f;
            public float farClipPlane = -1f;
            public float orthographicSize = -1f;
            public int allowHDR = -1;
        }

        #endregion

        #region Physics Data Classes

        [Serializable]
        public class ConfigureRigidbodyRequest
        {
            public int instanceId;
            public float mass = -1f;
            public float drag = -1f;
            public float angularDrag = -1f;
            public int useGravity = -1;     // -1 = not set, 0 = false, 1 = true
            public int isKinematic = -1;
            public string interpolation;
            public string collisionDetectionMode;
            public string constraints;      // comma-separated e.g. "FreezeRotationX,FreezeRotationZ"
        }

        [Serializable]
        public class ConfigureColliderRequest
        {
            public int instanceId;
            public string colliderType;     // Box, Sphere, Capsule, Mesh
            public int isTrigger = -1;
            public float[] center;
            public float[] size;
            public float radius = -1f;
            public float height = -1f;
            public int direction = -1;
            public PhysicMaterialData physicMaterial;
        }

        [Serializable]
        public class PhysicMaterialData
        {
            public float dynamicFriction = -1f;
            public float staticFriction = -1f;
            public float bounciness = -1f;
            public string frictionCombine;
            public string bounceCombine;
        }

        [Serializable]
        public class PhysicsSettingsResponse
        {
            public bool success;
            public float[] gravity;
            public float defaultContactOffset;
            public float bounceThreshold;
            public float sleepThreshold;
            public int defaultSolverIterations;
            public int defaultSolverVelocityIterations;
            public string error;
        }

        [Serializable]
        public class SetPhysicsSettingsRequest
        {
            public float[] gravity;
            public float defaultContactOffset = -1f;
            public float bounceThreshold = -1f;
            public float sleepThreshold = -1f;
            public int defaultSolverIterations = -1;
            public int defaultSolverVelocityIterations = -1;
            public List<LayerCollisionEntry> layerCollisions;
        }

        [Serializable]
        public class LayerCollisionEntry
        {
            public int layer1;
            public int layer2;
            public bool ignore;
        }

        #endregion

        #region Scene Search Data Classes

        [Serializable]
        public class FindGameObjectsResponse
        {
            public int totalFound;
            public bool truncated;
            public int maxResults;
            public List<FoundGameObject> gameObjects;
        }

        [Serializable]
        public class FoundGameObject
        {
            public int instanceId;
            public string name;
            public string path;
            public string tag;
            public int layer;
            public string layerName;
            public bool active;
            public bool activeInHierarchy;
            public List<string> components;
        }

        #endregion

        #region Script Intelligence Data Classes

        [Serializable]
        public class ScriptListResponse
        {
            public int totalCount;
            public List<ScriptListEntry> scripts;
        }

        [Serializable]
        public class ScriptListEntry
        {
            public string path;
            public string fileName;
            public string className;
            public string namespaceName;
            public string baseClass;
            public bool isMonoBehaviour;
            public bool isScriptableObject;
            public bool isEditor;
        }

        [Serializable]
        public class ScriptStructureResponse
        {
            public bool success;
            public string error;
            public string path;
            public string className;
            public string fullTypeName;
            public string namespaceName;
            public string baseClass;
            public List<string> inheritanceChain;
            public List<string> interfaces;
            public List<string> attributes;
            public bool isAbstract;
            public bool isSealed;
            public bool isGeneric;
            public List<ScriptMethodInfo> methods;
            public List<ScriptFieldInfo> fields;
            public List<ScriptPropertyInfoData> properties;
            public List<ScriptEventInfo> events;
            public int methodsTotal;
            public int fieldsTotal;
            public int propertiesTotal;
            public int eventsTotal;
            public bool methodsTruncated;
            public bool fieldsTruncated;
            public bool propertiesTruncated;
            public bool eventsTruncated;
            public int maxMethods;
            public int maxFields;
            public int maxProperties;
            public int maxEvents;
            public bool includeMethods;
            public bool includeFields;
            public bool includeProperties;
            public bool includeEvents;
            public bool includeAttributes;
            public bool includeMethodParameters;
        }

        [Serializable]
        public class ScriptMethodInfo
        {
            public string name;
            public string returnType;
            public string access;
            public bool isStatic;
            public bool isVirtual;
            public bool isAbstract;
            public bool isOverride;
            public List<string> attributes;
            public List<ScriptParameterInfo> parameters;
        }

        [Serializable]
        public class ScriptParameterInfo
        {
            public string name;
            public string type;
            public string defaultValue;
            public bool isOut;
            public bool isRef;
        }

        [Serializable]
        public class ScriptFieldInfo
        {
            public string name;
            public string type;
            public string access;
            public bool isStatic;
            public bool isReadonly;
            public bool isSerialized;
            public List<string> attributes;
        }

        [Serializable]
        public class ScriptPropertyInfoData
        {
            public string name;
            public string type;
            public string access;
            public bool hasGetter;
            public bool hasSetter;
            public bool isStatic;
        }

        [Serializable]
        public class ScriptEventInfo
        {
            public string name;
            public string handlerType;
            public string access;
        }

        #endregion

        #region Scene Knowledge Data Classes

        [Serializable]
        public class SnapObjectsRequest
        {
            public int sourceId;
            public int targetId;
            public string alignment; // right-of, left-of, above, below, in-front-of, behind, on-top-of
            public float gap = 0f;
        }

        [Serializable]
        public class SaveLookPresetRequest
        {
            public string name;
            public string description;
        }

        [Serializable]
        public class LoadLookPresetRequest
        {
            public string name;
            public string mode;       // replace (default), merge
            public int applyLights = 1;
            public int applyVolume = 1;
            public int applyRenderSettings = 1;
            public int applyCamera = 1;
            public int replaceLights = 1;
            public string matchBy;    // none (default), name, type
        }

        [Serializable]
        public class ExtractSceneProfileRequest
        {
            public string name;
            public string savePath;
        }

        [Serializable]
        public class GenerateAssetCatalogRequest2
        {
            public string rootFolder;
            public string name;
            public int includeGeometry = 0;
            public int reuseExisting = 1;
            public int forceRegenerate = 0;
        }

        [Serializable]
        public class PinAssetPackRequest
        {
            public string name;
            public string description;
            public string rootFolder;
            public string catalogName;
            public int includeGeometry = 0;
            public int reuseExisting = 1;
            public int forceRefresh = 0;
            public int captureLookPreset = 0;
            public string lookPresetName;
            public int captureSceneProfile = 0;
            public string sceneProfileName;
            public string sceneProfileSavePath;
        }

        [Serializable]
        public class LookPresetLightInfo
        {
            public string name;
            public string type;
            public float[] position;
            public float[] rotation;
            public float[] color;
            public float intensity;
            public float range;
            public float spotAngle;
            public string shadows;
        }

        #endregion

        #region UI Toolkit Data Classes

        [Serializable]
        public class UIDocumentRequest
        {
            public string name;
            public string panelSettingsPath;
            public string uxmlPath;
            public int sortingOrder = 0;
        }

        [Serializable]
        public class PanelSettingsRequest
        {
            public string path;
            public string scaleMode;           // ConstantPixelSize, ConstantPhysicalSize, ScaleWithScreenSize
            public float[] referenceResolution; // [width, height]
            public float match = -1f;           // -1 = not set
            public string screenMatchMode;      // MatchWidthOrHeight, Expand, Shrink
        }

        [Serializable]
        public class UXMLRequest
        {
            public string path;
            public string content;             // Raw UXML XML string
        }

        [Serializable]
        public class USSRequest
        {
            public string path;
            public string content;             // Raw USS stylesheet string
        }

        [Serializable]
        public class VisualTreeQueryRequest
        {
            public string name;
            public string className;
            public string typeName;
            public int maxDepth = -1;          // -1 = unlimited
            public int includeStyles = 0;      // 0 = false, 1 = true
            public int offset = 0;             // pagination start index
            public int limit = -1;             // -1 = unlimited
            public int compact = 0;            // 0 = verbose, 1 = compact
            public int includeBounds = -1;     // -1 = auto (depends on compact)
            public int includeClasses = -1;    // -1 = auto (depends on compact)
            public int includeText = -1;       // -1 = auto (depends on compact)
        }

        [Serializable]
        public class VisualElementModifyRequest
        {
            public string elementName;
            public string className;
            public string typeName;
            public string text;
            public string tooltip;
            public int visible = -1;           // -1 = not set, 0 = false, 1 = true
            public string[] addClasses;
            public string[] removeClasses;
            public string styleJson;           // JSON dict of style properties
        }

        [Serializable]
        public class VisualElementCreateRequest
        {
            public string parentSelector;      // CSS selector for parent element
            public string elementType;         // VisualElement, Button, Label, TextField, etc.
            public string name;
            public string[] classes;
            public string text;
            public string styleJson;           // JSON dict of style properties
            public int insertIndex = -1;       // -1 = append
        }

        [Serializable]
        public class MigrateUIRequest
        {
            public string outputUxmlPath;
            public string outputUssPath;
        }

        #endregion

        #region Batch Modify Children Data Classes

        [Serializable]
        public class BatchModifyChildrenRequest
        {
            public int parentInstanceId;
            // Filters (all optional, AND logic)
            public string nameContains = "";
            public string tag = "";
            public string componentType = "";
            public int recursive = 1;        // -1=not set, 0=direct children only, 1=all descendants
            // Modifications (all optional, sentinel = not set)
            public Vector3Data localPosition; // null = don't change
            public Vector3Data localScale;    // null = don't change
            public Vector3Data rotation;      // null = don't change (world euler)
            public float positionX = -999f;   // sentinel: set only X
            public float positionY = -999f;   // sentinel: set only Y
            public float positionZ = -999f;   // sentinel: set only Z
            public int active = -1;           // -1=not set, 0=false, 1=true
            public string setTag = "";        // empty = don't change
            public string setLayer = "";      // empty = don't change
        }

        #endregion

        #region Multi-POV Snapshot Data Classes

        [Serializable]
        public class MultiPovSnapshotRequest
        {
            public int targetInstanceId = -1;   // focus object (-1 = use current pivot)
            public string[] presets;            // ["front","back","top","left","right"]
            public string presetsShorthand;     // "all" = all 5 cardinal + player
            public string povs;                 // custom POV configs as JSON array (parsed via MiniJSON)
            public int width = 800;
            public int height = 600;
            public string format = "jpeg";
            public int includePlayerView = 1;   // also capture Camera.main game view
            public int brief = -1;              // 1 = handles+names only
            public float sizeMultiplier = 1.5f; // bounds-to-camera-size ratio
        }

        [Serializable]
        public class PovConfig
        {
            public string name;
            public float[] pivot;       // explicit world position
            public float[] rotation;    // euler angles
            public float size = -1f;
            public string preset;       // shorthand: "front"/"back"/"top" etc.
        }

        #endregion

        #region Spatial Enclosure Data Classes

        [Serializable]
        public class CheckEnclosureRequest
        {
            public int[] wallIds;             // Instance IDs of wall objects (or parent with children)
            public int[] ceilingIds;          // Instance IDs of ceiling objects (or parent)
            public int[] floorIds;            // Instance IDs of floor objects (or parent)
            public float gapThreshold = 0.1f; // Max acceptable gap (meters)
            public int includeChildren = 1;   // Expand each ID to include children renderers
        }

        // ==================== Spatial Audit ====================

        [Serializable]
        public class CameraVisibilityAuditRequest
        {
            public string view = "game";              // "game" or "scene"
            public string nameContains = "";
            public string tag = "";
            public string layer = "";
            public int rootInstanceId = 0;            // 0 = entire scene
            public string targetInstanceIds = "";     // comma-separated instance IDs
            public int maxObjects = 500;              // clamped: 10..5000
            public int raySamples = 9;                // sample points per object (center + 8 corners)
            public int includeVisible = 0;            // 0 = only issues, 1 = full report
            public int checkAttachment = 0;           // 0 = skip, 1 = check proximity to surfaces
            public float attachMaxDistance = 0.5f;     // meters; objects farther from any surface = detached
            public int occluderLayerMask = -1;        // -1 = all layers
            public int ignoreTriggers = 1;            // 1 = ignore trigger colliders
            public int timeoutMs = -1;                // -1 = use default
        }

        [Serializable]
        public class RaycastCoverageRequest
        {
            public int rootInstanceId = 0;            // 0 = must provide explicit bounds
            public float boundsMinX = -1f;
            public float boundsMinY = -1f;
            public float boundsMinZ = -1f;
            public float boundsMaxX = -1f;
            public float boundsMaxY = -1f;
            public float boundsMaxZ = -1f;
            public string direction = "down";         // "down", "up", "forward", "back", "left", "right"
            public float spacing = 0.5f;              // grid spacing in meters
            public float maxRayDistance = 100f;
            public float originOffset = 10f;          // offset above scan area
            public string surfaceNameContains = "";
            public string surfaceTag = "";
            public string surfaceLayer = "";
            public int surfaceLayerMask = -1;         // -1 = all layers
            public int ignoreTriggers = 1;            // 1 = ignore trigger colliders
            public int maxGaps = 50;                  // max gap clusters to report
            public int timeoutMs = -1;                // -1 = use default
        }

        #endregion
    }
}
