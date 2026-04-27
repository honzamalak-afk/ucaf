using UnityEngine;
using UnityEditor;
using System;
using System.IO;
using System.Collections.Generic;
using System.Globalization;

public static partial class UCAF_Listener
{
    // ── Terrain (v4.2 Phase D, FR-167–170) ──────────────────────────────

    // FR-167: create_terrain
    static UCAFResult CmdCreateTerrain(UCAFCommand cmd)
    {
        string dataPath = cmd.GetParam("data_asset_path", "");
        if (string.IsNullOrEmpty(dataPath))
            return new UCAFResult { success = false, message = "data_asset_path required (e.g. Assets/Terrain/MyTerrain.asset)." };

        float width  = float.TryParse(cmd.GetParam("width",  "500"), NumberStyles.Float, CultureInfo.InvariantCulture, out float w)  ? w  : 500f;
        float height = float.TryParse(cmd.GetParam("height", "50"),  NumberStyles.Float, CultureInfo.InvariantCulture, out float h)  ? h  : 50f;
        float length = float.TryParse(cmd.GetParam("length", "500"), NumberStyles.Float, CultureInfo.InvariantCulture, out float ll) ? ll : 500f;
        int resolution = int.TryParse(cmd.GetParam("heightmap_resolution", "513"), out int r) ? r : 513;

        int[] valid = { 33, 65, 129, 257, 513, 1025, 2049, 4097 };
        int bestRes = valid[0];
        foreach (int v in valid) if (v <= resolution) bestRes = v;
        resolution = bestRes;

        string dir = Path.GetDirectoryName(dataPath);
        if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
        {
            Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir));
            AssetDatabase.Refresh();
        }

        var data = new TerrainData();
        data.heightmapResolution = resolution;
        data.size = new Vector3(width, height, length);
        AssetDatabase.CreateAsset(data, dataPath);
        AssetDatabase.SaveAssets();

        string objName = cmd.GetParam("obj_name", Path.GetFileNameWithoutExtension(dataPath));
        TryParseVector3(cmd.GetParam("position", "0,0,0"), out Vector3 pos);

        var go = Terrain.CreateTerrainGameObject(data);
        go.name = objName;
        go.transform.position = pos;

        string parentPath = cmd.GetParam("parent", "");
        if (!string.IsNullOrEmpty(parentPath))
        {
            var parentGo = GameObject.Find(parentPath);
            if (parentGo != null) go.transform.SetParent(parentGo.transform, true);
        }

        Undo.RegisterCreatedObjectUndo(go, "Create Terrain");
        EditorUtility.SetDirty(go);

        return new UCAFResult {
            success   = true,
            message   = $"Terrain '{objName}' created ({width}×{height}×{length}, res={resolution}).",
            data_json = JsonUtility.ToJson(BuildTerrainInfo(go, data, dataPath))
        };
    }

    // FR-168: set_terrain_heightmap
    static UCAFResult CmdSetTerrainHeightmap(UCAFCommand cmd)
    {
        var obj = ResolveObject(cmd, out string err);
        if (obj == null) return new UCAFResult { success = false, message = err };

        var terrain = obj.GetComponent<Terrain>();
        if (terrain == null)
            return new UCAFResult { success = false, message = $"No Terrain component on '{obj.name}'." };

        string source = cmd.GetParam("source", "flat").ToLowerInvariant();
        var data = terrain.terrainData;
        int res = data.heightmapResolution;

        if (source == "flat")
        {
            float level = float.TryParse(cmd.GetParam("level", "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out float lv) ? lv : 0f;
            float[,] heights = new float[res, res];
            float clamped = Mathf.Clamp01(level);
            for (int y = 0; y < res; y++)
                for (int x = 0; x < res; x++)
                    heights[y, x] = clamped;
            data.SetHeights(0, 0, heights);
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            return new UCAFResult { success = true, message = $"Heightmap set flat at level={level:F3}." };
        }

        if (source == "perlin")
        {
            float scale     = float.TryParse(cmd.GetParam("perlin_scale", "0.03"), NumberStyles.Float, CultureInfo.InvariantCulture, out float sc)  ? sc  : 0.03f;
            float amplitude = float.TryParse(cmd.GetParam("amplitude",    "1"),    NumberStyles.Float, CultureInfo.InvariantCulture, out float amp) ? amp : 1f;
            float offsetX   = float.TryParse(cmd.GetParam("offset_x",     "0"),    NumberStyles.Float, CultureInfo.InvariantCulture, out float ox)  ? ox  : 0f;
            float offsetZ   = float.TryParse(cmd.GetParam("offset_z",     "0"),    NumberStyles.Float, CultureInfo.InvariantCulture, out float oz)  ? oz  : 0f;

            float[,] heights = new float[res, res];
            for (int y = 0; y < res; y++)
                for (int x = 0; x < res; x++)
                    heights[y, x] = Mathf.Clamp01(
                        Mathf.PerlinNoise((x + offsetX) * scale, (y + offsetZ) * scale) * amplitude);
            data.SetHeights(0, 0, heights);
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            return new UCAFResult { success = true, message = $"Heightmap set via Perlin (scale={scale}, amplitude={amplitude})." };
        }

        if (source == "png")
        {
            string filePath = cmd.GetParam("file_path", "");
            if (!File.Exists(filePath))
                return new UCAFResult { success = false, message = $"file_path not found: {filePath}" };

            var tex = new Texture2D(2, 2);
            if (!tex.LoadImage(File.ReadAllBytes(filePath)))
                return new UCAFResult { success = false, message = "Failed to load PNG." };

            float[,] heights = new float[res, res];
            for (int y = 0; y < res; y++)
                for (int x = 0; x < res; x++)
                    heights[y, x] = tex.GetPixelBilinear((float)x / (res - 1), (float)y / (res - 1)).r;
            UnityEngine.Object.DestroyImmediate(tex);
            data.SetHeights(0, 0, heights);
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            return new UCAFResult { success = true, message = $"Heightmap loaded from PNG: {filePath}" };
        }

        if (source == "raw")
        {
            string filePath = cmd.GetParam("file_path", "");
            if (!File.Exists(filePath))
                return new UCAFResult { success = false, message = $"file_path not found: {filePath}" };

            byte[] raw = File.ReadAllBytes(filePath);
            bool is16bit = raw.Length == res * res * 2;
            bool is8bit  = raw.Length == res * res;
            if (!is16bit && !is8bit)
                return new UCAFResult { success = false, message = $"RAW size {raw.Length} doesn't match terrain res {res} (expected {res*res*2} bytes for 16-bit)." };

            bool bigEndian = cmd.GetParam("raw_byte_order", "little") == "big";
            float[,] heights = new float[res, res];
            for (int y = 0; y < res; y++)
                for (int x = 0; x < res; x++)
                {
                    int i = y * res + x;
                    heights[y, x] = is16bit
                        ? (bigEndian
                            ? (ushort)((raw[i*2] << 8) | raw[i*2+1])
                            : (ushort)(raw[i*2] | (raw[i*2+1] << 8))) / 65535f
                        : raw[i] / 255f;
                }
            data.SetHeights(0, 0, heights);
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            return new UCAFResult { success = true, message = $"Heightmap loaded from RAW: {filePath}" };
        }

        return new UCAFResult { success = false, message = $"Unknown source '{source}'. Use: flat, perlin, png, raw." };
    }

    // FR-169: paint_terrain_layer
    static UCAFResult CmdPaintTerrainLayer(UCAFCommand cmd)
    {
        var obj = ResolveObject(cmd, out string err);
        if (obj == null) return new UCAFResult { success = false, message = err };

        var terrain = obj.GetComponent<Terrain>();
        if (terrain == null)
            return new UCAFResult { success = false, message = $"No Terrain component on '{obj.name}'." };

        string layerAssetPath = cmd.GetParam("terrain_layer_asset", "");
        if (string.IsNullOrEmpty(layerAssetPath))
            return new UCAFResult { success = false, message = "terrain_layer_asset required (path to TerrainLayer .asset)." };

        var layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(layerAssetPath);
        if (layer == null)
        {
            string texAsset = cmd.GetParam("texture_asset", "");
            if (string.IsNullOrEmpty(texAsset))
                return new UCAFResult { success = false, message = $"TerrainLayer not found at '{layerAssetPath}'. Provide texture_asset to auto-create." };

            var diffuse = AssetDatabase.LoadAssetAtPath<Texture2D>(texAsset);
            if (diffuse == null) return new UCAFResult { success = false, message = $"Texture not found: {texAsset}" };

            layer = new TerrainLayer { diffuseTexture = diffuse };
            float tw = float.TryParse(cmd.GetParam("tile_size", "15"), NumberStyles.Float, CultureInfo.InvariantCulture, out float ts) ? ts : 15f;
            layer.tileSize = new Vector2(tw, tw);

            string ldir = Path.GetDirectoryName(layerAssetPath);
            if (!string.IsNullOrEmpty(ldir) && !AssetDatabase.IsValidFolder(ldir))
            {
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Application.dataPath), ldir));
                AssetDatabase.Refresh();
            }
            AssetDatabase.CreateAsset(layer, layerAssetPath);
            AssetDatabase.SaveAssets();
        }

        var data = terrain.terrainData;
        var existing = new List<TerrainLayer>(data.terrainLayers ?? Array.Empty<TerrainLayer>());
        int layerIdx = existing.IndexOf(layer);
        if (layerIdx < 0) { existing.Add(layer); data.terrainLayers = existing.ToArray(); layerIdx = existing.Count - 1; }

        if (cmd.GetParam("fill", "false") == "true")
        {
            int mapRes = data.alphamapResolution;
            int numL   = data.terrainLayers.Length;
            float[,,] maps = data.GetAlphamaps(0, 0, mapRes, mapRes);
            for (int y = 0; y < mapRes; y++)
                for (int x = 0; x < mapRes; x++)
                    for (int li = 0; li < numL; li++)
                        maps[y, x, li] = li == layerIdx ? 1f : 0f;
            data.SetAlphamaps(0, 0, maps);
        }

        EditorUtility.SetDirty(data);
        AssetDatabase.SaveAssets();
        return new UCAFResult {
            success = true,
            message = $"Layer '{layer.name}' at index {layerIdx} on '{obj.name}'" +
                      (cmd.GetParam("fill", "false") == "true" ? " (filled entire terrain)." : ".")
        };
    }

    // FR-170a: add_tree_prototype
    static UCAFResult CmdAddTreePrototype(UCAFCommand cmd)
    {
        var obj = ResolveObject(cmd, out string err);
        if (obj == null) return new UCAFResult { success = false, message = err };

        var terrain = obj.GetComponent<Terrain>();
        if (terrain == null)
            return new UCAFResult { success = false, message = $"No Terrain component on '{obj.name}'." };

        string prefabPath = cmd.GetParam("prefab_path", "");
        if (string.IsNullOrEmpty(prefabPath))
            return new UCAFResult { success = false, message = "prefab_path required." };

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab == null)
            return new UCAFResult { success = false, message = $"Prefab not found: {prefabPath}" };

        var data = terrain.terrainData;
        var protos = new List<TreePrototype>(data.treePrototypes ?? Array.Empty<TreePrototype>());
        foreach (var p in protos)
            if (p.prefab == prefab)
                return new UCAFResult { success = true, message = $"Tree prototype '{prefab.name}' already present." };

        var tp = new TreePrototype { prefab = prefab };
        if (float.TryParse(cmd.GetParam("bend_factor", "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out float bf))
            tp.bendFactor = bf;
        protos.Add(tp);
        data.treePrototypes = protos.ToArray();
        EditorUtility.SetDirty(data);
        AssetDatabase.SaveAssets();

        return new UCAFResult { success = true, message = $"Tree prototype '{prefab.name}' added at index {protos.Count - 1}." };
    }

    // FR-170b: paint_trees
    static UCAFResult CmdPaintTrees(UCAFCommand cmd)
    {
        var obj = ResolveObject(cmd, out string err);
        if (obj == null) return new UCAFResult { success = false, message = err };

        var terrain = obj.GetComponent<Terrain>();
        if (terrain == null)
            return new UCAFResult { success = false, message = $"No Terrain component on '{obj.name}'." };

        int protoIdx = int.TryParse(cmd.GetParam("prototype_index", "0"), out int pi) ? pi : 0;
        var data = terrain.terrainData;
        if (protoIdx < 0 || protoIdx >= data.treePrototypes.Length)
            return new UCAFResult { success = false, message = $"prototype_index {protoIdx} out of range (have {data.treePrototypes.Length})." };

        string mode = cmd.GetParam("mode", "scatter").ToLowerInvariant();

        if (mode == "clear")
        {
            var keep = new List<TreeInstance>();
            foreach (var ti in data.treeInstances)
                if (ti.prototypeIndex != protoIdx) keep.Add(ti);
            data.SetTreeInstances(keep.ToArray(), true);
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            return new UCAFResult { success = true, message = $"Cleared trees of prototype {protoIdx}." };
        }

        if (mode == "scatter")
        {
            int   count    = int.TryParse  (cmd.GetParam("count",     "100"), out int c)   ? c   : 100;
            float minScale = float.TryParse(cmd.GetParam("min_scale", "0.8"), NumberStyles.Float, CultureInfo.InvariantCulture, out float ms)  ? ms  : 0.8f;
            float maxScale = float.TryParse(cmd.GetParam("max_scale", "1.2"), NumberStyles.Float, CultureInfo.InvariantCulture, out float mxs) ? mxs : 1.2f;
            float minX     = float.TryParse(cmd.GetParam("min_x",     "0"),   NumberStyles.Float, CultureInfo.InvariantCulture, out float mnx) ? mnx : 0f;
            float maxX     = float.TryParse(cmd.GetParam("max_x",     "1"),   NumberStyles.Float, CultureInfo.InvariantCulture, out float mxx) ? mxx : 1f;
            float minZ     = float.TryParse(cmd.GetParam("min_z",     "0"),   NumberStyles.Float, CultureInfo.InvariantCulture, out float mnz) ? mnz : 0f;
            float maxZ     = float.TryParse(cmd.GetParam("max_z",     "1"),   NumberStyles.Float, CultureInfo.InvariantCulture, out float mxz) ? mxz : 1f;
            int   seed     = int.TryParse  (cmd.GetParam("seed",      "42"),  out int sd)  ? sd  : 42;

            var rng   = new System.Random(seed);
            var trees = new List<TreeInstance>(data.treeInstances);
            for (int i = 0; i < count; i++)
            {
                float sc = minScale + (float)(rng.NextDouble() * (maxScale - minScale));
                trees.Add(new TreeInstance {
                    prototypeIndex = protoIdx,
                    position       = new Vector3(
                        minX + (float)(rng.NextDouble() * (maxX - minX)),
                        0f,
                        minZ + (float)(rng.NextDouble() * (maxZ - minZ))),
                    widthScale    = sc,
                    heightScale   = sc,
                    color         = Color.white,
                    lightmapColor = Color.white
                });
            }
            data.SetTreeInstances(trees.ToArray(), true);
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            return new UCAFResult { success = true, message = $"Scattered {count} trees (prototype {protoIdx}) on '{obj.name}'." };
        }

        return new UCAFResult { success = false, message = $"Unknown mode '{mode}'. Use: scatter, clear." };
    }

    static UCAFTerrainInfo BuildTerrainInfo(GameObject go, TerrainData data, string dataPath) =>
        new UCAFTerrainInfo {
            obj_path             = GetPath(go),
            data_asset_path      = dataPath,
            width                = data.size.x,
            height               = data.size.y,
            length               = data.size.z,
            heightmap_resolution = data.heightmapResolution,
            layer_count          = data.terrainLayers?.Length ?? 0,
            tree_prototype_count = data.treePrototypes?.Length ?? 0,
            tree_instance_count  = data.treeInstanceCount
        };
}
