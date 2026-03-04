using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class SceneViewTools
{
    [McpServerTool(Name = "unity_get_scene_view_camera")]
    [Description(@"Get the current scene view camera state including pivot, rotation, size, orthographic mode, and camera position.
Use this to understand the current viewing angle before taking screenshots or repositioning.")]
    public static async Task<string> GetSceneViewCamera(UnityClient client)
    {
        return await client.GetSceneViewCameraAsync();
    }

    [McpServerTool(Name = "unity_set_scene_view_camera")]
    [Description(@"Set the scene view camera state. Control pivot point, rotation, zoom level, and orthographic mode.
Use this to position the editor camera for screenshots or to set up specific viewing angles.

Example: Top-down view at origin with zoom 20:
  pivot: [0, 0, 0], rotation: [90, 0, 0], size: 20

Example: Front perspective view:
  pivot: [0, 1, 0], rotation: [0, 0, 0], size: 10")]
    public static async Task<string> SetSceneViewCamera(
        UnityClient client,
        [Description("Camera pivot point as [x, y, z]. The point the camera orbits around.")] float[]? pivot = null,
        [Description("Camera rotation as euler angles [x, y, z] in degrees.")] float[]? rotation = null,
        [Description("Camera zoom/size. Smaller = closer. Typical range 1-50.")] float? size = null,
        [Description("True for orthographic projection, false for perspective.")] bool? orthographic = null)
    {
        var data = new Dictionary<string, object?>();
        if (pivot != null) data["pivot"] = pivot;
        if (rotation != null) data["rotation"] = rotation;
        if (size.HasValue) data["size"] = size.Value;
        if (orthographic.HasValue) data["orthographic"] = orthographic.Value ? 1 : 0;
        return await client.SetSceneViewCameraAsync(data);
    }

    [McpServerTool(Name = "unity_frame_object")]
    [Description(@"Frame the scene view camera on a specific GameObject, centering it in the view.
This selects the object and adjusts the camera to show it fully. Useful for focusing on a specific object before taking a screenshot.")]
    public static async Task<string> FrameObject(
        UnityClient client,
        [Description("The instance ID of the GameObject to frame. Get this from unity_get_hierarchy.")] int instanceId)
    {
        return await client.FrameObjectAsync(new { instanceId });
    }

    [McpServerTool(Name = "unity_look_at_point")]
    [Description(@"Point the scene view camera at a specific world position with optional direction and zoom.
Use this to look at a specific location in the scene without needing to know an object's instance ID.

Example: Look at origin from 45 degrees:
  point: [0, 0, 0], direction: [45, 0, 0], size: 15")]
    public static async Task<string> LookAtPoint(
        UnityClient client,
        [Description("World position to look at as [x, y, z].")] float[] point,
        [Description("View direction as euler angles [x, y, z] in degrees. Defaults to current direction.")] float[]? direction = null,
        [Description("Camera zoom/size. Defaults to current size.")] float? size = null)
    {
        var data = new Dictionary<string, object?>();
        data["point"] = point;
        if (direction != null) data["direction"] = direction;
        if (size.HasValue) data["size"] = size.Value;
        return await client.LookAtPointAsync(data);
    }

    [McpServerTool(Name = "unity_orbit_camera")]
    [Description(@"Orbit the scene view camera around its pivot by relative yaw/pitch degrees.
Uses quaternion composition (no gimbal lock). Positive yaw = right, positive pitch = up.
Optionally orbit around a specific object by providing targetInstanceId.

Example - rotate 30° right and 15° up:  yaw: 30, pitch: 15
Example - orbit around a specific object:  yaw: 45, targetInstanceId: 12345")]
    public static async Task<string> OrbitCamera(
        UnityClient client,
        [Description("Horizontal rotation in degrees. Positive = right.")] float yaw = 0,
        [Description("Vertical rotation in degrees. Positive = up.")] float pitch = 0,
        [Description("Optional: orbit around this object's position instead of current pivot.")] int? targetInstanceId = null,
        [Description("If true, return only success status. Default true for token efficiency.")] bool brief = true)
    {
        var data = new Dictionary<string, object?>();
        data["yaw"] = yaw;
        data["pitch"] = pitch;
        if (targetInstanceId.HasValue) data["targetInstanceId"] = targetInstanceId.Value;
        if (brief) data["brief"] = 1;
        return await client.OrbitCameraAsync(data);
    }

    [McpServerTool(Name = "unity_pan_camera")]
    [Description(@"Pan the scene view camera by moving the pivot in the camera's local plane.
Movement is automatically scaled by zoom level (sceneView.size).
Optionally pan relative to a target object.

Example - shift 2 units right:  deltaRight: 2
Example - pan to re-center on a target:  targetInstanceId: 12345")]
    public static async Task<string> PanCamera(
        UnityClient client,
        [Description("Pan in camera's right direction (world units, zoom-scaled).")] float deltaRight = 0,
        [Description("Pan in camera's up direction (world units, zoom-scaled).")] float deltaUp = 0,
        [Description("Optional: set pivot to this object's position before panning.")] int? targetInstanceId = null,
        [Description("If true, return only success status. Default true for token efficiency.")] bool brief = true)
    {
        var data = new Dictionary<string, object?>();
        data["deltaRight"] = deltaRight;
        data["deltaUp"] = deltaUp;
        if (targetInstanceId.HasValue) data["targetInstanceId"] = targetInstanceId.Value;
        if (brief) data["brief"] = 1;
        return await client.PanCameraAsync(data);
    }

    [McpServerTool(Name = "unity_zoom_camera")]
    [Description(@"Zoom the scene view in or out by a factor.
Factor < 1 zooms in, > 1 zooms out. Clamped to [0.01, 2000].

Example - zoom in 50%:  factor: 0.5
Example - zoom out 2x:  factor: 2.0")]
    public static async Task<string> ZoomCamera(
        UnityClient client,
        [Description("Zoom multiplier. 0.5 = zoom in, 2.0 = zoom out. Range: 0.01-100.")] float factor = 1.0f,
        [Description("If true, return only success status. Default true for token efficiency.")] bool brief = true)
    {
        var data = new Dictionary<string, object?>();
        data["factor"] = factor;
        if (brief) data["brief"] = 1;
        return await client.ZoomCameraAsync(data);
    }

    [McpServerTool(Name = "unity_pick_at_screen")]
    [Description(@"Raycast from normalized screenshot coordinates to find and select the object at that position.
Coordinates match unity_screenshot output: (0,0) = top-left, (1,1) = bottom-right.
Primary: HandleUtility.PickGameObject (GPU-based, all visible objects).
Fallback: Physics.Raycast + renderer bounds intersection.
Selects the hit object in the editor.

Example - pick at screen center:  x: 0.5, y: 0.5
Example - pick in game view:  x: 0.3, y: 0.7, view: 'game'")]
    public static async Task<string> PickAtScreen(
        UnityClient client,
        [Description("Normalized X (0-1). 0 = left, 1 = right.")] float x,
        [Description("Normalized Y (0-1). 0 = top, 1 = bottom.")] float y,
        [Description("'scene' or 'game'. Default 'scene'.")] string? view = null,
        [Description("If true, return only instanceId and name on hit. Default true for token efficiency.")] bool brief = true)
    {
        var data = new Dictionary<string, object?>();
        data["x"] = x;
        data["y"] = y;
        if (view != null) data["view"] = view;
        if (brief) data["brief"] = 1;
        return await client.PickAtScreenAsync(data);
    }
}
