using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace WobblySceneOverlay
{
    // Wobbly Scene Overlay
    //   Press F3 (changeable) to open the overlay. It finds scenes, lets you search them, and can
    //   LOAD (replace the world), LOAD ON TOP (into empty space), RELOAD the world, and UNLOAD scenes.
    //
    // This file = the window. The other files: Catalog (finding scenes), Loading (the engine),
    // Space (free-space finder) and Explorer (optional Unity Explorer link).
    [BepInPlugin("com.example.wobbly.sceneoverlay", "Wobbly Scene Overlay", "1.0.0")]
    public partial class SceneOverlayPlugin : BaseUnityPlugin
    {
        private class LoadedInfo
        {
            public Scene Scene;
            public string Name;
            public bool Active;
        }

        // ---------- config ----------
        private ConfigEntry<KeyCode> _toggleKey;
        private ConfigEntry<KeyCode> _reloadKey;
        private ConfigEntry<bool> _closeAfter;
        private ConfigEntry<float> _uiScale;
        private ConfigEntry<float> _brightness;
        private ConfigEntry<string> _fontName;
        private ConfigEntry<float> _cfgW;
        private ConfigEntry<float> _cfgH;

        // ---------- window state ----------
        private bool _show;
        private bool _settingsPage;
        private string _filter = "";
        private string _vFilter = "";
        private Vector2 _scroll;
        private Vector2 _loadedScroll;
        private string _status = "";
        private float _statusUntil;
        private string _error;
        private bool _listening;
        private int _listenFor;         // 0 = overlay key, 1 = reload key

        private readonly List<LoadedInfo> _loadedNow = new List<LoadedInfo>();
        private readonly HashSet<string> _loadedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // text input
        private const int FieldSearch = 1;
        private int _focus;
        private int _bsFrame = -1;

        // window geometry (scaled GUI units)
        private const float MinW = 860f, MinH = 640f, DefW = 980f, DefH = 720f;
        private Rect _window;
        private bool _windowPlaced;
        private float _winW, _winH;
        private float _reqW, _reqH;
        private float _scale = 1f;
        private Vector2 _resizeOffset;

        // ---------- fonts / styles ----------
        private readonly List<Font> _fonts = new List<Font>();
        private bool _fontsLogged;
        private Font _font;
        private bool _stylesReady;
        private GUISkin _skin;
        private GUIStyle _sPanel, _sInset, _sTitle, _sLabel, _sSub, _sInfoTitle, _sInfo, _sNote, _sStatus,
                         _sField, _sHint, _sBtn, _sSmall, _sTiny, _sTabOn, _sRow, _sRowSel,
                         _sErrBox, _sErrText;

        private static float _lift = 1f;
        private static readonly List<Texture2D> _made = new List<Texture2D>();

        // =====================================================================
        //  setup
        // =====================================================================
        private void Awake()
        {
            // stay alive while every scene is unloaded during a reload
            gameObject.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(gameObject);

            _toggleKey = Config.Bind("General", "OverlayKey", KeyCode.F3,
                "Key that opens/closes the overlay. You can also change it inside the overlay (Settings).");
            _reloadKey = Config.Bind("General", "ReloadKey (used with Ctrl)", KeyCode.None,
                "Ctrl + this key reloads the whole world. None = off. Ctrl+Shift+key cancels a stuck operation first.");
            _closeAfter = Config.Bind("General", "CloseAfterLoad", false,
                "Close the overlay automatically after a scene is loaded.");
            _uiScale = Config.Bind("Window", "UiScale", 1.0f,
                new ConfigDescription("Overlay size multiplier.", new AcceptableValueRange<float>(0.6f, 1.8f)));
            _brightness = Config.Bind("Window", "Brightness", 1.0f,
                new ConfigDescription("Overlay brightness. Higher = brighter colors.", new AcceptableValueRange<float>(0.6f, 2.4f)));
            _fontName = Config.Bind("Window", "Font", "",
                "Font used by the overlay. Empty = pick automatically, 'default' = Unity's plain font.");
            _cfgW = Config.Bind("Window", "Width", DefW, "Overlay width.");
            _cfgH = Config.Bind("Window", "Height", DefH, "Overlay height.");

            BindCatalogConfig();
            BindLoadingConfig();
            BindSpaceConfig();

            _reqW = _winW = Mathf.Max(MinW, _cfgW.Value);
            _reqH = _winH = Mathf.Max(MinH, _cfgH.Value);

            LoadBundleCache();
            RebuildItems();
            Logger.LogInfo($"Scene Overlay ready. Press {_toggleKey.Value} in game.");
        }

        private void OnDestroy()
        {
            Logger.LogWarning("Scene Overlay object destroyed (hotkeys will stop working).");
        }

        private void Update()
        {
            // safety net: free a stuck operation so you can try again
            if (_busy && Time.realtimeSinceStartup - _startedAt > _stuckTimeout.Value)
            {
                StopAllCoroutines();
                _busy = false;
                _scanning = false;
                ShowError("The operation took longer than " + Mathf.RoundToInt(_stuckTimeout.Value) + " seconds and was cancelled. You can try again.");
            }
        }

        private void LateUpdate()
        {
            if (_show)
            {
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
            }
        }

        // =====================================================================
        //  per-frame hooks
        // =====================================================================
        private void OnGUI()
        {
            Event e = Event.current;
            if (e == null) return;

            _scale = Mathf.Max(0.75f, Screen.height / 1080f) * _uiScale.Value;

            if (e.type == EventType.Layout) RefreshFrameState();
            if (e.type == EventType.KeyDown) HandleHotkeys(e);

            if (!_show) return;
            if (!_stylesReady) BuildStyles();

            Matrix4x4 oldMatrix = GUI.matrix;
            GUISkin oldSkin = GUI.skin;

            GUI.skin = _skin;
            GUI.matrix = Matrix4x4.Scale(new Vector3(_scale, _scale, 1f));

            if (!_windowPlaced)
            {
                _window = new Rect((Screen.width / _scale - _winW) / 2f, (Screen.height / _scale - _winH) / 2f, _winW, _winH);
                _windowPlaced = true;
            }
            _window.width = _winW;
            _window.height = _winH;

            GUI.depth = -1000;
            _window = GUI.Window(982342, _window, DrawWindow, GUIContent.none, _sPanel);

            _window.width = _winW;
            _window.height = _winH;
            _window.x = Mathf.Clamp(_window.x, 90f - _winW, Screen.width / _scale - 90f);
            _window.y = Mathf.Clamp(_window.y, 0f, Screen.height / _scale - 50f);

            GUI.skin = oldSkin;
            GUI.matrix = oldMatrix;
        }

        // Runs at the start of every Layout pass so every event in a frame sees the same data.
        private void RefreshFrameState()
        {
            _reqW = Mathf.Clamp(_reqW, MinW, Mathf.Max(MinW, Screen.width / _scale - 20f));
            _reqH = Mathf.Clamp(_reqH, MinH, Mathf.Max(MinH, Screen.height / _scale - 20f));
            _winW = _reqW;
            _winH = _reqH;

            _vFilter = _filter;
            RebuildViewIfNeeded();

            _loadedNow.Clear();
            _loadedNames.Clear();
            Scene active = SceneManager.GetActiveScene();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene sc = SceneManager.GetSceneAt(i);
                if (!sc.IsValid() || !sc.isLoaded || sc.name == HolderName) continue;
                _loadedNow.Add(new LoadedInfo { Scene = sc, Name = sc.name, Active = sc.handle == active.handle });
                _loadedNames.Add(sc.name);
            }
        }

        private void OpenMenu()
        {
            RebuildItems();
            _show = true;
            _focus = 0;
            _listening = false;
            _settingsPage = false;
        }

        private void CloseMenu()
        {
            _show = false;
            _focus = 0;
            _listening = false;
        }

        // ---------- hotkeys ----------
        private void HandleHotkeys(Event e)
        {
            // waiting for a new key
            if (_show && _listening)
            {
                if (e.keyCode == KeyCode.Escape)
                {
                    _listening = false;
                    e.Use();
                }
                else if (e.keyCode != KeyCode.None && !IsMouseKey(e.keyCode))
                {
                    if (_listenFor == 0)
                    {
                        _toggleKey.Value = e.keyCode;
                        SetStatus($"Overlay key is now {e.keyCode}");
                    }
                    else
                    {
                        _reloadKey.Value = e.keyCode;
                        SetStatus($"Reload key is now Ctrl+{e.keyCode}");
                    }
                    _listening = false;
                    e.Use();
                }
                return;
            }

            if (_show && e.keyCode == KeyCode.Escape)
            {
                if (_focus != 0) _focus = 0;
                else CloseMenu();
                e.Use();
                return;
            }

            // Ctrl + reload key (Ctrl+Shift cancels a stuck operation first)
            if (_reloadKey.Value != KeyCode.None && e.keyCode == _reloadKey.Value && (e.control || e.command))
            {
                if (e.shift && (_busy || _scanning))
                {
                    Logger.LogWarning("Forcing: cancelling the current operation.");
                    StopAllCoroutines();
                    _busy = false;
                    _scanning = false;
                }
                StartReload();
                e.Use();
                return;
            }

            if (e.keyCode == _toggleKey.Value && !(_show && _focus != 0 && IsTypingKey(e.keyCode)))
            {
                if (_show) CloseMenu(); else OpenMenu();
                e.Use();
            }
        }

        private static bool IsMouseKey(KeyCode k)
        {
            return k >= KeyCode.Mouse0 && k <= KeyCode.Mouse6;
        }

        private static bool IsTypingKey(KeyCode k)
        {
            return (k >= KeyCode.A && k <= KeyCode.Z) ||
                   (k >= KeyCode.Alpha0 && k <= KeyCode.Alpha9) ||
                   (k >= KeyCode.Keypad0 && k <= KeyCode.Keypad9) ||
                   k == KeyCode.Space || k == KeyCode.Minus || k == KeyCode.Equals ||
                   k == KeyCode.Comma || k == KeyCode.Period || k == KeyCode.Slash ||
                   k == KeyCode.Backslash || k == KeyCode.Semicolon || k == KeyCode.Quote ||
                   k == KeyCode.LeftBracket || k == KeyCode.RightBracket || k == KeyCode.BackQuote;
        }

        // =====================================================================
        //  the window (everything is placed with exact rectangles)
        // =====================================================================
        private void DrawWindow(int id)
        {
            float W = _winW, H = _winH;
            const float P = 22f;

            // title row
            GUI.Label(new Rect(0f, 12f, W, 44f), "Scene Overlay", _sTitle);
            if (GUI.Button(new Rect(W - P - 100f, 18f, 100f, 34f), _settingsPage ? "Back" : "Settings", _sSmall))
            {
                _settingsPage = !_settingsPage;
                _focus = 0;
                _listening = false;
            }

            Rect statusRect = new Rect(P, H - 14f - 48f, W - 2f * P - 34f, 48f);
            float top = 66f;
            float bottom = statusRect.y - 8f;

            if (_settingsPage) DrawSettings(P, top, W - 2f * P, bottom - top);
            else DrawMain(P, top, W - 2f * P, bottom - top);

            DrawStatus(statusRect);
            DrawResizeGrip();
            GUI.DragWindow(new Rect(0f, 0f, W - 130f, 60f));
        }

        // ---------- main page ----------
        private void DrawMain(float x, float y, float w, float h)
        {
            const float rightW = 330f, gap = 12f;
            float leftW = w - rightW - gap;

            // ----- left: search + scene list -----
            GUI.Label(new Rect(x, y, 74f, 34f), "Search", _sLabel);
            bool hasFilter = _vFilter.Length > 0;
            float fieldW = leftW - 78f - (hasFilter ? 82f : 0f);
            _filter = TextBox(FieldSearch, new Rect(x + 78f, y, fieldW, 34f), _filter, "Type to search scenes...", 60);
            if (hasFilter && GUI.Button(new Rect(x + leftW - 78f, y, 78f, 34f), "Clear", _sSmall))
                _filter = "";

            string info = _view.Count + " of " + _items.Count + " scenes";
            if (_scanning) info += "   -   scanning bundle files " + _scanDone + "/" + _scanTotal + "...";
            else if (_nBundleFile == 0) info += "   -   press SCAN BUNDLES to look inside asset bundle files";
            GUI.Label(new Rect(x, y + 38f, leftW, 24f), info, _sSub);

            DrawSceneList(new Rect(x, y + 66f, leftW, h - 66f));

            // ----- right: selected scene, actions, loaded scenes -----
            float rx = x + leftW + gap;
            float ry = y;

            DrawSelectedCard(new Rect(rx, ry, rightW, 124f));
            ry += 132f;

            if (GUI.Button(new Rect(rx, ry, rightW, 40f), "LOAD  (REPLACE WORLD)", _sBtn)) StartReplace(_selected);
            ry += 44f;
            if (GUI.Button(new Rect(rx, ry, rightW, 40f), "LOAD ON TOP", _sBtn)) StartLoadOnTop(_selected);
            ry += 44f;
            if (GUI.Button(new Rect(rx, ry, rightW, 40f), "RELOAD WORLD", _sBtn)) StartReload();
            ry += 44f;

            float half = (rightW - 8f) / 2f;
            if (GUI.Button(new Rect(rx, ry, half, 34f), _scanning ? "SCANNING..." : "SCAN BUNDLES", _sSmall)) StartBundleScan();
            if (GUI.Button(new Rect(rx + half + 8f, ry, half, 34f), "EXPLORER", _sSmall)) ToggleExplorer();
            ry += 42f;

            GUI.Label(new Rect(rx, ry, rightW, 24f), "Loaded now", _sLabel);
            ry += 26f;
            DrawLoadedList(new Rect(rx, ry, rightW, Mathf.Max(60f, y + h - ry)));
        }

        private static string TagFor(SceneItem s)
        {
            if (s.Source == SceneSource.Build) return "Build";
            if (s.Source == SceneSource.Addressable) return "Addr";
            return "Bundle";
        }

        private static string SourceText(SceneItem s)
        {
            if (s.Source == SceneSource.Build) return "Build scene #" + s.BuildIndex;
            if (s.Source == SceneSource.Addressable) return "Addressables scene";
            if (!string.IsNullOrEmpty(s.BundleFile)) return "Bundle file: " + System.IO.Path.GetFileName(s.BundleFile);
            return "Scene in a loaded bundle";
        }

        private void DrawSelectedCard(Rect r)
        {
            GUI.Box(r, GUIContent.none, _sInset);
            SceneItem s = _selected;
            if (s == null)
            {
                GUI.Label(new Rect(r.x + 10f, r.y + 8f, r.width - 20f, r.height - 16f),
                    "Pick a scene from the list.\nDouble-click a scene to load it.", _sNote);
                return;
            }

            GUI.Label(new Rect(r.x + 10f, r.y + 6f, r.width - 20f, 28f), s.Name, _sInfoTitle);
            GUI.Label(new Rect(r.x + 10f, r.y + 34f, r.width - 20f, 42f), s.Path, _sInfo);
            GUI.Label(new Rect(r.x + 10f, r.y + 78f, r.width - 20f, 22f), SourceText(s), _sInfo);
            if (_loadedNames.Contains(s.Name))
                GUI.Label(new Rect(r.x + 10f, r.y + 98f, r.width - 20f, 22f), "This scene is loaded right now.", _sInfo);
        }

        private void DrawSceneList(Rect area)
        {
            GUI.Box(area, GUIContent.none, _sInset);
            Rect inner = new Rect(area.x + 8f, area.y + 8f, area.width - 16f, area.height - 16f);

            if (_view.Count == 0)
            {
                GUI.Label(inner, _items.Count == 0 ? "No scenes were found automatically.\nTry SCAN BUNDLES." : "No scenes match your search.", _sNote);
                return;
            }

            const float rowH = 38f, gap = 4f, step = rowH + gap;
            float contentH = _view.Count * step;
            float w = inner.width - (contentH > inner.height ? 20f : 0f);

            _scroll.y = Mathf.Clamp(_scroll.y, 0f, Mathf.Max(0f, contentH - inner.height));
            _scroll = GUI.BeginScrollView(inner, _scroll, new Rect(0f, 0f, w, contentH), false, false,
                GUIStyle.none, _skin.verticalScrollbar);

            int first = Mathf.Max(0, (int)(_scroll.y / step));
            int last = Mathf.Min(_view.Count, first + (int)(inner.height / step) + 2);

            for (int i = first; i < last; i++)
            {
                SceneItem s = _view[i];
                Rect rr = new Rect(0f, i * step, w, rowH);
                string label = s.Name + "  [" + TagFor(s) + "]" + (_loadedNames.Contains(s.Name) ? "   (loaded)" : "");

                int clicks = Event.current.clickCount;
                if (GUI.Button(rr, label, s == _selected ? _sRowSel : _sRow))
                {
                    _selected = s;
                    if (clicks >= 2) StartReplace(s);   // double-click = load
                }
            }
            GUI.EndScrollView();
        }

        private void DrawLoadedList(Rect area)
        {
            GUI.Box(area, GUIContent.none, _sInset);
            Rect inner = new Rect(area.x + 6f, area.y + 6f, area.width - 12f, area.height - 12f);

            if (_loadedNow.Count == 0)
            {
                GUI.Label(inner, "No scenes are loaded.", _sNote);
                return;
            }

            const float rowH = 36f, gap = 4f, step = rowH + gap;
            float contentH = _loadedNow.Count * step;
            float w = inner.width - (contentH > inner.height ? 20f : 0f);
            bool explorer = ExplorerAvailable;

            _loadedScroll.y = Mathf.Clamp(_loadedScroll.y, 0f, Mathf.Max(0f, contentH - inner.height));
            _loadedScroll = GUI.BeginScrollView(inner, _loadedScroll, new Rect(0f, 0f, w, contentH), false, false,
                GUIStyle.none, _skin.verticalScrollbar);

            for (int i = 0; i < _loadedNow.Count; i++)
            {
                LoadedInfo li = _loadedNow[i];
                float y = i * step;

                Rect rUnload = new Rect(w - 66f, y, 66f, rowH);
                Rect rUse = new Rect(rUnload.x - 4f - 44f, y, 44f, rowH);
                float nameRight = rUse.x - 4f;
                Rect rInspect = new Rect(0f, y, 0f, rowH);
                if (explorer)
                {
                    rInspect = new Rect(rUse.x - 4f - 36f, y, 36f, rowH);
                    nameRight = rInspect.x - 4f;
                }

                GUI.Box(new Rect(0f, y, Mathf.Max(20f, nameRight), rowH),
                    li.Name + (li.Active ? "  *" : ""), li.Active ? _sRowSel : _sRow);

                if (GUI.Button(rUse, "Use", _sTiny)) MakeActive(li.Scene);
                if (GUI.Button(rUnload, "Unload", _sTiny)) UnloadLoaded(li.Scene);
                if (explorer && GUI.Button(rInspect, "Ex", _sTiny)) InspectInExplorer(li.Scene);
            }
            GUI.EndScrollView();
        }

        private void DrawStatus(Rect r)
        {
            if (!string.IsNullOrEmpty(_error))
            {
                GUI.Box(r, GUIContent.none, _sErrBox);
                GUI.Label(new Rect(r.x + 12f, r.y + 2f, r.width - 24f, r.height - 4f),
                    "ERROR: " + _error + "   (click to dismiss)", _sErrText);
                if (GUI.Button(r, GUIContent.none, GUIStyle.none)) _error = null;
                return;
            }

            string text;
            if (Time.realtimeSinceStartup < _statusUntil) text = _status;
            else
            {
                Scene a = SceneManager.GetActiveScene();
                text = "Active scene: " + (a.IsValid() ? a.name : "-") + "   |   " + _toggleKey.Value + " or Esc closes this window";
            }
            GUI.Label(r, text, _sStatus);
        }

        // ---------- settings page ----------
        private void DrawSettings(float x, float y, float w, float h)
        {
            float cy = y;
            const float rowH = 34f;

            // overlay key
            GUI.Label(new Rect(x, cy, w, 22f), "Overlay key  (opens and closes this window)", _sLabel);
            cy += 24f;
            string keyText = _listening && _listenFor == 0 ? "Press any key...   (Esc = cancel)" : _toggleKey.Value.ToString();
            if (GUI.Button(new Rect(x, cy, w - 160f, rowH), keyText, _listening && _listenFor == 0 ? _sTabOn : _sBtn))
            {
                _listenFor = 0;
                _listening = !_listening;
                _focus = 0;
            }
            if (GUI.Button(new Rect(x + w - 150f, cy, 150f, rowH), "Reset to F3", _sSmall))
            {
                _toggleKey.Value = KeyCode.F3;
                _listening = false;
                SetStatus("Overlay key is now F3");
            }
            cy += rowH + 10f;

            // reload key
            GUI.Label(new Rect(x, cy, w, 22f), "Reload hotkey  (used together with Ctrl)", _sLabel);
            cy += 24f;
            string rText = _listening && _listenFor == 1 ? "Press any key...   (Esc = cancel)"
                         : _reloadKey.Value == KeyCode.None ? "Off" : "Ctrl + " + _reloadKey.Value;
            if (GUI.Button(new Rect(x, cy, w - 160f, rowH), rText, _listening && _listenFor == 1 ? _sTabOn : _sBtn))
            {
                _listenFor = 1;
                _listening = !_listening;
                _focus = 0;
            }
            if (GUI.Button(new Rect(x + w - 150f, cy, 150f, rowH), "Turn off", _sSmall))
            {
                _reloadKey.Value = KeyCode.None;
                _listening = false;
                SetStatus("Reload hotkey is off");
            }
            cy += rowH + 10f;

            // after loading
            GUI.Label(new Rect(x, cy, w, 22f), "After loading a scene", _sLabel);
            cy += 24f;
            float halfW = (w - 8f) / 2f;
            if (GUI.Button(new Rect(x, cy, halfW, rowH), "Keep window open", _closeAfter.Value ? _sBtn : _sTabOn))
                _closeAfter.Value = false;
            if (GUI.Button(new Rect(x + halfW + 8f, cy, halfW, rowH), "Close window", _closeAfter.Value ? _sTabOn : _sBtn))
                _closeAfter.Value = true;
            cy += rowH + 10f;

            // size
            GUI.Label(new Rect(x, cy, w, 22f), "Window size:  " + Mathf.RoundToInt(_uiScale.Value * 100f) + "%", _sLabel);
            cy += 24f;
            float thirdW = (w - 16f) / 3f;
            if (GUI.Button(new Rect(x, cy, thirdW, rowH), "Smaller", _sBtn))
                _uiScale.Value = Mathf.Clamp(Mathf.Round((_uiScale.Value - 0.1f) * 10f) / 10f, 0.6f, 1.8f);
            if (GUI.Button(new Rect(x + thirdW + 8f, cy, thirdW, rowH), "Bigger", _sBtn))
                _uiScale.Value = Mathf.Clamp(Mathf.Round((_uiScale.Value + 0.1f) * 10f) / 10f, 0.6f, 1.8f);
            if (GUI.Button(new Rect(x + 2f * (thirdW + 8f), cy, thirdW, rowH), "100%", _sSmall))
                _uiScale.Value = 1f;
            cy += rowH + 10f;

            // brightness
            GUI.Label(new Rect(x, cy, w, 22f), "Brightness:  " + Mathf.RoundToInt(_brightness.Value * 100f) + "%", _sLabel);
            cy += 24f;
            if (GUI.Button(new Rect(x, cy, thirdW, rowH), "Dimmer", _sBtn)) SetBrightness(_brightness.Value - 0.1f);
            if (GUI.Button(new Rect(x + thirdW + 8f, cy, thirdW, rowH), "Brighter", _sBtn)) SetBrightness(_brightness.Value + 0.1f);
            if (GUI.Button(new Rect(x + 2f * (thirdW + 8f), cy, thirdW, rowH), "Reset", _sSmall)) SetBrightness(1f);
            cy += rowH + 10f;

            // font
            GUI.Label(new Rect(x, cy, w, 22f), "Font:  " + CurrentFontLabel(), _sLabel);
            cy += 24f;
            if (GUI.Button(new Rect(x, cy, thirdW, rowH), "Previous", _sBtn)) CycleFont(-1);
            if (GUI.Button(new Rect(x + thirdW + 8f, cy, thirdW, rowH), "Next", _sBtn)) CycleFont(1);
            if (GUI.Button(new Rect(x + 2f * (thirdW + 8f), cy, thirdW, rowH), "Auto", _sSmall))
            {
                _fontName.Value = "";
                _stylesReady = false;
            }
            cy += rowH + 10f;

            // bundle cache
            GUI.Label(new Rect(x, cy, w, 22f), "Bundle files remembered:  " + _bundleCache.Count, _sLabel);
            cy += 24f;
            if (GUI.Button(new Rect(x, cy, halfW, rowH), "Clear bundle cache", _sBtn)) ClearBundleCache();
            if (GUI.Button(new Rect(x + halfW + 8f, cy, halfW, rowH), "Reset window size", _sBtn))
            {
                _reqW = DefW;
                _reqH = DefH;
                _cfgW.Value = DefW;
                _cfgH.Value = DefH;
                _windowPlaced = false;
                SetStatus("Window size reset");
            }
        }

        // ---------- resize handle ----------
        private void DrawResizeGrip()
        {
            Event e = Event.current;
            Rect grip = new Rect(_winW - 32f, _winH - 32f, 28f, 28f);
            int cid = GUIUtility.GetControlID(FocusType.Passive);

            if (e.type == EventType.Repaint)
            {
                Color prev = GUI.color;
                GUI.color = new Color(0.45f, 0.28f, 0.08f, 0.85f);
                for (int r = 0; r < 3; r++)
                    for (int c = 0; c < 3; c++)
                        if (r + c >= 2)
                            GUI.DrawTexture(new Rect(grip.xMax - 8f + (c - 2) * 7f, grip.yMax - 8f + (r - 2) * 7f, 3f, 3f),
                                Texture2D.whiteTexture);
                GUI.color = prev;
            }

            switch (e.GetTypeForControl(cid))
            {
                case EventType.MouseDown:
                    if (grip.Contains(e.mousePosition))
                    {
                        GUIUtility.hotControl = cid;
                        _resizeOffset = new Vector2(_winW - e.mousePosition.x, _winH - e.mousePosition.y);
                        e.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == cid)
                    {
                        _reqW = e.mousePosition.x + _resizeOffset.x;
                        _reqH = e.mousePosition.y + _resizeOffset.y;
                        e.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == cid)
                    {
                        GUIUtility.hotControl = 0;
                        _cfgW.Value = _winW;
                        _cfgH.Value = _winH;
                        e.Use();
                    }
                    break;
            }
        }

        // =====================================================================
        //  text box (so Backspace, Delete, paste and Enter always work)
        // =====================================================================
        private string TextBox(int id, Rect r, string value, string hint, int maxLen)
        {
            Event e = Event.current;
            bool focused = _focus == id;

            switch (e.type)
            {
                case EventType.MouseDown:
                    if (r.Contains(e.mousePosition))
                    {
                        _focus = id;
                        _listening = false;
                        focused = true;
                        e.Use();
                    }
                    else if (_focus == id)
                    {
                        _focus = 0;
                        focused = false;
                    }
                    break;

                case EventType.KeyDown:
                    if (focused) value = HandleTyping(value, maxLen);
                    break;

                case EventType.Repaint:
                    DrawTextBox(r, value, hint, focused);
                    break;
            }
            return value;
        }

        private string HandleTyping(string value, int maxLen)
        {
            Event e = Event.current;
            bool ctrl = e.control || e.command;

            if (e.keyCode == KeyCode.Backspace)
            {
                _bsFrame = Time.frameCount;
                value = ctrl ? DeleteWord(value) : DeleteLast(value);
                e.Use();
            }
            else if (e.character == '\b')
            {
                if (_bsFrame != Time.frameCount) value = DeleteLast(value);
                e.Use();
            }
            else if (e.keyCode == KeyCode.Delete)
            {
                value = "";
                e.Use();
            }
            else if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
            {
                _focus = 0;
                e.Use();
            }
            else if (ctrl && e.keyCode == KeyCode.V)
            {
                string clip = (GUIUtility.systemCopyBuffer ?? "").Replace("\r", "").Replace("\n", "");
                value += clip;
                if (value.Length > maxLen) value = value.Substring(0, maxLen);
                e.Use();
            }
            else if (!ctrl && e.character >= ' ' && e.character != (char)127)
            {
                if (value.Length < maxLen) value += e.character;
                e.Use();
            }
            return value;
        }

        private static string DeleteLast(string s)
        {
            return s.Length > 0 ? s.Substring(0, s.Length - 1) : s;
        }

        private static string DeleteWord(string s)
        {
            int i = s.Length;
            while (i > 0 && s[i - 1] == ' ') i--;
            while (i > 0 && s[i - 1] != ' ') i--;
            return s.Substring(0, i);
        }

        private void DrawTextBox(Rect r, string value, string hint, bool focused)
        {
            bool showHint = value.Length == 0 && !focused;
            GUIStyle st = showHint ? _sHint : _sField;

            string shown = showHint ? hint : value;
            if (focused && ((int)(Time.realtimeSinceStartup * 2f)) % 2 == 0) shown += "|";

            float avail = r.width - st.padding.horizontal;
            int guard = 0;
            while (shown.Length > 1 && st.CalcSize(new GUIContent(shown)).x > avail && guard++ < 200)
                shown = shown.Substring(1);

            st.Draw(r, new GUIContent(shown), r.Contains(Event.current.mousePosition), false, false, focused);
        }

        // =====================================================================
        //  fonts
        // =====================================================================
        private void RefreshFonts()
        {
            _fonts.Clear();
            var seen = new HashSet<string>();
            try
            {
                foreach (UnityEngine.Object o in Resources.FindObjectsOfTypeAll(typeof(Font)))
                {
                    Font f = o as Font;
                    if (f == null || !f.dynamic || string.IsNullOrEmpty(f.name)) continue;
                    if (seen.Add(f.name)) _fonts.Add(f);
                }
                _fonts.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) { Logger.LogWarning("Font scan failed: " + ex.Message); }

            if (!_fontsLogged)
            {
                _fontsLogged = true;
                Logger.LogInfo("Fonts available for the overlay: " +
                               (_fonts.Count == 0 ? "(none)" : string.Join(", ", _fonts.Select(f => f.name).ToArray())));
            }
        }

        private static bool IsPlainFont(string name)
        {
            string n = name.ToLowerInvariant();
            return n.Contains("arial") || n.Contains("legacyruntime") || n.Contains("liberation") || n.Contains("dejavu");
        }

        private Font PickFont()
        {
            string want = _fontName.Value ?? "";
            if (want == "default") return null;

            if (want.Length > 0)
            {
                Font f = _fonts.Find(x => x.name == want);
                if (f != null) return f;
            }
            return _fonts.Find(x => !IsPlainFont(x.name));
        }

        private string CurrentFontLabel()
        {
            if (_fontName.Value == "default") return "Default (Unity)";
            return _font != null ? _font.name : "Default (Unity)";
        }

        private void CycleFont(int dir)
        {
            var names = new List<string> { "default" };
            foreach (Font f in _fonts) names.Add(f.name);

            string cur = _fontName.Value == "default" ? "default" : (_font != null ? _font.name : "default");
            int idx = Mathf.Max(0, names.IndexOf(cur));
            idx = (idx + dir + names.Count) % names.Count;

            _fontName.Value = names[idx];
            _stylesReady = false;
        }

        // =====================================================================
        //  look and feel: solid game-style gold, no shadows, fades or dark overlays
        // =====================================================================
        // Fill colors go through the brightness setting; outlines and text use Raw so they stay crisp.
        private static Color Rgb(int r, int g, int b)
        {
            float inv = 1f / Mathf.Max(0.3f, _lift);
            return new Color(Mathf.Pow(r / 255f, inv), Mathf.Pow(g / 255f, inv), Mathf.Pow(b / 255f, inv), 1f);
        }

        private static Color Raw(int r, int g, int b) { return new Color(r / 255f, g / 255f, b / 255f, 1f); }

        private void SetBrightness(float v)
        {
            _brightness.Value = Mathf.Clamp(Mathf.Round(v * 10f) / 10f, 0.6f, 2.4f);
            _stylesReady = false;
        }

        private void DisposeStyles()
        {
            foreach (Texture2D t in _made)
                if (t != null) UnityEngine.Object.Destroy(t);
            _made.Clear();
            if (_skin != null) UnityEngine.Object.Destroy(_skin);
            _skin = null;
        }

        private static Texture2D Rounded(Color fill, Color outline, int outlineW = 3, int size = 48, int radius = 16)
        {
            return RoundedGrad(fill, fill, outline, outlineW, size, radius);
        }

        private static Texture2D RoundedGrad(Color top, Color bottom, Color outline, int outlineW = 3, int size = 48, int radius = 16)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            _made.Add(tex);

            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                Color fill = Color.Lerp(bottom, top, (y + 0.5f) / size);
                for (int x = 0; x < size; x++)
                {
                    float fx = x + 0.5f, fy = y + 0.5f;
                    float cx = Mathf.Clamp(fx, radius, size - radius);
                    float cy = Mathf.Clamp(fy, radius, size - radius);
                    float d = Mathf.Sqrt((fx - cx) * (fx - cx) + (fy - cy) * (fy - cy));
                    float alpha = Mathf.Clamp01(radius - d + 0.5f);
                    float t = Mathf.Clamp01(d - (radius - outlineW) + 0.5f);
                    Color c = Color.Lerp(fill, outline, t);
                    c.a *= alpha;
                    px[y * size + x] = c;
                }
            }
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }

        private static void SetState(GUIStyleState st, Texture2D bg, Color text)
        {
            st.background = bg;
            st.textColor = text;
        }

        private FontStyle Weight { get { return _font != null ? FontStyle.Normal : FontStyle.Bold; } }

        private GUIStyle MakeButton(Color fill, Color outline, Color hover, Color pressed, Color text,
                                    int fontSize, TextAnchor anchor, int outlineW = 3)
        {
            var s = new GUIStyle(GUI.skin.button);
            Texture2D n = RoundedGrad(Color.Lerp(fill, Color.white, 0.18f), Color.Lerp(fill, Color.black, 0.10f), outline, outlineW);
            Texture2D h = RoundedGrad(Color.Lerp(hover, Color.white, 0.18f), Color.Lerp(hover, Color.black, 0.10f), outline, outlineW);
            Texture2D a = RoundedGrad(Color.Lerp(pressed, Color.black, 0.10f), Color.Lerp(pressed, Color.white, 0.08f), outline, outlineW);

            SetState(s.normal, n, text);   SetState(s.onNormal, n, text);
            SetState(s.hover, h, text);    SetState(s.onHover, h, text);
            SetState(s.active, a, text);   SetState(s.onActive, a, text);
            SetState(s.focused, n, text);  SetState(s.onFocused, n, text);

            s.border = new RectOffset(16, 16, 16, 16);
            s.padding = new RectOffset(10, 10, 4, 4);
            s.margin = new RectOffset(0, 0, 0, 0);
            s.font = _font;
            s.fontSize = fontSize;
            s.fontStyle = Weight;
            s.alignment = anchor;
            s.wordWrap = false;
            s.clipping = TextClipping.Clip;
            return s;
        }

        private GUIStyle MakeBox(Color fill, Color outline, int outlineW = 3, int size = 48, int radius = 16)
        {
            var s = new GUIStyle(GUI.skin.box);
            SetState(s.normal, Rounded(fill, outline, outlineW, size, radius), Color.white);
            s.border = new RectOffset(radius, radius, radius, radius);
            s.padding = new RectOffset(0, 0, 0, 0);
            s.margin = new RectOffset(0, 0, 0, 0);
            s.font = _font;
            return s;
        }

        private GUIStyle MakeLabel(int size, Color color, TextAnchor anchor, bool wrap = false)
        {
            var s = new GUIStyle(GUI.skin.label);
            s.font = _font;
            s.fontSize = size;
            s.fontStyle = Weight;
            s.alignment = anchor;
            s.wordWrap = wrap;
            s.clipping = TextClipping.Clip;
            s.richText = false;
            s.margin = new RectOffset(0, 0, 0, 0);
            s.padding = new RectOffset(0, 0, 0, 0);
            s.normal.textColor = color;
            return s;
        }

        private void BuildStyles()
        {
            DisposeStyles();
            _lift = _brightness.Value;
            RefreshFonts();
            _font = PickFont();

            Color ink = Raw(78, 44, 8);               // dark brown text
            Color outline = Raw(112, 64, 12);         // brown outline like the game's buttons
            Color cream = Raw(255, 248, 214);         // highlight outline

            // our own skin, so we never change the skin other mods use
            _skin = UnityEngine.Object.Instantiate(GUI.skin);
            _skin.hideFlags = HideFlags.HideAndDontSave;
            if (_font != null) _skin.font = _font;

            Texture2D track = Rounded(Rgb(190, 130, 20), outline, 2, 24, 8);
            Texture2D thumb = Rounded(Rgb(255, 214, 80), outline, 2, 24, 8);
            Texture2D thumbHot = Rounded(Rgb(255, 232, 140), outline, 2, 24, 8);
            GUIStyle sb = _skin.verticalScrollbar;
            SetState(sb.normal, track, Color.white); SetState(sb.hover, track, Color.white);
            SetState(sb.active, track, Color.white); SetState(sb.focused, track, Color.white);
            sb.border = new RectOffset(8, 8, 8, 8);
            sb.fixedWidth = 16;
            sb.margin = new RectOffset(2, 2, 2, 2);
            GUIStyle th = _skin.verticalScrollbarThumb;
            SetState(th.normal, thumb, Color.white); SetState(th.hover, thumbHot, Color.white);
            SetState(th.active, thumbHot, Color.white); SetState(th.focused, thumb, Color.white);
            th.border = new RectOffset(8, 8, 8, 8);
            th.fixedWidth = 16;
            th.overflow = new RectOffset(0, 0, 0, 0);

            // window frame and the wells behind the lists (solid colors only)
            _sPanel = MakeBox(Rgb(236, 176, 34), Raw(124, 74, 16), 5, 64, 22);
            _sInset = MakeBox(Rgb(220, 160, 28), Raw(150, 92, 18), 2);
            _sErrBox = MakeBox(Rgb(214, 70, 56), Raw(110, 28, 20), 3);

            // text
            _sTitle = MakeLabel(32, ink, TextAnchor.MiddleCenter);
            _sLabel = MakeLabel(17, ink, TextAnchor.MiddleLeft);
            _sSub = MakeLabel(14, Raw(110, 70, 20), TextAnchor.MiddleLeft);
            _sInfoTitle = MakeLabel(19, ink, TextAnchor.MiddleLeft);
            _sInfo = MakeLabel(14, ink, TextAnchor.UpperLeft, true);
            _sNote = MakeLabel(17, ink, TextAnchor.MiddleCenter, true);
            _sStatus = MakeLabel(15, Raw(110, 58, 8), TextAnchor.MiddleLeft, true);
            _sErrText = MakeLabel(14, Color.white, TextAnchor.MiddleLeft, true);

            // search box: solid dark brown
            Texture2D fieldBg = Rounded(Raw(112, 76, 14), outline);
            _sField = new GUIStyle(GUI.skin.textField);
            SetState(_sField.normal, fieldBg, cream);
            SetState(_sField.hover, Rounded(Raw(126, 86, 18), outline), cream);
            SetState(_sField.focused, Rounded(Raw(98, 66, 10), cream, 4), cream);
            SetState(_sField.active, fieldBg, cream);
            _sField.border = new RectOffset(16, 16, 16, 16);
            _sField.padding = new RectOffset(14, 14, 5, 5);
            _sField.margin = new RectOffset(0, 0, 0, 0);
            _sField.font = _font;
            _sField.fontSize = 18;
            _sField.fontStyle = Weight;
            _sField.alignment = TextAnchor.MiddleLeft;
            _sField.clipping = TextClipping.Clip;
            _sField.wordWrap = false;

            _sHint = new GUIStyle(_sField);
            Color hintCol = Raw(206, 176, 124);
            _sHint.normal.textColor = hintCol;
            _sHint.hover.textColor = hintCol;
            _sHint.focused.textColor = hintCol;
            _sHint.active.textColor = hintCol;

            // gold buttons with brown outlines
            Color gold = Rgb(255, 184, 22), goldHot = Rgb(255, 208, 70), goldDown = Rgb(232, 150, 10);
            _sBtn = MakeButton(gold, outline, goldHot, goldDown, ink, 16, TextAnchor.MiddleCenter);
            _sSmall = MakeButton(gold, outline, goldHot, goldDown, ink, 15, TextAnchor.MiddleCenter);
            _sTiny = MakeButton(gold, outline, goldHot, goldDown, ink, 13, TextAnchor.MiddleCenter);
            _sTiny.padding = new RectOffset(2, 2, 2, 2);
            _sTabOn = MakeButton(Rgb(255, 226, 100), cream, Rgb(255, 236, 140), Rgb(240, 200, 60), ink, 16, TextAnchor.MiddleCenter, 4);

            // list rows
            _sRow = MakeButton(gold, outline, goldHot, goldDown, ink, 17, TextAnchor.MiddleCenter);
            _sRowSel = MakeButton(Rgb(255, 226, 100), cream, Rgb(255, 236, 140), Rgb(240, 200, 60), ink, 17, TextAnchor.MiddleCenter, 4);

            _stylesReady = true;
        }
    }
}
