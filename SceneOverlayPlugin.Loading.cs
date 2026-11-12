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
    // The loading engine. It follows the same steps the working WobblyReload mod uses:
    //   save what is loaded -> park on an empty "holder" scene -> unload everything -> load -> make it the active scene.
    public partial class SceneOverlayPlugin
    {
        private const string HolderName = "SceneOverlay_EmptyHolder";

        // everything needed to load one scene
        private class LoadTarget
        {
            public string Name;
            public string Path;
            public int Index = -1;
            public bool WasActive;
            public string BundleFile;       // bundle file on disk that contains the scene (optional)
            public object AddrLocation;     // Addressables IResourceLocation
            public string StreamTarget;     // name/path usable with SceneManager.LoadSceneAsync
            public bool Resolved;

            public override string ToString()
            {
                return Name + " (" + Path + ") [build " + Index + "]";
            }
        }

        private class LoadResult { public bool Ok; }

        private ConfigEntry<bool> _resetTimeScale;
        private ConfigEntry<float> _stuckTimeout;
        private ConfigEntry<bool> _firstAsSingle;
        private ConfigEntry<bool> _unloadUnused;
        private ConfigEntry<bool> _useAddressables;
        private ConfigEntry<string> _addrOverride;

        private bool _busy;
        private string _busyWhat = "";
        private float _startedAt;
        private List<LoadTarget> _lastLayout;
        private Type _addrType;
        private bool _addrTypeSearched;
        private readonly List<AssetBundle> _ownedBundles = new List<AssetBundle>();

        private void BindLoadingConfig()
        {
            _resetTimeScale = Config.Bind("Loading", "Reset Time Scale", true, "Set Time.timeScale to 1 before unloading.");
            _stuckTimeout = Config.Bind("Loading", "Stuck Timeout (seconds)", 90f, "If an operation takes longer than this it is cancelled so you can try again.");
            _firstAsSingle = Config.Bind("Loading", "Load First Scene As Single", true, "When reloading, load the first scene in Single mode and the others on top.");
            _unloadUnused = Config.Bind("Loading", "Unload Unused Assets", false, "Call Resources.UnloadUnusedAssets between unloading and loading.");
            _useAddressables = Config.Bind("Loading", "Use Addressables", true, "Load scenes through Addressables when the game has it (this loads their dependencies).");
            _addrOverride = Config.Bind("Loading", "Addressables Key Override", "", "Optional: exact Addressables address of the main scene if detection fails.");
        }

        // =====================================================================
        //  small helpers
        // =====================================================================
        private Type FindType(string fullName)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = a.GetType(fullName, false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        private Type GetAddressablesType()
        {
            if (_addrTypeSearched && _addrType != null) return _addrType;
            _addrTypeSearched = true;
            _addrType = FindType("UnityEngine.AddressableAssets.Addressables");
            return _addrType;
        }

        private void SetStatus(string text, float seconds = 4f)
        {
            _status = text;
            _statusUntil = Time.realtimeSinceStartup + seconds;
        }

        private void ShowError(string message)
        {
            _error = message;
            _statusUntil = 0f;
            Logger.LogError(message);
        }

        // Only one heavy operation at a time.
        private void StartOperation(string what, IEnumerator routine)
        {
            if (_scanning) { SetStatus("Please wait for the bundle scan to finish."); return; }
            if (_busy) { SetStatus("Please wait, still " + _busyWhat + "..."); return; }

            _busy = true;
            _busyWhat = what;
            _startedAt = Time.realtimeSinceStartup;
            _error = null;
            StartCoroutine(RunOperation(routine));
        }

        private IEnumerator RunOperation(IEnumerator inner)
        {
            yield return StartCoroutine(inner);
            _busy = false;
        }

        // =====================================================================
        //  looking at what is loaded now
        // =====================================================================
        private List<LoadTarget> CaptureLayout()
        {
            var list = new List<LoadTarget>();
            Scene active = SceneManager.GetActiveScene();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene s = SceneManager.GetSceneAt(i);
                if (!s.IsValid() || !s.isLoaded || s.name == HolderName) continue;

                string file = null;
                if (!string.IsNullOrEmpty(s.path)) _pathToBundle.TryGetValue(s.path, out file);
                if (file == null) _pathToBundle.TryGetValue(s.name, out file);

                list.Add(new LoadTarget
                {
                    Name = s.name, Path = s.path, Index = s.buildIndex,
                    WasActive = s.handle == active.handle, BundleFile = file
                });
            }
            return list;
        }

        private Scene EnsureHolder()
        {
            Scene s = SceneManager.GetSceneByName(HolderName);
            if (s.IsValid() && s.isLoaded) return s;
            return SceneManager.CreateScene(HolderName);
        }

        private void RemoveHolder()
        {
            Scene h = SceneManager.GetSceneByName(HolderName);
            if (h.IsValid() && h.isLoaded && SceneManager.sceneCount > 1)
            {
                try { SceneManager.UnloadSceneAsync(h); }
                catch (Exception ex) { Logger.LogWarning("Could not remove holder: " + ex.Message); }
            }
        }

        private void TrySetActive(Scene s)
        {
            try { SceneManager.SetActiveScene(s); }
            catch (Exception ex) { Logger.LogWarning("SetActiveScene failed: " + ex.Message); }
        }

        private static Scene FindScene(LoadTarget t)
        {
            Scene s = default(Scene);
            if (!string.IsNullOrEmpty(t.Path)) s = SceneManager.GetSceneByPath(t.Path);
            if (!s.IsValid()) s = SceneManager.GetSceneByName(t.Name);
            return s;
        }

        private LoadTarget ToTarget(SceneItem item)
        {
            return new LoadTarget
            {
                Name = item.Name,
                Path = item.Path,
                Index = item.Source == SceneSource.Build ? item.BuildIndex : -1,
                BundleFile = item.BundleFile
            };
        }

        // =====================================================================
        //  unloading
        // =====================================================================
        private IEnumerator UnloadAllExcept(Scene keep)
        {
            var list = new List<Scene>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene s = SceneManager.GetSceneAt(i);
                if (s.handle != keep.handle) list.Add(s);
            }

            foreach (Scene s in list)
            {
                if (!s.IsValid()) continue;
                string n = s.name;
                AsyncOperation op = null;
                try { op = SceneManager.UnloadSceneAsync(s); }
                catch (Exception ex) { Logger.LogWarning("Unload of '" + n + "' threw: " + ex.Message); }

                if (op == null)
                {
                    Logger.LogWarning("Could not unload '" + n + "'.");
                    continue;
                }

                float t = Time.realtimeSinceStartup;
                while (!op.isDone && Time.realtimeSinceStartup - t < 15f) yield return null;
                Logger.LogInfo("Unloaded '" + n + "'.");
            }
        }

        // Unloads one scene from the "Loaded now" list.
        private void UnloadLoaded(Scene s)
        {
            if (_busy) { SetStatus("Please wait, still " + _busyWhat + "..."); return; }
            if (!s.IsValid() || !s.isLoaded) { SetStatus("That scene is not loaded."); return; }

            if (SceneManager.sceneCount <= 1)
            {
                ShowError("Cannot unload '" + s.name + "': it is the only loaded scene. Use LOAD (REPLACE WORLD) to swap it for another one.");
                return;
            }

            try
            {
                string n = s.name;
                AsyncOperation op = SceneManager.UnloadSceneAsync(s);
                if (op == null) ShowError("Unity refused to unload '" + n + "'.");
                else SetStatus("Unloading " + n + "...");
            }
            catch (Exception ex)
            {
                ShowError("Could not unload '" + s.name + "': " + ex.Message);
            }
        }

        private void MakeActive(Scene s)
        {
            if (!s.IsValid() || !s.isLoaded) { SetStatus("That scene is not loaded."); return; }
            TrySetActive(s);
            SetStatus("Active scene is now " + s.name + ".");
        }

        // =====================================================================
        //  working out HOW a scene can be loaded
        // =====================================================================
        private void ResolveSafe(LoadTarget t)
        {
            try { Resolve(t); }
            catch (Exception ex) { Logger.LogWarning("Resolve failed for " + t + ": " + ex.Message); }
        }

        private void Resolve(LoadTarget t)
        {
            t.Resolved = false;

            // 1) normal build index
            if (t.Index >= 0 && t.Index < SceneManager.sceneCountInBuildSettings)
                t.Resolved = true;

            // 2) Addressables
            if (_useAddressables.Value && GetAddressablesType() != null)
            {
                if (t.AddrLocation == null) t.AddrLocation = FindAddressLocation(t);
                if (t.AddrLocation != null) t.Resolved = true;
                else if (t.WasActive && !string.IsNullOrEmpty(_addrOverride.Value)) t.Resolved = true;
            }

            // 3) a bundle that is already loaded (streamed scene)
            t.StreamTarget = GetStreamTarget(t);
            if (t.StreamTarget == null && FindInLoadedBundles(t)) t.StreamTarget = GetStreamTarget(t);
            if (t.StreamTarget != null) t.Resolved = true;

            // 4) a bundle file on disk that we know contains the scene
            if (string.IsNullOrEmpty(t.BundleFile))
            {
                string file;
                if ((!string.IsNullOrEmpty(t.Path) && _pathToBundle.TryGetValue(t.Path, out file)) ||
                    (!string.IsNullOrEmpty(t.Name) && _pathToBundle.TryGetValue(t.Name, out file)))
                    t.BundleFile = file;
            }
            if (!t.Resolved && !string.IsNullOrEmpty(t.BundleFile) && System.IO.File.Exists(t.BundleFile))
                t.Resolved = true;
        }

        private static string GetStreamTarget(LoadTarget t)
        {
            if (!string.IsNullOrEmpty(t.Path) && Application.CanStreamedLevelBeLoaded(t.Path)) return t.Path;
            if (!string.IsNullOrEmpty(t.Name) && Application.CanStreamedLevelBeLoaded(t.Name)) return t.Name;
            return null;
        }

        private List<string> AddressKeys(LoadTarget t)
        {
            var keys = new List<string>();
            if (t.WasActive && !string.IsNullOrEmpty(_addrOverride.Value)) keys.Add(_addrOverride.Value);
            if (!string.IsNullOrEmpty(t.Path)) keys.Add(t.Path);
            if (!string.IsNullOrEmpty(t.Name)) keys.Add(t.Name);
            if (!string.IsNullOrEmpty(t.Path)) keys.Add(System.IO.Path.GetFileName(t.Path));
            return keys.Distinct().ToList();
        }

        private object FindAddressLocation(LoadTarget t)
        {
            Type addr = GetAddressablesType();
            if (addr == null) return null;

            PropertyInfo locProp = addr.GetProperty("ResourceLocators", BindingFlags.Public | BindingFlags.Static);
            IEnumerable locators = locProp != null ? locProp.GetValue(null, null) as IEnumerable : null;
            if (locators == null) return null;

            Type sceneInstance = FindType("UnityEngine.ResourceManagement.ResourceProviders.SceneInstance");
            List<string> keys = AddressKeys(t);

            foreach (object locator in locators)
            {
                if (locator == null) continue;
                Type iface = locator.GetType().GetInterfaces().FirstOrDefault(i => i.Name == "IResourceLocator");
                if (iface == null) continue;
                MethodInfo locate = iface.GetMethod("Locate");
                if (locate == null) continue;

                foreach (string key in keys)
                {
                    object loc = LocateScene(locator, locate, key, sceneInstance);
                    if (loc != null) { LogLocation(t, loc); return loc; }
                }

                PropertyInfo keysProp = iface.GetProperty("Keys");
                IEnumerable allKeys = keysProp != null ? keysProp.GetValue(locator, null) as IEnumerable : null;
                if (allKeys == null) continue;
                foreach (object k in allKeys)
                {
                    string sk = k as string;
                    if (sk == null || !LooksLikeScene(sk, t)) continue;
                    object loc = LocateScene(locator, locate, sk, sceneInstance);
                    if (loc != null) { LogLocation(t, loc); return loc; }
                }
            }
            return null;
        }

        private static bool LooksLikeScene(string key, LoadTarget t)
        {
            if (string.Equals(key, t.Name, StringComparison.OrdinalIgnoreCase)) return true;
            return key.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(System.IO.Path.GetFileNameWithoutExtension(key), t.Name, StringComparison.OrdinalIgnoreCase);
        }

        private static object LocateScene(object locator, MethodInfo locate, object key, Type wanted)
        {
            try
            {
                object[] args = { key, wanted, null };
                bool ok = (bool)locate.Invoke(locator, args);
                IEnumerable locs = args[2] as IEnumerable;
                if (!ok || locs == null) return null;

                foreach (object loc in locs)
                {
                    if (loc == null) continue;
                    if (wanted == null) return loc;
                    PropertyInfo rt = loc.GetType().GetProperty("ResourceType");
                    Type resType = rt != null ? rt.GetValue(loc, null) as Type : null;
                    if (resType == wanted) return loc;
                }
            }
            catch { }
            return null;
        }

        private void LogLocation(LoadTarget t, object loc)
        {
            PropertyInfo id = loc.GetType().GetProperty("InternalId");
            Logger.LogInfo("Addressables location for " + t + ": " + (id != null ? id.GetValue(loc, null) : "?"));
        }

        private static bool BundleHasScene(AssetBundle b, LoadTarget t)
        {
            if (b == null || !b.isStreamedSceneAssetBundle) return false;
            foreach (string p in b.GetAllScenePaths())
            {
                if (!string.IsNullOrEmpty(t.Path) && string.Equals(p, t.Path, StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(p), t.Name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private bool FindInLoadedBundles(LoadTarget t)
        {
            try
            {
                foreach (AssetBundle b in AssetBundle.GetAllLoadedAssetBundles())
                    if (BundleHasScene(b, t)) return true;
            }
            catch (Exception ex) { Logger.LogWarning("Bundle search failed: " + ex.Message); }
            return false;
        }

        // =====================================================================
        //  starting one load
        // =====================================================================
        private static AsyncOperation SafeStart(Func<AsyncOperation> start, BepInEx.Logging.ManualLogSource log)
        {
            try { return start(); }
            catch (Exception ex)
            {
                log.LogWarning("Load start failed: " + ex.Message);
                return null;
            }
        }

        // Calls Addressables.LoadSceneAsync(location-or-key, mode, true, 100) with reflection.
        private object StartAddressables(object keyOrLocation, bool isLocation, LoadSceneMode mode)
        {
            Type addr = GetAddressablesType();
            if (addr == null) return null;

            try
            {
                foreach (MethodInfo m in addr.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "LoadSceneAsync" || m.IsGenericMethod) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length < 4) continue;

                    bool isLoc = ps[0].ParameterType.Name == "IResourceLocation";
                    bool isObj = ps[0].ParameterType == typeof(object);
                    if (isLocation ? !isLoc : !isObj) continue;

                    object modeArg;
                    if (ps[1].ParameterType == typeof(LoadSceneMode)) modeArg = mode;
                    else if (ps[1].ParameterType == typeof(LoadSceneParameters)) modeArg = new LoadSceneParameters(mode);
                    else continue;

                    if (ps[2].ParameterType != typeof(bool)) continue;
                    if (ps[3].ParameterType != typeof(int)) continue;

                    object[] args = new object[ps.Length];
                    args[0] = keyOrLocation;
                    args[1] = modeArg;
                    args[2] = true;
                    args[3] = 100;
                    for (int i = 4; i < args.Length; i++) args[i] = Type.Missing;
                    return m.Invoke(null, args);
                }
                Logger.LogWarning("No matching Addressables.LoadSceneAsync overload found.");
            }
            catch (Exception ex)
            {
                Exception inner = ex.InnerException ?? ex;
                Logger.LogWarning("Addressables call failed: " + inner.Message);
            }
            return null;
        }

        private static bool HandleDone(object handle)
        {
            try
            {
                PropertyInfo p = handle.GetType().GetProperty("IsDone");
                return p == null || (bool)p.GetValue(handle, null);
            }
            catch { return true; }
        }

        private static string HandleStatus(object handle)
        {
            try
            {
                PropertyInfo p = handle.GetType().GetProperty("Status");
                object v = p != null ? p.GetValue(handle, null) : null;
                return v != null ? v.ToString() : "Unknown";
            }
            catch { return "Unknown"; }
        }

        private static string HandleError(object handle)
        {
            try
            {
                PropertyInfo p = handle.GetType().GetProperty("OperationException");
                object v = p != null ? p.GetValue(handle, null) : null;
                return v != null ? v.ToString() : "none";
            }
            catch { return "unknown"; }
        }

        private AssetBundle FindOpenBundle(string file)
        {
            string fn = System.IO.Path.GetFileName(file);
            foreach (AssetBundle b in _ownedBundles)
                if (b != null && string.Equals(b.name, fn, StringComparison.OrdinalIgnoreCase)) return b;
            return FindLoadedBundleForFile(file);
        }

        // Loads one scene. Order: build index, Addressables, already-loaded bundle, bundle file on disk.
        private IEnumerator LoadScene(LoadTarget s, LoadSceneMode mode, LoadResult result)
        {
            result.Ok = false;

            // 1) build index
            if (s.Index >= 0 && s.Index < SceneManager.sceneCountInBuildSettings)
            {
                int idx = s.Index;
                AsyncOperation op = SafeStart(() => SceneManager.LoadSceneAsync(idx, mode), Logger);
                if (op != null)
                {
                    Logger.LogInfo("Loading " + s + " via build index (" + mode + ").");
                    while (!op.isDone) yield return null;
                    result.Ok = true;
                    yield break;
                }
            }

            // 2) Addressables
            if (_useAddressables.Value && GetAddressablesType() != null)
            {
                var tries = new List<KeyValuePair<object, bool>>();
                if (s.AddrLocation != null)
                    tries.Add(new KeyValuePair<object, bool>(s.AddrLocation, true));
                else
                    foreach (string k in AddressKeys(s))
                        tries.Add(new KeyValuePair<object, bool>(k, false));

                foreach (KeyValuePair<object, bool> attempt in tries)
                {
                    object handle = StartAddressables(attempt.Key, attempt.Value, mode);
                    if (handle == null) continue;

                    Logger.LogInfo("Loading " + s + " via Addressables " +
                                   (attempt.Value ? "location" : "key '" + attempt.Key + "'") + " (" + mode + ").");

                    while (!HandleDone(handle)) yield return null;

                    string status = HandleStatus(handle);
                    if (status == "Succeeded")
                    {
                        result.Ok = true;
                        yield break;
                    }
                    Logger.LogWarning("Addressables load failed (" + status + "): " + HandleError(handle));
                }
            }

            // 3) scene inside a bundle that is already open
            if (!string.IsNullOrEmpty(s.StreamTarget))
            {
                string target = s.StreamTarget;
                AsyncOperation op2 = SafeStart(() => SceneManager.LoadSceneAsync(target, mode), Logger);
                if (op2 != null)
                {
                    Logger.LogInfo("Loading " + s + " via open bundle scene '" + target + "' (" + mode + ").");
                    while (!op2.isDone) yield return null;
                    result.Ok = true;
                    yield break;
                }
            }

            // 4) open the bundle file from disk, then load the scene from it
            if (!string.IsNullOrEmpty(s.BundleFile) && System.IO.File.Exists(s.BundleFile))
            {
                AssetBundle bundle = FindOpenBundle(s.BundleFile);
                if (bundle == null)
                {
                    AssetBundleCreateRequest req = null;
                    try { req = AssetBundle.LoadFromFileAsync(s.BundleFile); }
                    catch (Exception ex) { Logger.LogWarning("Could not open bundle '" + s.BundleFile + "': " + ex.Message); }

                    if (req != null)
                    {
                        while (!req.isDone) yield return null;
                        bundle = req.assetBundle;
                        if (bundle != null) _ownedBundles.Add(bundle);
                    }
                }

                if (bundle != null)
                {
                    string target3 = GetStreamTarget(s) ?? s.Path;
                    AsyncOperation op3 = SafeStart(() => SceneManager.LoadSceneAsync(target3, mode), Logger);
                    if (op3 != null)
                    {
                        Logger.LogInfo("Loading " + s + " from bundle file '" + System.IO.Path.GetFileName(s.BundleFile) +
                                       "' (" + mode + "). Note: other bundles it depends on are not loaded automatically.");
                        while (!op3.isDone) yield return null;
                        result.Ok = true;
                        yield break;
                    }
                }
            }

            Logger.LogError("All load methods failed for " + s);
        }

        // =====================================================================
        //  actions behind the buttons
        // =====================================================================
        private void StartReplace(SceneItem item)
        {
            if (item == null) { SetStatus("Pick a scene from the list first."); return; }
            StartOperation("loading", ReplaceRoutine(item));
        }

        private void StartLoadOnTop(SceneItem item)
        {
            if (item == null) { SetStatus("Pick a scene from the list first."); return; }
            StartOperation("loading", LoadOnTopRoutine(item));
        }

        private void StartReload()
        {
            StartOperation("reloading", ReloadRoutine());
        }

        private void AfterLoadStatus(string name)
        {
            if (Camera.allCamerasCount == 0)
                SetStatus("Loaded " + name + ", but nothing in the world has a camera, so the screen may be black. Try Explorer's Freecam.", 12f);
            else
                SetStatus("Loaded " + name + ".");
            if (_closeAfter.Value) CloseMenu();
        }

        // ---------- LOAD (REPLACE WORLD): unload -> load -> use ----------
        private IEnumerator ReplaceRoutine(SceneItem item)
        {
            LoadTarget target = ToTarget(item);
            ResolveSafe(target);
            if (!target.Resolved)
            {
                ShowError("Cannot load '" + item.Name + "': no way was found to load it (not in the build list, Addressables or any known bundle).");
                yield break;
            }

            // remember what is loaded now, so it can be restored if the new scene fails
            List<LoadTarget> before = CaptureLayout();
            foreach (LoadTarget b in before) ResolveSafe(b);

            if (_resetTimeScale.Value) Time.timeScale = 1f;

            SetStatus("Unloading the current scenes...", 120f);
            Scene holder = EnsureHolder();
            TrySetActive(holder);
            yield return StartCoroutine(UnloadAllExcept(holder));
            yield return null;
            yield return null;

            if (_unloadUnused.Value)
            {
                AsyncOperation uo = Resources.UnloadUnusedAssets();
                while (uo != null && !uo.isDone) yield return null;
            }

            SetStatus("Loading " + item.Name + "...", 120f);
            var r = new LoadResult();
            yield return StartCoroutine(LoadScene(target, LoadSceneMode.Single, r));

            if (!r.Ok)
            {
                bool restore = before.Any(x => x.Resolved);
                if (restore)
                {
                    SetStatus("Load failed. Restoring the previous scenes...", 120f);
                    yield return StartCoroutine(LoadLayout(before));
                }
                ShowError("Could not load '" + item.Name + "' (see the log). " +
                          (restore ? "The previous scenes were restored." : "You may be on an empty scene; press RELOAD WORLD or load another scene."));
                yield break;
            }
            yield return null;

            // "use" it: make it the active scene and clean up the holder
            Scene sc = FindScene(target);
            if (sc.IsValid() && sc.isLoaded) TrySetActive(sc);
            RemoveHolder();
            yield return null;
            yield return null;

            AfterLoadStatus(item.Name);
        }

        // ---------- RELOAD WORLD: same as WobblyReload ----------
        private IEnumerator ReloadRoutine()
        {
            List<LoadTarget> layout = CaptureLayout();
            if (layout.Count > 0)
            {
                _lastLayout = layout;
            }
            else if (_lastLayout != null && _lastLayout.Count > 0)
            {
                layout = _lastLayout;
                Logger.LogInfo("No real scene loaded right now. Using the layout saved earlier.");
            }
            else
            {
                ShowError("Nothing to reload: no scenes are loaded and nothing was saved.");
                yield break;
            }

            Logger.LogInfo("Reloading " + layout.Count + " scene(s):");
            foreach (LoadTarget s in layout) Logger.LogInfo("   " + s + (s.WasActive ? "  [active]" : ""));
            foreach (LoadTarget s in layout) ResolveSafe(s);

            if (!layout.Any(x => x.Resolved))
            {
                ShowError("Cannot reload: no way was found to load any of the current scenes. Nothing was unloaded.");
                yield break;
            }

            if (_resetTimeScale.Value) Time.timeScale = 1f;

            SetStatus("Reloading: unloading scenes...", 120f);
            Scene holder = EnsureHolder();
            TrySetActive(holder);
            yield return StartCoroutine(UnloadAllExcept(holder));
            yield return null;
            yield return null;

            if (_unloadUnused.Value)
            {
                AsyncOperation uo = Resources.UnloadUnusedAssets();
                while (uo != null && !uo.isDone) yield return null;
            }

            yield return StartCoroutine(LoadLayout(layout));

            yield return null;
            yield return null;
            Logger.LogInfo("Reload complete.");
            AfterLoadStatus("the world");
        }

        // Loads a saved layout: first scene first, then the rest on top, then the old active scene.
        private IEnumerator LoadLayout(List<LoadTarget> layout)
        {
            List<LoadTarget> usable = layout.Where(x => x.Resolved).ToList();
            if (usable.Count == 0)
            {
                ShowError("None of the saved scenes can be loaded again.");
                yield break;
            }

            LoadTarget first = usable[0];
            SetStatus("Loading " + first.Name + "...", 120f);
            var r = new LoadResult();
            LoadSceneMode firstMode = _firstAsSingle.Value ? LoadSceneMode.Single : LoadSceneMode.Additive;
            yield return StartCoroutine(LoadScene(first, firstMode, r));

            if (!r.Ok)
            {
                ShowError("Failed to load '" + first.Name + "'. You are on an empty scene; press RELOAD WORLD to try again (the layout is saved).");
                yield break;
            }
            yield return null;

            for (int i = 1; i < usable.Count; i++)
            {
                LoadTarget si = usable[i];
                SetStatus("Loading " + si.Name + "...", 120f);
                var ar = new LoadResult();
                yield return StartCoroutine(LoadScene(si, LoadSceneMode.Additive, ar));
                if (!ar.Ok) Logger.LogWarning("Could not restore " + si);
            }

            foreach (LoadTarget s in usable)
            {
                if (!s.WasActive) continue;
                Scene sc = FindScene(s);
                if (sc.IsValid() && sc.isLoaded) TrySetActive(sc);
            }
            RemoveHolder();
        }

        // ---------- LOAD ON TOP: find empty space, check it fits, load, move into place ----------
        private IEnumerator LoadOnTopRoutine(SceneItem item)
        {
            LoadTarget t = ToTarget(item);
            ResolveSafe(t);
            if (!t.Resolved)
            {
                ShowError("Cannot load '" + item.Name + "' on top: no way was found to load it.");
                yield break;
            }

            // what is loaded now
            var beforeHandles = new List<int>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene s = SceneManager.GetSceneAt(i);
                if (s.IsValid() && s.isLoaded) beforeHandles.Add(s.handle);
            }

            // 1) where is there nothing?
            SetStatus("Mapping the space that is already used...", 60f);
            yield return null;
            HashSet<long> occupied = BuildOccupancy();
            Logger.LogInfo("Occupied map: " + occupied.Count + " cell(s) of " + CellSize + " m.");
            yield return null;

            // 2) load it on top
            SetStatus("Loading " + item.Name + " on top...", 120f);
            var r = new LoadResult();
            yield return StartCoroutine(LoadScene(t, LoadSceneMode.Additive, r));
            if (!r.Ok)
            {
                ShowError("Cannot load '" + item.Name + "' on top: it failed to load additively (see the log).");
                yield break;
            }
            yield return null;
            yield return null;

            // 3) which scene is the new one, and did the old ones survive?
            Scene added = default(Scene);
            bool found = false;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene s = SceneManager.GetSceneAt(i);
                if (s.IsValid() && s.isLoaded && !beforeHandles.Contains(s.handle)) { added = s; found = true; break; }
            }
            if (!found)
            {
                ShowError("Cannot load '" + item.Name + "' on top: no new scene appeared (it may already be loaded or it cannot be added to other scenes).");
                yield break;
            }

            int lost = 0;
            foreach (int h in beforeHandles)
            {
                bool still = false;
                for (int i = 0; i < SceneManager.sceneCount; i++)
                    if (SceneManager.GetSceneAt(i).handle == h) { still = true; break; }
                if (!still) lost++;
            }
            if (lost > 0)
            {
                ShowError("'" + item.Name + "' cannot be loaded on top: loading it removed " + lost +
                          " existing scene(s). It can only be loaded on its own (use LOAD, or RELOAD WORLD to bring the world back).");
                yield break;
            }

            // 4) how big is it, and is there room?
            SetStatus("Measuring " + item.Name + "...", 60f);
            int[] fx, fz;
            Bounds total;
            bool hasGeometry = MeasureFootprint(added, out fx, out fz, out total);

            if (!hasGeometry)
            {
                Logger.LogInfo("'" + item.Name + "' has no visible geometry, so it was left where it is.");
                SetStatus("Loaded " + item.Name + " on top (it has no visible geometry, so it was left in place).", 8f);
                if (_closeAfter.Value) CloseMenu();
                yield break;
            }

            string size = Mathf.CeilToInt(total.size.x) + " x " + Mathf.CeilToInt(total.size.z) + " m";

            if (IsFree(occupied, fx, fz, 0, 0))
            {
                Logger.LogInfo("Nothing was in the way: '" + item.Name + "' (" + size + ") stays where it is.");
                SetStatus("Loaded " + item.Name + " on top. That area was empty (" + size + ").", 8f);
                if (_closeAfter.Value) CloseMenu();
                yield break;
            }

            // something is in the way: look for a free spot big enough
            var res = new OffsetResult();
            yield return StartCoroutine(FindFreeOffset(occupied, fx, fz, res));

            if (!res.Found)
            {
                try { SceneManager.UnloadSceneAsync(added); } catch { }
                ShowError("Cannot load '" + item.Name + "' on top: it needs about " + size +
                          " of empty space and none was found within " + Mathf.RoundToInt(_searchRadius.Value) +
                          " m. It was unloaded again.");
                yield break;
            }

            Vector3 move = new Vector3(res.Dx * CellSize, 0f, res.Dz * CellSize);
            foreach (GameObject root in added.GetRootGameObjects())
                if (root != null) root.transform.position += move;

            Logger.LogInfo("Moved '" + item.Name + "' by " + move + " to fit (" + size + ").");
            SetStatus("Loaded " + item.Name + " on top in an empty spot (moved " +
                      Mathf.RoundToInt(move.magnitude) + " m, size " + size + ").", 10f);
            if (_closeAfter.Value) CloseMenu();
        }
    }
}
