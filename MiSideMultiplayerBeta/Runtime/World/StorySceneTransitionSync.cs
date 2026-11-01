using System;
using System.Text.Json;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using MiSideMultiplayer.Runtime.Lobby;
using MiSideMultiplayer.Runtime;
using MiSideMultiplayer.Runtime.Loading;

namespace MiSideMultiplayer
{
    /// <summary>
    /// Mirrors the host's native transition into Scene 5 - StartHorror. The
    /// joiner uses BlackScreen.NextLevel so the game's own scene bootstrap and
    /// fade-out logic run on that client as well.
    /// </summary>
    internal static class StorySceneTransitionSync
    {
        private const string TargetScene = "Scene 5 - StartHorror";
        private const string TransitionEvent = "miside.scene.story-transition";
        private const float StoryLoadFallbackDelay = 8f;
        private const float CoordinatedLoadFallbackDelay = 20f;
        private const float SceneLoadRetryInterval = 2f;
        private const float BlackScreenRecoveryDelay = 2f;
        private const float BlackScreenRecoveryInterval = 0.2f;
        private const float BlackScreenRecoveryWindow = 10f;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true
        };

        private sealed class TransitionMessage
        {
            public string senderId;
            public string transitionId;
            public string sceneName;
        }

        private static RpcDispatcher _dispatcher;
        private static string _localPlayerId;
        private static string _lastHostTransitionScene;
        private static string _lastClientTransitionId;
        private static string _lastClientTransitionScene;
        private static string _clientTransitionSourceScene;
        private static float _clientTransitionStartedAt = -1f;
        private static float _nextSceneFallbackAt = -1f;
        private static float _blackScreenRecoveryUntil = -1f;
        private static float _nextBlackScreenRecoveryAt;
        private static bool _clientTransitionCoordinated;
        private static bool _blackScreenRecoveryLogged;
        private static bool _initialized;

        public static void Initialize(Harmony harmony)
        {
            if (_initialized || harmony == null) return;
            _initialized = true;

            try
            {
                var original = typeof(BlackScreen).GetMethod(
                    "NextLevel",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic,
                    null, new[] { typeof(string) }, null);
                if (original == null)
                {
                    DiagnosticLog.Warning("StorySceneTransitionSync: BlackScreen.NextLevel(string) not found.");
                    return;
                }

                var prefix = new HarmonyMethod(typeof(StorySceneTransitionSync).GetMethod(
                    nameof(NextLevelPrefix),
                    System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.NonPublic));
                harmony.Patch(original, prefix, null);
                DiagnosticLog.Info("StorySceneTransitionSync: patched BlackScreen.NextLevel(string).");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("StorySceneTransitionSync: patch failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void Configure(RpcDispatcher dispatcher, string localPlayerId)
        {
            if (_dispatcher != null)
                _dispatcher.CustomMessageReceived -= OnCustomMessage;

            _dispatcher = dispatcher;
            _localPlayerId = localPlayerId ?? string.Empty;
            _clientTransitionSourceScene = null;
            _clientTransitionStartedAt = -1f;
            _nextSceneFallbackAt = -1f;
            _blackScreenRecoveryUntil = -1f;
            _clientTransitionCoordinated = false;
            _blackScreenRecoveryLogged = false;
            if (_dispatcher != null)
                _dispatcher.CustomMessageReceived += OnCustomMessage;
        }

        private static void NextLevelPrefix(string level)
        {
            if (!string.Equals(level, TargetScene, StringComparison.Ordinal) ||
                !IsConnectedHost())
                return;

            BroadcastTransition(level);
        }

        public static void OnSceneLoaded(string sceneName)
        {
            if (!string.Equals(sceneName, TargetScene, StringComparison.Ordinal))
            {
                if (!string.Equals(sceneName, "SceneLoading", StringComparison.Ordinal))
                    _lastHostTransitionScene = null;
                if (string.Equals(sceneName, "SceneMenu", StringComparison.Ordinal) &&
                    !IsCoordinatedTransitionPending())
                    ClearClientTransitionWatch();
                return;
            }

            if (IsConnectedHost() && _dispatcher != null &&
                !string.Equals(_lastHostTransitionScene, sceneName, StringComparison.Ordinal))
                BroadcastTransition(sceneName);

            if (!MitaHost.IsHost &&
                string.Equals(_lastClientTransitionScene, sceneName, StringComparison.Ordinal))
                BeginBlackScreenRecovery(sceneName);
        }

        public static void Tick()
        {
            if (MitaHost.IsHost || string.IsNullOrEmpty(_lastClientTransitionScene)) return;

            string activeScene = SceneManager.GetActiveScene().name;
            if (string.Equals(activeScene, _lastClientTransitionScene, StringComparison.Ordinal))
            {
                if (_blackScreenRecoveryUntil < 0f)
                    BeginBlackScreenRecovery(activeScene);

                float now = Time.unscaledTime;
                bool recoveryDeadlineReached = now >= _blackScreenRecoveryUntil;
                if (MultiplayerLoadingScene.State != MultiplayerLoadingScene.LoadState.Idle &&
                    !recoveryDeadlineReached)
                {
                    _nextBlackScreenRecoveryAt = now + 0.25f;
                    return;
                }

                // Once the multiplayer loading overlay is gone, reveal after
                // the short transition grace period. If the overlay itself
                // wedges, the hard deadline wins so the joiner cannot remain
                // behind a permanent black screen.
                if (now >= _nextBlackScreenRecoveryAt || recoveryDeadlineReached)
                {
                    ForceReleaseBlackScreen();
                    ClearClientTransitionWatch();
                }
                return;
            }

            if (string.Equals(activeScene, "SceneMenu", StringComparison.Ordinal) &&
                !IsCoordinatedTransitionPending())
            {
                ClearClientTransitionWatch();
                return;
            }

            if (!IsCoordinatedTransitionPending() &&
                !string.Equals(activeScene, "SceneLoading", StringComparison.Ordinal) &&
                !string.IsNullOrEmpty(_clientTransitionSourceScene) &&
                !string.Equals(activeScene, _clientTransitionSourceScene, StringComparison.Ordinal))
            {
                ClearClientTransitionWatch();
                return;
            }

            if (string.Equals(activeScene, "SceneLoading", StringComparison.Ordinal) &&
                IsCoordinatedTransitionPending() && !LoadingStageManager.PipelineCompleted &&
                Time.unscaledTime - _clientTransitionStartedAt < CoordinatedLoadFallbackDelay)
                return;

            if (IsCoordinatedTransitionPending() && LoadingStageManager.PipelineCompleted &&
                _nextSceneFallbackAt > Time.unscaledTime + SceneLoadRetryInterval)
                _nextSceneFallbackAt = Time.unscaledTime + SceneLoadRetryInterval;

            if (Time.unscaledTime >= _nextSceneFallbackAt)
            {
                DiagnosticLog.Warning("StorySceneTransitionSync: destination '" +
                    _lastClientTransitionScene + "' is still not active after host GO; requesting direct scene-load recovery.");
                SceneManagerHook.RequestFallbackLoad(_lastClientTransitionScene,
                    "host-authoritative story transition watchdog");
                _nextSceneFallbackAt = Time.unscaledTime + SceneLoadRetryInterval;
            }
        }

        private static void OnCustomMessage(string eventName, string json)
        {
            if (eventName != TransitionEvent || MitaHost.IsHost) return;

            try
            {
                TransitionMessage message = JsonSerializer.Deserialize<TransitionMessage>(json, JsonOptions);
                if (message == null || message.senderId != MitaHost.HostPlayerId ||
                    string.IsNullOrWhiteSpace(message.transitionId) ||
                    !string.Equals(message.sceneName, TargetScene, StringComparison.Ordinal))
                    return;

                if (_lastClientTransitionId == message.transitionId)
                    return;

                _lastClientTransitionId = message.transitionId;
                _lastClientTransitionScene = message.sceneName;
                string activeScene = SceneManager.GetActiveScene().name;
                _clientTransitionSourceScene = activeScene;
                _clientTransitionStartedAt = Time.unscaledTime;
                _clientTransitionCoordinated =
                    MultiplayerLoadingScene.State != MultiplayerLoadingScene.LoadState.Idle &&
                    string.Equals(MultiplayerLoadingScene.TargetSceneName,
                                  message.sceneName, StringComparison.Ordinal);
                _nextSceneFallbackAt = Time.unscaledTime +
                    (_clientTransitionCoordinated
                        ? CoordinatedLoadFallbackDelay
                        : StoryLoadFallbackDelay);
                _blackScreenRecoveryUntil = -1f;
                _blackScreenRecoveryLogged = false;

                if (string.Equals(activeScene, message.sceneName, StringComparison.Ordinal))
                {
                    BeginBlackScreenRecovery(message.sceneName);
                    return;
                }

                // If the coordinated initial load already targets this scene,
                // let its loading barrier finish instead of starting a second
                // native load on top of it.
                if (_clientTransitionCoordinated)
                    return;

                BlackScreen blackScreen = UnityEngine.Object.FindObjectOfType<BlackScreen>();
                if (blackScreen != null)
                {
                    DiagnosticLog.Info("StorySceneTransitionSync: following host into '" +
                                       message.sceneName + "' through BlackScreen.NextLevel.");
                    blackScreen.NextLevel(message.sceneName);
                }
                else
                {
                    // This path is only used if the source scene has no native
                    // transition controller. The post-load safety below clears
                    // a stale black overlay after the destination world appears.
                    DiagnosticLog.Warning("StorySceneTransitionSync: BlackScreen was absent; loading '" +
                                          message.sceneName + "' directly.");
                    SceneManagerHook.RequestFallbackLoad(message.sceneName,
                        "BlackScreen.NextLevel was unavailable");
                    _nextSceneFallbackAt = Time.unscaledTime + SceneLoadRetryInterval;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("StorySceneTransitionSync: transition message failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void BroadcastTransition(string sceneName)
        {
            if (_dispatcher == null || !IsConnectedHost()) return;

            string transitionId = Guid.NewGuid().ToString("N");
            _lastHostTransitionScene = sceneName;
            try
            {
                _dispatcher.SendCustom(TransitionEvent,
                    JsonSerializer.Serialize(new TransitionMessage
                    {
                        senderId = _localPlayerId,
                        transitionId = transitionId,
                        sceneName = sceneName
                    }, JsonOptions));
                DiagnosticLog.Info("StorySceneTransitionSync: host announced transition '" +
                                   transitionId + "' to '" + sceneName + "'.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("StorySceneTransitionSync: could not announce '" +
                                      sceneName + "': " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void BeginBlackScreenRecovery(string sceneName)
        {
            _lastClientTransitionScene = sceneName;
            _blackScreenRecoveryUntil = Time.unscaledTime + BlackScreenRecoveryWindow;
            _nextBlackScreenRecoveryAt = Time.unscaledTime + BlackScreenRecoveryDelay;
            _blackScreenRecoveryLogged = false;
        }

        private static bool IsCoordinatedTransitionPending()
        {
            return _clientTransitionCoordinated &&
                   MultiplayerLoadingScene.State != MultiplayerLoadingScene.LoadState.Idle &&
                   string.Equals(MultiplayerLoadingScene.TargetSceneName,
                                 _lastClientTransitionScene, StringComparison.Ordinal);
        }

        private static void ClearClientTransitionWatch()
        {
            _lastClientTransitionScene = null;
            _clientTransitionSourceScene = null;
            _clientTransitionStartedAt = -1f;
            _nextSceneFallbackAt = -1f;
            _blackScreenRecoveryUntil = -1f;
            _nextBlackScreenRecoveryAt = 0f;
            _clientTransitionCoordinated = false;
            _blackScreenRecoveryLogged = false;
        }

        private static void ForceReleaseBlackScreen()
        {
            BlackScreen blackScreen = UnityEngine.Object.FindObjectOfType<BlackScreen>();
            if (blackScreen == null) return;

            bool released = false;
            try
            {
                blackScreen.HoldBlack(false, true);
                released = true;
            }
            catch { }

            try
            {
                blackScreen.BlackScreenAlpha(0f);
                released = true;
            }
            catch { }

            if (released && !_blackScreenRecoveryLogged)
            {
                DiagnosticLog.Warning("StorySceneTransitionSync: released a stuck black screen after '" +
                                      _lastClientTransitionScene + "' loaded.");
                _blackScreenRecoveryLogged = true;
            }
        }

        private static bool IsConnectedHost()
        {
            SessionController session = SessionController.Active;
            return session != null && session.SessionStarted && session.IsHost &&
                   MiSideMultiplayerPlugin.IsRelayConnected();
        }
    }
}
