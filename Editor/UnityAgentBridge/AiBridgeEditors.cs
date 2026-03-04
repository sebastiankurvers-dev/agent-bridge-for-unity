using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityAgentBridge
{
    // ─── Camera ───────────────────────────────────────────────────

    [AiBridgeEditor(typeof(Camera))]
    public class CameraAiBridgeEditor : AiBridgeEditor
    {
        private SerializedProperty m_FOV;
        private SerializedProperty m_NearClip;
        private SerializedProperty m_FarClip;
        private SerializedProperty m_Depth;
        private SerializedProperty m_ClearFlags;
        private SerializedProperty m_BackgroundColor;
        private SerializedProperty m_Orthographic;
        private SerializedProperty m_OrthographicSize;

        protected override void OnEnable()
        {
            m_FOV = serializedObject.FindProperty("field of view");
            m_NearClip = serializedObject.FindProperty("near clip plane");
            m_FarClip = serializedObject.FindProperty("far clip plane");
            m_Depth = serializedObject.FindProperty("m_Depth");
            m_ClearFlags = serializedObject.FindProperty("m_ClearFlags");
            m_BackgroundColor = serializedObject.FindProperty("m_BackGroundColor");
            m_Orthographic = serializedObject.FindProperty("orthographic");
            m_OrthographicSize = serializedObject.FindProperty("orthographic size");

            var cam = (Camera)target;

            AddProperty("fieldOfView", "float", "Vertical FOV in degrees (perspective mode)",
                () => cam.fieldOfView,
                v => cam.fieldOfView = ParseFloat(v));

            AddProperty("nearClipPlane", "float", "Near clipping plane distance",
                () => cam.nearClipPlane,
                v => cam.nearClipPlane = ParseFloat(v));

            AddProperty("farClipPlane", "float", "Far clipping plane distance",
                () => cam.farClipPlane,
                v => cam.farClipPlane = ParseFloat(v));

            AddProperty("depth", "float", "Camera render order (higher = later)",
                () => cam.depth,
                v => cam.depth = ParseFloat(v));

            AddProperty("orthographic", "bool", "True for orthographic, false for perspective",
                () => cam.orthographic,
                v => cam.orthographic = ParseBool(v));

            AddProperty("orthographicSize", "float", "Half-height of ortho camera in world units",
                () => cam.orthographicSize,
                v => cam.orthographicSize = ParseFloat(v));

            AddProperty("backgroundColor", "Color", "Clear color (when clearFlags uses solid color)",
                () => SerializeColor(cam.backgroundColor),
                v => cam.backgroundColor = ParseColor(v));

            AddProperty("clearFlags", "string", "How camera clears: Skybox, SolidColor, Depth, Nothing",
                () => cam.clearFlags.ToString(),
                v =>
                {
                    if (System.Enum.TryParse<CameraClearFlags>(v.ToString(), true, out var f))
                        cam.clearFlags = f;
                    else
                        throw new System.ArgumentException($"Unknown ClearFlags: {v}");
                });

            AddProperty("cullingMask", "int", "Layer bitmask for rendering",
                () => cam.cullingMask,
                v => cam.cullingMask = ParseInt(v));

            AddProperty("worldPosition", "Vector3", "Camera world position (read-only via transform)",
                () => SerializeVector3(cam.transform.position), null);

            AddProperty("worldRotation", "Vector3", "Camera euler angles (read-only via transform)",
                () => SerializeVector3(cam.transform.eulerAngles), null);
        }
    }

    // ─── Light ────────────────────────────────────────────────────

    [AiBridgeEditor(typeof(Light))]
    public class LightAiBridgeEditor : AiBridgeEditor
    {
        protected override void OnEnable()
        {
            var light = (Light)target;

            AddProperty("type", "string", "Light type: Directional, Point, Spot, Area",
                () => light.type.ToString(),
                v =>
                {
                    if (System.Enum.TryParse<LightType>(v.ToString(), true, out var t))
                        light.type = t;
                });

            AddProperty("color", "Color", "Light color (HDR values > 1.0 allowed)",
                () => SerializeColor(light.color),
                v => light.color = ParseColor(v));

            AddProperty("intensity", "float", "Light intensity (lux for directional, lumens for point/spot in URP)",
                () => light.intensity,
                v => light.intensity = ParseFloat(v));

            AddProperty("range", "float", "Light range (point/spot only)",
                () => light.range,
                v => light.range = ParseFloat(v));

            AddProperty("spotAngle", "float", "Outer cone angle in degrees (spot only)",
                () => light.spotAngle,
                v => light.spotAngle = ParseFloat(v));

            AddProperty("innerSpotAngle", "float", "Inner cone angle in degrees (spot only)",
                () => light.innerSpotAngle,
                v => light.innerSpotAngle = ParseFloat(v));

            AddProperty("shadows", "string", "Shadow mode: None, Hard, Soft",
                () => light.shadows.ToString(),
                v =>
                {
                    if (System.Enum.TryParse<LightShadows>(v.ToString(), true, out var s))
                        light.shadows = s;
                });

            AddProperty("shadowStrength", "float", "Shadow darkness (0=transparent, 1=opaque)",
                () => light.shadowStrength,
                v => light.shadowStrength = ParseFloat(v));

            AddProperty("bounceIntensity", "float", "Indirect light multiplier for GI",
                () => light.bounceIntensity,
                v => light.bounceIntensity = ParseFloat(v));

            AddProperty("enabled", "bool", "Is this light active",
                () => light.enabled,
                v => light.enabled = ParseBool(v));
        }
    }

    // ─── BoxCollider ──────────────────────────────────────────────

    [AiBridgeEditor(typeof(BoxCollider))]
    public class BoxColliderAiBridgeEditor : AiBridgeEditor
    {
        protected override void OnEnable()
        {
            var col = (BoxCollider)target;

            AddProperty("center", "Vector3", "Center offset in local space",
                () => SerializeVector3(col.center),
                v => col.center = ParseVector3(v));

            AddProperty("size", "Vector3", "Box dimensions in local space",
                () => SerializeVector3(col.size),
                v => col.size = ParseVector3(v));

            AddProperty("isTrigger", "bool", "If true, acts as trigger (no physics collision)",
                () => col.isTrigger,
                v => col.isTrigger = ParseBool(v));

            AddProperty("enabled", "bool", "Is this collider active",
                () => col.enabled,
                v => col.enabled = ParseBool(v));
        }
    }

    // ─── SphereCollider ───────────────────────────────────────────

    [AiBridgeEditor(typeof(SphereCollider))]
    public class SphereColliderAiBridgeEditor : AiBridgeEditor
    {
        protected override void OnEnable()
        {
            var col = (SphereCollider)target;

            AddProperty("center", "Vector3", "Center offset in local space",
                () => SerializeVector3(col.center),
                v => col.center = ParseVector3(v));

            AddProperty("radius", "float", "Sphere radius",
                () => col.radius,
                v => col.radius = ParseFloat(v));

            AddProperty("isTrigger", "bool", "If true, acts as trigger",
                () => col.isTrigger,
                v => col.isTrigger = ParseBool(v));

            AddProperty("enabled", "bool", "Is this collider active",
                () => col.enabled,
                v => col.enabled = ParseBool(v));
        }
    }

    // ─── MeshRenderer ─────────────────────────────────────────────

    [AiBridgeEditor(typeof(MeshRenderer))]
    public class MeshRendererAiBridgeEditor : AiBridgeEditor
    {
        protected override void OnEnable()
        {
            var renderer = (MeshRenderer)target;

            AddProperty("enabled", "bool", "Is this renderer visible",
                () => renderer.enabled,
                v => renderer.enabled = ParseBool(v));

            AddProperty("materialCount", "int", "Number of material slots (read-only)",
                () => renderer.sharedMaterials.Length, null);

            AddProperty("materials", "string[]", "Material names (read-only, use PUT /material to modify)",
                () =>
                {
                    var mats = new List<object>();
                    foreach (var mat in renderer.sharedMaterials)
                    {
                        mats.Add(mat != null
                            ? new Dictionary<string, object> { { "name", mat.name }, { "shader", mat.shader.name } }
                            : (object)"<null>");
                    }
                    return mats;
                }, null);

            AddProperty("shadowCastingMode", "string", "Off, On, TwoSided, ShadowsOnly",
                () => renderer.shadowCastingMode.ToString(),
                v =>
                {
                    if (System.Enum.TryParse<ShadowCastingMode>(v.ToString(), true, out var m))
                        renderer.shadowCastingMode = m;
                });

            AddProperty("receiveShadows", "bool", "Does this renderer receive shadows",
                () => renderer.receiveShadows,
                v => renderer.receiveShadows = ParseBool(v));

            AddProperty("bounds", "object", "World-space axis-aligned bounding box (read-only)",
                () => new Dictionary<string, object>
                {
                    { "center", SerializeVector3(renderer.bounds.center) },
                    { "size", SerializeVector3(renderer.bounds.size) }
                }, null);
        }
    }

    // ─── Transform ────────────────────────────────────────────────

    [AiBridgeEditor(typeof(Transform))]
    public class TransformAiBridgeEditor : AiBridgeEditor
    {
        protected override void OnEnable()
        {
            var t = (Transform)target;

            AddProperty("localPosition", "Vector3", "Position relative to parent",
                () => SerializeVector3(t.localPosition),
                v => t.localPosition = ParseVector3(v));

            AddProperty("localRotation", "Vector3", "Euler angles relative to parent",
                () => SerializeVector3(t.localEulerAngles),
                v => t.localEulerAngles = ParseVector3(v));

            AddProperty("localScale", "Vector3", "Scale relative to parent",
                () => SerializeVector3(t.localScale),
                v => t.localScale = ParseVector3(v));

            AddProperty("worldPosition", "Vector3", "World-space position",
                () => SerializeVector3(t.position),
                v => t.position = ParseVector3(v));

            AddProperty("worldRotation", "Vector3", "World-space euler angles",
                () => SerializeVector3(t.eulerAngles),
                v => t.eulerAngles = ParseVector3(v));

            AddProperty("lossyScale", "Vector3", "Approximate world-space scale (read-only)",
                () => SerializeVector3(t.lossyScale), null);

            AddProperty("childCount", "int", "Number of direct children (read-only)",
                () => t.childCount, null);

            AddProperty("parent", "string", "Parent name (read-only, use /reparent to change)",
                () => t.parent != null ? t.parent.name : "<root>", null);
        }
    }

    // ─── Rigidbody ────────────────────────────────────────────────

    [AiBridgeEditor(typeof(Rigidbody))]
    public class RigidbodyAiBridgeEditor : AiBridgeEditor
    {
        protected override void OnEnable()
        {
            var rb = (Rigidbody)target;

            AddProperty("mass", "float", "Mass in kilograms",
                () => rb.mass,
                v => rb.mass = ParseFloat(v));

            // Unity 6 API names
            AddProperty("linearDamping", "float", "Linear velocity drag",
                () => rb.linearDamping,
                v => rb.linearDamping = ParseFloat(v));

            AddProperty("angularDamping", "float", "Angular velocity drag",
                () => rb.angularDamping,
                v => rb.angularDamping = ParseFloat(v));

            AddProperty("useGravity", "bool", "Is affected by gravity",
                () => rb.useGravity,
                v => rb.useGravity = ParseBool(v));

            AddProperty("isKinematic", "bool", "If true, not driven by physics engine",
                () => rb.isKinematic,
                v => rb.isKinematic = ParseBool(v));

            AddProperty("interpolation", "string", "None, Interpolate, Extrapolate",
                () => rb.interpolation.ToString(),
                v =>
                {
                    if (System.Enum.TryParse<RigidbodyInterpolation>(v.ToString(), true, out var i))
                        rb.interpolation = i;
                });

            AddProperty("collisionDetectionMode", "string", "Discrete, Continuous, ContinuousDynamic, ContinuousSpeculative",
                () => rb.collisionDetectionMode.ToString(),
                v =>
                {
                    if (System.Enum.TryParse<CollisionDetectionMode>(v.ToString(), true, out var m))
                        rb.collisionDetectionMode = m;
                });

            AddProperty("constraints", "string", "Freeze position/rotation axes",
                () => rb.constraints.ToString(),
                v =>
                {
                    if (System.Enum.TryParse<RigidbodyConstraints>(v.ToString(), true, out var c))
                        rb.constraints = c;
                });

            AddProperty("linearVelocity", "Vector3", "Current velocity (read-only in edit mode)",
                () => SerializeVector3(rb.linearVelocity), null);
        }
    }
}
