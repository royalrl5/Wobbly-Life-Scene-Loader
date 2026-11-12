using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace WobblySceneOverlay
{
    // Works out where there is "nothing there" so a scene can be loaded on top without overlapping the world.
    // The ground (X/Z plane) is split into square cells. Cells that contain a visible object are "occupied".
    public partial class SceneOverlayPlugin
    {
        private ConfigEntry<float> _cellSize;
        private ConfigEntry<float> _marginMeters;
        private ConfigEntry<float> _searchRadius;
        private ConfigEntry<float> _maxObjectSize;

        private class OffsetResult
        {
            public bool Found;
            public int Dx, Dz;
        }

        private void BindSpaceConfig()
        {
            _cellSize = Config.Bind("Load On Top", "Cell Size (meters)", 20f,
                "Size of one square used to map free space. Smaller = more exact but slower.");
            _marginMeters = Config.Bind("Load On Top", "Empty Margin (meters)", 20f,
                "Extra empty space kept around objects that are already there.");
            _searchRadius = Config.Bind("Load On Top", "Search Radius (meters)", 4000f,
                "How far from the scene's own position to look for an empty spot.");
            _maxObjectSize = Config.Bind("Load On Top", "Ignore Objects Wider Than (meters)", 2000f,
                "Huge objects (sky domes, endless water planes) are ignored when mapping space.");
        }

        private float CellSize { get { return Mathf.Max(2f, _cellSize.Value); } }

        private static long CellKey(int cx, int cz)
        {
            return ((long)(cx + 1048576) << 32) | (uint)(cz + 1048576);
        }

        // ---------- reading the size of things ----------
        private static bool SkipRenderer(Renderer r)
        {
            if (r == null || !r.enabled) return true;
            string n = r.GetType().Name;
            return n == "ParticleSystemRenderer" || n == "TrailRenderer" || n == "BillboardRenderer";
        }

        private void AddBoundsCells(HashSet<long> set, Bounds b, float cell, ref Bounds total, ref bool any)
        {
            float max = _maxObjectSize.Value;
            if (b.size.x > max || b.size.z > max) return;
            if (b.size.x <= 0f && b.size.z <= 0f) return;

            int x0 = Mathf.FloorToInt(b.min.x / cell), x1 = Mathf.FloorToInt(b.max.x / cell);
            int z0 = Mathf.FloorToInt(b.min.z / cell), z1 = Mathf.FloorToInt(b.max.z / cell);
            if ((long)(x1 - x0 + 1) * (z1 - z0 + 1) > 40000L) return;

            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                    set.Add(CellKey(x, z));

            if (!any) { total = b; any = true; }
            else total.Encapsulate(b);
        }

        private void ScanSceneCells(Scene sc, float cell, HashSet<long> set, ref Bounds total, ref bool any)
        {
            GameObject[] roots;
            try { roots = sc.GetRootGameObjects(); }
            catch { return; }

            foreach (GameObject root in roots)
            {
                if (root == null) continue;
                Renderer[] rs;
                try { rs = root.GetComponentsInChildren<Renderer>(false); }
                catch { continue; }

                foreach (Renderer r in rs)
                {
                    if (SkipRenderer(r)) continue;
                    AddBoundsCells(set, r.bounds, cell, ref total, ref any);
                }
            }

            AddTerrainCells(sc, cell, set, ref total, ref any);
        }

        // Terrain lives in an optional Unity module, so it is read with reflection.
        private void AddTerrainCells(Scene sc, float cell, HashSet<long> set, ref Bounds total, ref bool any)
        {
            try
            {
                Type tt = FindType("UnityEngine.Terrain");
                if (tt == null) return;

                PropertyInfo active = tt.GetProperty("activeTerrains", BindingFlags.Public | BindingFlags.Static);
                Array arr = active != null ? active.GetValue(null, null) as Array : null;
                if (arr == null) return;

                PropertyInfo dataProp = tt.GetProperty("terrainData");
                foreach (object o in arr)
                {
                    Component c = o as Component;
                    if (c == null || c.gameObject.scene.handle != sc.handle) continue;

                    object td = dataProp != null ? dataProp.GetValue(o, null) : null;
                    if (td == null) continue;
                    PropertyInfo sizeProp = td.GetType().GetProperty("size");
                    if (sizeProp == null) continue;

                    Vector3 size = (Vector3)sizeProp.GetValue(td, null);
                    Vector3 pos = c.transform.position;
                    AddBoundsCells(set, new Bounds(pos + size * 0.5f, size), cell, ref total, ref any);
                }
            }
            catch (Exception ex) { Logger.LogWarning("Terrain scan failed: " + ex.Message); }
        }

        // ---------- the map of what is already there ----------
        private HashSet<long> BuildOccupancy()
        {
            float cell = CellSize;
            var set = new HashSet<long>();
            Bounds total = new Bounds();
            bool any = false;

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene sc = SceneManager.GetSceneAt(i);
                if (!sc.IsValid() || !sc.isLoaded || sc.name == HolderName) continue;
                ScanSceneCells(sc, cell, set, ref total, ref any);
            }

            // keep a margin of empty ground around everything
            int m = Mathf.Max(0, Mathf.CeilToInt(_marginMeters.Value / cell));
            if (m > 0 && set.Count > 0)
            {
                var original = new long[set.Count];
                set.CopyTo(original);
                foreach (long key in original)
                {
                    int cx = (int)(key >> 32) - 1048576;
                    int cz = (int)(key & 0xFFFFFFFFL) - 1048576;
                    for (int dx = -m; dx <= m; dx++)
                        for (int dz = -m; dz <= m; dz++)
                            set.Add(CellKey(cx + dx, cz + dz));
                }
            }
            return set;
        }

        // The cells a freshly loaded scene covers.
        private bool MeasureFootprint(Scene sc, out int[] fx, out int[] fz, out Bounds total)
        {
            float cell = CellSize;
            var set = new HashSet<long>();
            total = new Bounds();
            bool any = false;
            ScanSceneCells(sc, cell, set, ref total, ref any);

            fx = new int[set.Count];
            fz = new int[set.Count];
            int n = 0;
            foreach (long key in set)
            {
                fx[n] = (int)(key >> 32) - 1048576;
                fz[n] = (int)(key & 0xFFFFFFFFL) - 1048576;
                n++;
            }
            return any && set.Count > 0;
        }

        private static bool IsFree(HashSet<long> occupied, int[] fx, int[] fz, int dx, int dz)
        {
            for (int i = 0; i < fx.Length; i++)
                if (occupied.Contains(CellKey(fx[i] + dx, fz[i] + dz))) return false;
            return true;
        }

        // Looks outward in growing rings for the nearest offset where the whole footprint fits in empty ground.
        private IEnumerator FindFreeOffset(HashSet<long> occupied, int[] fx, int[] fz, OffsetResult res)
        {
            float cell = CellSize;
            int maxD = Mathf.Max(1, Mathf.CeilToInt(_searchRadius.Value / cell));
            var sw = System.Diagnostics.Stopwatch.StartNew();

            for (int d = 0; d <= maxD; d++)
            {
                if (d % 4 == 0)
                    SetStatus("Looking for empty space... " + Mathf.RoundToInt(d * cell) + " m out", 30f);

                for (int dx = -d; dx <= d; dx++)
                {
                    bool edgeColumn = dx == -d || dx == d;
                    int step = edgeColumn ? 1 : 2 * d;

                    for (int dz = -d; dz <= d; dz += step)
                    {
                        if (IsFree(occupied, fx, fz, dx, dz))
                        {
                            res.Found = true;
                            res.Dx = dx;
                            res.Dz = dz;
                            yield break;
                        }
                        if (step <= 0) break;
                    }

                    if (sw.ElapsedMilliseconds > 8)
                    {
                        yield return null;
                        sw.Reset();
                        sw.Start();
                    }
                }
            }
        }
    }
}
