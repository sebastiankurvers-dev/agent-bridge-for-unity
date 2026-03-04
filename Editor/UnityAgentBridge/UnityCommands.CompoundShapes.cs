using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityAgentBridge
{
    public static partial class UnityCommands
    {
        [Serializable]
        private class CompoundShapeRequest
        {
            public string preset;           // tree, lantern, steps, fence, rock_cluster, simple_building
            public string name;
            public float[] position;        // [x,y,z]
            public float[] rotation;        // [x,y,z] euler
            public float scale = 1f;
            public int parentId = -1;
            // Tree params
            public float trunkHeight = 1.5f;
            public float trunkRadius = 0.15f;
            public float canopyHeight = 2f;
            public float canopyRadius = 1f;
            public float[] trunkColor;      // [r,g,b]
            public float[] canopyColor;     // [r,g,b]
            public string canopyShape = "cone";  // cone, sphere
            // Lantern params
            public float postHeight = 0.3f;
            public float housingSize = 0.25f;
            public float lightIntensity = 1f;
            public float lightRange = 8f;
            public float[] lightColor;      // [r,g,b]
            public float chainLength = 0f;  // 0 = no chain (ground lantern), >0 = hanging
            // Steps params
            public int stepCount = 4;
            public float stepWidth = 2f;
            public float stepDepth = 0.4f;
            public float stepRise = 0.2f;
            public float[] stepColor;       // [r,g,b]
            // Fence params
            public float fenceLength = 5f;
            public float fenceHeight = 1f;
            public float postSpacing = 1.5f;
            public float postRadius = 0.05f;
            public float[] fenceColor;      // [r,g,b]
            // Rock cluster params
            public int rockCount = 5;
            public float clusterRadius = 1.5f;
            public float rockMinScale = 0.3f;
            public float rockMaxScale = 1f;
            public float[] rockColor;       // [r,g,b]
            public int seed = 0;
            // Simple building params
            public float buildingWidth = 4f;
            public float buildingDepth = 3f;
            public float buildingHeight = 3f;
            public float roofHeight = 1.5f;
            public string roofType = "gable";  // gable, flat
            public float[] wallColor;       // [r,g,b]
            public float[] roofColor;       // [r,g,b]
        }

        [BridgeRoute("POST", "/compound-shape", Category = "objects", Description = "Create compound shape preset")]
        public static string CreateCompoundShape(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<CompoundShapeRequest>(NormalizeColorFields(jsonData));

                if (string.IsNullOrEmpty(request.preset))
                    return JsonError("preset is required (tree, lantern, steps, fence, rock_cluster, simple_building)");

                var preset = request.preset.ToLowerInvariant().Trim();
                var validPresets = new[] { "tree", "lantern", "steps", "fence", "rock_cluster", "simple_building" };
                if (Array.IndexOf(validPresets, preset) < 0)
                    return JsonError($"Invalid preset '{request.preset}'. Must be one of: {string.Join(", ", validPresets)}");

                float s = Mathf.Max(0.01f, request.scale);
                var pos = request.position != null && request.position.Length >= 3
                    ? new Vector3(request.position[0], request.position[1], request.position[2])
                    : Vector3.zero;
                var rot = request.rotation != null && request.rotation.Length >= 3
                    ? Quaternion.Euler(request.rotation[0], request.rotation[1], request.rotation[2])
                    : Quaternion.identity;

                // Create root container
                var rootName = request.name ?? $"CompoundShape_{preset}";
                var root = new GameObject(rootName);
                Undo.RegisterCreatedObjectUndo(root, "Agent Bridge Create Compound Shape");
                root.transform.position = pos;
                root.transform.rotation = rot;

                if (request.parentId != -1)
                {
                    var parent = EditorUtility.EntityIdToObject(request.parentId) as GameObject;
                    if (parent != null)
                        root.transform.SetParent(parent.transform, true);
                }

                int childCount = 0;
                switch (preset)
                {
                    case "tree":
                        childCount = BuildTree(root, request, s);
                        break;
                    case "lantern":
                        childCount = BuildLantern(root, request, s);
                        break;
                    case "steps":
                        childCount = BuildSteps(root, request, s);
                        break;
                    case "fence":
                        childCount = BuildFence(root, request, s);
                        break;
                    case "rock_cluster":
                        childCount = BuildRockCluster(root, request, s);
                        break;
                    case "simple_building":
                        childCount = BuildSimpleBuilding(root, request, s);
                        break;
                }

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "instanceId", root.GetInstanceID() },
                    { "name", rootName },
                    { "preset", preset },
                    { "childCount", childCount }
                });
            }
            catch (Exception ex)
            {
                return JsonError($"Failed to create compound shape: {ex.Message}");
            }
        }

        // ─── Tree ───────────────────────────────────────────────────────

        private static int BuildTree(GameObject root, CompoundShapeRequest req, float s)
        {
            var trunkColor = ParseColor(req.trunkColor, new Color(0.45f, 0.28f, 0.15f));
            var canopyColor = ParseColor(req.canopyColor, new Color(0.2f, 0.55f, 0.15f));
            float trunkH = req.trunkHeight * s;
            float trunkR = req.trunkRadius * s;
            float canopyH = req.canopyHeight * s;
            float canopyR = req.canopyRadius * s;

            // Trunk (cylinder)
            var trunk = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            trunk.name = "Trunk";
            trunk.transform.SetParent(root.transform, false);
            trunk.transform.localPosition = new Vector3(0, trunkH / 2f, 0);
            trunk.transform.localScale = new Vector3(trunkR * 2f, trunkH / 2f, trunkR * 2f);
            ApplyColor(trunk, trunkColor, 0f, 0.3f);

            // Canopy
            bool useSphere = !string.IsNullOrEmpty(req.canopyShape) &&
                             req.canopyShape.Equals("sphere", StringComparison.OrdinalIgnoreCase);

            if (useSphere)
            {
                var canopy = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                canopy.name = "Canopy";
                canopy.transform.SetParent(root.transform, false);
                canopy.transform.localPosition = new Vector3(0, trunkH + canopyR * 0.7f, 0);
                canopy.transform.localScale = new Vector3(canopyR * 2f, canopyR * 1.5f, canopyR * 2f);
                ApplyColor(canopy, canopyColor, 0f, 0.2f);
            }
            else
            {
                // Cone canopy — use a multi-layered approach with scaled spheres for now
                // since we can't call procedural mesh from here, use stacked scaled capsules
                var canopy = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                canopy.name = "Canopy";
                canopy.transform.SetParent(root.transform, false);
                canopy.transform.localPosition = new Vector3(0, trunkH + canopyH * 0.4f, 0);
                canopy.transform.localScale = new Vector3(canopyR * 2f, canopyH / 2f, canopyR * 2f);
                ApplyColor(canopy, canopyColor, 0f, 0.2f);
            }

            return 2;
        }

        // ─── Lantern ────────────────────────────────────────────────────

        private static int BuildLantern(GameObject root, CompoundShapeRequest req, float s)
        {
            var lightCol = ParseColor(req.lightColor, new Color(1f, 0.85f, 0.5f));
            float housingSize = req.housingSize * s;
            float postH = req.postHeight * s;
            float chainLen = req.chainLength * s;
            int parts = 0;

            if (chainLen > 0.01f)
            {
                // Hanging lantern — chain + housing
                var chain = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                chain.name = "Chain";
                chain.transform.SetParent(root.transform, false);
                chain.transform.localPosition = new Vector3(0, -chainLen / 2f, 0);
                chain.transform.localScale = new Vector3(0.02f * s, chainLen / 2f, 0.02f * s);
                ApplyColor(chain, new Color(0.3f, 0.3f, 0.3f), 0.8f, 0.4f);
                parts++;

                // Housing at bottom of chain
                var housing = GameObject.CreatePrimitive(PrimitiveType.Cube);
                housing.name = "Housing";
                housing.transform.SetParent(root.transform, false);
                housing.transform.localPosition = new Vector3(0, -chainLen - housingSize / 2f, 0);
                housing.transform.localScale = new Vector3(housingSize, housingSize * 1.2f, housingSize);
                ApplyColor(housing, new Color(0.35f, 0.25f, 0.15f), 0.2f, 0.3f);
                parts++;

                // Light inside
                var lightGo = new GameObject("Light");
                lightGo.transform.SetParent(root.transform, false);
                lightGo.transform.localPosition = new Vector3(0, -chainLen - housingSize / 2f, 0);
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = lightCol;
                light.intensity = req.lightIntensity;
                light.range = req.lightRange * s;
                light.shadows = LightShadows.Soft;
                parts++;
            }
            else
            {
                // Ground/post lantern
                if (postH > 0.05f)
                {
                    var post = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    post.name = "Post";
                    post.transform.SetParent(root.transform, false);
                    post.transform.localPosition = new Vector3(0, postH / 2f, 0);
                    post.transform.localScale = new Vector3(0.06f * s, postH / 2f, 0.06f * s);
                    ApplyColor(post, new Color(0.3f, 0.3f, 0.3f), 0.7f, 0.4f);
                    parts++;
                }

                // Housing
                var housing = GameObject.CreatePrimitive(PrimitiveType.Cube);
                housing.name = "Housing";
                housing.transform.SetParent(root.transform, false);
                housing.transform.localPosition = new Vector3(0, postH + housingSize * 0.6f, 0);
                housing.transform.localScale = new Vector3(housingSize, housingSize * 1.2f, housingSize);
                ApplyColor(housing, new Color(0.35f, 0.25f, 0.15f), 0.2f, 0.3f);
                parts++;

                // Roof cap
                var cap = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cap.name = "Cap";
                cap.transform.SetParent(root.transform, false);
                cap.transform.localPosition = new Vector3(0, postH + housingSize * 1.3f, 0);
                cap.transform.localScale = new Vector3(housingSize * 1.3f, housingSize * 0.15f, housingSize * 1.3f);
                ApplyColor(cap, new Color(0.25f, 0.2f, 0.15f), 0.5f, 0.3f);
                parts++;

                // Light
                var lightGo = new GameObject("Light");
                lightGo.transform.SetParent(root.transform, false);
                lightGo.transform.localPosition = new Vector3(0, postH + housingSize * 0.6f, 0);
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = lightCol;
                light.intensity = req.lightIntensity;
                light.range = req.lightRange * s;
                light.shadows = LightShadows.Soft;
                parts++;
            }

            return parts;
        }

        // ─── Steps ──────────────────────────────────────────────────────

        private static int BuildSteps(GameObject root, CompoundShapeRequest req, float s)
        {
            var stepCol = ParseColor(req.stepColor, new Color(0.6f, 0.55f, 0.5f));
            int count = Mathf.Clamp(req.stepCount, 1, 30);
            float w = req.stepWidth * s;
            float d = req.stepDepth * s;
            float rise = req.stepRise * s;

            for (int i = 0; i < count; i++)
            {
                var step = GameObject.CreatePrimitive(PrimitiveType.Cube);
                step.name = $"Step_{i}";
                step.transform.SetParent(root.transform, false);
                step.transform.localPosition = new Vector3(0, rise * (i + 0.5f), d * i);
                step.transform.localScale = new Vector3(w, rise, d);
                // Slightly vary color per step for visual depth
                float shade = 1f - (i * 0.03f);
                ApplyColor(step, stepCol * shade, 0f, 0.3f);
            }

            return count;
        }

        // ─── Fence ──────────────────────────────────────────────────────

        private static int BuildFence(GameObject root, CompoundShapeRequest req, float s)
        {
            var fenceCol = ParseColor(req.fenceColor, new Color(0.45f, 0.3f, 0.18f));
            float len = req.fenceLength * s;
            float h = req.fenceHeight * s;
            float spacing = Mathf.Max(0.3f, req.postSpacing * s);
            float r = req.postRadius * s;
            int parts = 0;

            int postCount = Mathf.Max(2, Mathf.CeilToInt(len / spacing) + 1);
            float actualSpacing = len / (postCount - 1);

            for (int i = 0; i < postCount; i++)
            {
                var post = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                post.name = $"Post_{i}";
                post.transform.SetParent(root.transform, false);
                post.transform.localPosition = new Vector3(actualSpacing * i - len / 2f, h / 2f, 0);
                post.transform.localScale = new Vector3(r * 2f, h / 2f, r * 2f);
                ApplyColor(post, fenceCol, 0f, 0.3f);
                parts++;
            }

            // Top rail
            var topRail = GameObject.CreatePrimitive(PrimitiveType.Cube);
            topRail.name = "TopRail";
            topRail.transform.SetParent(root.transform, false);
            topRail.transform.localPosition = new Vector3(0, h * 0.85f, 0);
            topRail.transform.localScale = new Vector3(len + r * 2f, r * 2f, r * 2f);
            ApplyColor(topRail, fenceCol * 0.9f, 0f, 0.3f);
            parts++;

            // Mid rail
            var midRail = GameObject.CreatePrimitive(PrimitiveType.Cube);
            midRail.name = "MidRail";
            midRail.transform.SetParent(root.transform, false);
            midRail.transform.localPosition = new Vector3(0, h * 0.4f, 0);
            midRail.transform.localScale = new Vector3(len + r * 2f, r * 2f, r * 2f);
            ApplyColor(midRail, fenceCol * 0.9f, 0f, 0.3f);
            parts++;

            return parts;
        }

        // ─── Rock Cluster ───────────────────────────────────────────────

        private static int BuildRockCluster(GameObject root, CompoundShapeRequest req, float s)
        {
            var rockCol = ParseColor(req.rockColor, new Color(0.5f, 0.48f, 0.44f));
            int count = Mathf.Clamp(req.rockCount, 1, 20);
            float radius = req.clusterRadius * s;
            float minSc = req.rockMinScale * s;
            float maxSc = req.rockMaxScale * s;
            var rng = new System.Random(req.seed);

            for (int i = 0; i < count; i++)
            {
                var rock = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                rock.name = $"Rock_{i}";
                rock.transform.SetParent(root.transform, false);

                // Random position within radius
                float angle = (float)(rng.NextDouble() * Math.PI * 2);
                float dist = (float)(rng.NextDouble() * radius);
                float x = Mathf.Cos(angle) * dist;
                float z = Mathf.Sin(angle) * dist;

                // Random non-uniform scale for natural look
                float baseScale = Mathf.Lerp(minSc, maxSc, (float)rng.NextDouble());
                float sx = baseScale * Mathf.Lerp(0.6f, 1.4f, (float)rng.NextDouble());
                float sy = baseScale * Mathf.Lerp(0.4f, 0.9f, (float)rng.NextDouble());
                float sz = baseScale * Mathf.Lerp(0.6f, 1.4f, (float)rng.NextDouble());

                rock.transform.localPosition = new Vector3(x, sy / 2f, z);
                rock.transform.localScale = new Vector3(sx, sy, sz);
                rock.transform.localRotation = Quaternion.Euler(
                    (float)(rng.NextDouble() * 20 - 10),
                    (float)(rng.NextDouble() * 360),
                    (float)(rng.NextDouble() * 20 - 10));

                // Slightly vary color
                float colorVar = Mathf.Lerp(0.85f, 1.15f, (float)rng.NextDouble());
                ApplyColor(rock, rockCol * colorVar, 0.1f, Mathf.Lerp(0.2f, 0.5f, (float)rng.NextDouble()));
            }

            return count;
        }

        // ─── Simple Building ────────────────────────────────────────────

        private static int BuildSimpleBuilding(GameObject root, CompoundShapeRequest req, float s)
        {
            var wallCol = ParseColor(req.wallColor, new Color(0.75f, 0.7f, 0.6f));
            var roofCol = ParseColor(req.roofColor, new Color(0.5f, 0.35f, 0.2f));
            float w = req.buildingWidth * s;
            float d = req.buildingDepth * s;
            float h = req.buildingHeight * s;
            float roofH = req.roofHeight * s;
            int parts = 0;

            // Walls (single cube)
            var walls = GameObject.CreatePrimitive(PrimitiveType.Cube);
            walls.name = "Walls";
            walls.transform.SetParent(root.transform, false);
            walls.transform.localPosition = new Vector3(0, h / 2f, 0);
            walls.transform.localScale = new Vector3(w, h, d);
            ApplyColor(walls, wallCol, 0f, 0.3f);
            parts++;

            bool isFlat = !string.IsNullOrEmpty(req.roofType) &&
                          req.roofType.Equals("flat", StringComparison.OrdinalIgnoreCase);

            if (isFlat)
            {
                // Flat roof — slightly wider than walls
                var roof = GameObject.CreatePrimitive(PrimitiveType.Cube);
                roof.name = "Roof";
                roof.transform.SetParent(root.transform, false);
                roof.transform.localPosition = new Vector3(0, h + 0.05f * s, 0);
                roof.transform.localScale = new Vector3(w * 1.1f, 0.1f * s, d * 1.1f);
                ApplyColor(roof, roofCol, 0.1f, 0.3f);
                parts++;
            }
            else
            {
                // Gable roof — two angled planes + ridge beam + gable fills
                float roofThickness = 0.08f * s;
                float roofSlope = Mathf.Atan2(roofH, w / 2f) * Mathf.Rad2Deg;
                float roofLen = Mathf.Sqrt(roofH * roofH + (w / 2f) * (w / 2f));
                // Extend panels slightly past the ridge so they overlap
                float overExtend = roofThickness / Mathf.Max(0.01f, Mathf.Cos(roofSlope * Mathf.Deg2Rad));

                var roofLeft = GameObject.CreatePrimitive(PrimitiveType.Cube);
                roofLeft.name = "RoofLeft";
                roofLeft.transform.SetParent(root.transform, false);
                roofLeft.transform.localPosition = new Vector3(-w / 4f, h + roofH / 2f, 0);
                roofLeft.transform.localRotation = Quaternion.Euler(0, 0, roofSlope);
                roofLeft.transform.localScale = new Vector3(roofLen + overExtend, roofThickness, d * 1.05f);
                ApplyColor(roofLeft, roofCol, 0.1f, 0.3f);
                parts++;

                var roofRight = GameObject.CreatePrimitive(PrimitiveType.Cube);
                roofRight.name = "RoofRight";
                roofRight.transform.SetParent(root.transform, false);
                roofRight.transform.localPosition = new Vector3(w / 4f, h + roofH / 2f, 0);
                roofRight.transform.localRotation = Quaternion.Euler(0, 0, -roofSlope);
                roofRight.transform.localScale = new Vector3(roofLen + overExtend, roofThickness, d * 1.05f);
                ApplyColor(roofRight, roofCol, 0.1f, 0.3f);
                parts++;

                // Ridge beam — covers the gap where panels meet at the peak
                var ridge = GameObject.CreatePrimitive(PrimitiveType.Cube);
                ridge.name = "Ridge";
                ridge.transform.SetParent(root.transform, false);
                ridge.transform.localPosition = new Vector3(0, h + roofH, 0);
                ridge.transform.localScale = new Vector3(roofThickness * 2f, roofThickness * 1.5f, d * 1.08f);
                ApplyColor(ridge, roofCol * 0.85f, 0.1f, 0.3f);
                parts++;

                // Gable fill triangles (front and back) — use thin cubes to fill the triangular gap
                // Front gable fill
                var gableFront = GameObject.CreatePrimitive(PrimitiveType.Cube);
                gableFront.name = "GableFront";
                gableFront.transform.SetParent(root.transform, false);
                gableFront.transform.localPosition = new Vector3(0, h + roofH * 0.4f, -d / 2f);
                gableFront.transform.localScale = new Vector3(w * 0.85f, roofH * 0.75f, 0.05f * s);
                ApplyColor(gableFront, wallCol, 0f, 0.3f);
                parts++;

                // Back gable fill
                var gableBack = GameObject.CreatePrimitive(PrimitiveType.Cube);
                gableBack.name = "GableBack";
                gableBack.transform.SetParent(root.transform, false);
                gableBack.transform.localPosition = new Vector3(0, h + roofH * 0.4f, d / 2f);
                gableBack.transform.localScale = new Vector3(w * 0.85f, roofH * 0.75f, 0.05f * s);
                ApplyColor(gableBack, wallCol, 0f, 0.3f);
                parts++;
            }

            // Floor
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Floor";
            floor.transform.SetParent(root.transform, false);
            floor.transform.localPosition = new Vector3(0, -0.025f * s, 0);
            floor.transform.localScale = new Vector3(w * 1.1f, 0.05f * s, d * 1.1f);
            ApplyColor(floor, wallCol * 0.8f, 0f, 0.4f);
            parts++;

            return parts;
        }

        // ─── Helpers ────────────────────────────────────────────────────

        private static void ApplyColor(GameObject go, Color color, float metallic, float smoothness)
        {
            var renderer = go.GetComponent<Renderer>();
            if (renderer == null) return;

            var shader = Shader.Find("Universal Render Pipeline/Lit")
                      ?? Shader.Find("Standard");
            if (shader == null) return;

            var mat = new Material(shader);
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", color);
            else if (mat.HasProperty("_Color"))
                mat.SetColor("_Color", color);
            if (mat.HasProperty("_Metallic"))
                mat.SetFloat("_Metallic", metallic);
            if (mat.HasProperty("_Smoothness"))
                mat.SetFloat("_Smoothness", smoothness);

            renderer.sharedMaterial = mat;
        }
    }
}
