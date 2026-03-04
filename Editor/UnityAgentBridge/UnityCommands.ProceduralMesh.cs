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
        [Serializable]
        private class ProceduralMeshRequest
        {
            public string meshType;      // cone, wedge, arch, torus, prism
            public string name;          // GameObject name
            public float[] position;     // [x,y,z]
            public float[] rotation;     // [x,y,z] euler
            public float[] scale;        // [x,y,z]
            public float[] color;        // [r,g,b,a]
            public int parentId = -1;
            // Shape params
            public float radius = 0.5f;
            public float height = 1f;
            public float depth = 1f;
            public float width = 1f;
            public int sides = 16;
            public float innerRadius = 0.3f;
            public float outerRadius = 0.5f;
            public float arcAngle = 180f;
            public int segments = 16;
            public float minorRadius = 0.15f;
            public float majorRadius = 0.5f;
            public int radialSegments = 12;
            public int tubularSegments = 24;
            public float metallic = -1f;
            public float smoothness = -1f;
        }

        [BridgeRoute("POST", "/mesh/procedural", Category = "gameobjects", Description = "Create a procedural mesh (cone, wedge, arch, torus, prism)")]
        public static string CreateProceduralMesh(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<ProceduralMeshRequest>(NormalizeColorFields(jsonData));
                if (string.IsNullOrEmpty(request.meshType))
                    return JsonError("meshType is required (cone, wedge, arch, torus, prism)");

                Mesh mesh;
                string meshTypeLower = request.meshType.ToLowerInvariant();
                switch (meshTypeLower)
                {
                    case "cone":
                        mesh = GenerateConeMesh(request.radius, request.height, request.sides);
                        break;
                    case "wedge":
                        mesh = GenerateWedgeMesh(request.width, request.height, request.depth);
                        break;
                    case "arch":
                        mesh = GenerateArchMesh(request.innerRadius, request.outerRadius, request.height, request.arcAngle, request.segments);
                        break;
                    case "torus":
                        mesh = GenerateTorusMesh(request.majorRadius, request.minorRadius, request.radialSegments, request.tubularSegments);
                        break;
                    case "prism":
                        mesh = GeneratePrismMesh(request.radius, request.depth, request.sides);
                        break;
                    default:
                        return JsonError($"Unknown meshType: '{request.meshType}'. Valid: cone, wedge, arch, torus, prism");
                }

                mesh.name = meshTypeLower + "_mesh";

                var go = new GameObject(string.IsNullOrEmpty(request.name) ? meshTypeLower : request.name);
                Undo.RegisterCreatedObjectUndo(go, "Agent Bridge Create Procedural Mesh");

                var mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();

                // Position
                if (request.position != null && request.position.Length >= 3)
                    go.transform.position = new Vector3(request.position[0], request.position[1], request.position[2]);

                // Rotation
                if (request.rotation != null && request.rotation.Length >= 3)
                    go.transform.eulerAngles = new Vector3(request.rotation[0], request.rotation[1], request.rotation[2]);

                // Scale
                if (request.scale != null && request.scale.Length >= 3)
                    go.transform.localScale = new Vector3(request.scale[0], request.scale[1], request.scale[2]);

                // Parent
                if (request.parentId != -1)
                {
                    var parent = EditorUtility.EntityIdToObject(request.parentId) as GameObject;
                    if (parent != null)
                        go.transform.SetParent(parent.transform);
                }

                // Material (same pattern as SpawnPrimitive)
                if (request.color != null && request.color.Length >= 3)
                {
                    var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                    var mat = new Material(shader);
                    var color = new Color(
                        request.color[0],
                        request.color[1],
                        request.color[2],
                        request.color.Length >= 4 ? request.color[3] : 1f);
                    mat.color = color;
                    if (mat.HasProperty("_BaseColor"))
                        mat.SetColor("_BaseColor", color);
                    if (request.metallic >= 0f)
                        mat.SetFloat("_Metallic", request.metallic);
                    if (request.smoothness >= 0f)
                    {
                        if (mat.HasProperty("_Smoothness"))
                            mat.SetFloat("_Smoothness", request.smoothness);
                        else if (mat.HasProperty("_Glossiness"))
                            mat.SetFloat("_Glossiness", request.smoothness);
                    }
                    mr.sharedMaterial = mat;
                }
                else
                {
                    // Default material
                    var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                    mr.sharedMaterial = new Material(shader);
                }

                EditorUtility.SetDirty(go);

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "instanceId", go.GetInstanceID() },
                    { "name", go.name },
                    { "meshType", meshTypeLower }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("POST", "/mesh/raw", Category = "gameobjects", Description = "Create a mesh from raw vertices, triangles, normals, and UVs")]
        public static string CreateRawMesh(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<RawMeshRequest>(NormalizeColorFields(jsonData));

                if (request.vertices == null || request.vertices.Length < 9)
                    return JsonError("vertices requires at least 3 vertices (9 floats: [x,y,z, x,y,z, x,y,z, ...])");
                if (request.vertices.Length % 3 != 0)
                    return JsonError("vertices length must be a multiple of 3 (x,y,z per vertex)");
                if (request.triangles == null || request.triangles.Length < 3)
                    return JsonError("triangles requires at least 3 indices");
                if (request.triangles.Length % 3 != 0)
                    return JsonError("triangles length must be a multiple of 3");

                int vertexCount = request.vertices.Length / 3;

                // Validate triangle indices
                for (int i = 0; i < request.triangles.Length; i++)
                {
                    if (request.triangles[i] < 0 || request.triangles[i] >= vertexCount)
                        return JsonError($"Triangle index {request.triangles[i]} at position {i} is out of range (0-{vertexCount - 1})");
                }

                // Build vertex array
                var verts = new Vector3[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                    verts[i] = new Vector3(request.vertices[i * 3], request.vertices[i * 3 + 1], request.vertices[i * 3 + 2]);

                var mesh = new Mesh();
                if (vertexCount > 65535)
                    mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.vertices = verts;
                mesh.triangles = request.triangles;

                // Normals
                if (request.normals != null && request.normals.Length == request.vertices.Length)
                {
                    var norms = new Vector3[vertexCount];
                    for (int i = 0; i < vertexCount; i++)
                        norms[i] = new Vector3(request.normals[i * 3], request.normals[i * 3 + 1], request.normals[i * 3 + 2]);
                    mesh.normals = norms;
                }
                else
                {
                    mesh.RecalculateNormals();
                }

                // UVs
                if (request.uvs != null && request.uvs.Length == vertexCount * 2)
                {
                    var uv = new Vector2[vertexCount];
                    for (int i = 0; i < vertexCount; i++)
                        uv[i] = new Vector2(request.uvs[i * 2], request.uvs[i * 2 + 1]);
                    mesh.uv = uv;
                }

                mesh.RecalculateBounds();

                string meshName = string.IsNullOrEmpty(request.name) ? "raw_mesh" : request.name;
                mesh.name = meshName;

                var go = new GameObject(meshName);
                Undo.RegisterCreatedObjectUndo(go, "Agent Bridge Create Raw Mesh");

                var mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();

                // Position, rotation, scale
                if (request.position != null && request.position.Length >= 3)
                    go.transform.position = new Vector3(request.position[0], request.position[1], request.position[2]);
                if (request.rotation != null && request.rotation.Length >= 3)
                    go.transform.eulerAngles = new Vector3(request.rotation[0], request.rotation[1], request.rotation[2]);
                if (request.scale != null && request.scale.Length >= 3)
                    go.transform.localScale = new Vector3(request.scale[0], request.scale[1], request.scale[2]);

                // Parent
                if (request.parentId != -1)
                {
                    var parent = EditorUtility.EntityIdToObject(request.parentId) as GameObject;
                    if (parent != null)
                        go.transform.SetParent(parent.transform);
                }

                // Material
                if (request.color != null && request.color.Length >= 3)
                {
                    var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                    var mat = new Material(shader);
                    var color = new Color(
                        request.color[0], request.color[1], request.color[2],
                        request.color.Length >= 4 ? request.color[3] : 1f);
                    mat.color = color;
                    if (mat.HasProperty("_BaseColor"))
                        mat.SetColor("_BaseColor", color);
                    if (request.metallic >= 0f)
                        mat.SetFloat("_Metallic", request.metallic);
                    if (request.smoothness >= 0f)
                    {
                        if (mat.HasProperty("_Smoothness"))
                            mat.SetFloat("_Smoothness", request.smoothness);
                        else if (mat.HasProperty("_Glossiness"))
                            mat.SetFloat("_Glossiness", request.smoothness);
                    }
                    mr.sharedMaterial = mat;
                }
                else
                {
                    var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                    mr.sharedMaterial = new Material(shader);
                }

                // Optionally save mesh as asset
                string savedPath = null;
                if (!string.IsNullOrEmpty(request.saveMeshPath))
                {
                    var assetPath = request.saveMeshPath;
                    if (!assetPath.StartsWith("Assets/"))
                        assetPath = "Assets/" + assetPath;
                    if (!assetPath.EndsWith(".asset"))
                        assetPath += ".asset";

                    if (ValidateAssetPath(assetPath) != null)
                    {
                        string dir = System.IO.Path.GetDirectoryName(assetPath);
                        if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
                            CreateFolderRecursive(dir);
                        AssetDatabase.CreateAsset(mesh, assetPath);
                        AssetDatabase.SaveAssets();
                        savedPath = assetPath;
                    }
                }

                EditorUtility.SetDirty(go);

                var result = new Dictionary<string, object>
                {
                    { "success", true },
                    { "instanceId", go.GetInstanceID() },
                    { "name", go.name },
                    { "vertexCount", vertexCount },
                    { "triangleCount", request.triangles.Length / 3 }
                };
                if (savedPath != null)
                    result["savedMeshPath"] = savedPath;

                return JsonResult(result);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        #region Procedural Mesh Generators

        private static Mesh GenerateConeMesh(float radius, float height, int sides)
        {
            sides = Mathf.Max(3, sides);
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var uvs = new List<Vector2>();

            // Apex
            vertices.Add(new Vector3(0f, height, 0f));
            uvs.Add(new Vector2(0.5f, 1f));

            // Base circle vertices (for the side)
            for (int i = 0; i <= sides; i++)
            {
                float angle = (float)i / sides * Mathf.PI * 2f;
                float x = Mathf.Cos(angle) * radius;
                float z = Mathf.Sin(angle) * radius;
                vertices.Add(new Vector3(x, 0f, z));
                uvs.Add(new Vector2((float)i / sides, 0f));
            }

            // Side triangles
            for (int i = 0; i < sides; i++)
            {
                triangles.Add(0);
                triangles.Add(i + 1);
                triangles.Add(i + 2);
            }

            // Base center
            int baseCenterIdx = vertices.Count;
            vertices.Add(new Vector3(0f, 0f, 0f));
            uvs.Add(new Vector2(0.5f, 0.5f));

            // Base circle vertices (separate for normals)
            int baseStartIdx = vertices.Count;
            for (int i = 0; i <= sides; i++)
            {
                float angle = (float)i / sides * Mathf.PI * 2f;
                float x = Mathf.Cos(angle) * radius;
                float z = Mathf.Sin(angle) * radius;
                vertices.Add(new Vector3(x, 0f, z));
                uvs.Add(new Vector2(x / (2f * radius) + 0.5f, z / (2f * radius) + 0.5f));
            }

            // Base triangles (wound in reverse for downward normal)
            for (int i = 0; i < sides; i++)
            {
                triangles.Add(baseCenterIdx);
                triangles.Add(baseStartIdx + i + 1);
                triangles.Add(baseStartIdx + i);
            }

            var mesh = new Mesh();
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.SetUVs(0, uvs);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh GenerateWedgeMesh(float width, float height, float depth)
        {
            // Right-triangle cross-section: bottom-left (0,0,0), bottom-right (0,0,depth), top-left (0,height,0)
            // Extruded along X by width
            float hw = width * 0.5f;

            var vertices = new Vector3[]
            {
                // Left face (triangle at x = -hw)
                new Vector3(-hw, 0f, 0f),
                new Vector3(-hw, height, 0f),
                new Vector3(-hw, 0f, depth),
                // Right face (triangle at x = +hw)
                new Vector3(hw, 0f, 0f),
                new Vector3(hw, 0f, depth),
                new Vector3(hw, height, 0f),
                // Bottom face
                new Vector3(-hw, 0f, 0f),
                new Vector3(-hw, 0f, depth),
                new Vector3(hw, 0f, depth),
                new Vector3(hw, 0f, 0f),
                // Back face (vertical at z=0)
                new Vector3(-hw, 0f, 0f),
                new Vector3(hw, 0f, 0f),
                new Vector3(hw, height, 0f),
                new Vector3(-hw, height, 0f),
                // Slope face (hypotenuse)
                new Vector3(-hw, height, 0f),
                new Vector3(hw, height, 0f),
                new Vector3(hw, 0f, depth),
                new Vector3(-hw, 0f, depth),
            };

            var triangles = new int[]
            {
                // Left face
                0, 1, 2,
                // Right face
                3, 4, 5,
                // Bottom
                6, 7, 8,
                6, 8, 9,
                // Back
                10, 11, 12,
                10, 12, 13,
                // Slope
                14, 15, 16,
                14, 16, 17,
            };

            var uvs = new Vector2[]
            {
                // Left face
                new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 0f),
                // Right face
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f),
                // Bottom
                new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f),
                // Back
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f),
                // Slope
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f), new Vector2(0f, 0f),
            };

            var mesh = new Mesh();
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.uv = uvs;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh GenerateArchMesh(float innerRadius, float outerRadius, float height, float arcAngle, int segments)
        {
            segments = Mathf.Max(2, segments);
            float arcRad = arcAngle * Mathf.Deg2Rad;
            float halfArc = arcRad * 0.5f;

            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var uvs = new List<Vector2>();

            // Generate arch as extruded arc cross-section
            // For each segment step, we have 4 vertices: inner-bottom, outer-bottom, outer-top, inner-top
            for (int i = 0; i <= segments; i++)
            {
                float t = (float)i / segments;
                float angle = -halfArc + t * arcRad;
                float cosA = Mathf.Cos(angle);
                float sinA = Mathf.Sin(angle);

                // Inner bottom, outer bottom, outer top, inner top
                vertices.Add(new Vector3(sinA * innerRadius, 0f, cosA * innerRadius));
                vertices.Add(new Vector3(sinA * outerRadius, 0f, cosA * outerRadius));
                vertices.Add(new Vector3(sinA * outerRadius, height, cosA * outerRadius));
                vertices.Add(new Vector3(sinA * innerRadius, height, cosA * innerRadius));

                uvs.Add(new Vector2(t, 0f));
                uvs.Add(new Vector2(t, 0.33f));
                uvs.Add(new Vector2(t, 0.67f));
                uvs.Add(new Vector2(t, 1f));
            }

            // Connect quads between segment strips
            for (int i = 0; i < segments; i++)
            {
                int b = i * 4;
                int n = (i + 1) * 4;

                // Outer face
                triangles.Add(b + 1); triangles.Add(n + 1); triangles.Add(n + 2);
                triangles.Add(b + 1); triangles.Add(n + 2); triangles.Add(b + 2);

                // Inner face
                triangles.Add(b + 0); triangles.Add(b + 3); triangles.Add(n + 3);
                triangles.Add(b + 0); triangles.Add(n + 3); triangles.Add(n + 0);

                // Top face
                triangles.Add(b + 3); triangles.Add(b + 2); triangles.Add(n + 2);
                triangles.Add(b + 3); triangles.Add(n + 2); triangles.Add(n + 3);

                // Bottom face
                triangles.Add(b + 0); triangles.Add(n + 0); triangles.Add(n + 1);
                triangles.Add(b + 0); triangles.Add(n + 1); triangles.Add(b + 1);
            }

            // Cap the two ends
            // Start cap (i=0)
            int s = 0;
            triangles.Add(s + 0); triangles.Add(s + 1); triangles.Add(s + 2);
            triangles.Add(s + 0); triangles.Add(s + 2); triangles.Add(s + 3);

            // End cap (i=segments)
            int e = segments * 4;
            triangles.Add(e + 0); triangles.Add(e + 2); triangles.Add(e + 1);
            triangles.Add(e + 0); triangles.Add(e + 3); triangles.Add(e + 2);

            var mesh = new Mesh();
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.SetUVs(0, uvs);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh GenerateTorusMesh(float majorRadius, float minorRadius, int radialSegments, int tubularSegments)
        {
            radialSegments = Mathf.Max(3, radialSegments);
            tubularSegments = Mathf.Max(3, tubularSegments);

            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var uvs = new List<Vector2>();

            for (int i = 0; i <= radialSegments; i++)
            {
                float u = (float)i / radialSegments * Mathf.PI * 2f;
                for (int j = 0; j <= tubularSegments; j++)
                {
                    float v = (float)j / tubularSegments * Mathf.PI * 2f;

                    float x = (majorRadius + minorRadius * Mathf.Cos(v)) * Mathf.Cos(u);
                    float y = minorRadius * Mathf.Sin(v);
                    float z = (majorRadius + minorRadius * Mathf.Cos(v)) * Mathf.Sin(u);

                    vertices.Add(new Vector3(x, y, z));
                    uvs.Add(new Vector2((float)i / radialSegments, (float)j / tubularSegments));
                }
            }

            for (int i = 0; i < radialSegments; i++)
            {
                for (int j = 0; j < tubularSegments; j++)
                {
                    int a = i * (tubularSegments + 1) + j;
                    int b = a + tubularSegments + 1;

                    triangles.Add(a);
                    triangles.Add(b);
                    triangles.Add(a + 1);

                    triangles.Add(a + 1);
                    triangles.Add(b);
                    triangles.Add(b + 1);
                }
            }

            var mesh = new Mesh();
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.SetUVs(0, uvs);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh GeneratePrismMesh(float radius, float depth, int sides)
        {
            sides = Mathf.Max(3, sides);
            float halfDepth = depth * 0.5f;

            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var uvs = new List<Vector2>();

            // Front cap center
            int frontCenter = vertices.Count;
            vertices.Add(new Vector3(0f, 0f, -halfDepth));
            uvs.Add(new Vector2(0.5f, 0.5f));

            // Front cap vertices
            int frontStart = vertices.Count;
            for (int i = 0; i <= sides; i++)
            {
                float angle = (float)i / sides * Mathf.PI * 2f;
                float x = Mathf.Cos(angle) * radius;
                float y = Mathf.Sin(angle) * radius;
                vertices.Add(new Vector3(x, y, -halfDepth));
                uvs.Add(new Vector2(Mathf.Cos(angle) * 0.5f + 0.5f, Mathf.Sin(angle) * 0.5f + 0.5f));
            }

            // Front cap triangles
            for (int i = 0; i < sides; i++)
            {
                triangles.Add(frontCenter);
                triangles.Add(frontStart + i + 1);
                triangles.Add(frontStart + i);
            }

            // Back cap center
            int backCenter = vertices.Count;
            vertices.Add(new Vector3(0f, 0f, halfDepth));
            uvs.Add(new Vector2(0.5f, 0.5f));

            // Back cap vertices
            int backStart = vertices.Count;
            for (int i = 0; i <= sides; i++)
            {
                float angle = (float)i / sides * Mathf.PI * 2f;
                float x = Mathf.Cos(angle) * radius;
                float y = Mathf.Sin(angle) * radius;
                vertices.Add(new Vector3(x, y, halfDepth));
                uvs.Add(new Vector2(Mathf.Cos(angle) * 0.5f + 0.5f, Mathf.Sin(angle) * 0.5f + 0.5f));
            }

            // Back cap triangles (reversed winding)
            for (int i = 0; i < sides; i++)
            {
                triangles.Add(backCenter);
                triangles.Add(backStart + i);
                triangles.Add(backStart + i + 1);
            }

            // Side quads
            for (int i = 0; i < sides; i++)
            {
                float angle0 = (float)i / sides * Mathf.PI * 2f;
                float angle1 = (float)(i + 1) / sides * Mathf.PI * 2f;
                float x0 = Mathf.Cos(angle0) * radius;
                float y0 = Mathf.Sin(angle0) * radius;
                float x1 = Mathf.Cos(angle1) * radius;
                float y1 = Mathf.Sin(angle1) * radius;

                int idx = vertices.Count;
                // 4 vertices per side quad (separate for normals)
                vertices.Add(new Vector3(x0, y0, -halfDepth));
                vertices.Add(new Vector3(x1, y1, -halfDepth));
                vertices.Add(new Vector3(x1, y1, halfDepth));
                vertices.Add(new Vector3(x0, y0, halfDepth));

                float u0 = (float)i / sides;
                float u1 = (float)(i + 1) / sides;
                uvs.Add(new Vector2(u0, 0f));
                uvs.Add(new Vector2(u1, 0f));
                uvs.Add(new Vector2(u1, 1f));
                uvs.Add(new Vector2(u0, 1f));

                triangles.Add(idx);
                triangles.Add(idx + 1);
                triangles.Add(idx + 2);
                triangles.Add(idx);
                triangles.Add(idx + 2);
                triangles.Add(idx + 3);
            }

            var mesh = new Mesh();
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.SetUVs(0, uvs);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        #endregion
    }
}
