using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace WobblySceneOverlay
{
    // Optional link to (Cinematic) Unity Explorer. Everything is done with reflection, so this mod
    // builds and runs fine when Explorer is not installed; the buttons simply say so.
    public partial class SceneOverlayPlugin
    {
        private Type _exUi;
        private Type _exInspector;
        private float _exNextCheck;

        private bool ExplorerAvailable
        {
            get
            {
                if (_exUi == null && Time.realtimeSinceStartup >= _exNextCheck)
                {
                    _exNextCheck = Time.realtimeSinceStartup + 3f;
                    _exUi = FindType("UnityExplorer.UI.UIManager");
                    _exInspector = FindType("UnityExplorer.InspectorManager");
                }
                return _exUi != null;
            }
        }

        // Shows or hides Explorer's own window.
        private void ToggleExplorer()
        {
            if (!ExplorerAvailable)
            {
                SetStatus("Unity Explorer was not found. It is optional; install it if you want its inspector and Freecam.", 8f);
                return;
            }

            try
            {
                PropertyInfo p = _exUi.GetProperty("ShowMenu", BindingFlags.Public | BindingFlags.Static);
                FieldInfo f = p == null ? _exUi.GetField("ShowMenu", BindingFlags.Public | BindingFlags.Static) : null;
                if (p == null && f == null)
                {
                    SetStatus("Found Explorer, but not its menu switch. Use Explorer's own hotkey.", 8f);
                    return;
                }

                bool now = p != null ? (bool)p.GetValue(null, null) : (bool)f.GetValue(null);
                if (p != null) p.SetValue(null, !now, null); else f.SetValue(null, !now);
                SetStatus(now ? "Explorer hidden." : "Explorer shown.");
            }
            catch (Exception ex)
            {
                Exception inner = ex.InnerException ?? ex;
                Logger.LogWarning("Explorer toggle failed: " + inner.Message);
                SetStatus("Could not toggle Explorer (see the log). Use Explorer's own hotkey.", 8f);
            }
        }

        // Opens the first object of a loaded scene in Explorer's inspector.
        private void InspectInExplorer(Scene sc)
        {
            if (!ExplorerAvailable || _exInspector == null)
            {
                SetStatus("Unity Explorer was not found (it is optional).", 6f);
                return;
            }
            if (!sc.IsValid() || !sc.isLoaded)
            {
                SetStatus("That scene is not loaded.");
                return;
            }

            try
            {
                GameObject[] roots = sc.GetRootGameObjects();
                if (roots.Length == 0) { SetStatus("That scene has no objects to inspect."); return; }

                foreach (MethodInfo m in _exInspector.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "Inspect") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 0 || ps[0].ParameterType != typeof(object)) continue;

                    object[] args = new object[ps.Length];
                    args[0] = roots[0];
                    for (int i = 1; i < args.Length; i++) args[i] = Type.Missing;
                    m.Invoke(null, args);

                    SetStatus("Opened '" + roots[0].name + "' in Explorer.");
                    return;
                }
                SetStatus("This Explorer version has no Inspect method I can call.", 8f);
            }
            catch (Exception ex)
            {
                Exception inner = ex.InnerException ?? ex;
                Logger.LogWarning("Explorer inspect failed: " + inner.Message);
                SetStatus("Could not open that in Explorer (see the log).", 8f);
            }
        }
    }
}
