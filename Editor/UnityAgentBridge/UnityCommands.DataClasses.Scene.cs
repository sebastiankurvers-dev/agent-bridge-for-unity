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
        // ==================== Asset Catalog Data Classes ====================

        [Serializable]
        public class AssetCatalogRequest
        {
            public string prefabSearch;
            public string materialSearch;
            public int maxPrefabs = 30;
            public int maxMaterials = 30;
            public int thumbnailSize = 64;
            public int includeShaders = -1; // -1 = not set, 0 = false, 1 = true
            public int includeThumbnails = 0; // 0 = false, 1 = true
        }

        [Serializable]
        public class AssetCatalogResponse
        {
            public bool success;
            public List<AssetCatalogPrefabInfo> prefabs = new List<AssetCatalogPrefabInfo>();
            public List<AssetCatalogMaterialInfo> materials = new List<AssetCatalogMaterialInfo>();
            public List<AssetCatalogShaderInfo> shaders = new List<AssetCatalogShaderInfo>();
            public string error;
        }

        [Serializable]
        public class AssetCatalogPrefabInfo
        {
            public string name;
            public string path;
            public string thumbnail;
        }

        [Serializable]
        public class AssetCatalogMaterialInfo
        {
            public string name;
            public string path;
            public string shaderName;
            public string mainColor;
            public string thumbnail;
        }

        [Serializable]
        public class AssetCatalogShaderInfo
        {
            public string name;
            public string path;
        }

        // ==================== Build & Screenshot Data Classes ====================

        [Serializable]
        public class BuildAndScreenshotRequest
        {
            public string descriptor;       // nested SceneDescriptor JSON string
            public string screenshotView;
            public float[] cameraPosition;
            public float[] cameraRotation;
        }

        [Serializable]
        public class BuildAndScreenshotResponse
        {
            public bool success;
            public int createdCount;
            public List<int> instanceIds = new List<int>();
            public ScreenshotData screenshot;
            public string error;
        }

        [Serializable]
        public class ScreenshotData
        {
            public string base64;
            public string mimeType = "image/jpeg";
            public int width;
            public int height;
        }

        // ==================== Scene Transaction Data Classes ====================

        [Serializable]
        public class SceneTransactionRequest
        {
            public string descriptor;
            public string screenshotView;
            public float[] cameraPosition;
            public float[] cameraRotation;
            public string checkpointName;
            public int autoRollbackOnError = 1; // -1 = not set, 0 = false, 1 = true (default true)
        }

        [Serializable]
        public class SceneTransactionResponse
        {
            public bool success;
            public string checkpointId;
            public int createdCount;
            public List<int> instanceIds = new List<int>();
            public ScreenshotData screenshot;
            public bool rolledBack;
            public string error;
        }

        [Serializable]
        public class CompareImagesRequest
        {
            public string referenceImageBase64;
            public string referenceImageHandle;
            public string currentImageBase64;
            public string currentImageHandle;
            public int captureCurrentScreenshot = 0; // 0 = false, 1 = true
            public string screenshotView = "scene";
            public int screenshotWidth = 0;
            public int screenshotHeight = 0;
            public int downsampleMaxSize = 256;
            public int gridSize = 8;
            public float changedPixelThreshold = 0.12f;
            public float hotThreshold = 0.2f;
            public int includeHeatmap = 0; // 0 = false, 1 = true
            public int storeReferenceHandle = 0; // 0 = false, 1 = true
            public int includeImageHandles = 0; // 0 = false, 1 = true
            public string aspectMode; // "none" (default), "crop", "fit_letterbox"
            public string fields;
            public int omitEmpty = 1; // 0 = false, 1 = true
            public int maxItems = 256;
        }

        [Serializable]
        public class ReproStepRequest
        {
            public string referenceImageBase64;
            public string referenceImageHandle;
            public string currentImageBase64;
            public string currentImageHandle;
            public int captureCurrentScreenshot = 1; // 0 = false, 1 = true
            public string screenshotView = "scene";
            public int screenshotWidth = 0;
            public int screenshotHeight = 0;
            public int cameraInstanceId;
            public string cameraName;
            public int volumeInstanceId;
            public string profilePath;
            public int maxProposals = 6;
            public float minConfidence = 0.35f;
            public int downsampleMaxSize = 256;
            public int gridSize = 8;
            public float changedPixelThreshold = 0.12f;
            public float hotThreshold = 0.2f;
            public int includeHeatmap = 0; // 0 = false, 1 = true
            public int storeReferenceHandle = 0; // 0 = false, 1 = true
            public int includeImageHandles = 0; // 0 = false, 1 = true
            public string fields;
            public int omitEmpty = 1; // 0 = false, 1 = true
            public int maxItems = 256;
        }

        [Serializable]
        public class SceneQualityChecksRequest
        {
            public int includeInactive = 1;           // 0 = false, 1 = true
            public int includeInfo = 0;               // 0 = false, 1 = true
            public int checkRequireComponents = 1;    // 0 = false, 1 = true
            public int checkSerializedReferences = 1; // 0 = false, 1 = true
            public int checkPhysicsSanity = 1;        // 0 = false, 1 = true
            public int checkUISanity = 1;             // 0 = false, 1 = true
            public int checkLifecycleHeuristics = 1;  // 0 = false, 1 = true
            public int checkRenderingHealth = 1;      // 0 = false, 1 = true
            public int checkObjectScales = 1;         // 0 = false, 1 = true
            public int maxIssues = 200;               // clamped: 10..5000
            public string failOnSeverity = "error";   // info|warning|error
        }

        [Serializable]
        public class AuditRenderersRequest
        {
            public string nameContains = "";
            public string tag = "";
            public string layer = "";
            public int includeInactive = 0;
            public int checkCameraCulling = 1;
            public int maxObjects = 500;
            public int rootInstanceId = 0;   // 0 = entire scene, non-zero = scope to this GO subtree
        }

        [Serializable]
        public class GetHierarchyRenderersRequest
        {
            public int instanceId;
            public int includeInactive = 0;
        }

        [Serializable]
        public class AuditSceneLightingRequest
        {
            public int includeRecommendations = 1;
        }

        [Serializable]
        public class PerformanceTelemetryRequest
        {
            public int includeHotspots = 1;           // 0 = false, 1 = true
            public int includeInactive = 0;           // 0 = false, 1 = true
            public int onlyEnabledBehaviours = 1;     // 0 = false, 1 = true
            public int maxHotspots = 12;              // clamped: 1..100
        }

        [Serializable]
        public class PerformanceBaselineRequest
        {
            public string name = "default";
            public int includeHotspots = 1;           // 0 = false, 1 = true
            public int includeInactive = 0;           // 0 = false, 1 = true
            public int onlyEnabledBehaviours = 1;     // 0 = false, 1 = true
            public int maxHotspots = 12;              // clamped: 1..100
        }

        [Serializable]
        public class PerformanceBudgetCheckRequest
        {
            public string baselineName = "default";
            public int useBaseline = 1;               // 0 = false, 1 = true
            public int captureBaselineIfMissing = 0;  // 0 = false, 1 = true
            public int includeHotspots = 1;           // 0 = false, 1 = true
            public int includeInactive = 0;           // 0 = false, 1 = true
            public int onlyEnabledBehaviours = 1;     // 0 = false, 1 = true
            public int maxHotspots = 8;               // clamped: 1..100

            public double maxFrameTimeMs = -1d;
            public double minFps = -1d;
            public double maxGcAllocBytesPerFrame = -1d;
            public double maxDrawCalls = -1d;
            public double maxBatches = -1d;
            public double maxSetPassCalls = -1d;
            public double maxTotalAllocatedMemoryBytes = -1d;

            public double maxFrameTimeDeltaMs = -1d;
            public double maxGcAllocDeltaBytesPerFrame = -1d;
            public double maxDrawCallsDelta = -1d;
            public double maxBatchesDelta = -1d;
            public double maxSetPassCallsDelta = -1d;
            public double maxTotalAllocatedMemoryDeltaBytes = -1d;
        }

        [Serializable]
        public class ScriptHotspotsRequest
        {
            public int includeInactive = 0;           // 0 = false, 1 = true
            public int onlyEnabledBehaviours = 1;     // 0 = false, 1 = true
            public int maxHotspots = 20;              // clamped: 1..200
        }

        #region Runtime/Gameplay Data Classes

        [Serializable]
        public class RuntimeValuesRequest
        {
            public int instanceId;
            public string componentType;
            public string[] fieldNames;
            public int includePrivate = 1;      // int sentinel: 1=true (default)
            public int includeProperties = 0;   // int sentinel: 0=false (default)
        }

        [Serializable]
        public class RuntimeValuesResponse
        {
            public bool success;
            public string gameObjectName;
            public int instanceId;
            public string componentType;
            public bool isPlayMode;
            public List<RuntimeFieldInfo> fields;
            public string error;
        }

        [Serializable]
        public class RuntimeFieldInfo
        {
            public string name;
            public string type;
            public string valueJson;
            public bool isProperty;
            public bool isPrivate;
            public bool isSerialized;
        }

        [Serializable]
        public class InvokeMethodRequest
        {
            public int instanceId;
            public string componentType;
            public string methodName;
            public string[] args;
            // Screenshot support
            public int screenshotBefore = -1;   // -1 = not set, 1 = true
            public int screenshotAfter = -1;    // -1 = not set, 1 = true
            public string screenshotView;       // "game" or "scene"
            public int screenshotWidth;
            public int screenshotHeight;
        }

        [Serializable]
        public class InvokeStep
        {
            public string componentType;
            public string methodName;
            public int delayMs;     // delay BEFORE this step
            public string[] args;
        }

        [Serializable]
        public class InvokeSequenceRequest
        {
            public int instanceId;
            public string stepsJson;            // JSON array of InvokeStep
            public string screenshotView;       // "" = no screenshot
            public int screenshotWidth;
            public int screenshotHeight;
        }

        [Serializable]
        public class GetRendererStateRequest
        {
            public int instanceId;
            public int rendererIndex;           // default 0
            public string[] propertyNames;      // optional MPB property names to check
        }

        [Serializable]
        public class GetMeshInfoRequest
        {
            public int instanceId;              // primary lookup
            public string name = "";            // fallback: GameObject.Find(name)
        }

        [Serializable]
        public class ValidateMaterialRequest
        {
            public string materialPath;
            public int instanceId;              // alternative: get material from renderer on this GO
        }

        [Serializable]
        public class InvokeMethodResponse
        {
            public bool success;
            public string gameObjectName;
            public int instanceId;
            public string componentType;
            public string methodName;
            public string returnType;
            public string returnValueJson;
            public bool isPlayMode;
            public string error;
        }

        #endregion

        #region Animation Data Classes

        [Serializable]
        public class CreateAnimatorControllerRequest
        {
            public string path;
            public string name;
            public int attachToInstanceId;
            public string parametersJson;
            public string prefabPath;
            public int applyRootMotion = -1;
        }

        [Serializable]
        public class AnimatorParameterData
        {
            public string name;
            public string type;
            public float defaultFloat;
            public int defaultInt;
            public int defaultBool = -1;
        }

        [Serializable]
        public class AnimatorParameterDataList
        {
            public List<AnimatorParameterData> items = new List<AnimatorParameterData>();
        }

        [Serializable]
        public class AddAnimationStateRequest
        {
            public string controllerPath;
            public int layerIndex;
            public string stateName;
            public int setAsDefault = -1;
            public string motionClipPath;
            public string motionClipName;
            public float speed = -1f;
            public string speedParameterName;
        }

        [Serializable]
        public class GetFbxClipsRequest
        {
            public string fbxPath;
        }

        [Serializable]
        public class AddAnimationTransitionRequest
        {
            public string controllerPath;
            public int layerIndex;
            public string sourceStateName;
            public string destinationStateName;
            public int hasExitTime = -1;
            public float exitTime = -1f;
            public float transitionDuration = -1f;
            public float transitionOffset = -1f;
            public int hasFixedDuration = -1;
            public int canTransitionToSelf = -1;
            public string conditionsJson;
        }

        [Serializable]
        public class AnimatorConditionData
        {
            public string parameterName;
            public string mode;
            public float threshold;
        }

        [Serializable]
        public class AnimatorConditionDataList
        {
            public List<AnimatorConditionData> items = new List<AnimatorConditionData>();
        }

        [Serializable]
        public class SetAnimationParameterRequest
        {
            public string controllerPath;
            public string parameterName;
            public string type;
            public float defaultFloat;
            public int defaultInt;
            public int defaultBool = -1;
            public int remove = -1;
        }

        [Serializable]
        public class CreateAnimationClipRequest
        {
            public string path;
            public string wrapMode;
            public float frameRate = -1f;
            public string curvesJson;
        }

        [Serializable]
        public class AnimationCurveData
        {
            public string relativePath;
            public string componentType;
            public string propertyName;
            public string keyframesJson;
        }

        [Serializable]
        public class AnimationCurveDataList
        {
            public List<AnimationCurveData> items = new List<AnimationCurveData>();
        }

        [Serializable]
        public class AnimationKeyframeData
        {
            public float time;
            public float value;
            public float inTangent;
            public float outTangent;
        }

        [Serializable]
        public class AnimationKeyframeDataList
        {
            public List<AnimationKeyframeData> items = new List<AnimationKeyframeData>();
        }

        [Serializable]
        public class GetAnimatorInfoRequest
        {
            public string controllerPath;
            public int layerIndex = -1;
        }

        #endregion

        #region Asset Management Data Classes

        [Serializable]
        public class MoveAssetRequest
        {
            public string sourcePath;
            public string destinationPath;
        }

        [Serializable]
        public class DuplicateAssetRequest
        {
            public string sourcePath;
            public string destinationPath;
        }

        [Serializable]
        public class GetAssetInfoRequest
        {
            public string path;
        }

        #endregion

        #region Event Data Classes

        [Serializable]
        public class UnityEvent
        {
            public int id;
            public string type;
            public string timestamp;
            public string data;
        }

        [Serializable]
        public class EventsResponse
        {
            public List<UnityEvent> events = new List<UnityEvent>();
            public int lastId;
        }

        #endregion

        #region Scene View Camera Data Classes

        [Serializable]
        public class SceneViewCameraResponse
        {
            public bool success;
            public float[] pivot;
            public float[] rotation;
            public float size;
            public bool orthographic;
            public float[] cameraPosition;
            public float cameraDistance;
            public string error;
        }

        [Serializable]
        public class SetSceneViewCameraRequest
        {
            public float[] pivot;
            public float[] rotation;
            public float size = -1f;
            public int orthographic = -1; // -1 = not set, 0 = false, 1 = true
        }

        [Serializable]
        public class FrameObjectRequest
        {
            public int instanceId;
        }

        [Serializable]
        public class LookAtPointRequest
        {
            public float[] point;
            public float[] direction;
            public float size = -1f;
        }

        [Serializable]
        public class OrbitCameraRequest
        {
            public float yaw;
            public float pitch;
            public int targetInstanceId = -1;
            public int brief = -1;
        }

        [Serializable]
        public class PanCameraRequest
        {
            public float deltaRight;
            public float deltaUp;
            public int targetInstanceId = -1;
            public int brief = -1;
        }

        [Serializable]
        public class ZoomCameraRequest
        {
            public float factor = 1f;
            public int brief = -1;
        }

        [Serializable]
        public class PickAtScreenRequest
        {
            public float x;
            public float y;
            public string view;
            public int brief = -1;
        }

        #endregion
    }
}
