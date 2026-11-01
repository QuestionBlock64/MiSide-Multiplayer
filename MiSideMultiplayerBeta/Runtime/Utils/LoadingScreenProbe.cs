using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace MiSideMultiplayer.Runtime.Loading
{
    /// <summary>
    /// Throwaway diagnostic. On the first SceneLoading.Start during a
    /// multiplayer load it dumps the entire loading-screen hierarchy and
    /// every component on every node, then watches every observable value
    /// on those components each frame and logs the first change of each.
    ///
    /// Once we know which property actually fades and who moves it, this
    /// file gets deleted.
    /// </summary>
    internal static class LoadingScreenProbe
    {
        private static bool _initialized;
        private static bool _dumpedThisInstance;
        private static int _lastDumpedInstanceId;

        private sealed class Watched
        {
            public Component Component;
            public string Path;
            public string Kind;
            public object LastValue;
            public bool Reported;
        }

        private static readonly List<Watched> _watched = new List<Watched>();
        private static readonly HashSet<string> _seenComponents = new HashSet<string>();

        private static int _sceneLoadingUpdateHits;
        private const int SceneLoadingUpdateLogLimit = 90;   // ~1.5s @ 60fps

        public static void Initialize(Harmony harmony)
        {
            if (_initialized) return;
            _initialized = true;

            if (harmony == null) return;

            try
            {
                MethodInfo original = typeof(SceneLoading).GetMethod(
                    "Start",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (original != null)
                {
                    HarmonyMethod postfix = new HarmonyMethod(typeof(LoadingScreenProbe).GetMethod(
                        nameof(StartPostfix), BindingFlags.Static | BindingFlags.NonPublic));
                    harmony.Patch(original, null, postfix);
                    DiagnosticLog.Info("[LS-PROBE] patched SceneLoading.Start (postfix).");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("[LS-PROBE] Start patch failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                MethodInfo original = typeof(SceneLoading).GetMethod(
                    "Update",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (original != null)
                {
                    HarmonyMethod postfix = new HarmonyMethod(typeof(LoadingScreenProbe).GetMethod(
                        nameof(UpdatePostfix), BindingFlags.Static | BindingFlags.NonPublic));
                    harmony.Patch(original, null, postfix);
                    DiagnosticLog.Info("[LS-PROBE] patched SceneLoading.Update (postfix).");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("[LS-PROBE] Update patch failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ── Start postfix: dump + register watches ──────────────────────

        private static void StartPostfix(SceneLoading __instance)
        {
            if (__instance == null) return;

            var state = MultiplayerLoadingScene.State;
            if (state == MultiplayerLoadingScene.LoadState.Idle) return;

            int id = 0;
            try { id = __instance.GetInstanceID(); } catch { }
            if (id == _lastDumpedInstanceId) return;
            _lastDumpedInstanceId = id;

            _watched.Clear();
            _seenComponents.Clear();
            _dumpedThisInstance = true;
            _sceneLoadingUpdateHits = 0;

            DiagnosticLog.Info("[LS-PROBE] ===== start dump (instance " + id + ", state=" + state + ") =====");

            try
            {
                Transform root = __instance.transform.root;
                if (root == null)
                {
                    DiagnosticLog.Warning("[LS-PROBE] root transform is null");
                    return;
                }

                DiagnosticLog.Info("[LS-PROBE] root='" + root.name +
                                   "' scene='" + SafeSceneName(root.gameObject) + "'");

                DumpRecursive(root, 0);

                DiagnosticLog.Info("[LS-PROBE] ===== dump complete: " + _watched.Count +
                                   " component(s) being watched =====");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("[LS-PROBE] dump threw: " + ex.ToString());
            }
        }

        private static void DumpRecursive(Transform t, int depth)
        {
            if (t == null || depth > 12) return;

            GameObject go = t.gameObject;
            string path = SafePath(go);
            string indent = new string(' ', Math.Min(depth * 2, 24));

            DiagnosticLog.Info("[LS-PROBE] " + indent + "- '" + go.name +
                               "' activeSelf=" + go.activeSelf +
                               " activeInHierarchy=" + go.activeInHierarchy +
                               " scene='" + SafeSceneName(go) + "'");

            Component[] comps = null;
            try { comps = go.GetComponents<Component>(); } catch { }
            if (comps != null)
            {
                foreach (var c in comps)
                {
                    if (c == null) { DiagnosticLog.Info("[LS-PROBE] " + indent + "    <null component>"); continue; }

                    string typeName = SafeTypeName(c);
                    DiagnosticLog.Info("[LS-PROBE] " + indent + "    [" + typeName + "] " +
                                       DescribeComponent(c));

                    RegisterWatch(c, path, typeName);
                }
            }

            for (int i = 0; i < t.childCount; i++)
            {
                DumpRecursive(t.GetChild(i), depth + 1);
            }
        }

        // ── Register things worth watching ──────────────────────────────

        private static void RegisterWatch(Component c, string path, string typeName)
        {
            string key = path + "/" + typeName;
            if (_seenComponents.Contains(key)) return;
            _seenComponents.Add(key);

            try
            {
                var cg = c.TryCast<CanvasGroup>();
                if (cg != null)
                {
                    _watched.Add(new Watched { Component = c, Path = path, Kind = "CanvasGroup.alpha", LastValue = cg.alpha });
                    _watched.Add(new Watched { Component = c, Path = path, Kind = "CanvasGroup.interactable", LastValue = cg.interactable });
                    _watched.Add(new Watched { Component = c, Path = path, Kind = "CanvasGroup.blocksRaycasts", LastValue = cg.blocksRaycasts });
                    return;
                }

                var img = c.TryCast<Image>();
                if (img != null)
                {
                    _watched.Add(new Watched { Component = c, Path = path, Kind = "Image.color", LastValue = img.color });
                    _watched.Add(new Watched { Component = c, Path = path, Kind = "Image.enabled", LastValue = img.enabled });
                    _watched.Add(new Watched { Component = c, Path = path, Kind = "Image.fillAmount", LastValue = img.fillAmount });
                    return;
                }

                var canvas = c.TryCast<Canvas>();
                if (canvas != null)
                {
                    _watched.Add(new Watched { Component = c, Path = path, Kind = "Canvas.enabled", LastValue = canvas.enabled });
                    _watched.Add(new Watched { Component = c, Path = path, Kind = "Canvas.sortingOrder", LastValue = canvas.sortingOrder });
                    return;
                }

                var anim = c.TryCast<Animator>();
                if (anim != null)
                {
                    _watched.Add(new Watched { Component = c, Path = path, Kind = "Animator.speed", LastValue = anim.speed });
                    return;
                }

                var rt = c.TryCast<RectTransform>();
                if (rt != null)
                {
                    _watched.Add(new Watched { Component = c, Path = path, Kind = "RectTransform.localScale", LastValue = rt.localScale });
                    return;
                }

                // UIGradient lives in the UIEffect assembly. We don't have a
                // compile-time reference, so we detect it by type name and
                // watch its public fields via reflection.
                if (typeName == "UIGradient")
                {
                    foreach (var field in c.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
                    {
                        // Only watch fields whose type is trivially comparable.
                        string ft = field.FieldType.Name;
                        if (ft == "Color" || ft == "Color32" || ft == "Single" || ft == "Boolean" || ft == "Int32")
                        {
                            object v = null;
                            try { v = field.GetValue(c); } catch { }
                            _watched.Add(new Watched { Component = c, Path = path, Kind = "UIGradient." + field.Name, LastValue = v });
                        }
                    }
                    foreach (var prop in c.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
                    {
                        if (!prop.CanRead) continue;
                        string pt = prop.PropertyType.Name;
                        if (pt == "Color" || pt == "Color32" || pt == "Single" || pt == "Boolean" || pt == "Int32")
                        {
                            object v = null;
                            try { v = prop.GetValue(c); } catch { }
                            _watched.Add(new Watched { Component = c, Path = path, Kind = "UIGradient." + prop.Name, LastValue = v });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("[LS-PROBE] register watch for " + path + "/" + typeName +
                                      " threw: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ── Per-frame watch ─────────────────────────────────────────────

        public static void Tick()
        {
            if (!_dumpedThisInstance) return;
            if (_watched.Count == 0) return;

            for (int i = 0; i < _watched.Count; i++)
            {
                Watched w = _watched[i];
                if (w.Reported) continue;
                if (w.Component == null) continue;

                object current = ReadValue(w);
                if (current == null) continue;

                if (!Equals(w.LastValue, current))
                {
                    w.Reported = true;
                    DiagnosticLog.Info("[LS-PROBE] CHANGE " + w.Kind + " on '" + w.Path +
                                       "': " + FormatValue(w.LastValue) + " -> " + FormatValue(current) +
                                       " at t=" + Time.unscaledTime.ToString("0.000"));
                    w.LastValue = current;
                }
            }
        }

        private static object ReadValue(Watched w)
        {
            try
            {
                if (w.Kind == "CanvasGroup.alpha") return w.Component.TryCast<CanvasGroup>()?.alpha;
                if (w.Kind == "CanvasGroup.interactable") return w.Component.TryCast<CanvasGroup>()?.interactable;
                if (w.Kind == "CanvasGroup.blocksRaycasts") return w.Component.TryCast<CanvasGroup>()?.blocksRaycasts;

                if (w.Kind == "Image.color") return w.Component.TryCast<Image>()?.color;
                if (w.Kind == "Image.enabled") return w.Component.TryCast<Image>()?.enabled;
                if (w.Kind == "Image.fillAmount") return w.Component.TryCast<Image>()?.fillAmount;

                if (w.Kind == "Canvas.enabled") return w.Component.TryCast<Canvas>()?.enabled;
                if (w.Kind == "Canvas.sortingOrder") return w.Component.TryCast<Canvas>()?.sortingOrder;

                if (w.Kind == "Animator.speed") return w.Component.TryCast<Animator>()?.speed;

                if (w.Kind == "RectTransform.localScale") return w.Component.TryCast<RectTransform>()?.localScale;

                if (w.Kind.StartsWith("UIGradient."))
                {
                    string member = w.Kind.Substring("UIGradient.".Length);
                    var t = w.Component.GetType();
                    var f = t.GetField(member, BindingFlags.Instance | BindingFlags.Public);
                    if (f != null) return f.GetValue(w.Component);
                    var p = t.GetProperty(member, BindingFlags.Instance | BindingFlags.Public);
                    if (p != null && p.CanRead) return p.GetValue(w.Component);
                }
            }
            catch { }
            return null;
        }

        // ── SceneLoading.Update postfix: game's own frame-by-frame state ─

        private static void UpdatePostfix(SceneLoading __instance)
        {
            if (__instance == null) return;
            if (!_dumpedThisInstance) return;
            if (_sceneLoadingUpdateHits >= SceneLoadingUpdateLogLimit) return;
            _sceneLoadingUpdateHits++;

            try
            {
                string target = null;
                try { target = __instance.levelLoadName; } catch { }
                string go = "?"; try { go = __instance.go.ToString(); } catch { }
                string ready = "?"; try { ready = __instance.loadReady.ToString(); } catch { }
                string acync = "?"; try { acync = __instance.sceneAcyncGo.ToString(); } catch { }
                string prog = "<none>";
                try
                {
                    var op = __instance.asyncLoad;
                    if (op != null) prog = op.progress.ToString("0.000");
                }
                catch { prog = "<unreadable>"; }

                DiagnosticLog.Info("[LS-PROBE] SceneLoading.Update frame=" + _sceneLoadingUpdateHits +
                                   " target='" + (target ?? "<null>") + "' go=" + go +
                                   " loadReady=" + ready + " sceneAcyncGo=" + acync +
                                   " asyncProgress=" + prog);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("[LS-PROBE] Update postfix threw: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void Reset()
        {
            _watched.Clear();
            _seenComponents.Clear();
            _dumpedThisInstance = false;
            _sceneLoadingUpdateHits = 0;
        }

        // ── Describe / format helpers ───────────────────────────────────

        private static string DescribeComponent(Component c)
        {
            try
            {
                var cg = c.TryCast<CanvasGroup>();
                if (cg != null) return "alpha=" + cg.alpha + " interactable=" + cg.interactable;

                var img = c.TryCast<Image>();
                if (img != null) return "color=" + img.color + " enabled=" + img.enabled +
                                        " fill=" + img.fillAmount + " sprite=" +
                                        (img.sprite != null ? img.sprite.name : "<null>");

                var canvas = c.TryCast<Canvas>();
                if (canvas != null) return "enabled=" + canvas.enabled + " sortingOrder=" + canvas.sortingOrder +
                                           " overrideSorting=" + canvas.overrideSorting;

                var rt = c.TryCast<RectTransform>();
                if (rt != null) return "scale=" + rt.localScale + " anchoredPos=" + rt.anchoredPosition +
                                        " sizeDelta=" + rt.sizeDelta;

                return "";
            }
            catch { return ""; }
        }

        private static string FormatValue(object v)
        {
            if (v == null) return "<null>";
            return v.ToString();
        }

        private static string SafeTypeName(Component c)
        {
            try { return c.GetType().Name; } catch { return "<unknown>"; }
        }

        private static string SafePath(GameObject go)
        {
            try
            {
                if (go == null) return "<null>";
                string path = go.name;
                Transform t = go.transform.parent;
                int guard = 0;
                while (t != null && guard++ < 12)
                {
                    path = t.name + "/" + path;
                    t = t.parent;
                }
                return path;
            }
            catch { return "<unreadable>"; }
        }

        private static string SafeSceneName(GameObject go)
        {
            try
            {
                if (go == null) return "<null>";
                var s = go.scene;
                return s.IsValid() ? s.name : "<invalid>";
            }
            catch { return "<unreadable>"; }
        }
    }
}