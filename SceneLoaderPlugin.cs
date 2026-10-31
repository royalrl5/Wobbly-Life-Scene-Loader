using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace WobblySceneLoader
{
    [BepInPlugin("com.example.wobbly.sceneloader", "Wobbly Scene Loader", "1.7.0")]
    public class SceneLoaderPlugin : BaseUnityPlugin
    {
        private enum Source { Build, Bundle }

        private class SceneEntry
        {
            public Source Source;
            public int Index = -1;
            public string Name;
            public string Path;
        }

        // ---------- config ----------
        private ConfigEntry<KeyCode> _toggleKey;
        private ConfigEntry<bool> _closeAfter;
        private ConfigEntry<float> _uiScale;
        private ConfigEntry<float> _cfgW;
        private ConfigEntry<float> _cfgH;

        // ---------- scenes ----------
        private readonly List<SceneEntry> _scenes = new List<SceneEntry>();
        private SceneEntry _selectedEntry;

        // ---------- menu state ----------
        private bool _show;
        private bool _settingsPage;
        private bool _vPage;
        private string _filter = "";
        private string _vFilter = "";
        private Vector2 _scroll;

        // text input
        private const int FieldSearch = 1;
        private int _focus;

        // window geometry
        private const float MinW = 640f, MinH = 680f, DefW = 760f, DefH = 820f;
        private Rect _window;
        private bool _windowPlaced;
        private float _winW, _winH;
        private float _reqW, _reqH;
        private float _scale = 1f;

        private readonly List<Action> _deferred = new List<Action>();

        // ---------- styles ----------
        private bool _stylesReady;
        private GUISkin _skin;
        private GUIStyle _sPanel, _sBanner, _sInset,
                         _sTitle, _sTitleShadow, _sSubtitle, _sLabel,
                         _sField, _sHint, _sRow, _sRowSelected, _sRowText,
                         _sGreen, _sOrange, _sRed;

        private void Awake()
        {
            _toggleKey = Config.Bind("General", "ToggleKey", KeyCode.F11, "Key that opens/closes the scene loader menu.");
            _closeAfter = Config.Bind("General", "CloseAfterLoad", false, "Close menu automatically after loading.");
            _uiScale = Config.Bind("Window", "UiScale", 1.0f, new ConfigDescription("Menu size.", new AcceptableValueRange<float>(0.6f, 1.8f)));
            _cfgW = Config.Bind("Window", "Width", DefW, "Menu width.");
            _cfgH = Config.Bind("Window", "Height", DefH, "Menu height.");

            _reqW = _winW = Mathf.Max(MinW, _cfgW.Value);
            _reqH = _winH = Mathf.Max(MinH, _cfgH.Value);

            RefreshScenes();
            Logger.LogInfo("Scene Loader ready.");
        }

        private void Defer(Action a) { _deferred.Add(a); }

        private void RefreshScenes()
        {
            _scenes.Clear();

            // 1) Scan Build Settings (.unity scenes)
            try
            {
                int count = SceneManager.sceneCountInBuildSettings;
                for (int i = 0; i < count; i++)
                {
                    string path = SceneUtility.GetScenePathByBuildIndex(i);
                    string name = System.IO.Path.GetFileNameWithoutExtension(path);
                    _scenes.Add(new SceneEntry
                    {
                        Source = Source.Build, Index = i, Path = path, Name = name
                    });
                }
            }
            catch (Exception ex) { Logger.LogWarning("Build scan failed: " + ex.Message); }

            // 2) Scan already loaded Asset Bundles for scenes
            try
            {
                foreach (AssetBundle b in AssetBundle.GetAllLoadedAssetBundles())
                {
                    if (b == null || !b.isStreamedSceneAssetBundle) continue;
                    foreach (string p in b.GetAllScenePaths())
                    {
                        string name = System.IO.Path.GetFileNameWithoutExtension(p);
                        if (!_scenes.Exists(s => s.Path == p))
                        {
                            _scenes.Add(new SceneEntry
                            {
                                Source = Source.Bundle, Path = p, Name = name
                            });
                        }
                    }
                }
            }
            catch (Exception ex) { Logger.LogWarning("Loaded bundle scan failed: " + ex.Message); }

            // 3) Scan .bundle files from the BepInEx plugins folder on disk
            try
            {
                string pluginDir = Paths.PluginPath;
                if (System.IO.Directory.Exists(pluginDir))
                {
                    foreach (string file in System.IO.Directory.GetFiles(pluginDir, "*.bundle", System.IO.SearchOption.AllDirectories))
                    {
                        try
                        {
                            AssetBundle b = AssetBundle.LoadFromFile(file);
                            if (b != null && b.isStreamedSceneAssetBundle)
                            {
                                foreach (string p in b.GetAllScenePaths())
                                {
                                    string name = System.IO.Path.GetFileNameWithoutExtension(p);
                                    if (!_scenes.Exists(s => s.Path == p))
                                    {
                                        _scenes.Add(new SceneEntry
                                        {
                                            Source = Source.Bundle, Path = p, Name = name
                                        });
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { Logger.LogWarning("Disk bundle scan failed: " + ex.Message); }

            if (_scenes.Count > 0 && _selectedEntry == null) _selectedEntry = _scenes[0];
            Logger.LogInfo($"Total scenes loaded: {_scenes.Count}");
        }

        private void LateUpdate()
        {
            if (_show)
            {
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
            }
        }

        private void OnGUI()
        {
            Event e = Event.current;
            if (e == null) return;

            _scale = Mathf.Max(0.75f, Screen.height / 1080f) * _uiScale.Value;

            if (e.type == EventType.Layout) ApplyLayoutState();
            if (e.type == EventType.KeyDown) HandleHotkeys(e);

            if (!_show) return;
            if (!_stylesReady) BuildStyles();

            Matrix4x4 oldMatrix = GUI.matrix;
            Color oldColor = GUI.color;
            GUISkin oldSkin = GUI.skin;

            GUI.skin = _skin;
            GUI.matrix = Matrix4x4.Scale(new Vector3(_scale, _scale, 1f));

            if (!_windowPlaced)
            {
                _window = new Rect((Screen.width / _scale - _winW) / 2f, (Screen.height / _scale - _winH) / 2f, _winW, _winH);
                _windowPlaced = true;
            }

            GUI.color = Color.white;
            GUI.depth = -1000;

            _window = GUILayout.Window(982341, _window, DrawWindow, GUIContent.none, _sPanel,
                GUILayout.Width(_winW), GUILayout.Height(_winH));

            _window.width = _winW;
            _window.height = _winH;

            GUI.skin = oldSkin;
            GUI.color = oldColor;
            GUI.matrix = oldMatrix;
        }

        private void ApplyLayoutState()
        {
            if (_deferred.Count > 0)
            {
                var list = new List<Action>(_deferred);
                _deferred.Clear();
                foreach (Action a in list) a();
            }

            _reqW = Mathf.Clamp(_reqW, MinW, Screen.width / _scale - 20f);
            _reqH = Mathf.Clamp(_reqH, MinH, Screen.height / _scale - 20f);
            _winW = _reqW;
            _winH = _reqH;
            _vFilter = _filter;
            _vPage = _settingsPage;
        }

        private void OpenMenu() { RefreshScenes(); _show = true; _focus = 0; _settingsPage = false; }
        private void CloseMenu() { _show = false; _focus = 0; }

        private void HandleHotkeys(Event e)
        {
            if (_show && e.keyCode == KeyCode.Escape)
            {
                if (_focus != 0) _focus = 0;
                else Defer(CloseMenu);
                e.Use();
                return;
            }

            if (e.keyCode == _toggleKey.Value)
            {
                if (_show) Defer(CloseMenu); else Defer(OpenMenu);
                e.Use();
            }
        }

        private void DrawWindow(int id)
        {
            GUILayout.BeginVertical(_sBanner, GUILayout.Height(55));
            GUILayout.Space(6);
            DrawCenteredLabel("Wobbly Life Scene Browser", _sTitle, _sTitleShadow);
            GUILayout.EndVertical();

            if (_vPage) DrawSettings();
            else DrawScenes();

            DrawResizeGrip();
            GUI.DragWindow(new Rect(0, 0, _winW, 55f));
        }

        private void DrawCenteredLabel(string text, GUIStyle style, GUIStyle shadow = null)
        {
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (shadow != null)
            {
                Rect r = GUILayoutUtility.GetRect(new GUIContent(text), style);
                GUI.Label(new Rect(r.x + 1f, r.y + 2f, r.width, r.height), text, shadow);
                GUI.Label(r, text, style);
            }
            else
            {
                GUILayout.Label(text, style);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private void DrawScenes()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Search", _sLabel, GUILayout.Width(55));
            _filter = TextBox(FieldSearch, _filter, "", 60, GUILayout.Height(32), GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            int shown = 0;

            float listH = Mathf.Max(180f, _winH - 260f);
            GUILayout.BeginVertical(_sInset);
            _scroll = GUILayout.BeginScrollView(_scroll, false, false, GUIStyle.none, _skin.verticalScrollbar, GUIStyle.none, GUILayout.Height(listH));

            foreach (SceneEntry s in _scenes)
            {
                if (!string.IsNullOrEmpty(_vFilter) && s.Name.IndexOf(_vFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                shown++;
                bool isSelected = (_selectedEntry == s);

                Rect r = GUILayoutUtility.GetRect(GUIContent.none, isSelected ? _sRowSelected : _sRow, GUILayout.Height(38), GUILayout.ExpandWidth(true));
                if (GUI.Button(r, GUIContent.none, isSelected ? _sRowSelected : _sRow))
                {
                    _selectedEntry = s;
                }

                string tag = s.Source == Source.Build ? "[Build]" : "[Bundle]";
                GUI.Label(new Rect(r.x + 12, r.y + 7, r.width - 24, 24), $"{s.Name}  {tag}", _sRowText);
            }

            if (shown == 0)
            {
                GUILayout.Label("No scenes match your search.", _sLabel);
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();

            GUILayout.Space(6);
            if (_selectedEntry != null)
            {
                GUILayout.Label(_selectedEntry.Name, _sLabel);
                GUILayout.Label(_selectedEntry.Path, _sSubtitle);
            }
            else
            {
                GUILayout.Label("Select a scene above", _sLabel);
                GUILayout.Label("", _sSubtitle);
            }
            GUILayout.Space(8);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("LOAD SELECTED SCENE", _sOrange, GUILayout.Height(40)))
            {
                if (_selectedEntry != null) LoadEntry(_selectedEntry);
            }
            if (GUILayout.Button("RETURN / UNLOAD", _sOrange, GUILayout.Height(40)))
            {
                Defer(CloseMenu);
            }
            if (GUILayout.Button("CLOSE", _sOrange, GUILayout.Width(110), GUILayout.Height(40)))
            {
                Defer(CloseMenu);
            }
            GUILayout.EndHorizontal();
        }

        private void DrawSettings()
        {
            GUILayout.Space(10);
            GUILayout.BeginVertical(_sInset);
            GUILayout.Label("Settings", _sTitle);
            GUILayout.Space(10);

            if (GUILayout.Button("Back to Scene Browser", _sGreen, GUILayout.Height(40)))
            {
                _settingsPage = false;
            }
            GUILayout.EndVertical();
        }

        private void DrawResizeGrip()
        {
            Event e = Event.current;
            Rect grip = new Rect(_winW - 32f, _winH - 32f, 28f, 28f);
            int cid = GUIUtility.GetControlID(FocusType.Passive);
            switch (e.GetTypeForControl(cid))
            {
                case EventType.MouseDown:
                    if (grip.Contains(e.mousePosition)) { GUIUtility.hotControl = cid; _resizeOffset = new Vector2(_winW - e.mousePosition.x, _winH - e.mousePosition.y); e.Use(); }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == cid) { _reqW = e.mousePosition.x + _resizeOffset.x; _reqH = e.mousePosition.y + _resizeOffset.y; e.Use(); }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == cid) { GUIUtility.hotControl = 0; _cfgW.Value = _winW; _cfgH.Value = _winH; e.Use(); }
                    break;
            }
        }
        private Vector2 _resizeOffset;

        private string TextBox(int id, string value, string hint, int maxLen, params GUILayoutOption[] opts)
        {
            Rect r = GUILayoutUtility.GetRect(GUIContent.none, _sField, opts);
            Event e = Event.current;
            bool focused = _focus == id;

            switch (e.type)
            {
                case EventType.MouseDown:
                    if (r.Contains(e.mousePosition)) { _focus = id; focused = true; e.Use(); }
                    else if (_focus == id) { _focus = 0; focused = false; }
                    break;
                case EventType.KeyDown:
                    if (focused) value = HandleTyping(id, value, maxLen);
                    break;
                case EventType.Repaint:
                    DrawTextBox(r, value, hint, focused);
                    break;
            }
            return value;
        }

        private string HandleTyping(int id, string value, int maxLen)
        {
            Event e = Event.current;
            if (e.keyCode == KeyCode.Backspace && value.Length > 0) { value = value.Substring(0, value.Length - 1); e.Use(); }
            else if (e.keyCode == KeyCode.Return) { _focus = 0; e.Use(); }
            else if (!e.control && e.character >= ' ' && value.Length < maxLen) { value += e.character; e.Use(); }
            return value;
        }

        private void DrawTextBox(Rect r, string value, string hint, bool focused)
        {
            bool showHint = value.Length == 0 && !focused;
            GUIStyle st = showHint ? _sHint : _sField;
            string shown = showHint ? hint : value;
            st.Draw(r, new GUIContent(shown), false, false, false, focused);
        }

        private void LoadEntry(SceneEntry s)
        {
            try
            {
                if (s.Source == Source.Build)
                {
                    SceneManager.LoadScene(s.Index, LoadSceneMode.Single);
                }
                else
                {
                    // Bundle scenes load directly via their asset path
                    SceneManager.LoadScene(s.Path, LoadSceneMode.Single);
                }
                if (_closeAfter.Value) Defer(CloseMenu);
            }
            catch (Exception ex) { Logger.LogError("Load failed: " + ex); }
        }

        private static Color Rgb(int r, int g, int b) { return new Color(r / 255f, g / 255f, b / 255f, 1f); }

        private static Texture2D Rounded(Color fill, Color outline, int outlineW = 2, int size = 32)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool isBorder = (x < outlineW || x >= size - outlineW || y < outlineW || y >= size - outlineW);
                    px[y * size + x] = isBorder ? outline : fill;
                }
            }
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }

        private void BuildStyles()
        {
            Color white = Color.white;
            Color ink = Rgb(66, 46, 30);
            Color outline = Rgb(48, 34, 24);

            _skin = UnityEngine.Object.Instantiate(GUI.skin);
            _skin.hideFlags = HideFlags.HideAndDontSave;

            _sPanel = new GUIStyle(GUI.skin.box);
            SetState(_sPanel.normal, Rounded(Rgb(244, 185, 66), outline, 3), ink);
            _sPanel.padding = new RectOffset(16, 16, 16, 16);

            _sBanner = new GUIStyle(GUI.skin.box);
            SetState(_sBanner.normal, Rounded(Rgb(244, 178, 30), outline, 3), ink);

            _sInset = new GUIStyle(GUI.skin.box);
            SetState(_sInset.normal, Rounded(Rgb(230, 165, 45), outline, 2), ink);
            _sInset.padding = new RectOffset(6, 6, 6, 6);

            _sTitle = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _sTitle.normal.textColor = white;
            _sTitleShadow = new GUIStyle(_sTitle);
            _sTitleShadow.normal.textColor = outline;

            _sSubtitle = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
            _sSubtitle.normal.textColor = outline;

            _sLabel = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
            _sLabel.normal.textColor = ink;

            _sField = new GUIStyle(GUI.skin.textField);
            SetState(_sField.normal, Rounded(Rgb(70, 50, 35), outline, 2), white);
            _sField.padding = new RectOffset(8, 8, 4, 4);

            _sHint = new GUIStyle(_sField);
            _sHint.normal.textColor = Color.gray;

            _sRow = new GUIStyle(GUI.skin.button);
            SetState(_sRow.normal, Rounded(Rgb(245, 215, 120), outline, 2), ink);
            _sRow.alignment = TextAnchor.MiddleLeft;

            _sRowSelected = new GUIStyle(GUI.skin.button);
            SetState(_sRowSelected.normal, Rounded(Rgb(245, 215, 120), Rgb(220, 110, 10), 3), ink);
            _sRowSelected.alignment = TextAnchor.MiddleLeft;

            _sRowText = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
            _sRowText.normal.textColor = ink;

            _sGreen = MakeBtn(Rgb(92, 190, 74), outline, white);
            _sOrange = MakeBtn(Rgb(244, 178, 30), outline, ink);
            _sRed = MakeBtn(Rgb(232, 84, 70), outline, white);

            _stylesReady = true;
        }

        private static void SetState(GUIStyleState st, Texture2D bg, Color text)
        {
            st.background = bg;
            st.textColor = text;
        }

        private static GUIStyle MakeBtn(Color fill, Color outline, Color text)
        {
            var s = new GUIStyle(GUI.skin.button);
            SetState(s.normal, Rounded(fill, outline, 2), text);
            SetState(s.hover, Rounded(Color.Lerp(fill, Color.white, 0.2f), outline, 2), text);
            SetState(s.active, Rounded(Color.Lerp(fill, Color.black, 0.1f), outline, 2), text);
            s.fontStyle = FontStyle.Bold;
            s.fontSize = 12;
            return s;
        }
    }
}