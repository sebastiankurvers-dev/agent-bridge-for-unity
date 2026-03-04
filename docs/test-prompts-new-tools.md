# Test Prompts for New Tools

Copy-paste these into your MCP client (Claude Code, Cursor, etc.) to verify each new route works against a live Unity project.

---

## 1. Particle Templates

### 1a. Create fire effect
```
Create a fire particle effect at position [0, 0, 0] using the particle template system. Name it "TestFire".
```

### 1b. Create smoke with overrides
```
Create a smoke particle template at position [3, 0, 0] with scale 2 and intensity 0.5. Override the color to [0.3, 0.3, 0.4, 0.5]. Name it "TestSmoke".
```

### 1c. Create all 8 templates in a row
```
Create all 8 particle templates (fire, smoke, rain, sparks, snow, dust, fountain, fireflies) spaced 3 units apart along the X axis starting at x=0. Use default settings for each. Take a screenshot when done.
```

### 1d. Template with parenting
```
Create an empty GameObject called "VFX_Group" at [0, 0, 0]. Then create a sparks particle template as a child of that group at local position [0, 1, 0].
```

---

## 2. Raw Mesh API

### 2a. Simple triangle
```
Create a raw mesh with 3 vertices forming a triangle:
- vertices: [0,0,0, 1,0,0, 0.5,1,0]
- triangles: [0,1,2]
Give it a red color [1,0,0]. Name it "TestTriangle".
```

### 2b. Quad with UVs
```
Create a raw mesh quad with 4 vertices:
- vertices: [0,0,0, 2,0,0, 2,2,0, 0,2,0]
- triangles: [0,1,2, 0,2,3]
- uvs: [0,0, 1,0, 1,1, 0,1]
Give it a blue color. Name it "TestQuad".
```

### 2c. Save mesh as asset
```
Create a raw mesh pyramid (4 triangular faces + square base) at position [0, 0, 5]. Save the mesh to "Assets/Meshes/TestPyramid.asset". Give it a green color with metallic=0.5 and smoothness=0.7.
```

### 2d. Validation — bad indices
```
Try to create a raw mesh with vertices [0,0,0, 1,0,0, 0.5,1,0] but triangles [0,1,5]. This should fail with an out-of-range error.
```

---

## 3. Terrain — Create & Shape

### 3a. Create basic terrain
```
Create a new terrain called "TestTerrain" at position [0, 0, 0] with width=100, length=100, height=50, and heightmapResolution=129.
```

### 3b. Set noise heightmap
```
Get the instance ID of the terrain you just created, then set its heightmap to noise mode with noiseScale=0.02, noiseAmplitude=0.4, noiseOctaves=4, noiseSeed=42. Take a screenshot after.
```

### 3c. Set plateau heightmap
```
Set the terrain heights to plateau mode with plateauHeight=0.25, plateauRadius=0.2, plateauFalloff=0.1. Take a screenshot to verify there's a raised area in the center.
```

### 3d. Set flat heightmap
```
Set the terrain heights to flat mode with flatHeight=0.05. Verify it's nearly flat.
```

### 3e. Set slope heightmap
```
Set the terrain heights to slope mode from 0 to 0.3 along the Z direction. Take a screenshot.
```

### 3f. Get terrain info
```
Get the terrain info for the TestTerrain — show me its heightmap resolution, size, layer count, and tree count.
```

---

## 4. Terrain — Texture Layers & Painting

### 4a. Add terrain layer
```
First, create a procedural checkerboard texture at "Assets/Textures/TestChecker.png" (256x256, tileSize=32, color1=[0.2,0.6,0.1] green, color2=[0.1,0.3,0.05] dark green).
Then add it as a terrain layer to TestTerrain with tileSizeX=5, tileSizeY=5.
```

### 4b. Add second terrain layer
```
Create a procedural noise texture at "Assets/Textures/TestRock.png" (256x256, scale=0.1, color1=[0.4,0.35,0.3], color2=[0.6,0.55,0.5]).
Add it as a second terrain layer to TestTerrain with tileSizeX=3, tileSizeY=3.
```

### 4c. Fill terrain with base layer
```
Paint the terrain fully with layer index 0 (fill mode). Take a screenshot.
```

### 4d. Paint brush stroke
```
Paint layer index 1 (rock) at centerX=0.5, centerY=0.5 with radius=0.2 and opacity=0.9 using a circle brush. Take a screenshot to see the rock patch in the center.
```

### 4e. Paint multiple patches
```
Paint 3 rock patches on the terrain at different positions:
- centerX=0.2, centerY=0.3, radius=0.1
- centerX=0.7, centerY=0.6, radius=0.15
- centerX=0.4, centerY=0.8, radius=0.08
Take a screenshot after.
```

---

## 5. Terrain — Trees

### 5a. Place trees (needs a tree prefab)
```
Check if there are any prefabs in the project that could work as trees (search for "tree" in prefabs). If found, place 30 trees on the terrain with random height scale 0.7-1.4 and width scale 0.8-1.2. If no tree prefab exists, create a simple green cylinder prefab first.
```

### 5b. Place trees with slope constraint
```
Place 50 trees on the terrain but only on slopes less than 20 degrees and at altitudes between 0.05 and 0.8. Use seed=123 for reproducibility.
```

### 5c. Verify tree placement
```
Get the terrain info and confirm the tree instance count increased. Take a screenshot showing the trees.
```

---

## 6. Procedural Mesh Presets (from previous session)

### 6a. Create all 5 types
```
Create one of each procedural mesh type (cone, wedge, arch, torus, prism) spaced 3 units apart along the X axis. Use different colors for each. Take a screenshot.
```

---

## 7. Procedural Skybox (from previous session)

### 7a. Create and apply sunset skybox
```
Create a procedural skybox with warm sunset colors: skyTint=[1,0.6,0.3], groundColor=[0.2,0.15,0.1], sunSize=0.06, atmosphereThickness=2.5, exposure=1.5. Apply it to the scene. Take a screenshot.
```

---

## 8. Group & Scatter Objects (from previous session)

### 8a. Group objects
```
Create 3 cubes at positions [0,0,0], [2,0,0], and [4,0,0]. Then group them under a parent called "CubeGroup". Verify the group is centered on the children.
```

### 8b. Scatter objects
```
Create a sphere, then scatter 20 copies of it within a bounding box centered at [0,1,0] with size [10,0.5,10]. Use random rotation on Y axis (0-360) and scale range 0.5-1.5. Use seed=42.
```

---

## 9. Compare Improvements (from previous session)

### 9a. Suggestions in compare response
```
Take a screenshot of the current scene. Store it as a reference. Then change the directional light intensity to 3x its current value. Take another screenshot and compare with the reference. Check that the response includes a "suggestions" array with actionable hints about brightness.
```

### 9b. Convergence tracking
```
Take a reference screenshot. Then run unity_capture_and_compare 5 times in a row without making any changes. The convergence metadata should show plateau=true after 3 calls since similarity stays the same.
```

### 9c. Hotspot compass mapping
```
Compare two screenshots and check that the response includes "hotspotSummary" with compass directions (top-left, center, bottom-right, etc.) instead of raw grid indices.
```

---

## 10. Execute C# Safety (from previous session)

### 10a. Material auto-rewrite
```
Run execute_csharp with this code:
var r = GameObject.Find("Cube").GetComponent<Renderer>();
r.material.color = Color.red;

Check the response — it should warn about auto-replacing .material with .sharedMaterial.
```

### 10b. Namespace resolution
```
Run execute_csharp with this code:
float x = Random.Range(0f, 10f);
Debug.Log($"Random value: {x}");
Print($"Random value: {x}");

This should compile without namespace ambiguity errors (Random resolves to UnityEngine.Random).
```

---

## 11. Workflow Guide Discovery

### 11a. Find terrain workflow
```
Call unity_workflow_guide with task="terrain" and verify it returns the Terrain workflow with all 6 terrain tools listed.
```

### 11b. Find particle workflow
```
Call unity_workflow_guide with task="particle effects" and verify it returns the Particles & VFX workflow with the template tool listed.
```

### 11c. List all categories
```
Call unity_workflow_guide with task="list" and verify both "Terrain" and "Particles & VFX" appear in the category list.
```

---

## Full Integration Test

```
Build a small outdoor scene from scratch:
1. Create a terrain (150x150, height 40)
2. Shape it with noise (amplitude 0.3, octaves 4)
3. Add a grass texture layer and fill the terrain with it
4. Add a rock texture layer and paint it on the steep areas
5. Create a procedural sunset skybox and apply it
6. Add a directional light as sun
7. Create a fire particle effect on top of the terrain
8. Create a smoke particle effect above the fire
9. Place some scattered cubes as "buildings"
10. Take a screenshot and verify the scene looks reasonable
```
