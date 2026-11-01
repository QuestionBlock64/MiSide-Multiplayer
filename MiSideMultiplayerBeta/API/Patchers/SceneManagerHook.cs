using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer.Runtime.Loading
{
    /// <summary>
    /// Blocks the actual world scene load until the multiplayer pipeline
    /// finishes.
    ///
    /// The probe run told us the world commit comes through
    /// SceneManager.LoadSceneAsync(string) — the single-arg overload.
    /// We veto only that one, only when the target matches our multiplayer
    /// target and the pipeline is not done. Interstitial loads go through
    /// a different overload and are left alone.
    ///
    /// When blocking we return a real AsyncOperation
    /// (Resources.UnloadUnusedAssets) instead of null, so the caller does
    /// not NRE when it dereferences the handle. That NRE was leaving
    /// SceneLoading's internal state wedged, which meant its DontDestroyOnLoad
    /// Canvas (with the gradient background) never got torn down and stayed
    /// on screen into the world.
    /// </summary>
    internal static class SceneManagerHook
    {
        private static bool _initialized;

        private static bool _releasing;

        private static bool _hasPendingLoad;
        private static string _pendingSceneName;
        private static AsyncOperation _pendingOperation;
        private static float _pendingOperationDeadline;
        private static float _nextPendingRetryTime;

        private static string _fallbackSceneName;
        private static AsyncOperation _fallbackOperation;
        private static float _fallbackOperationDeadline;
        private static float _nextFallbackRetryTime;
        private static int _fallbackAttempts;

        private const float PendingOperationTimeoutSeconds = 8f;
        private const float FallbackRetryIntervalSeconds = 2f;
        private const float FailedCallRetryIntervalSeconds = 0.5f;

        private static int _blockedCount;

        public static void Initialize(Harmony harmony)
        {
            if (_initialized) return;
            _initialized = true;

            if (harmony == null)
            {
                DiagnosticLog.Warning("SceneManagerHook: null harmony; not installed.");
                return;
            }

            PatchAsyncString(harmony);

            DiagnosticLog.Info("SceneManagerHook: veto installed on " +
                               "SceneManager.LoadSceneAsync(string). World load will be " +
                               "held until FinishingUp completes.");
        }

        public static void ReleasePendingLoad()
        {
            if (!_hasPendingLoad)
            {
                DiagnosticLog.Verbose("SceneManagerHook.ReleasePendingLoad: no pending load.");
                return;
            }

            TryReleasePendingLoad("loading pipeline completed");
        }

        public static void Tick()
        {
            string activeScene = SceneManager.GetActiveScene().name;
            if (_hasPendingLoad)
            {
                if (string.Equals(activeScene, _pendingSceneName, StringComparison.Ordinal))
                {
                    ClearPendingLoad();
                }
                else if (LoadingStageManager.PipelineCompleted &&
                         Time.unscaledTime >= _nextPendingRetryTime)
                {
                    bool operationTimedOut = _pendingOperation != null &&
                        !IsOperationDone(_pendingOperation) &&
                        Time.unscaledTime >= _pendingOperationDeadline;
                    bool operationFinishedWithoutScene = _pendingOperation != null &&
                        IsOperationDone(_pendingOperation);
                    if (operationTimedOut || operationFinishedWithoutScene ||
                        _pendingOperation == null)
                    {
                        string target = _pendingSceneName;
                        _pendingOperation = null;
                        DiagnosticLog.Warning("SceneManagerHook: deferred load of '" + target +
                            "' did not activate its destination; switching to the direct-load watchdog.");
                        RequestFallbackLoad(target, "deferred multiplayer load watchdog");
                        _nextPendingRetryTime = Time.unscaledTime + FallbackRetryIntervalSeconds;
                    }
                }
            }

            if (!string.IsNullOrEmpty(_fallbackSceneName))
            {
                if (string.Equals(activeScene, _fallbackSceneName, StringComparison.Ordinal))
                {
                    ClearFallbackLoad();
                }
                else
                {
                    bool timedOut = _fallbackOperation != null &&
                        !IsOperationDone(_fallbackOperation) &&
                        Time.unscaledTime >= _fallbackOperationDeadline;
                    bool finishedWithoutScene = _fallbackOperation != null &&
                        IsOperationDone(_fallbackOperation);
                    if ((_fallbackOperation == null || timedOut || finishedWithoutScene) &&
                        Time.unscaledTime >= _nextFallbackRetryTime)
                    {
                        _fallbackOperation = null;
                        StartFallbackLoad(_fallbackSceneName, "watchdog retry");
                    }
                }
            }
        }

        public static void RequestFallbackLoad(string sceneName, string reason)
        {
            if (string.IsNullOrWhiteSpace(sceneName)) return;
            if (string.Equals(SceneManager.GetActiveScene().name, sceneName,
                    StringComparison.Ordinal))
            {
                ClearFallbackLoad();
                return;
            }

            if (!string.Equals(_fallbackSceneName, sceneName, StringComparison.Ordinal))
            {
                ClearFallbackLoad();
                _fallbackSceneName = sceneName;
                _nextFallbackRetryTime = 0f;
            }

            if (_fallbackOperation != null && !IsOperationDone(_fallbackOperation)) return;
            if (Time.unscaledTime < _nextFallbackRetryTime) return;
            StartFallbackLoad(sceneName, reason);
        }

        public static void ResetForNewLoad()
        {
            ClearPendingLoad();
            ClearFallbackLoad();
        }

        public static void CancelPendingLoad()
        {
            ClearPendingLoad();
            ClearFallbackLoad();
        }

        public static void OnSceneLoaded(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return;
            if (_hasPendingLoad && string.Equals(sceneName, _pendingSceneName,
                    StringComparison.Ordinal))
                ClearPendingLoad();
            if (!string.IsNullOrEmpty(_fallbackSceneName) &&
                string.Equals(sceneName, _fallbackSceneName, StringComparison.Ordinal))
                ClearFallbackLoad();
        }

        private static void TryReleasePendingLoad(string reason)
        {
            if (!_hasPendingLoad || string.IsNullOrEmpty(_pendingSceneName)) return;
            if (string.Equals(SceneManager.GetActiveScene().name, _pendingSceneName,
                    StringComparison.Ordinal))
            {
                ClearPendingLoad();
                return;
            }
            if (_pendingOperation != null && !IsOperationDone(_pendingOperation)) return;
            if (Time.unscaledTime < _nextPendingRetryTime) return;

            string target = _pendingSceneName;
            DiagnosticLog.Info("SceneManagerHook: releasing deferred world load of '" +
                               target + "' (" + reason + ").");
            _releasing = true;
            try
            {
                _pendingOperation = SceneManager.LoadSceneAsync(target);
                if (_pendingOperation == null)
                {
                    _nextPendingRetryTime = Time.unscaledTime + FailedCallRetryIntervalSeconds;
                    DiagnosticLog.Warning("SceneManagerHook: deferred load returned no operation; retrying.");
                    return;
                }

                _pendingOperationDeadline = Time.unscaledTime + PendingOperationTimeoutSeconds;
                _nextPendingRetryTime = _pendingOperationDeadline;
            }
            catch (Exception ex)
            {
                _pendingOperation = null;
                _nextPendingRetryTime = Time.unscaledTime + FailedCallRetryIntervalSeconds;
                DiagnosticLog.Warning("SceneManagerHook: release load threw: " +
                                      ex.GetType().Name + ": " + ex.Message + "; retrying.");
            }
            finally
            {
                _releasing = false;
            }
        }

        private static void StartFallbackLoad(string sceneName, string reason)
        {
            if (string.IsNullOrWhiteSpace(sceneName)) return;
            if (string.Equals(SceneManager.GetActiveScene().name, sceneName,
                    StringComparison.Ordinal))
            {
                ClearFallbackLoad();
                return;
            }

            try
            {
                // This overload bypasses the multiplayer string-overload veto
                // and gives the recovery path an independent native operation.
                _fallbackOperation = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
                _fallbackAttempts++;
                _nextFallbackRetryTime = Time.unscaledTime + FallbackRetryIntervalSeconds;
                if (_fallbackOperation == null)
                {
                    DiagnosticLog.Warning("SceneManagerHook: direct scene-load recovery returned no operation for '" +
                                          sceneName + "' (" + reason + "); retry scheduled.");
                    _fallbackOperationDeadline = Time.unscaledTime;
                    return;
                }

                _fallbackOperationDeadline = Time.unscaledTime + PendingOperationTimeoutSeconds;
                DiagnosticLog.Warning("SceneManagerHook: direct scene-load recovery started for '" +
                                      sceneName + "' (attempt " + _fallbackAttempts + ", " + reason + ").");
            }
            catch (Exception ex)
            {
                _fallbackOperation = null;
                _nextFallbackRetryTime = Time.unscaledTime + FallbackRetryIntervalSeconds;
                _fallbackOperationDeadline = Time.unscaledTime;
                DiagnosticLog.Warning("SceneManagerHook: direct scene-load recovery failed for '" +
                                      sceneName + "': " + ex.GetType().Name + ": " + ex.Message +
                                      "; retry scheduled.");
            }
        }

        private static bool IsOperationDone(AsyncOperation operation)
        {
            if (operation == null) return true;
            try { return operation.isDone; }
            catch { return true; }
        }

        private static void ClearPendingLoad()
        {
            _hasPendingLoad = false;
            _pendingSceneName = null;
            _pendingOperation = null;
            _pendingOperationDeadline = 0f;
            _nextPendingRetryTime = 0f;
        }

        private static void ClearFallbackLoad()
        {
            _fallbackSceneName = null;
            _fallbackOperation = null;
            _fallbackOperationDeadline = 0f;
            _nextFallbackRetryTime = 0f;
            _fallbackAttempts = 0;
        }

        private static void PatchAsyncString(Harmony harmony)
        {
            try
            {
                MethodInfo original = typeof(SceneManager).GetMethod(
                    "LoadSceneAsync",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new Type[] { typeof(string) },
                    null);
                if (original == null)
                {
                    DiagnosticLog.Warning("SceneManagerHook: SceneManager.LoadSceneAsync(string) not found.");
                    return;
                }

                HarmonyMethod prefix = new HarmonyMethod(typeof(SceneManagerHook).GetMethod(
                    nameof(LoadSceneAsyncStringPrefix),
                    BindingFlags.Static | BindingFlags.NonPublic));

                harmony.Patch(original, prefix, null);
                DiagnosticLog.Info("SceneManagerHook: patched SceneManager.LoadSceneAsync(string) (veto).");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("SceneManagerHook: LoadSceneAsync(string) patch failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool LoadSceneAsyncStringPrefix(string sceneName,
                                                       ref AsyncOperation __result)
        {
            if (_releasing) return true;
            if (string.IsNullOrEmpty(sceneName)) return true;

            var state = MultiplayerLoadingScene.State;
            if (state == MultiplayerLoadingScene.LoadState.Idle) return true;

            if (LoadingStageManager.PipelineCompleted) return true;

            string target = MultiplayerLoadingScene.TargetSceneName;
            if (string.IsNullOrEmpty(target)) return true;

            if (!string.Equals(sceneName, target, StringComparison.Ordinal)) return true;

            // Block. Remember the target so ReleasePendingLoad can re-issue.
            _blockedCount++;
            _hasPendingLoad = true;
            _pendingSceneName = sceneName;
            _pendingOperation = null;
            _pendingOperationDeadline = 0f;
            _nextPendingRetryTime = 0f;

            DiagnosticLog.Info("SceneManagerHook: BLOCKED LoadSceneAsync('" + sceneName +
                               "') until FinishingUp completes. (block #" + _blockedCount + ")");

            // Return a real AsyncOperation so the caller does not NRE on a
            // null handle. Resources.UnloadUnusedAssets returns one and
            // completes on its own schedule; nothing reads the result.
            // The caller just needs a non-null object.
            try
            {
                __result = Resources.UnloadUnusedAssets();
            }
            catch
            {
                __result = null;
            }

            return false;
        }
    }
}
