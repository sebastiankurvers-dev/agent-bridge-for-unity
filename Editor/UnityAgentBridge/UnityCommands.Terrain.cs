using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge
{
    public static partial class UnityCommands
    {
        #region Terrain

        [BridgeRoute("POST", "/terrain", Category = "terrain", Description = "Create a new terrain")]
        public static string CreateTerrain(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<CreateTerrainRequest>(jsonData);

                // Validate heightmap resolution (must be 2^n+1)
                int res = request.heightmapResolution;
                int valid = res - 1;
                if (valid < 32 || (valid & (valid - 1)) != 0)
                    return JsonError($"heightmapResolution must be 2^n+1 (33, 65, 129, 257, 513, 1025). Got: {res}");

                var terrainData = new TerrainData();
                terrainData.heightmapResolution = res;
                terrainData.size = new Vector3(request.terrainWidth, request.terrainHeight, request.terrainLength);

                string goName = string.IsNullOrEmpty(request.name) ? "Terrain" : request.name;
                var go = Terrain.CreateTerrainGameObject(terrainData);
                go.name = goName;
                Undo.RegisterCreatedObjectUndo(go, "Agent Bridge Create Terrain");

                if (request.position != null && request.position.Length >= 3)
                    go.transform.position = new Vector3(request.position[0], request.position[1], request.position[2]);

                if (request.parentId != -1)
                {
                    var parent = EditorUtility.EntityIdToObject(request.parentId) as GameObject;
                    if (parent != null)
                        go.transform.SetParent(parent.transform);
                }

                // Save TerrainData as asset so it persists
                string assetPath = $"Assets/{goName}_TerrainData.asset";
                assetPath = AssetDatabase.GenerateUniqueAssetPath(assetPath);
                AssetDatabase.CreateAsset(terrainData, assetPath);
                AssetDatabase.SaveAssets();

                EditorUtility.SetDirty(go);

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "instanceId", go.GetInstanceID() },
                    { "name", go.name },
                    { "terrainDataPath", assetPath },
                    { "heightmapResolution", res },
                    { "size", new Dictionary<string, object> { { "x", request.terrainWidth }, { "y", request.terrainHeight }, { "z", request.terrainLength } } }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("GET", "/terrain", Category = "terrain", Description = "Get terrain info", ReadOnly = true)]
        public static (string body, int statusCode) GetTerrainInfo(string path, string method, string body, System.Collections.Specialized.NameValueCollection query)
        {
            int instanceId = 0;
            var idStr = query["instanceId"];
            if (string.IsNullOrEmpty(idStr) || !int.TryParse(idStr, out instanceId) || instanceId == 0)
                return (JsonError("instanceId is required"), 400);

            var go = EditorUtility.EntityIdToObject(instanceId) as GameObject;
            if (go == null) return (JsonError("GameObject not found"), 404);

            var terrain = go.GetComponent<Terrain>();
            if (terrain == null) return (JsonError("No Terrain component on this GameObject"), 400);

            var td = terrain.terrainData;
            if (td == null) return (JsonError("TerrainData is null"), 400);

            var layers = new List<object>();
            if (td.terrainLayers != null)
            {
                foreach (var layer in td.terrainLayers)
                {
                    if (layer == null) continue;
                    layers.Add(new Dictionary<string, object>
                    {
                        { "diffusePath", layer.diffuseTexture != null ? AssetDatabase.GetAssetPath(layer.diffuseTexture) : "" },
                        { "normalPath", layer.normalMapTexture != null ? AssetDatabase.GetAssetPath(layer.normalMapTexture) : "" },
                        { "tileSize", new float[] { layer.tileSize.x, layer.tileSize.y } },
                        { "metallic", layer.metallic },
                        { "smoothness", layer.smoothness }
                    });
                }
            }

            return (JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "instanceId", instanceId },
                { "name", go.name },
                { "heightmapResolution", td.heightmapResolution },
                { "size", new float[] { td.size.x, td.size.y, td.size.z } },
                { "alphamapResolution", td.alphamapResolution },
                { "layerCount", td.terrainLayers != null ? td.terrainLayers.Length : 0 },
                { "layers", layers },
                { "treeInstanceCount", td.treeInstanceCount },
                { "treePrototypeCount", td.treePrototypes != null ? td.treePrototypes.Length : 0 }
            }), 200);
        }

        [BridgeRoute("PUT", "/terrain/heights", Category = "terrain", Description = "Set terrain heightmap")]
        public static string SetTerrainHeights(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<SetTerrainHeightsRequest>(jsonData);

                var go = EditorUtility.EntityIdToObject(request.instanceId) as GameObject;
                if (go == null) return JsonError("GameObject not found");

                var terrain = go.GetComponent<Terrain>();
                if (terrain == null) return JsonError("No Terrain component");

                var td = terrain.terrainData;
                if (td == null) return JsonError("TerrainData is null");

                Undo.RecordObject(td, "Agent Bridge Set Terrain Heights");

                int res = td.heightmapResolution;
                float[,] heights = new float[res, res];

                string mode = string.IsNullOrEmpty(request.mode) ? "flat" : request.mode.ToLowerInvariant();

                switch (mode)
                {
                    case "raw":
                        if (request.heights == null || request.heights.Length != res * res)
                            return JsonError($"raw mode requires {res * res} height values ({res}x{res}), got {request.heights?.Length ?? 0}");
                        for (int y = 0; y < res; y++)
                            for (int x = 0; x < res; x++)
                                heights[y, x] = Mathf.Clamp01(request.heights[y * res + x]);
                        break;

                    case "flat":
                        float h = Mathf.Clamp01(request.flatHeight);
                        for (int y = 0; y < res; y++)
                            for (int x = 0; x < res; x++)
                                heights[y, x] = h;
                        break;

                    case "slope":
                        bool alongX = request.slopeDirection == "x";
                        for (int y = 0; y < res; y++)
                            for (int x = 0; x < res; x++)
                            {
                                float t = alongX ? (float)x / (res - 1) : (float)y / (res - 1);
                                heights[y, x] = Mathf.Lerp(request.slopeFrom, request.slopeTo, t);
                            }
                        break;

                    case "plateau":
                        float pr = Mathf.Max(0.01f, request.plateauRadius);
                        float pf = Mathf.Max(0.01f, request.plateauFalloff);
                        for (int y = 0; y < res; y++)
                            for (int x = 0; x < res; x++)
                            {
                                float nx = (float)x / (res - 1) - 0.5f;
                                float ny = (float)y / (res - 1) - 0.5f;
                                float dist = Mathf.Sqrt(nx * nx + ny * ny);
                                float factor = 1f - Mathf.Clamp01((dist - pr) / pf);
                                heights[y, x] = request.plateauHeight * factor;
                            }
                        break;

                    case "noise":
                        int seed = request.noiseSeed != 0 ? request.noiseSeed : UnityEngine.Random.Range(0, 100000);
                        float scale = Mathf.Max(0.001f, request.noiseScale);
                        float amp = request.noiseAmplitude;
                        int octaves = Mathf.Clamp(request.noiseOctaves, 1, 8);
                        float persistence = Mathf.Clamp01(request.noisePersistence);
                        for (int y = 0; y < res; y++)
                            for (int x = 0; x < res; x++)
                            {
                                float value = 0f;
                                float frequency = 1f;
                                float amplitude = 1f;
                                float maxVal = 0f;
                                for (int o = 0; o < octaves; o++)
                                {
                                    float sx = (x + seed) * scale * frequency;
                                    float sy = (y + seed) * scale * frequency;
                                    value += Mathf.PerlinNoise(sx, sy) * amplitude;
                                    maxVal += amplitude;
                                    amplitude *= persistence;
                                    frequency *= 2f;
                                }
                                heights[y, x] = Mathf.Clamp01((value / maxVal) * amp);
                            }
                        break;

                    default:
                        return JsonError($"Unknown mode: '{mode}'. Valid: raw, noise, flat, slope, plateau");
                }

                td.SetHeights(0, 0, heights);
                EditorUtility.SetDirty(td);

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "instanceId", request.instanceId },
                    { "mode", mode },
                    { "resolution", res }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("POST", "/terrain/layer", Category = "terrain", Description = "Add a terrain texture layer")]
        public static string AddTerrainLayer(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<AddTerrainLayerRequest>(jsonData);

                var go = EditorUtility.EntityIdToObject(request.instanceId) as GameObject;
                if (go == null) return JsonError("GameObject not found");

                var terrain = go.GetComponent<Terrain>();
                if (terrain == null) return JsonError("No Terrain component");

                var td = terrain.terrainData;
                if (td == null) return JsonError("TerrainData is null");

                Undo.RecordObject(td, "Agent Bridge Add Terrain Layer");

                // Load diffuse texture
                Texture2D diffuse = null;
                if (!string.IsNullOrEmpty(request.diffusePath))
                {
                    if (ValidateAssetPath(request.diffusePath) == null)
                        return JsonError($"Invalid diffuse path: {request.diffusePath}");
                    diffuse = AssetDatabase.LoadAssetAtPath<Texture2D>(request.diffusePath);
                    if (diffuse == null)
                        return JsonError($"Texture not found at: {request.diffusePath}");
                }

                // Load normal map
                Texture2D normal = null;
                if (!string.IsNullOrEmpty(request.normalPath))
                {
                    if (ValidateAssetPath(request.normalPath) != null)
                        normal = AssetDatabase.LoadAssetAtPath<Texture2D>(request.normalPath);
                }

                var layer = new TerrainLayer
                {
                    diffuseTexture = diffuse,
                    normalMapTexture = normal,
                    tileSize = new Vector2(request.tileSizeX, request.tileSizeY),
                    metallic = request.metallic,
                    smoothness = request.smoothness
                };

                if (request.tint != null && request.tint.Length >= 3)
                {
                    layer.specular = new Color(
                        request.tint[0], request.tint[1], request.tint[2],
                        request.tint.Length >= 4 ? request.tint[3] : 1f);
                }

                // Save layer as asset
                string layerName = diffuse != null ? diffuse.name : "TerrainLayer";
                string layerPath = AssetDatabase.GenerateUniqueAssetPath($"Assets/{layerName}.terrainlayer");
                AssetDatabase.CreateAsset(layer, layerPath);

                // Add to terrain
                var existingLayers = td.terrainLayers != null ? td.terrainLayers.ToList() : new List<TerrainLayer>();
                existingLayers.Add(layer);
                td.terrainLayers = existingLayers.ToArray();

                AssetDatabase.SaveAssets();
                EditorUtility.SetDirty(td);

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "instanceId", request.instanceId },
                    { "layerIndex", existingLayers.Count - 1 },
                    { "layerPath", layerPath },
                    { "totalLayers", existingLayers.Count }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("PUT", "/terrain/paint", Category = "terrain", Description = "Paint a terrain texture layer")]
        public static string PaintTerrain(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<PaintTerrainRequest>(jsonData);

                var go = EditorUtility.EntityIdToObject(request.instanceId) as GameObject;
                if (go == null) return JsonError("GameObject not found");

                var terrain = go.GetComponent<Terrain>();
                if (terrain == null) return JsonError("No Terrain component");

                var td = terrain.terrainData;
                if (td == null) return JsonError("TerrainData is null");

                int layerCount = td.terrainLayers != null ? td.terrainLayers.Length : 0;
                if (layerCount == 0)
                    return JsonError("No terrain layers. Add at least one layer first with POST /terrain/layer");
                if (request.layerIndex < 0 || request.layerIndex >= layerCount)
                    return JsonError($"layerIndex {request.layerIndex} out of range (0-{layerCount - 1})");

                Undo.RecordObject(td, "Agent Bridge Paint Terrain");

                int alphaRes = td.alphamapResolution;
                float[,,] alphamaps = td.GetAlphamaps(0, 0, alphaRes, alphaRes);

                if (request.fill == 1)
                {
                    // Fill entire terrain with this layer
                    for (int y = 0; y < alphaRes; y++)
                        for (int x = 0; x < alphaRes; x++)
                        {
                            for (int l = 0; l < layerCount; l++)
                                alphamaps[y, x, l] = l == request.layerIndex ? 1f : 0f;
                        }
                }
                else
                {
                    // Paint brush
                    float cx = Mathf.Clamp01(request.centerX) * (alphaRes - 1);
                    float cy = Mathf.Clamp01(request.centerY) * (alphaRes - 1);
                    float radius = Mathf.Max(0.001f, request.radius) * alphaRes;
                    float opacity = Mathf.Clamp01(request.opacity);
                    bool isCircle = request.shape != "square";

                    int minX = Mathf.Max(0, (int)(cx - radius));
                    int maxX = Mathf.Min(alphaRes - 1, (int)(cx + radius));
                    int minY = Mathf.Max(0, (int)(cy - radius));
                    int maxY = Mathf.Min(alphaRes - 1, (int)(cy + radius));

                    for (int y = minY; y <= maxY; y++)
                        for (int x = minX; x <= maxX; x++)
                        {
                            float strength = opacity;
                            if (isCircle)
                            {
                                float dist = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                                if (dist > radius) continue;
                                strength *= 1f - (dist / radius); // Soft falloff
                            }

                            // Blend: increase target layer, decrease others proportionally
                            float current = alphamaps[y, x, request.layerIndex];
                            float newVal = Mathf.Clamp01(current + strength);
                            float delta = newVal - current;
                            alphamaps[y, x, request.layerIndex] = newVal;

                            // Redistribute remaining weight among other layers
                            float otherSum = 0f;
                            for (int l = 0; l < layerCount; l++)
                                if (l != request.layerIndex) otherSum += alphamaps[y, x, l];

                            if (otherSum > 0f)
                            {
                                float scale = (1f - newVal) / otherSum;
                                for (int l = 0; l < layerCount; l++)
                                    if (l != request.layerIndex) alphamaps[y, x, l] *= scale;
                            }
                        }
                }

                td.SetAlphamaps(0, 0, alphamaps);
                EditorUtility.SetDirty(td);

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "instanceId", request.instanceId },
                    { "layerIndex", request.layerIndex },
                    { "mode", request.fill == 1 ? "fill" : "brush" }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("POST", "/terrain/trees", Category = "terrain", Description = "Place trees on terrain")]
        public static string PlaceTerrainTrees(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<PlaceTerrainTreesRequest>(NormalizeColorFields(jsonData));

                var go = EditorUtility.EntityIdToObject(request.instanceId) as GameObject;
                if (go == null) return JsonError("GameObject not found");

                var terrain = go.GetComponent<Terrain>();
                if (terrain == null) return JsonError("No Terrain component");

                var td = terrain.terrainData;
                if (td == null) return JsonError("TerrainData is null");

                if (string.IsNullOrEmpty(request.prefabPath))
                    return JsonError("prefabPath is required");

                var validatedPath = ValidateAssetPath(request.prefabPath);
                if (validatedPath == null)
                    return JsonError($"Invalid prefab path: {request.prefabPath}");

                var treePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(validatedPath);
                if (treePrefab == null)
                    return JsonError($"Prefab not found at: {validatedPath}");

                Undo.RecordObject(td, "Agent Bridge Place Trees");

                // Find or add tree prototype
                int protoIndex = -1;
                var protos = td.treePrototypes != null ? td.treePrototypes.ToList() : new List<TreePrototype>();
                for (int i = 0; i < protos.Count; i++)
                {
                    if (protos[i].prefab == treePrefab)
                    {
                        protoIndex = i;
                        break;
                    }
                }
                if (protoIndex < 0)
                {
                    protos.Add(new TreePrototype { prefab = treePrefab });
                    td.treePrototypes = protos.ToArray();
                    protoIndex = protos.Count - 1;
                }

                // Place trees
                int count = Mathf.Clamp(request.count, 1, 5000);
                var rng = request.seed != 0 ? new System.Random(request.seed) : new System.Random();

                Color treeColor = Color.white;
                if (request.color != null && request.color.Length >= 3)
                    treeColor = new Color(request.color[0], request.color[1], request.color[2],
                        request.color.Length >= 4 ? request.color[3] : 1f);

                var existingTrees = td.treeInstances.ToList();
                int placed = 0;
                int maxAttempts = count * 5;
                int attempts = 0;

                while (placed < count && attempts < maxAttempts)
                {
                    attempts++;
                    float x = (float)rng.NextDouble();
                    float z = (float)rng.NextDouble();

                    // Check altitude constraint
                    float height = td.GetInterpolatedHeight(x, z) / td.size.y;
                    if (height < request.minAltitude || height > request.maxAltitude)
                        continue;

                    // Check slope constraint
                    float slope = td.GetSteepness(x, z);
                    if (slope < request.minSlope || slope > request.maxSlope)
                        continue;

                    float treeHeight = Mathf.Lerp(request.minHeight, request.maxHeight, (float)rng.NextDouble());
                    float treeWidth = Mathf.Lerp(request.minWidth, request.maxWidth, (float)rng.NextDouble());

                    existingTrees.Add(new TreeInstance
                    {
                        prototypeIndex = protoIndex,
                        position = new Vector3(x, height, z),
                        heightScale = treeHeight,
                        widthScale = treeWidth,
                        color = treeColor,
                        lightmapColor = Color.white,
                        rotation = (float)(rng.NextDouble() * Mathf.PI * 2f)
                    });
                    placed++;
                }

                td.treeInstances = existingTrees.ToArray();
                EditorUtility.SetDirty(td);
                terrain.Flush();

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "instanceId", request.instanceId },
                    { "treesPlaced", placed },
                    { "totalTreeInstances", existingTrees.Count },
                    { "prototypeIndex", protoIndex }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        #endregion
    }
}
