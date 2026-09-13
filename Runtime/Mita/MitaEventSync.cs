using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace MiSideMultiplayer
{
    public static class MitaEventSync
    {
        private static readonly List<MitaEvent> _pending = new List<MitaEvent>();
        private static readonly object _pendingLock = new object();

        private static bool _initialized;
        private static Harmony _harmony;

        private static Type _mitaPersonType;
        private static string _mitaPersonTypeName;

        public static void Initialize(Harmony harmony)
        {
            if (_initialized) return;
            _initialized = true;
            _harmony = harmony;

            try
            {
                _mitaPersonType     = FindType("MitaPerson");
                _mitaPersonTypeName = _mitaPersonType != null ? _mitaPersonType.Name : null;

                int installed = 0;
                installed += PatchMethod(_mitaPersonType, "FaceEmotion",
                    new[] { typeof(string) }, nameof(FaceEmotionPrefix));
                installed += PatchMethod(_mitaPersonType, "FaceEmotionFast",
                    new[] { typeof(string) }, nameof(FaceEmotionFastPrefix));
                installed += PatchMethod(_mitaPersonType, "FaceEmotionOff",
                    Type.EmptyTypes, nameof(FaceEmotionOffPrefix));
                installed += PatchMethod(_mitaPersonType, "FaceLayer",
                    new[] { typeof(int) }, nameof(FaceLayerPrefix));
                installed += PatchMethod(_mitaPersonType, "FaceLayerFast",
                    new[] { typeof(int) }, nameof(FaceLayerFastPrefix));

                // Dialogue replay is intentionally disabled. Both clients run
                // the same quest scripts, so Dialogue_3DText.Start already
                // fires locally on the receiving side — replaying it produced
                // duplicate, overlapping dialogue in the HUD.

                DiagnosticLog.Info("MitaEventSync: patched " + installed + " event hook(s).");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MitaEventSync: init failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static MitaEvent[] DrainPending(bool _)
        {
            lock (_pendingLock)
            {
                if (_pending.Count == 0) return new MitaEvent[0];
                MitaEvent[] arr = _pending.ToArray();
                _pending.Clear();
                return arr;
            }
        }

        private static void Record(string mitaPath, string kind, string s = null, int i = 0)
        {
            MitaEvent ev = new MitaEvent();
            ev.mitaPath = mitaPath;
            ev.kind = kind;
            ev.stringValue = s;
            ev.intValue = i;
            lock (_pendingLock) _pending.Add(ev);
        }

        private static int PatchMethod(Type type, string methodName, Type[] args, string prefixName)
        {
            if (type == null) return 0;
            MethodInfo target = type.GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, args, null);
            if (target == null)
            {
                DiagnosticLog.Warning("MitaEventSync: " + type.Name + "." + methodName + " NOT FOUND.");
                return 0;
            }

            MethodInfo prefix = typeof(MitaEventSync).GetMethod(
                prefixName, BindingFlags.Static | BindingFlags.NonPublic);
            try
            {
                _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                return 1;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MitaEventSync: failed to patch " + methodName + ": " + ex.Message);
                return 0;
            }
        }

        private static string PathOf(Component c)
        {
            return c == null ? "" : LocalPlayerLocator.GetPath(c.transform);
        }

        private static void FaceEmotionPrefix(Component __instance, string _name)
        { Record(PathOf(__instance), "faceEmotion", _name); }
        private static void FaceEmotionFastPrefix(Component __instance, string _name)
        { Record(PathOf(__instance), "faceEmotionFast", _name); }
        private static void FaceEmotionOffPrefix(Component __instance)
        { Record(PathOf(__instance), "faceEmotionOff"); }
        private static void FaceLayerPrefix(Component __instance, int _index)
        { Record(PathOf(__instance), "faceLayer", null, _index); }
        private static void FaceLayerFastPrefix(Component __instance, int _index)
        { Record(PathOf(__instance), "faceLayerFast", null, _index); }

        public static void ApplyEvent(MitaEvent ev)
        {
            if (ev == null || string.IsNullOrEmpty(ev.kind)) return;

            try
            {
                switch (ev.kind)
                {
                    case "faceEmotion":     InvokeMitaPerson(ev.mitaPath, "FaceEmotion",     new object[] { ev.stringValue ?? "" }); break;
                    case "faceEmotionFast": InvokeMitaPerson(ev.mitaPath, "FaceEmotionFast", new object[] { ev.stringValue ?? "" }); break;
                    case "faceEmotionOff":  InvokeMitaPerson(ev.mitaPath, "FaceEmotionOff",  null); break;
                    case "faceLayer":       InvokeMitaPerson(ev.mitaPath, "FaceLayer",       new object[] { ev.intValue }); break;
                    case "faceLayerFast":   InvokeMitaPerson(ev.mitaPath, "FaceLayerFast",   new object[] { ev.intValue }); break;
                    default: break;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MitaEventSync.ApplyEvent('" + ev.kind + "') failed: " + ex.Message);
            }
        }

        private static void InvokeMitaPerson(string mitaPath, string methodName, object[] args)
        {
            if (string.IsNullOrEmpty(_mitaPersonTypeName)) return;
            GameObject mitaGO = FindByPath(mitaPath);
            if (mitaGO == null) return;
            Component mita = mitaGO.GetComponent(_mitaPersonTypeName);
            if (mita == null) return;

            // Resolve off the proxy's actual type. Using the cached _mitaPersonType
            // triggers "Object does not match target type" under Il2CppInterop.
            MethodInfo m = mita.GetType().GetMethod(methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null) return;
            m.Invoke(mita, args);
        }

        private static GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string[] parts = path.Split('/');
            if (parts.Length == 0) return null;

            UnityEngine.SceneManagement.Scene scene =
                UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded) return null;

            GameObject[] roots = scene.GetRootGameObjects();
            for (int r = 0; r < roots.Length; r++)
            {
                if (roots[r] == null || roots[r].name != parts[0]) continue;
                Transform cur = roots[r].transform;
                for (int i = 1; i < parts.Length && cur != null; i++)
                    cur = cur.Find(parts[i]);
                if (cur != null) return cur.gameObject;
            }
            return null;
        }

        private static Type FindType(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(name, false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }
    }
}