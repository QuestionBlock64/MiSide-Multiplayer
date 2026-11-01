using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer
{
    /// <summary>
    /// Temporary, observation-only trace of MiSide's native scene pipeline.
    /// Does not start, stop, or modify any transition.
    ///
    /// v4 changes:
    ///  - FieldProbe now falls back to AccessTools.Property when the field
    ///    lookup misses. Some SceneLoading members are properties in this
    ///    build (asyncLoad in particular), and the old field-only probe was
    ///    silently discarding them.
    ///  - Removed dead patches: Scene_Load.StartLoad, Scene_Load.GoScene,
    ///    GameController.StartSceneLoadAsync, GameController.SceneLoadAsyncReady,
    ///    GameController.FinishSceneLoadAsync. None of them fired in the v3
    ///    run, so leaving them attached only produces misleading
    ///    "patched X" startup lines.
    ///  - Added one-shot reflection dumps of SceneLoading and GameController
    ///    at Initialize, at Info level, so we can see the real member surface
    ///    without guessing method names.
    /// </summary>
    internal static class SceneLoadTrace
    {
        private const string Version = "v4";
        private const int ProofOfLifeLimit = 4;

        private sealed class ScheduledProbe
        {
            public string SceneName;
            public string Reason;
            public float DueAt;
        }

        private sealed class SceneLoadingState
        {
            public int ProgressBucket;
            public bool? LoadReady;
            public bool? Go;
            public bool? ActivationIssued;
        }

        /// <summary>
        /// One-shot member resolver. Tries a field, then a property, then
        /// gives up and marks the (Type, name) pair dead for the session.
        /// Once dead, no further reflection is attempted for that key.
        /// </summary>
        private static class FieldProbe
        {
            private sealed class Member
            {
                public FieldInfo Field;
                public PropertyInfo Property;

                public bool TryRead(object instance, out object value)
                {
                    value = null;
                    try
                    {
                        if (Field != null) { value = Field.GetValue(instance); return true; }
                        if (Property != null && Property.CanRead)
                        {
                            value = Property.GetValue(instance);
                            return true;
                        }
                    }
                    catch { }
                    return false;
                }
            }

            private static readonly Dictionary<Type, Dictionary<string, Member>> Cache =
                new Dictionary<Type, Dictionary<string, Member>>();
            private static readonly HashSet<string> KnownDead = new HashSet<string>();

            public static void Preseed(Type type, params string[] names)
            {
                if (type == null || names == null) return;
                for (int i = 0; i < names.Length; i++)
                    Resolve(type, names[i]);
            }

            public static bool TryGet<T>(object instance, string name, out T value)
            {
                value = default(T);
                if (instance == null) return false;

                Type type = instance.GetType();
                Member member = Resolve(type, name);
                if (member == null) return false;

                if (!member.TryRead(instance, out object raw)) return false;
                if (raw is T typed) { value = typed; return true; }
                return false;
            }

            private static Member Resolve(Type type, string name)
            {
                string key = type.FullName + "." + name;
                if (KnownDead.Contains(key)) return null;

                if (Cache.TryGetValue(type, out Dictionary<string, Member> perType))
                {
                    if (perType.TryGetValue(name, out Member cached)) return cached;
                }
                else
                {
                    perType = new Dictionary<string, Member>();
                    Cache[type] = perType;
                }

                Member member = new Member();

                try { member.Field = AccessTools.Field(type, name); }
                catch { member.Field = null; }

                if (member.Field == null)
                {
                    try { member.Property = AccessTools.Property(type, name); }
                    catch { member.Property = null; }
                }

                if (member.Field == null && member.Property == null)
                {
                    perType[name] = null;
                    KnownDead.Add(key);
                    DiagnosticLog.Info("[SCENE-TRACE] member " + key +
                                       " not in IL2CPP metadata as field or property; suppressed.");
                    return null;
                }

                perType[name] = member;
                DiagnosticLog.Info("[SCENE-TRACE] member " + key + " resolved as " +
                                   (member.Field != null ? "field" : "property") + ".");
                return member;
            }
        }

        private static readonly Dictionary<int, SceneLoadingState> SceneLoadingStates =
            new Dictionary<int, SceneLoadingState>();
        private static readonly List<ScheduledProbe> PendingProbes =
            new List<ScheduledProbe>();

        private static bool _initialized;
        private static UnityAction<Scene, LoadSceneMode> _sceneLoadedHandler;
        private static UnityAction<Scene> _sceneUnloadedHandler;
        private static UnityAction<Scene, Scene> _activeSceneChangedHandler;

        private static int _sceneLoadingStartHits;
        private static int _sceneLoadingUpdateHits;
        private static int _blackScreenHits;

        private static readonly float[] ProbeDelays = { 0f, 0.10f, 0.50f, 1.50f, 5f };

        public static void Initialize(Harmony harmony)
        {
            if (_initialized) return;
            _initialized = true;

            DiagnosticLog.Info("SceneLoadTrace " + Version + ": initializing.");

            DumpType(typeof(SceneLoading), "SceneLoading");
            DumpType(typeof(GameController), "GameController");
            DumpType(typeof(Scene_Load), "Scene_Load");
            DumpType(typeof(BlackScreen), "BlackScreen");

            FieldProbe.Preseed(typeof(SceneLoading),
                "asyncLoad", "levelLoadName", "loadReady", "go", "sceneAcyncGo");

            SubscribeSceneEvents();

            // BlackScreen.NextLevel fires when the game is asked to load a
            // named scene. This is the only real entry-point signal we have
            // so far.
            PatchLifecycle(harmony, typeof(BlackScreen), "NextLevel", null,
                nameof(BlackScreenNextLevelPrefix));

            // SceneLoading is the interstitial scene's driver. Start and
            // Update both fire while 'SceneLoading' is the active scene.
            PatchLifecycle(harmony, typeof(SceneLoading), "Start",
                nameof(SceneLoadingStartPostfix), null);
            PatchLifecycle(harmony, typeof(SceneLoading), "Update", null,
                nameof(SceneLoadingUpdatePostfix));

            DiagnosticLog.Info("SceneLoadTrace " + Version + ": initialization complete. " +
                               "If you do not see this line, the DLL is stale.");
        }

        public static void Tick()
        {
            for (int i = PendingProbes.Count - 1; i >= 0; i--)
            {
                ScheduledProbe probe = PendingProbes[i];
                if (Time.unscaledTime < probe.DueAt) continue;
                PendingProbes.RemoveAt(i);

                string active = SafeActiveSceneName();
                if (string.Equals(active, probe.SceneName, StringComparison.Ordinal))
                    LogSceneSnapshot(probe.Reason);
                else
                    DiagnosticLog.Verbose("[SCENE-TRACE] probe '" + probe.Reason +
                                          "' expected active='" + probe.SceneName +
                                          "' but active='" + active + "'.");
            }
        }

        // ---------------------------------------------------------------
        // Reflection dump
        // ---------------------------------------------------------------

        private static void DumpType(Type type, string label)
        {
            if (type == null)
            {
                DiagnosticLog.Info("[SCENE-TRACE] DUMP " + label + ": <type null>.");
                return;
            }

            try
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.Static |
                                     BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.DeclaredOnly;

                MethodInfo[] methods = type.GetMethods(flags);
                FieldInfo[] fields = type.GetFields(flags);
                PropertyInfo[] properties = type.GetProperties(flags);

                DiagnosticLog.Info("[SCENE-TRACE] DUMP " + label + " (" + type.FullName +
                                   "): methods=" + methods.Length +
                                   " fields=" + fields.Length +
                                   " properties=" + properties.Length + ".");

                if (fields.Length > 0)
                {
                    for (int i = 0; i < fields.Length; i++)
                        DiagnosticLog.Info("[SCENE-TRACE]   field " + fields[i].FieldType.Name +
                                           " " + fields[i].Name + ".");
                }
                else
                {
                    DiagnosticLog.Info("[SCENE-TRACE]   (no declared fields)");
                }

                if (properties.Length > 0)
                {
                    for (int i = 0; i < properties.Length; i++)
                        DiagnosticLog.Info("[SCENE-TRACE]   property " + properties[i].PropertyType.Name +
                                           " " + properties[i].Name + ".");
                }
                else
                {
                    DiagnosticLog.Info("[SCENE-TRACE]   (no declared properties)");
                }

                if (methods.Length > 0)
                {
                    for (int i = 0; i < methods.Length; i++)
                    {
                        MethodInfo m = methods[i];
                        string pnames = string.Empty;
                        try
                        {
                            ParameterInfo[] ps = m.GetParameters();
                            if (ps != null && ps.Length > 0)
                            {
                                string[] parts = new string[ps.Length];
                                for (int p = 0; p < ps.Length; p++)
                                    parts[p] = ps[p].ParameterType.Name + " " + ps[p].Name;
                                pnames = string.Join(", ", parts);
                            }
                        }
                        catch { pnames = "<?>"; }

                        DiagnosticLog.Info("[SCENE-TRACE]   method " + m.ReturnType.Name + " " +
                                           m.Name + "(" + pnames + ").");
                    }
                }
                else
                {
                    DiagnosticLog.Info("[SCENE-TRACE]   (no declared methods)");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("SceneLoadTrace: DUMP " + label + " failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ---------------------------------------------------------------
        // Event subscriptions and lifecycle patches
        // ---------------------------------------------------------------

        private static void SubscribeSceneEvents()
        {
            try
            {
                _sceneLoadedHandler = DelegateSupport.ConvertDelegate<UnityAction<Scene, LoadSceneMode>>(
                    OnSceneLoaded);
                _sceneUnloadedHandler = DelegateSupport.ConvertDelegate<UnityAction<Scene>>(
                    OnSceneUnloaded);
                _activeSceneChangedHandler = DelegateSupport.ConvertDelegate<UnityAction<Scene, Scene>>(
                    OnActiveSceneChanged);

                SceneManager.sceneLoaded += _sceneLoadedHandler;
                SceneManager.sceneUnloaded += _sceneUnloadedHandler;
                SceneManager.activeSceneChanged += _activeSceneChangedHandler;

                DiagnosticLog.Info("SceneLoadTrace " + Version + ": SceneManager events subscribed.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("SceneLoadTrace: could not subscribe scene events: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void PatchLifecycle(Harmony harmony, Type type, string originalName,
                                           string prefixName, string postfixName)
        {
            if (harmony == null || type == null) return;

            try
            {
                MethodInfo original = type.GetMethod(originalName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (original == null)
                {
                    DiagnosticLog.Info("SceneLoadTrace " + Version + ": " + type.Name + "." +
                                       originalName + "() not found; patch skipped.");
                    return;
                }

                HarmonyMethod prefix = string.IsNullOrEmpty(prefixName) ? null :
                    new HarmonyMethod(typeof(SceneLoadTrace).GetMethod(prefixName,
                        BindingFlags.Static | BindingFlags.NonPublic));
                HarmonyMethod postfix = string.IsNullOrEmpty(postfixName) ? null :
                    new HarmonyMethod(typeof(SceneLoadTrace).GetMethod(postfixName,
                        BindingFlags.Static | BindingFlags.NonPublic));

                harmony.Patch(original, prefix, postfix);
                DiagnosticLog.Info("SceneLoadTrace " + Version + ": patched " + type.Name + "." +
                                   originalName + "() (prefix=" + (prefix != null) +
                                   ", postfix=" + (postfix != null) + ").");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("SceneLoadTrace: patch " + type.Name + "." + originalName +
                                      " failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ---------------------------------------------------------------
        // Patch bodies
        // ---------------------------------------------------------------

        private static void BlackScreenNextLevelPrefix(string level)
        {
            if (_blackScreenHits < ProofOfLifeLimit)
            {
                _blackScreenHits++;
                DiagnosticLog.Info("[SCENE-TRACE] PROOF BlackScreen.NextLevel target='" + level +
                                   "' active='" + SafeActiveSceneName() + "' (hit " +
                                   _blackScreenHits + "/" + ProofOfLifeLimit + ").");
            }
            DiagnosticLog.Verbose("[SCENE-TRACE] BlackScreen.NextLevel target='" + level +
                                  "' active='" + SafeActiveSceneName() + "'.");
        }

        private static void SceneLoadingStartPostfix(SceneLoading __instance)
        {
            if (_sceneLoadingStartHits < ProofOfLifeLimit)
            {
                _sceneLoadingStartHits++;
                DiagnosticLog.Info("[SCENE-TRACE] PROOF SceneLoading.Start active='" +
                                   SafeActiveSceneName() + "' (hit " + _sceneLoadingStartHits +
                                   "/" + ProofOfLifeLimit + ").");
            }
            DiagnosticLog.Verbose("[SCENE-TRACE] SceneLoading.Start active='" +
                                  SafeActiveSceneName() + "' GlobalGame.LoadingLevel='" +
                                  SafeLoadingLevel() + "'.");
            ObserveSceneLoading(__instance, "Start");
        }

        private static void SceneLoadingUpdatePostfix(SceneLoading __instance)
        {
            if (_sceneLoadingUpdateHits < ProofOfLifeLimit)
            {
                _sceneLoadingUpdateHits++;
                DiagnosticLog.Info("[SCENE-TRACE] PROOF SceneLoading.Update active='" +
                                   SafeActiveSceneName() + "' (hit " + _sceneLoadingUpdateHits +
                                   "/" + ProofOfLifeLimit + ").");
            }
            ObserveSceneLoading(__instance, "Update");
        }

        // ---------------------------------------------------------------
        // SceneManager event handlers
        // ---------------------------------------------------------------

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            int loaders = 0;
            int controllers = 0;
            try { loaders = UnityEngine.Object.FindObjectsOfType<SceneLoading>().Length; } catch { }
            try { controllers = UnityEngine.Object.FindObjectsOfType<GameController>().Length; } catch { }

            DiagnosticLog.Info("[SCENE-TRACE] sceneLoaded '" + scene.name + "' mode=" + mode +
                               " active='" + SafeActiveSceneName() +
                               "' SceneLoading=" + loaders + " GameController=" + controllers + ".");

            ScheduleSceneProbes(scene.name, "sceneLoaded");
        }

        private static void OnSceneUnloaded(Scene scene)
        {
            DiagnosticLog.Info("[SCENE-TRACE] sceneUnloaded '" + scene.name + "' active='" +
                               SafeActiveSceneName() + "'.");
        }

        private static void OnActiveSceneChanged(Scene previous, Scene next)
        {
            DiagnosticLog.Info("[SCENE-TRACE] activeSceneChanged '" + previous.name +
                               "' -> '" + next.name + "'.");

            int loaders = 0;
            try { loaders = UnityEngine.Object.FindObjectsOfType<SceneLoading>().Length; } catch { }
            DiagnosticLog.Info("[SCENE-TRACE]   SceneLoading components present in new scene: " +
                               loaders + ".");

            ScheduleSceneProbes(next.name, "activeSceneChanged");
        }

        private static void ScheduleSceneProbes(string sceneName, string reason)
        {
            if (string.IsNullOrEmpty(sceneName)) return;
            for (int i = 0; i < ProbeDelays.Length; i++)
            {
                PendingProbes.Add(new ScheduledProbe
                {
                    SceneName = sceneName,
                    Reason = reason + "+" + ProbeDelays[i].ToString("0.00") + "s",
                    DueAt = Time.unscaledTime + ProbeDelays[i]
                });
            }
        }

        // ---------------------------------------------------------------
        // SceneLoading observation
        // ---------------------------------------------------------------

        private static void ObserveSceneLoading(SceneLoading instance, string reason)
        {
            if (instance == null) return;

            int id = instance.GetInstanceID();
            if (!SceneLoadingStates.TryGetValue(id, out SceneLoadingState state))
            {
                state = new SceneLoadingState();
                SceneLoadingStates[id] = state;
            }

            AsyncOperation op = null;
            FieldProbe.TryGet(instance, "asyncLoad", out op);

            int bucket = ProgressBucket(op);
            if (bucket != state.ProgressBucket)
            {
                state.ProgressBucket = bucket;
                string target = null;
                FieldProbe.TryGet(instance, "levelLoadName", out target);
                DiagnosticLog.Verbose("[SCENE-TRACE] SceneLoading." + reason +
                                      " async=" + DescribeProgress(op) +
                                      " target='" + (target ?? "<unreadable>") + "'.");
            }

            ObserveBool(instance, "loadReady", ref state.LoadReady);
            ObserveBool(instance, "go", ref state.Go);
            ObserveBool(instance, "sceneAcyncGo", ref state.ActivationIssued);
        }

        private static void ObserveBool(SceneLoading instance, string name, ref bool? last)
        {
            if (!FieldProbe.TryGet(instance, name, out bool value)) return;
            if (last.HasValue && last.Value == value) return;
            last = value;

            AsyncOperation op = null;
            FieldProbe.TryGet(instance, "asyncLoad", out op);
            DiagnosticLog.Verbose("[SCENE-TRACE] SceneLoading." + name + "=" + value +
                                  " progress=" + DescribeProgress(op) + ".");
        }

        private static int ProgressBucket(AsyncOperation operation)
        {
            if (operation == null) return 0;
            float progress = operation.progress;
            if (progress >= 0.9f) return 4;
            if (progress >= 0.5f) return 3;
            if (progress >= 0.1f) return 2;
            return 1;
        }

        // ---------------------------------------------------------------
        // Snapshot helpers
        // ---------------------------------------------------------------

        private static void LogSceneSnapshot(string reason)
        {
            try
            {
                Scene scene = SceneManager.GetActiveScene();
                GameObject[] roots = scene.IsValid() && scene.isLoaded
                    ? scene.GetRootGameObjects() : new GameObject[0];
                int sceneLoaders = UnityEngine.Object.FindObjectsOfType<SceneLoading>().Length;
                int controllers = UnityEngine.Object.FindObjectsOfType<GameController>().Length;
                int cameras = UnityEngine.Object.FindObjectsOfType<Camera>().Length;

                DiagnosticLog.Verbose("[SCENE-TRACE] snapshot " + reason +
                                      " scene='" + scene.name + "' loaded=" + scene.isLoaded +
                                      " roots=" + roots.Length +
                                      " GlobalGame.play=" + GlobalGame.play +
                                      " GlobalGame.playWorld=" + GlobalGame.playWorld +
                                      " objects{SceneLoading=" + sceneLoaders +
                                      ",GameController=" + controllers +
                                      ",Camera=" + cameras + "}.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("SceneLoadTrace: snapshot failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static string SafeLoadingLevel()
        {
            try { return GlobalGame.LoadingLevel ?? "<null>"; }
            catch { return "<unavailable>"; }
        }

        private static string SafeActiveSceneName()
        {
            try { return SceneManager.GetActiveScene().name ?? "<unnamed>"; }
            catch { return "<unavailable>"; }
        }

        private static string DescribeProgress(AsyncOperation operation)
        {
            return operation == null ? "<none>" : operation.progress.ToString("0.000") +
                " done=" + operation.isDone + " allowActivation=" + operation.allowSceneActivation;
        }
    }
}