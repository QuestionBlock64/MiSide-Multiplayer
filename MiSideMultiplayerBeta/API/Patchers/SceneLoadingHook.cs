using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer.Runtime.Loading
{
    internal static class SceneLoadingHook
    {
        private static bool _initialized;

        private static int _loadGoHits;
        private static int _stopLoadHits;
        private static int _updateHoldHits;
        private const int LogLimit = 8;

        private static int _lastHiddenInstanceId;

        private static SceneLoading _pendingLoadGoInstance;
        private static bool _allowLoad;
        private static bool _loadGoFired;
        private static MethodInfo _loadGoMethod;

        private static SceneLoading _heldInstance;
        private static AsyncOperation _heldAsyncLoad;

        // Cached so RestoreHeldAsyncLoad can re-enable then immediately
        // re-hide it, killing the flash of the native loading bar that
        // otherwise shows for one frame at the tail end of the transition.
        private static GameObject _cachedObjectInterfaceLoading;
        // SceneLoading itself lives on MenuGame, while the persistent patterned
        // background lives on objectInterfaceLoading (Canvas). Keep both exact
        // objects: depending on Unity's transition order either one can survive.
        private static GameObject _nativeLoadingRoot;
        private static GameObject _nativeLoadingOwner;
        private static string _nativeTeardownScene;
        private static bool _nativeBackgroundCleanupFinished;
        private static bool _nativeBackgroundCleanupLogWritten;
        private static bool _nativeBackgroundRemovalErrorLogged;
        private static float _nextNativeBackgroundRetryTime = -1f;
        private static float _nextNativeBackgroundWatchdogTime;
        private static string _loadFallbackTarget;
        private static float _loadFallbackAt = -1f;
        private static float _nextLoadFallbackRetryTime;
        private static bool _menuReturnCleanupActive;
        private static bool _menuReturnHideLogged;
        private static bool _menuReturnCleanupErrorLogged;
        private static float _menuReturnCleanupUntil;
        private static float _nextMenuReturnCleanupTime;
        private const float NativeBackgroundRetryIntervalSeconds = 0.05f;
        private const float NativeBackgroundWatchdogIntervalSeconds = 0.5f;
        private const float NativeLoadFallbackDelaySeconds = 8f;
        private const float MenuReturnCleanupDurationSeconds = 3f;
        private const float MenuReturnCleanupIntervalSeconds = 0.1f;

        // GO arms cleanup. Remove the native loading UI as soon as its target
        // gameplay scene becomes active, and only attempt this once per load.
        private const string NativeLoadingSceneName = "SceneLoading";

        public static void Initialize(Harmony harmony)
        {
            if (_initialized) return;
            _initialized = true;

            if (harmony == null)
            {
                DiagnosticLog.Warning("SceneLoadingHook: null harmony; not installed.");
                return;
            }

            try
            {
                _loadGoMethod = typeof(SceneLoading).GetMethod(
                    "LoadGo",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("SceneLoadingHook: could not cache LoadGo MethodInfo: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }

            PatchStart(harmony);
            PatchUpdate(harmony);
            PatchLoadGo(harmony);
            PatchStopLoad(harmony);
        }

        public static void AllowLoad()
        {
            if (_allowLoad) return;
            _allowLoad = true;
            _nativeBackgroundCleanupFinished = false;
            _nativeBackgroundCleanupLogWritten = false;
            _nativeBackgroundRemovalErrorLogged = false;
            _nextNativeBackgroundRetryTime = -1f;
            _nextNativeBackgroundWatchdogTime = 0f;
            _loadFallbackTarget = null;
            _loadFallbackAt = -1f;
            _nextLoadFallbackRetryTime = 0f;

            CacheNativeLoadingObjects(_pendingLoadGoInstance ?? _heldInstance);

            // The game's SceneLoading canvas can survive its own scene in some
            // transition orders. Record the intended destination now and tear
            // that root down when the destination has really become active.
            string targetScene = MultiplayerLoadingScene.TargetSceneName;
            if (!string.IsNullOrEmpty(targetScene))
            {
                _nativeTeardownScene = targetScene;
                _loadFallbackTarget = targetScene;
                _loadFallbackAt = Time.unscaledTime + NativeLoadFallbackDelaySeconds;
                _nextLoadFallbackRetryTime = 0f;
            }

            RestoreHeldAsyncLoad();
            SchedulePostActivationCleanupIfReady(SceneManager.GetActiveScene().name);

            if (_loadGoFired) return;
            if (_pendingLoadGoInstance == null)
            {
                DiagnosticLog.Info("SceneLoadingHook.AllowLoad: no pending LoadGo instance.");
                return;
            }
            if (_loadGoMethod == null) return;

            SceneLoading target = _pendingLoadGoInstance;
            _loadGoFired = true;

            try
            {
                DiagnosticLog.Info("SceneLoadingHook: invoking LoadGo on pending SceneLoading instance.");
                _loadGoMethod.Invoke(target, null);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("SceneLoadingHook: manual LoadGo invoke threw: " +
                                      ex.GetType().Name + ": " + ex.Message);
                _loadFallbackAt = Time.unscaledTime + 1f;
            }
            finally
            {
                _pendingLoadGoInstance = null;
            }
        }

        /// <summary>
        /// Starts a clean loading-hook lifetime for a new multiplayer session.
        /// In particular, _allowLoad must never leak from a completed session
        /// into the next one.
        /// </summary>
        public static void BeginMultiplayerLoad()
        {
            // A previous transition can leave either native object alive when
            // Unity changes scenes in an unusual order. Remove it before the
            // next multiplayer transition so a stale background cannot leak
            // into this session.
            GameObject staleRoot = _nativeLoadingRoot;

            _allowLoad = false;
            _loadGoFired = false;
            _pendingLoadGoInstance = null;
            _heldInstance = null;
            _heldAsyncLoad = null;
            _cachedObjectInterfaceLoading = null;
            _nativeLoadingRoot = null;
            _nativeLoadingOwner = null;
            _nativeTeardownScene = null;
            _nativeBackgroundCleanupFinished = false;
            _nativeBackgroundCleanupLogWritten = false;
            _nativeBackgroundRemovalErrorLogged = false;
            _nextNativeBackgroundRetryTime = -1f;
            _nextNativeBackgroundWatchdogTime = 0f;
            _loadFallbackTarget = null;
            _loadFallbackAt = -1f;
            _nextLoadFallbackRetryTime = 0f;

            // MenuGame is the owner of the loading canvas and may also hold
            // transition logic. Retire only the specific Canvas/Loading root.
            DestroyNativeLoadingObject(staleRoot, "before a new multiplayer load");
        }

        /// <summary>
        /// Cancels a scheduled native-root cleanup when a failed connection is
        /// returning to SceneMenu instead of committing the multiplayer world.
        /// </summary>
        public static void CancelNativeTeardown()
        {
            _nativeTeardownScene = null;
            _nativeBackgroundCleanupFinished = true;
            _allowLoad = false;
            _loadFallbackTarget = null;
            _loadFallbackAt = -1f;
            _nextLoadFallbackRetryTime = 0f;
        }

        /// <summary>
        /// Called by RuntimeServices after every scene activation. Once GO has
        /// been received, remove the native loading UI as soon as the target
        /// gameplay scene is active.
        /// </summary>
        public static void OnSceneLoaded(string sceneName)
        {
            if (string.Equals(sceneName, "SceneMenu", StringComparison.Ordinal))
            {
                CleanupForMenuReturn();
                return;
            }

            _menuReturnCleanupActive = false;
            SchedulePostActivationCleanupIfReady(sceneName);
        }

        private static void CleanupForMenuReturn()
        {
            // A return from gameplay can leave the native SceneLoading object
            // alive with a stale async operation. Restore any operation we had
            // held, clear multiplayer load state, and kill the visible loading
            // canvas while keeping the game's object available for later loads.
            try { RestoreHeldAsyncLoad(); }
            catch { }
            try { MultiplayerLoadingScene.Abort(); }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("SceneLoadingHook: menu-return loading abort failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
            try { SceneManagerHook.CancelPendingLoad(); } catch { }

            _allowLoad = false;
            _loadGoFired = false;
            _pendingLoadGoInstance = null;
            _heldInstance = null;
            _heldAsyncLoad = null;
            _nativeTeardownScene = null;
            _nativeBackgroundCleanupFinished = true;
            _nativeBackgroundCleanupLogWritten = false;
            _nativeBackgroundRemovalErrorLogged = false;
            _nextNativeBackgroundRetryTime = -1f;
            _nextNativeBackgroundWatchdogTime = 0f;
            _loadFallbackTarget = null;
            _loadFallbackAt = -1f;
            _nextLoadFallbackRetryTime = 0f;
            _nativeLoadingRoot = null;
            _nativeLoadingOwner = null;
            _cachedObjectInterfaceLoading = null;

            _menuReturnCleanupActive = true;
            _menuReturnHideLogged = false;
            _menuReturnCleanupErrorLogged = false;
            _menuReturnCleanupUntil = Time.unscaledTime + MenuReturnCleanupDurationSeconds;
            _nextMenuReturnCleanupTime = Time.unscaledTime + MenuReturnCleanupIntervalSeconds;

            // Do one synchronous pass in the sceneLoaded callback to avoid a
            // visible frame of the old loading screen over the main menu.
            ForceHideNativeLoadingObjects("immediately after returning to SceneMenu");
        }

        private static void ForceHideNativeLoadingObjects(string reason)
        {
            GameObject root = null;
            GameObject owner = null;
            FindLingeringNativeLoadingObjects(ref root, ref owner);
            if (root == null) return;

            try
            {
                bool wasActive = root.activeSelf || root.activeInHierarchy;
                if (wasActive)
                {
                    root.SetActive(false);
                    if (!_menuReturnHideLogged)
                    {
                        DiagnosticLog.Info("SceneLoadingHook: force-hid native loading UI '" +
                                            SafePath(root) + "' " + reason + ".");
                        _menuReturnHideLogged = true;
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_menuReturnCleanupErrorLogged)
                {
                    DiagnosticLog.Warning("SceneLoadingHook: menu-return loading UI cleanup failed: " +
                                          ex.GetType().Name + ": " + ex.Message + "; retrying briefly.");
                    _menuReturnCleanupErrorLogged = true;
                }
            }
        }

        private static void SchedulePostActivationCleanupIfReady(string sceneName)
        {
            if (!_allowLoad || string.IsNullOrEmpty(sceneName) ||
                _nativeBackgroundCleanupFinished ||
                Time.unscaledTime < _nextNativeBackgroundRetryTime ||
                string.Equals(sceneName, NativeLoadingSceneName, StringComparison.Ordinal) ||
                !string.Equals(SceneManager.GetActiveScene().name, sceneName, StringComparison.Ordinal))
                return;

            // Don't let a GO received in a menu or an intermediate scene
            // remove the loading background before the requested destination
            // is active.
            if (!string.IsNullOrEmpty(_nativeTeardownScene) &&
                !string.Equals(_nativeTeardownScene, sceneName, StringComparison.Ordinal))
                return;

            if (string.IsNullOrEmpty(_nativeTeardownScene))
                _nativeTeardownScene = sceneName;

            CleanupNativeBackground(sceneName);
        }

        private static void DestroyNativeLoadingObject(GameObject target, string reason)
        {
            if (target == null) return;

            string targetName = "<unknown>";
            try { targetName = target.name; } catch { }
            try
            {
                UnityEngine.Object.Destroy(target);
                DiagnosticLog.Info("SceneLoadingHook: removed native loading object '" +
                                   targetName + "' " + reason + ".");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("SceneLoadingHook: native loading cleanup failed for '" +
                                      targetName + "': " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void RestoreNativeUI()
        {
            DiagnosticLog.Verbose("SceneLoadingHook.RestoreNativeUI: no-op.");
        }

        public static void Tick()
        {
            string activeScene = SceneManager.GetActiveScene().name;

            if (_menuReturnCleanupActive)
            {
                if (!string.Equals(activeScene, "SceneMenu", StringComparison.Ordinal))
                {
                    _menuReturnCleanupActive = false;
                }
                else if (Time.unscaledTime >= _nextMenuReturnCleanupTime)
                {
                    _nextMenuReturnCleanupTime = Time.unscaledTime +
                        MenuReturnCleanupIntervalSeconds;
                    ForceHideNativeLoadingObjects("during the SceneMenu cleanup window");
                    if (Time.unscaledTime >= _menuReturnCleanupUntil)
                        _menuReturnCleanupActive = false;
                }
            }

            if (_allowLoad)
            {
                if (!_nativeBackgroundCleanupFinished)
                    SchedulePostActivationCleanupIfReady(activeScene);

                if (!string.IsNullOrEmpty(_loadFallbackTarget) &&
                    !string.Equals(activeScene, _loadFallbackTarget, StringComparison.Ordinal) &&
                    !string.Equals(activeScene, "SceneMenu", StringComparison.Ordinal) &&
                    Time.unscaledTime >= _loadFallbackAt &&
                    Time.unscaledTime >= _nextLoadFallbackRetryTime)
                {
                    SceneManagerHook.RequestFallbackLoad(_loadFallbackTarget,
                        "SceneLoading did not activate the requested destination after GO");
                    _nextLoadFallbackRetryTime = Time.unscaledTime + 2f;
                }
            }

            // Unity can recreate or reparent this canvas after the first
            // successful cleanup. Keep scanning until the player leaves the
            // gameplay scenes so the patterned native background cannot return.
            if (!string.IsNullOrEmpty(activeScene) &&
                !string.Equals(activeScene, NativeLoadingSceneName, StringComparison.Ordinal) &&
                !string.Equals(activeScene, "SceneMenu", StringComparison.Ordinal) &&
                Time.unscaledTime >= _nextNativeBackgroundWatchdogTime)
            {
                _nextNativeBackgroundWatchdogTime = Time.unscaledTime +
                    NativeBackgroundWatchdogIntervalSeconds;
                ForceRemoveAllNativeLoadingObjects(activeScene);
            }
        }

        private static void CleanupNativeBackground(string sceneName)
        {
            if (_nativeBackgroundCleanupFinished) return;

            GameObject root = _nativeLoadingRoot != null
                ? _nativeLoadingRoot
                : _cachedObjectInterfaceLoading;
            GameObject owner = _nativeLoadingOwner;
            string targetScene = _nativeTeardownScene;

            FindLingeringNativeLoadingObjects(ref root, ref owner);

            if (root == null)
            {
                // Keep the immediate attempt, then retry lookup quietly in
                // case Unity has not exposed the persistent loading root yet.
                // The old timer reset its own sentinel and re-logged forever.
                _nextNativeBackgroundRetryTime =
                    Time.unscaledTime + NativeBackgroundRetryIntervalSeconds;
                if (!_nativeBackgroundCleanupLogWritten)
                {
                    DiagnosticLog.Verbose("SceneLoadingHook: native loading root not visible yet; retrying lookup while destination remains active.");
                    _nativeBackgroundCleanupLogWritten = true;
                }
                return;
            }

            try
            {
                if (!ForceRemoveNativeLoadingObject(root, "immediately after '" +
                    (targetScene ?? sceneName ?? "<destination>") + "' activated"))
                {
                    // A Unity/IL2CPP wrapper can remain non-null after the
                    // native object has become invalid. Do not let that stale
                    // reference block a fresh lookup on the next retry.
                    _nativeLoadingRoot = null;
                    _cachedObjectInterfaceLoading = null;
                    _nativeLoadingOwner = owner;
                    _nextNativeBackgroundRetryTime =
                        Time.unscaledTime + NativeBackgroundRetryIntervalSeconds;
                    return;
                }

                // Latch only after we actually hid and scheduled destruction
                // of the root. Repeated sceneLoaded/watchdog calls now no-op.
                _nativeBackgroundCleanupFinished = true;
                _nativeTeardownScene = null;
                _nativeLoadingRoot = null;
                _nativeLoadingOwner = null;
                _cachedObjectInterfaceLoading = null;
                _nextNativeBackgroundRetryTime = -1f;
                if (!_nativeBackgroundCleanupLogWritten)
                {
                    DiagnosticLog.Info("SceneLoadingHook: active destination scene '" + sceneName +
                                       "' loaded after GO; removing native loading background immediately.");
                    _nativeBackgroundCleanupLogWritten = true;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("SceneLoadingHook: immediate post-activation cleanup failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
                _nativeLoadingRoot = null;
                _cachedObjectInterfaceLoading = null;
                _nativeLoadingOwner = owner;
                _nextNativeBackgroundRetryTime =
                    Time.unscaledTime + NativeBackgroundRetryIntervalSeconds;
            }
        }

        private static void FindLingeringNativeLoadingObjects(ref GameObject root,
                                                               ref GameObject owner)
        {
            if (root != null) return;

            // First retry from the cached owner. Scene_Load can activate an
            // additive destination while Unity is still reparenting MenuGame,
            // so the persistent canvas may not be discoverable by its global
            // name/path during the first sceneLoaded callback.
            try
            {
                if (owner != null)
                {
                    Transform loading = owner.transform.Find("Canvas/Loading");
                    if (loading != null)
                    {
                        root = loading.gameObject;
                        return;
                    }
                }
            }
            catch { }

            // Ask the game's own SceneLoading component for the authoritative
            // objectInterfaceLoading reference before relying on hierarchy
            // names. This survives renamed/reparented MenuGame objects.
            try
            {
                UnityEngine.Object[] loaders = Resources.FindObjectsOfTypeAll(
                    Il2CppInterop.Runtime.Il2CppType.Of<SceneLoading>());
                for (int i = 0; i < loaders.Length; i++)
                {
                    SceneLoading loader = loaders[i].TryCast<SceneLoading>();
                    if (loader == null || loader.gameObject == null ||
                        !loader.gameObject.scene.IsValid())
                        continue;

                    GameObject loadingRoot = loader.objectInterfaceLoading;
                    if (loadingRoot == null) continue;

                    root = loadingRoot;
                    owner = loader.gameObject;
                    return;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("SceneLoadingHook: SceneLoading object lookup failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(
                    Il2CppInterop.Runtime.Il2CppType.Of<GameObject>());
                for (int i = 0; i < all.Length; i++)
                {
                    GameObject candidateOwner = all[i].TryCast<GameObject>();
                    if (candidateOwner == null || candidateOwner.name != "MenuGame" ||
                        !candidateOwner.scene.IsValid())
                        continue;

                    Transform loading = candidateOwner.transform.Find("Canvas/Loading");
                    if (loading == null) continue;

                    if (root == null) root = loading.gameObject;
                    if (owner == null) owner = candidateOwner;
                    if (root != null) return;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("SceneLoadingHook: lingering-root lookup failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void ForceRemoveAllNativeLoadingObjects(string activeScene)
        {
            string reason = "while destination scene '" + activeScene + "' is active";
            GameObject cachedRoot = _nativeLoadingRoot != null
                ? _nativeLoadingRoot
                : _cachedObjectInterfaceLoading;
            if (cachedRoot != null)
                ForceRemoveNativeLoadingObject(cachedRoot, reason);

            try
            {
                UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(
                    Il2CppInterop.Runtime.Il2CppType.Of<GameObject>());
                for (int i = 0; i < all.Length; i++)
                {
                    GameObject owner = all[i].TryCast<GameObject>();
                    if (owner == null || owner.name != "MenuGame" ||
                        !owner.scene.IsValid())
                        continue;

                    Transform loading = owner.transform.Find("Canvas/Loading");
                    if (loading != null)
                        ForceRemoveNativeLoadingObject(loading.gameObject, reason);
                }
            }
            catch (Exception ex)
            {
                if (!_nativeBackgroundRemovalErrorLogged)
                {
                    DiagnosticLog.Warning("SceneLoadingHook: native-background watchdog scan failed: " +
                                          ex.GetType().Name + ": " + ex.Message);
                    _nativeBackgroundRemovalErrorLogged = true;
                }
            }
        }

        private static bool ForceRemoveNativeLoadingObject(GameObject target, string reason)
        {
            if (target == null) return false;

            string targetName = "<unknown>";
            try { targetName = target.name; } catch { }
            bool hidden = false;
            bool destructionQueued = false;

            // SetActive(false) is the immediate visual kill. Keep it separate
            // from Destroy so an IL2CPP exception in one operation cannot
            // prevent the other cleanup path from running.
            try
            {
                target.SetActive(false);
                hidden = !target.activeInHierarchy;
            }
            catch (Exception ex)
            {
                LogNativeBackgroundRemovalFailure(targetName, "deactivation", ex);
            }

            try
            {
                UnityEngine.Object.Destroy(target);
                destructionQueued = true;
            }
            catch (Exception ex)
            {
                LogNativeBackgroundRemovalFailure(targetName, "destruction", ex);
            }

            if (hidden || destructionQueued)
            {
                DiagnosticLog.Warning("SceneLoadingHook: force-removed native loading object '" +
                                      targetName + "' " + reason + ".");
                return true;
            }

            return false;
        }

        private static void LogNativeBackgroundRemovalFailure(string targetName,
                                                               string operation,
                                                               Exception ex)
        {
            if (_nativeBackgroundRemovalErrorLogged) return;
            DiagnosticLog.Warning("SceneLoadingHook: native loading " + operation +
                                  " failed for '" + targetName + "': " +
                                  ex.GetType().Name + ": " + ex.Message +
                                  "; will retry while the destination remains active.");
            _nativeBackgroundRemovalErrorLogged = true;
        }

        public static void ClearWatch() { }

        private static void CacheNativeLoadingObjects(SceneLoading instance)
        {
            if (instance == null) return;
            try { _nativeLoadingOwner = instance.gameObject; } catch { }
            try { _nativeLoadingRoot = instance.objectInterfaceLoading; } catch { }
        }

        private static void RestoreHeldAsyncLoad()
        {
            if (_heldInstance == null)
            {
                // Nothing held. Still re-enable then immediately hide the
                // bar+text, in case the game's own logic has just shown it.
                TryReEnableThenHideBar();
                return;
            }

            try
            {
                if (_heldAsyncLoad != null)
                {
                    _heldInstance.asyncLoad = _heldAsyncLoad;
                    DiagnosticLog.Info("SceneLoadingHook: restored asyncLoad on held SceneLoading instance.");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("SceneLoadingHook: restoring asyncLoad threw: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                _heldInstance = null;
                _heldAsyncLoad = null;

                // Give the game its UI back so its fade sequence can play,
                // then hide the bar+text again on the same frame so the
                // player never sees the native loading bar flash.
                TryReEnableThenHideBar();
            }
        }

        private static void TryReEnableThenHideBar()
        {
            try
            {
                if (_cachedObjectInterfaceLoading == null) return;

                // Re-enable so the game's own teardown logic finds an active
                // object and can fade it. Then disable immediately so the
                // player sees nothing.
                _cachedObjectInterfaceLoading.SetActive(true);
                _cachedObjectInterfaceLoading.SetActive(false);

                DiagnosticLog.Info("SceneLoadingHook: bounced native loading UI root '" +
                                   SafePath(_cachedObjectInterfaceLoading) +
                                   "' (re-enable + hide) to suppress the tail-end flash.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("SceneLoadingHook: bouncing native bar threw: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ── Patching ────────────────────────────────────────────────────

        private static void PatchStart(Harmony harmony)
        {
            try
            {
                MethodInfo original = typeof(SceneLoading).GetMethod(
                    "Start",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (original == null) return;

                HarmonyMethod postfix = new HarmonyMethod(typeof(SceneLoadingHook).GetMethod(
                    nameof(StartPostfix),
                    BindingFlags.Static | BindingFlags.NonPublic));

                harmony.Patch(original, null, postfix);
                DiagnosticLog.Info("SceneLoadingHook: patched SceneLoading.Start() (postfix).");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("SceneLoadingHook: Start patch failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void PatchUpdate(Harmony harmony)
        {
            try
            {
                MethodInfo original = typeof(SceneLoading).GetMethod(
                    "Update",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (original == null) return;

                HarmonyMethod prefix = new HarmonyMethod(typeof(SceneLoadingHook).GetMethod(
                    nameof(UpdatePrefix),
                    BindingFlags.Static | BindingFlags.NonPublic));

                harmony.Patch(original, prefix, null);
                DiagnosticLog.Info("SceneLoadingHook: patched SceneLoading.Update() (prefix).");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("SceneLoadingHook: Update patch failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void PatchLoadGo(Harmony harmony)
        {
            try
            {
                MethodInfo original = _loadGoMethod ?? typeof(SceneLoading).GetMethod(
                    "LoadGo",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (original == null) return;

                HarmonyMethod prefix = new HarmonyMethod(typeof(SceneLoadingHook).GetMethod(
                    nameof(LoadGoPrefix),
                    BindingFlags.Static | BindingFlags.NonPublic));

                harmony.Patch(original, prefix, null);
                DiagnosticLog.Info("SceneLoadingHook: patched SceneLoading.LoadGo().");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("SceneLoadingHook: LoadGo patch failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void PatchStopLoad(Harmony harmony)
        {
            try
            {
                MethodInfo original = typeof(SceneLoading).GetMethod(
                    "StopLoad",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (original == null) return;

                HarmonyMethod prefix = new HarmonyMethod(typeof(SceneLoadingHook).GetMethod(
                    nameof(StopLoadPrefix),
                    BindingFlags.Static | BindingFlags.NonPublic));

                harmony.Patch(original, prefix, null);
                DiagnosticLog.Info("SceneLoadingHook: patched SceneLoading.StopLoad().");
            }
            catch { }
        }

        // ── Update prefix (loading-screen hold) ─────────────────────────

        private static bool UpdatePrefix(SceneLoading __instance)
        {
            if (__instance == null) return false;

            // SceneLoading can persist on MenuGame after its transition has
            // finished. Its native Update then dereferences the retired loading
            // UI and floods the console with NullReferenceExceptions. It has no
            // work to do in the menu scene, so stop that stale update there.
            if (string.Equals(SceneManager.GetActiveScene().name, "SceneMenu",
                    StringComparison.Ordinal))
                return false;

            if (!IsCurrentMultiplayerLoad()) return true;

            if (LoadingStageManager.PipelineCompleted || _allowLoad) return true;

            try
            {
                AsyncOperation op = __instance.asyncLoad;
                if (op == null) return true;

                _heldInstance = __instance;
                _heldAsyncLoad = op;
                __instance.asyncLoad = null;

                if (_updateHoldHits < LogLimit)
                {
                    _updateHoldHits++;
                    DiagnosticLog.Info("SceneLoadingHook: cleared asyncLoad to hold the loading screen. (hold #" +
                                       _updateHoldHits + "/" + LogLimit + ")");
                }
            }
            catch (Exception ex)
            {
                if (_updateHoldHits < LogLimit)
                {
                    _updateHoldHits++;
                    DiagnosticLog.Warning("SceneLoadingHook: Update prefix threw: " +
                                          ex.GetType().Name + ": " + ex.Message);
                }
            }

            return true;
        }

        // ── Start postfix ───────────────────────────────────────────────

        private static void StartPostfix(SceneLoading __instance)
        {
            if (__instance == null) return;

            if (!IsCurrentMultiplayerLoad()) return;

            CacheNativeLoadingObjects(__instance);

            var state = MultiplayerLoadingScene.State;

            bool enabled = true;
            try { enabled = MiSideMultiplayerPlugin.GetHideNativeLoadingUI(); } catch { }

            int instanceId = 0;
            try { instanceId = __instance.GetInstanceID(); } catch { }

            if (enabled && instanceId != _lastHiddenInstanceId)
            {
                _lastHiddenInstanceId = instanceId;

                try
                {
                    _cachedObjectInterfaceLoading = null;
                    try { _cachedObjectInterfaceLoading = __instance.objectInterfaceLoading; } catch { }

                    if (_cachedObjectInterfaceLoading == null)
                    {
                        DiagnosticLog.Warning("SceneLoadingHook: objectInterfaceLoading is null.");
                    }
                    else if (!_cachedObjectInterfaceLoading.activeSelf)
                    {
                        DiagnosticLog.Info("SceneLoadingHook: objectInterfaceLoading '" +
                                           _cachedObjectInterfaceLoading.name +
                                           "' already inactive; nothing to hide.");
                    }
                    else
                    {
                        _cachedObjectInterfaceLoading.SetActive(false);
                        DiagnosticLog.Info("SceneLoadingHook: hid native loading UI root '" +
                                           SafePath(_cachedObjectInterfaceLoading) +
                                           "' (kept gradientBackground/gradientThings, " +
                                           "state=" + state + ").");
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warning("SceneLoadingHook: hiding bar+text threw: " +
                                          ex.GetType().Name + ": " + ex.Message);
                }
            }

            try { MultiplayerLoadingScene.Show(); }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("SceneLoadingHook: Show() threw: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ── LoadGo prefix ───────────────────────────────────────────────

        private static bool LoadGoPrefix(SceneLoading __instance)
        {
            if (__instance == null) return true;

            if (!IsCurrentMultiplayerLoad()) return true;

            if (LoadingStageManager.PipelineCompleted || _allowLoad)
            {
                _loadGoFired = true;
                _pendingLoadGoInstance = null;
                return true;
            }

            _pendingLoadGoInstance = __instance;
            if (_loadGoHits < LogLimit)
            {
                _loadGoHits++;
                DiagnosticLog.Info("SceneLoadingHook: vetoing LoadGo until pipeline finishes " +
                                   "(hit " + _loadGoHits + "/" + LogLimit + ").");
            }
            return false;
        }

        // The host can leave through the game's normal exit path while a
        // multiplayer load is still armed. That path changes LoadingLevel to
        // SceneMenu. It must be allowed to use the native loading screen rather
        // than inheriting the in-progress multiplayer hold, which otherwise
        // leaves only the patterned background on screen.
        private static bool IsCurrentMultiplayerLoad()
        {
            if (MultiplayerLoadingScene.State == MultiplayerLoadingScene.LoadState.Idle)
                return false;

            string target = MultiplayerLoadingScene.TargetSceneName;
            string requested = null;
            try { requested = GlobalGame.LoadingLevel; } catch { }

            if (!string.IsNullOrEmpty(target) &&
                string.Equals(target, requested, StringComparison.Ordinal))
                return true;

            DiagnosticLog.Info("SceneLoadingHook: current native load is '" +
                               (requested ?? "<null>") + "', not multiplayer target '" +
                               (target ?? "<null>") + "'; releasing multiplayer loading state.");
            MultiplayerLoadingScene.Abort();
            return false;
        }

        private static void StopLoadPrefix(SceneLoading __instance)
        {
            if (__instance == null) return;
            if (_stopLoadHits < LogLimit)
            {
                _stopLoadHits++;
                DiagnosticLog.Info("SceneLoadingHook: SceneLoading.StopLoad() (hit " +
                                   _stopLoadHits + "/" + LogLimit + ").");
            }
        }

        private static string SafePath(GameObject go)
        {
            try
            {
                if (go == null) return "<null>";
                string path = go.name;
                Transform t = go.transform.parent;
                int guard = 0;
                while (t != null && guard++ < 64)
                {
                    path = t.name + "/" + path;
                    t = t.parent;
                }
                return path;
            }
            catch { return "<unreadable>"; }
        }
    }
}
