using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace WobblySceneOverlay
{
    // Finds every scene it can: the game's build list, Addressables, and scenes inside asset bundle files.
    public partial class SceneOverlayPlugin
    {
        private enum SceneSource { Build, Addressable, BundleFile }

        private class SceneItem
        {
            public SceneSource Source;
            public string Name;         // display name
            public string Path;         // scene path inside the game / bundle, or the Addressables key
            public int BuildIndex = -1; // Build scenes only
            public string BundleFile;   // BundleFile scenes only (file on disk)
        }

        private class BundleInfo
        {
            public long Size;
            public long Ticks;
            public List<string> Scenes = new List<string>();
        }

        private ConfigEntry<string> _bundleFolders;
        private ConfigEntry<string> _bundlePatterns;

        private readonly List<SceneItem> _items = new List<SceneItem>();
        private readonly List<SceneItem> _view = new List<SceneItem>();
        private int _nBuild, _nAddr, _nBundleFile;
        private int _itemsVer;
        private string _viewKey = "";
        private SceneItem _selected;

        // bundle files on disk (cached so a rescan only looks at new or changed files)
        private readonly Dictionary<string, BundleInfo> _bundleCache = new Dictionary<string, BundleInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _pathToBundle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private bool _scanning;
        private int _scanDone, _scanTotal;

        private void BindCatalogConfig()
        {
            _bundleFolders = Config.Bind("Bundles", "Extra Folders", "",
                "Extra folders to search for asset bundle files (separate several with ;). The game's StreamingAssets and Data folders are always searched.");
            _bundlePatterns = Config.Bind("Bundles", "File Patterns", "*.bundle,*.unity3d,*.assetbundle",
                "Which files count as asset bundles (comma separated).");
        }

        // =====================================================================
        //  building the scene list
        // =====================================================================
        private void AddItem(HashSet<string> seen, SceneSource src, string name, string path, int index, string bundleFile)
        {
            if (string.IsNullOrEmpty(name)) return;
            string key = src + "|" + (bundleFile ?? "") + "|" + path;
            if (!seen.Add(key)) return;

            _items.Add(new SceneItem
            {
                Source = src, Name = name, Path = path, BuildIndex = index, BundleFile = bundleFile
            });

            if (src == SceneSource.Build) _nBuild++;
            else if (src == SceneSource.Addressable) _nAddr++;
            else _nBundleFile++;
        }

        private void RebuildItems()
        {
            string selPath = _selected != null ? _selected.Path : null;
            SceneSource selSrc = _selected != null ? _selected.Source : SceneSource.Build;
            string selBundle = _selected != null ? _selected.BundleFile : null;

            _items.Clear();
            _pathToBundle.Clear();
            _nBuild = _nAddr = _nBundleFile = 0;
            var seen = new HashSet<string>();

            // 1) the game's build list
            try
            {
                int count = SceneManager.sceneCountInBuildSettings;
                for (int i = 0; i < count; i++)
                {
                    string path = SceneUtility.GetScenePathByBuildIndex(i);
                    AddItem(seen, SceneSource.Build, System.IO.Path.GetFileNameWithoutExtension(path), path, i, null);
                }
            }
            catch (Exception ex) { Logger.LogWarning("Build scene scan failed: " + ex.Message); }

            // 2) Addressables
            try { ScanAddressables(seen); }
            catch (Exception ex) { Logger.LogWarning("Addressables scan failed: " + ex.Message); }

            // 3) scenes inside bundle files we have looked at before
            foreach (KeyValuePair<string, BundleInfo> kv in _bundleCache)
            {
                foreach (string sp in kv.Value.Scenes)
                {
                    string nm = System.IO.Path.GetFileNameWithoutExtension(sp);
                    _pathToBundle[sp] = kv.Key;
                    _pathToBundle[nm] = kv.Key;
                    AddItem(seen, SceneSource.BundleFile, nm, sp, -1, kv.Key);
                }
            }

            // 4) scenes in bundles that are loaded right now
            try
            {
                foreach (AssetBundle b in AssetBundle.GetAllLoadedAssetBundles())
                {
                    if (b == null || !b.isStreamedSceneAssetBundle) continue;
                    foreach (string sp in b.GetAllScenePaths())
                    {
                        string file;
                        _pathToBundle.TryGetValue(sp, out file);
                        AddItem(seen, SceneSource.BundleFile, System.IO.Path.GetFileNameWithoutExtension(sp), sp, -1, file);
                    }
                }
            }
            catch (Exception ex) { Logger.LogWarning("Loaded bundle scan failed: " + ex.Message); }

            _items.Sort((a, b) =>
            {
                int c = a.Source.CompareTo(b.Source);
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            _selected = null;
            if (selPath != null)
                _selected = _items.Find(x => x.Source == selSrc && x.Path == selPath && x.BundleFile == selBundle);

            _itemsVer++;
            Logger.LogInfo($"Scenes found - Build: {_nBuild}, Addressables: {_nAddr}, Bundle files: {_nBundleFile}");
        }

        // Applies the search text. Only called at the start of a Layout pass so every event in a frame sees the same list.
        private void RebuildViewIfNeeded()
        {
            string key = _vFilter + "|" + _itemsVer;
            if (key == _viewKey) return;
            _viewKey = key;

            _view.Clear();
            foreach (SceneItem s in _items)
            {
                if (_vFilter.Length > 0 &&
                    s.Name.IndexOf(_vFilter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    (s.Path == null || s.Path.IndexOf(_vFilter, StringComparison.OrdinalIgnoreCase) < 0))
                    continue;
                _view.Add(s);
            }
            _scroll = Vector2.zero;
        }

        // =====================================================================
        //  Addressables (found with reflection, so the mod works without it too)
        // =====================================================================
        private void ScanAddressables(HashSet<string> seen)
        {
            Type addr = GetAddressablesType();
            if (addr == null) return;

            PropertyInfo locProp = addr.GetProperty("ResourceLocators", BindingFlags.Public | BindingFlags.Static);
            IEnumerable locators = locProp != null ? locProp.GetValue(null, null) as IEnumerable : null;
            if (locators == null) return;

            var done = new HashSet<string>();
            foreach (object locator in locators)
            {
                Type lt = locator.GetType();
                PropertyInfo keysProp = lt.GetProperty("Keys");
                IEnumerable keys = keysProp != null ? keysProp.GetValue(locator, null) as IEnumerable : null;
                if (keys == null) continue;

                MethodInfo locate = FindLocate(lt);
                if (locate == null) continue;

                foreach (object key in keys)
                {
                    string skey = key as string;
                    if (skey == null || done.Contains(skey)) continue;

                    try
                    {
                        object[] args = { key, null, null };
                        bool ok = (bool)locate.Invoke(locator, args);
                        IEnumerable locs = args[2] as IEnumerable;
                        if (!ok || locs == null) continue;

                        foreach (object loc in locs)
                        {
                            PropertyInfo rt = loc.GetType().GetProperty("ResourceType");
                            Type resType = rt != null ? rt.GetValue(loc, null) as Type : null;
                            if (resType != null && resType.Name == "SceneInstance")
                            {
                                done.Add(skey);
                                string nice = System.IO.Path.GetFileNameWithoutExtension(skey);
                                AddItem(seen, SceneSource.Addressable, string.IsNullOrEmpty(nice) ? skey : nice, skey, -1, null);
                                break;
                            }
                        }
                    }
                    catch { /* some keys can't be located; ignore */ }
                }
            }
        }

        private static MethodInfo FindLocate(Type t)
        {
            foreach (Type i in t.GetInterfaces())
                if (i.Name == "IResourceLocator")
                {
                    MethodInfo m = i.GetMethod("Locate");
                    if (m != null) return m;
                }
            return t.GetMethod("Locate");
        }

        // =====================================================================
        //  reading asset bundle files
        // =====================================================================
        private string CacheFile
        {
            get { return System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "SceneOverlay.bundlecache.txt"); }
        }

        private void LoadBundleCache()
        {
            try
            {
                if (!System.IO.File.Exists(CacheFile)) return;
                foreach (string line in System.IO.File.ReadAllLines(CacheFile))
                {
                    string[] p = line.Split(new[] { '|' }, 4);
                    if (p.Length < 3) continue;

                    long size, ticks;
                    if (!long.TryParse(p[0], out size) || !long.TryParse(p[1], out ticks)) continue;

                    var info = new BundleInfo { Size = size, Ticks = ticks };
                    if (p.Length == 4 && p[3].Length > 0)
                        info.Scenes.AddRange(p[3].Split(';').Where(x => x.Length > 0));
                    _bundleCache[p[2]] = info;
                }
            }
            catch (Exception ex) { Logger.LogWarning("Could not read the bundle cache: " + ex.Message); }
        }

        private void SaveBundleCache()
        {
            try
            {
                var lines = new List<string>();
                foreach (KeyValuePair<string, BundleInfo> kv in _bundleCache)
                    lines.Add(kv.Value.Size + "|" + kv.Value.Ticks + "|" + kv.Key + "|" + string.Join(";", kv.Value.Scenes.ToArray()));
                System.IO.File.WriteAllLines(CacheFile, lines.ToArray());
            }
            catch (Exception ex) { Logger.LogWarning("Could not save the bundle cache: " + ex.Message); }
        }

        private void ClearBundleCache()
        {
            _bundleCache.Clear();
            try { if (System.IO.File.Exists(CacheFile)) System.IO.File.Delete(CacheFile); } catch { }
            RebuildItems();
            SetStatus("Bundle cache cleared. Press SCAN BUNDLES to look again.");
        }

        private List<string> FindBundleFiles()
        {
            var folders = new List<string> { Application.streamingAssetsPath, Application.dataPath };
            foreach (string f in (_bundleFolders.Value ?? "").Split(';'))
                if (f.Trim().Length > 0) folders.Add(f.Trim());

            string[] patterns = (_bundlePatterns.Value ?? "")
                .Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();

            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string folder in folders)
            {
                if (string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder)) continue;
                foreach (string pattern in patterns)
                {
                    try
                    {
                        foreach (string f in System.IO.Directory.GetFiles(folder, pattern, System.IO.SearchOption.AllDirectories))
                            files.Add(f);
                    }
                    catch (Exception ex) { Logger.LogWarning("Could not scan '" + folder + "': " + ex.Message); }
                }
            }
            return files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // A bundle the game (Addressables) already has open can't be opened a second time, so match it by name.
        private static AssetBundle FindLoadedBundleForFile(string file)
        {
            string fn = System.IO.Path.GetFileName(file);
            string fnNoExt = System.IO.Path.GetFileNameWithoutExtension(file);
            try
            {
                foreach (AssetBundle b in AssetBundle.GetAllLoadedAssetBundles())
                {
                    if (b == null) continue;
                    string n = b.name;
                    if (string.Equals(n, fn, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(n, fnNoExt, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(n + ".bundle", fn, StringComparison.OrdinalIgnoreCase))
                        return b;
                }
            }
            catch { }
            return null;
        }

        private void StartBundleScan()
        {
            if (_scanning) { SetStatus("Already scanning bundle files..."); return; }
            StartCoroutine(ScanBundlesRoutine());
        }

        private IEnumerator ScanBundlesRoutine()
        {
            _scanning = true;
            _scanDone = 0;
            _scanTotal = 0;
            _error = null;
            SetStatus("Looking for asset bundle files...", 60f);
            yield return null;

            List<string> files = null;
            string findError = null;
            try { files = FindBundleFiles(); }
            catch (Exception ex) { findError = ex.Message; }

            if (files == null)
            {
                ShowError("Could not search for bundle files: " + findError);
                _scanning = false;
                yield break;
            }

            _scanTotal = files.Count;
            Logger.LogInfo("Found " + files.Count + " possible bundle file(s).");

            int reused = 0, scanned = 0, failed = 0, sinceYield = 0;

            foreach (string file in files)
            {
                _scanDone++;

                long size = 0, ticks = 0;
                bool statOk = true;
                try
                {
                    var fi = new System.IO.FileInfo(file);
                    size = fi.Length;
                    ticks = fi.LastWriteTimeUtc.Ticks;
                }
                catch { statOk = false; }
                if (!statOk) continue;

                BundleInfo cached;
                if (_bundleCache.TryGetValue(file, out cached) && cached.Size == size && cached.Ticks == ticks)
                {
                    reused++;
                    if (++sinceYield % 40 == 0) yield return null;
                    continue;
                }

                var info = new BundleInfo { Size = size, Ticks = ticks };
                bool readOk = false;

                AssetBundle loaded = FindLoadedBundleForFile(file);
                if (loaded != null)
                {
                    try
                    {
                        if (loaded.isStreamedSceneAssetBundle) info.Scenes.AddRange(loaded.GetAllScenePaths());
                        readOk = true;
                    }
                    catch (Exception ex) { Logger.LogWarning("Could not read loaded bundle '" + file + "': " + ex.Message); }
                }
                else
                {
                    AssetBundleCreateRequest req = null;
                    try { req = AssetBundle.LoadFromFileAsync(file); }
                    catch (Exception ex) { Logger.LogWarning("Could not open '" + file + "': " + ex.Message); }

                    if (req != null)
                    {
                        while (!req.isDone) yield return null;
                        AssetBundle b = req.assetBundle;
                        if (b != null)
                        {
                            try
                            {
                                if (b.isStreamedSceneAssetBundle) info.Scenes.AddRange(b.GetAllScenePaths());
                                readOk = true;
                            }
                            catch (Exception ex) { Logger.LogWarning("Could not read '" + file + "': " + ex.Message); }
                            try { b.Unload(false); } catch { }
                        }
                    }
                }

                if (readOk)
                {
                    _bundleCache[file] = info;
                    scanned++;
                    if (info.Scenes.Count > 0)
                        Logger.LogInfo("Bundle '" + System.IO.Path.GetFileName(file) + "' has " + info.Scenes.Count + " scene(s).");
                }
                else failed++;

                yield return null;
            }

            SaveBundleCache();
            RebuildItems();
            _scanning = false;

            string msg = $"Bundle scan finished: {scanned} read, {reused} cached, {failed} could not be opened. {_nBundleFile} scene(s) in bundle files.";
            Logger.LogInfo(msg);
            SetStatus(msg, 8f);
        }
    }
}
