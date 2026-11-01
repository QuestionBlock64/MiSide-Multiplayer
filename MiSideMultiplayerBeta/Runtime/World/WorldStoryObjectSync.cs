using System;
using System.Collections.Generic;
using System.Text.Json;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using MiSideMultiplayer.Runtime.Lobby;
using MiSideMultiplayer.Runtime.Loading;

namespace MiSideMultiplayer
{
    public sealed class WorldStoryObjectSync
    {
        public const string LoadingSnapshotRequestEvent = "miside.world.storyobj.snapshot.request";
        public const string LoadingSnapshotCompleteEvent = "miside.world.storyobj.snapshot.complete";
        public const string LoadingSnapshotAckEvent = "miside.world.storyobj.snapshot.ack";
        public const string LoadingSnapshotReleaseEvent = "miside.world.storyobj.snapshot.release";
        private const string Scene5BasementDoorEvent = RpcDispatcher.Scene5BasementDoorEvent;

        private static WorldStoryObjectSync _active;
        private static bool _setActivePatchInstalled;
        private static bool _basementEventsDataPatchInstalled;
        private static int _applyingRemoteStateDepth;
        private static string _cachedActiveSceneName = string.Empty;

        internal static bool IsApplyingRemoteState
        {
            get { return _applyingRemoteStateDepth > 0; }
        }

        private static readonly JsonSerializerOptions SnapshotJsonOptions = new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true
        };

        private static readonly HashSet<string> ExplicitKillPaths = new HashSet<string>(StringComparer.Ordinal)
        {
            "World/Quests/Quest 1/Loading/Box Floor",
            "World/Quests/Quest 1/Loading/BlackRoom Loading"
        };

        // The prologue's quest logic, controls, and HUD are evaluated by each
        // local game instance. Do not let one player's quest object state
        // enable, disable, or otherwise drive another player's prologue.
        private static readonly HashSet<string> LocalOnlyPrologueQuestRoots =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "Quest 2 - Приветствие",
                "Quest 3 - 1 день",
                "Quest 4 - 2 день",
                "Quest 5 - 2 день (Кушать)",
                "Quest 6 - 5 день (Игры)",
                "Quest 7 - 10 день",
                "Quest 8 - 20 день",
                "Quest 9 - 33 день",
                "Quest 10 - 37 день"
            };

        // The local player's UI is a per-client presentation layer. It must
        // never enter a world snapshot or have its active state driven by a
        // peer, even if a game update adds a Location script beneath it.
        private static readonly string[] LocalOnlyInterfaceRoots =
        {
            "Interface",
            "InterfaceUp"
        };

        private RpcDispatcher dispatcher;
        private string        localPlayerId;

        private float nextSampleTime;
        private float nextFullResyncTime;
        private const float SampleInterval     = 1.0f;
        private const float FullResyncInterval = 30.0f;
        private const int MaxFullSnapshotStatesPerFrame = 12;
        private const float SpecialStateHeartbeatInterval = 1.0f;
        private const float WardrobeAnimatorSyncInterval = 0.1f;
        private const float MitaFinalClickAnimatorSyncInterval = 0.1f;
        private const string MitaFinalClickObjectName = "AnimationMita Final (Click)";
        private const float Scene5QuestSyncInterval = 0.1f;
        private const float Scene5QuestRecacheInterval = 2f;
        private const float Scene5BasementDoorSyncInterval = 0.1f;
        private const string BedroomWardrobePath =
            "World/House/HouseGameNormal/HomeGame Tamagochi/House/Bedroom/Bedroom Wardrobe";
        private static readonly string[] BedroomWardrobePathCandidates =
        {
            BedroomWardrobePath,
            "World/House/HouseGameNormal/HomeGame Tamagotchi/House/Bedroom/Bedroom Wardrobe",
            "World/House/HouseGame Tamagotchi/House/Bedroom/Bedroom Wardrobe"
        };
        private static readonly string[] BasementDoorFramePathCandidates =
        {
            "World/House/HouseGameNormal/HouseGame Tamagotchi/House/Bedroom/General/BasementDoorFrame",
            "World/House/HouseGameNormal/HomeGame Tamagochi/House/Bedroom/General/BasementDoorFrame",
            "World/House/HouseGameNormal/HomeGame Tamagotchi/House/Bedroom/General/BasementDoorFrame"
        };
        private bool _fullSnapshotPending = true;
        private bool _fullSnapshotBudgetExceeded;
        private int _fullSnapshotTotalSentCount;
        private readonly HashSet<string> _fullSnapshotPathsSent =
            new HashSet<string>(StringComparer.Ordinal);
        private float _nextWardrobeAnimatorSyncTime;
        private float _nextMitaFinalClickAnimatorSyncTime;
        private float _nextScene5QuestSyncTime;
        private float _nextScene5BasementDoorSyncTime;
        private long _scene5QuestSequence;
        private long _scene5BasementDoorEventSequence;
        private int _applyingBasementDoorEventDepth;
        private readonly Dictionary<string, long> _lastScene5SequenceBySender =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _lastBasementDoorEventSequenceBySender =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, ulong> _lastSpecialStateFingerprintByPath =
            new Dictionary<string, ulong>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> _nextSpecialStateHeartbeatByPath =
            new Dictionary<string, float>(StringComparer.Ordinal);

        private readonly List<KeyValuePair<string, GameObject>> _cachedQuestsNodes =
            new List<KeyValuePair<string, GameObject>>();
        private bool _questCacheBuilt;
        private float _nextQuestsRecacheTime;
        private const float QuestsRecacheInterval = 60f;

        private readonly List<KeyValuePair<string, GameObject>> _cachedTrackedNodes =
            new List<KeyValuePair<string, GameObject>>();
        private readonly List<GameObject> _cachedWardrobeRoots = new List<GameObject>();
        private GameObject _cachedBasementDoorFrame;
        private bool _trackedCacheBuilt;
        private float _nextTrackedRecacheTime;
        private const float TrackedRecacheInterval = 60f;

        private readonly Dictionary<string, bool> lastKnownActive = new Dictionary<string, bool>();
        private readonly HashSet<string>          syncedPaths     = new HashSet<string>();
        private readonly Dictionary<int, string> _trackedPathByObjectId = new Dictionary<int, string>();
        private readonly HashSet<string> _terminallyDisabledPaths = new HashSet<string>(StringComparer.Ordinal);

        private readonly Dictionary<string, float> _lastTeleportByPath = new Dictionary<string, float>();
        private const float TeleportPathCooldown = 10f;

        private bool loggedFirstSend;
        private string lastSceneName;
        private float _nextMissingQuestTreeLogTime;
        private int _fullSnapshotSentCount;
        private bool _fullSnapshotHadError;

        private sealed class SnapshotRequest
        {
            public string playerId;
            public string sceneName;
        }

        private sealed class SnapshotComplete
        {
            public string senderId;
            public string sceneName;
            public string snapshotId;
            public int stateCount;
        }

        private sealed class SnapshotAck
        {
            public string playerId;
            public string sceneName;
            public string snapshotId;
            public int stateCount;
        }

        private sealed class SnapshotRelease
        {
            public string senderId;
            public string sceneName;
            public string snapshotId;
        }

        private sealed class BasementDoorEventMessage
        {
            public string senderId;
            public string sceneName;
            public string path;
            public int eventIndex;
            public long sequence;
        }

        private string _loadingSnapshotScene = string.Empty;
        private string _activeLoadingSnapshotId = string.Empty;
        private int _expectedLoadingSnapshotCount = -1;
        private readonly Dictionary<int, string> _receivedLoadingSnapshotRecords =
            new Dictionary<int, string>();
        private readonly HashSet<int> _appliedLoadingSnapshotRecords = new HashSet<int>();
        private readonly HashSet<string> _loadingSnapshotAcks = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<WorldStoryObjectState> _lastLoadingSnapshotStates =
            new List<WorldStoryObjectState>();
        private bool _loadingSnapshotCompleted;
        private bool _hostLoadingSnapshotSent;
        private bool _postLoadGateArmed;
        private bool _postLoadWorldReady;
        private bool _postLoadReleaseSent;
        private float _nextSnapshotRequestTime;
        private float _nextHostSnapshotRetryTime;
        private float _postLoadGateStartedAt;
        private const float SnapshotRequestInterval = 1f;
        private const float HostSnapshotRetryInterval = 2f;
        private const float PostLoadGateTimeoutSeconds = 15f;

        public static bool HasCompletedLoadingSnapshot
        {
            get { return _active != null && _active._loadingSnapshotCompleted; }
        }

        public static float LoadingSnapshotProgress
        {
            get
            {
                WorldStoryObjectSync sync = _active;
                if (sync == null) return 0f;
                if (sync._loadingSnapshotCompleted) return 1f;
                if (sync._expectedLoadingSnapshotCount <= 0) return 0f;
                return (float)sync._appliedLoadingSnapshotRecords.Count /
                       sync._expectedLoadingSnapshotCount;
            }
        }

        public static bool ShouldHoldLoadingOverlay
        {
            get { return _active != null && _active._postLoadGateArmed; }
        }
        public static void Initialize(Harmony harmony)
        {
            if (harmony == null) return;

            if (!_setActivePatchInstalled)
            {
                try
                {
                    System.Reflection.MethodInfo setActive = typeof(GameObject).GetMethod(
                        "SetActive", new[] { typeof(bool) });
                    System.Reflection.MethodInfo prefix = typeof(WorldStoryObjectSync).GetMethod(
                        nameof(SetActivePrefix),
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                    System.Reflection.MethodInfo postfix = typeof(WorldStoryObjectSync).GetMethod(
                        nameof(SetActivePostfix),
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                    if (setActive != null && prefix != null && postfix != null)
                    {
                        harmony.Patch(setActive,
                            prefix: new HarmonyMethod(prefix),
                            postfix: new HarmonyMethod(postfix));
                        _setActivePatchInstalled = true;
                        DiagnosticLog.Info("WorldStoryObjectSync: patched GameObject.SetActive for bidirectional quest-object state sync.");
                    }
                    else DiagnosticLog.Warning("WorldStoryObjectSync: SetActive story-sync patch target not found.");
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warning("WorldStoryObjectSync: SetActive patch failed: " +
                                          ex.GetType().Name + ": " + ex.Message);
                }
            }

            InstallBasementEventsDataPatches(harmony);
        }

        private static void InstallBasementEventsDataPatches(Harmony harmony)
        {
            if (_basementEventsDataPatchInstalled || harmony == null) return;
            Type eventsType = FindLoadedType("Events_Data");
            if (eventsType == null)
            {
                DiagnosticLog.Warning("WorldStoryObjectSync: Events_Data was not found; Scene 5 door callbacks will rely on state sync.");
                return;
            }

            try
            {
                System.Reflection.MethodInfo prefix = typeof(WorldStoryObjectSync).GetMethod(
                    nameof(BasementEventsDataPrefix),
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                const System.Reflection.BindingFlags flags =
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic;

                // Events_Data exposes two alternative callback names for the same serialized
                // event slot. In IL2CPP builds both wrappers can route through the same native
                // trampoline; patching both creates a recursive Harmony/IL2CPP call chain.
                // Prefer the current name and use the legacy name only when it is unavailable.
                System.Reflection.MethodInfo target = eventsType.GetMethod(
                    "NewEvent", flags, null, new[] { typeof(int) }, null);
                string callbackName = "NewEvent";
                if (target == null)
                {
                    target = eventsType.GetMethod("EV", flags, null, new[] { typeof(int) }, null);
                    callbackName = "EV";
                }

                if (target != null)
                {
                    harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                    _basementEventsDataPatchInstalled = true;
                    DiagnosticLog.Info("WorldStoryObjectSync: patched Events_Data." + callbackName +
                                       " for key-owner-authoritative basement door events.");
                }
                else
                {
                    DiagnosticLog.Warning("WorldStoryObjectSync: neither Events_Data.NewEvent nor EV was found; " +
                                           "Scene 5 door callbacks will rely on state sync.");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("WorldStoryObjectSync: Events_Data patch failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static Type FindLoadedType(string typeName)
        {
            foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!string.Equals(assembly.GetName().Name, "Assembly-CSharp", StringComparison.Ordinal))
                    continue;
                Type type = assembly.GetType(typeName, false);
                if (type != null) return type;
            }
            return null;
        }

        private static bool BasementEventsDataPrefix(Component __instance, int x)
        {
            WorldStoryObjectSync sync = _active;
            if (sync == null || __instance == null || sync._applyingBasementDoorEventDepth > 0 ||
                !IsMultiplayerSessionActive() || !IsScene5StartHorror())
                return true;

            string path;
            try { path = LocalPlayerLocator.GetPath(__instance.transform); }
            catch { return true; }
            if (!IsScene5BasementDoorPath(path)) return true;

            if (!sync.CanPublishBasementDoorState()) return false;
            sync.BroadcastBasementDoorEvent(path, x);
            return true;
        }

        public void Configure(RpcDispatcher rpcDispatcher, string playerId)
        {
            if (dispatcher != null)
                dispatcher.CustomMessageReceived -= OnCustomMessage;

            dispatcher    = rpcDispatcher;
            localPlayerId = playerId;
            _active       = this;

            if (dispatcher != null)
            {
                dispatcher.RemoteStoryObjectStateReceived -= OnRemoteStoryObjectState;
                dispatcher.RemoteStoryObjectStateReceived += OnRemoteStoryObjectState;
                dispatcher.CustomMessageReceived -= OnCustomMessage;
                dispatcher.CustomMessageReceived += OnCustomMessage;
            }
        }

        public static void ArmPostLoadSnapshotGate()
        {
            WorldStoryObjectSync sync = _active;
            if (sync == null) return;

            sync._postLoadGateArmed = IsMultiplayerSessionActive();
            sync._postLoadWorldReady = false;
            sync._postLoadReleaseSent = false;
            sync._postLoadGateStartedAt = 0f;
            sync._hostLoadingSnapshotSent = false;
            sync.ResetLoadingSnapshotState(string.Empty);
        }

        public static void OnDestinationSceneLoaded(string sceneName)
        {
            _cachedActiveSceneName = sceneName ?? string.Empty;
            WorldStoryObjectSync sync = _active;
            if (sync == null || !sync._postLoadGateArmed) return;

            if (string.Equals(sceneName, "SceneMenu", StringComparison.Ordinal))
            {
                sync.ReleasePostLoadGate(false);
                return;
            }

            if (string.IsNullOrEmpty(sceneName) ||
                string.Equals(sceneName, "SceneLoading", StringComparison.Ordinal))
                return;

            sync._loadingSnapshotScene = sceneName;
            sync._postLoadGateStartedAt = Time.unscaledTime;
            sync._postLoadWorldReady = false;
            sync._hostLoadingSnapshotSent = false;
            sync.ResetLoadingSnapshotState(sceneName);
            DiagnosticLog.Info("WorldStoryObjectSync: armed post-load world snapshot for '" +
                               sceneName + "'; multiplayer loading overlay remains until world state is applied.");
        }

        public static void NotifyLoadingGoReceived()
        {
            WorldStoryObjectSync sync = _active;
            if (sync == null || !sync._postLoadGateArmed) return;
            if (sync._postLoadGateStartedAt <= 0f)
                sync._postLoadGateStartedAt = Time.unscaledTime;
        }

        public static void ResetLoadingSnapshot(string sceneName)
        {
            WorldStoryObjectSync sync = _active;
            if (sync != null) sync.ResetLoadingSnapshotState(sceneName);
        }

        public static void RequestLoadingSnapshot()
        {
            WorldStoryObjectSync sync = _active;
            if (sync != null) sync.SendSnapshotRequest();
        }

        public static void CompletePostLoadGate()
        {
            WorldStoryObjectSync sync = _active;
            if (sync == null || !sync._postLoadGateArmed) return;

            sync._postLoadGateArmed = false;
            sync._postLoadReleaseSent = true;
            LoadingStageManager.ReleaseWorldSyncOverlay();
        }

        /// <summary>
        /// Drops the post-load barrier when a multiplayer session is ending.
        /// No release packet is sent because the relay/session is being torn
        /// down and its snapshot can no longer complete.
        /// </summary>
        public static void CancelPostLoadSnapshotGate()
        {
            WorldStoryObjectSync sync = _active;
            if (sync == null) return;

            sync._postLoadGateArmed = false;
            sync._postLoadWorldReady = false;
            sync._postLoadReleaseSent = true;
            sync._postLoadGateStartedAt = 0f;
            sync.ResetLoadingSnapshotState(string.Empty);
        }

        public static void ResetCachesForWorldChange()
        {
            WorldStoryObjectSync s = _active;
            if (s == null) return;

            s.lastKnownActive.Clear();
            s.syncedPaths.Clear();
            s._cachedQuestsNodes.Clear();
            s._questCacheBuilt = false;
            s._trackedCacheBuilt = false;
            s._cachedTrackedNodes.Clear();
            s._cachedWardrobeRoots.Clear();
            s._cachedBasementDoorFrame = null;
            s._trackedPathByObjectId.Clear();
            s._terminallyDisabledPaths.Clear();
            s._lastTeleportByPath.Clear();
            s._lastScene5SequenceBySender.Clear();
            s._lastBasementDoorEventSequenceBySender.Clear();
            s._lastSpecialStateFingerprintByPath.Clear();
            s._nextSpecialStateHeartbeatByPath.Clear();
            s._nextQuestsRecacheTime = 0f;
            s._nextTrackedRecacheTime = 0f;
            s._nextScene5BasementDoorSyncTime = 0f;
            s.nextSampleTime     = 0f;
            s.nextFullResyncTime = 0f;
            s._fullSnapshotPending = true;
            s._fullSnapshotBudgetExceeded = false;
            s._fullSnapshotTotalSentCount = 0;
            s._fullSnapshotPathsSent.Clear();

            DiagnosticLog.Info("WorldStoryObjectSync: caches cleared for world transition.");
        }

        public static void BroadcastSnapshot()
        {
            WorldStoryObjectSync sync = _active;
            if (sync == null || sync.dispatcher == null || !MitaHost.IsHost) return;

            sync.SendHostLoadingSnapshot(false);
        }

        private void ResetLoadingSnapshotState(string sceneName)
        {
            _loadingSnapshotScene = sceneName ?? string.Empty;
            _activeLoadingSnapshotId = string.Empty;
            _expectedLoadingSnapshotCount = -1;
            _receivedLoadingSnapshotRecords.Clear();
            _appliedLoadingSnapshotRecords.Clear();
            _loadingSnapshotAcks.Clear();
            _loadingSnapshotCompleted = false;
            _hostLoadingSnapshotSent = false;
            _lastLoadingSnapshotStates.Clear();
            _nextSnapshotRequestTime = 0f;
            _nextHostSnapshotRetryTime = 0f;
        }

        private void TickPostLoadSnapshot()
        {
            if (!_postLoadGateArmed) return;

            string activeScene = SceneManager.GetActiveScene().name;
            if (!string.IsNullOrEmpty(_loadingSnapshotScene) &&
                !string.Equals(activeScene, _loadingSnapshotScene, StringComparison.Ordinal))
                return;

            bool hasWorld = GameObject.Find("World") != null;
            if (hasWorld)
                _postLoadWorldReady = true;

            if (!_postLoadWorldReady)
            {
                if (_postLoadGateStartedAt > 0f &&
                    Time.unscaledTime - _postLoadGateStartedAt >= PostLoadGateTimeoutSeconds)
                {
                    DiagnosticLog.Warning("WorldStoryObjectSync: world hierarchy did not become ready in '" +
                                          _loadingSnapshotScene + "' within " +
                                          PostLoadGateTimeoutSeconds + "s; releasing loading overlay.");
                    ReleasePostLoadGate(MitaHost.IsHost);
                }
                return;
            }

            if (MitaHost.IsHost)
            {
                if (!_hostLoadingSnapshotSent)
                    SendHostLoadingSnapshot(false);
                else if (_loadingSnapshotAcks.Count < LoadingNetwork.ExpectedClientCount() &&
                         Time.unscaledTime >= _nextHostSnapshotRetryTime)
                    SendHostLoadingSnapshot(true);

                if (_hostLoadingSnapshotSent &&
                    _loadingSnapshotAcks.Count >= LoadingNetwork.ExpectedClientCount())
                    ReleasePostLoadGate(true);
            }
            else if (!_loadingSnapshotCompleted &&
                     Time.unscaledTime >= _nextSnapshotRequestTime)
            {
                SendSnapshotRequest();
            }

            if (_postLoadGateStartedAt > 0f &&
                Time.unscaledTime - _postLoadGateStartedAt >= PostLoadGateTimeoutSeconds)
            {
                DiagnosticLog.Warning("WorldStoryObjectSync: post-load snapshot barrier timed out in '" +
                                      _loadingSnapshotScene + "' (applied=" +
                                      _appliedLoadingSnapshotRecords.Count + "/" +
                                      Math.Max(0, _expectedLoadingSnapshotCount) +
                                      "); releasing the loading overlay.");
                ReleasePostLoadGate(MitaHost.IsHost);
            }
        }

        private void SendSnapshotRequest()
        {
            if (dispatcher == null || MitaHost.IsHost ||
                !_postLoadGateArmed || !_postLoadWorldReady)
                return;

            string sceneName = SceneManager.GetActiveScene().name;
            if (string.IsNullOrEmpty(sceneName) ||
                (!string.IsNullOrEmpty(_loadingSnapshotScene) &&
                 sceneName != _loadingSnapshotScene))
                return;

            try
            {
                dispatcher.SendCustom(LoadingSnapshotRequestEvent,
                    JsonSerializer.Serialize(new SnapshotRequest
                    {
                        playerId = localPlayerId,
                        sceneName = sceneName
                    }, SnapshotJsonOptions));
                _nextSnapshotRequestTime = Time.unscaledTime + SnapshotRequestInterval;
                DiagnosticLog.Verbose("WorldStoryObjectSync: requested host world snapshot for '" +
                                      sceneName + "'.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("WorldStoryObjectSync: snapshot request failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
                _nextSnapshotRequestTime = Time.unscaledTime + SnapshotRequestInterval;
            }
        }

        private void SendHostLoadingSnapshot(bool retransmit)
        {
            if (dispatcher == null || !MitaHost.IsHost) return;

            string sceneName = SceneManager.GetActiveScene().name;
            if (string.IsNullOrEmpty(sceneName) || sceneName != _loadingSnapshotScene)
                return;

            if (!retransmit || !_hostLoadingSnapshotSent ||
                string.IsNullOrEmpty(_activeLoadingSnapshotId))
            {
                List<WorldStoryObjectState> states;
                if (!TryBuildLoadingSnapshot(sceneName, out states))
                {
                    _nextHostSnapshotRetryTime = Time.unscaledTime + 0.5f;
                    return;
                }

                _activeLoadingSnapshotId = Guid.NewGuid().ToString("N");
                _expectedLoadingSnapshotCount = states.Count;
                _receivedLoadingSnapshotRecords.Clear();
                _appliedLoadingSnapshotRecords.Clear();
                _loadingSnapshotAcks.Clear();
                _loadingSnapshotAcks.Add(localPlayerId ?? string.Empty);
                _lastLoadingSnapshotStates.Clear();

                for (int i = 0; i < states.Count; i++)
                {
                    WorldStoryObjectState state = states[i];
                    state.isSnapshot = true;
                    state.snapshotId = _activeLoadingSnapshotId;
                    state.snapshotIndex = i;
                    state.snapshotCount = states.Count;
                    _lastLoadingSnapshotStates.Add(state);
                }
                _hostLoadingSnapshotSent = true;
                DiagnosticLog.Info("WorldStoryObjectSync: captured host loading snapshot '" +
                                   _activeLoadingSnapshotId + "' with " + states.Count +
                                   " hierarchy state(s) for '" + sceneName + "'.");
            }

            for (int i = 0; i < _lastLoadingSnapshotStates.Count; i++)
            {
                WorldStoryObjectState state = _lastLoadingSnapshotStates[i];
                dispatcher.SendStoryObjectState(state);
                lastKnownActive[state.path] = state.isActive;
            }

            dispatcher.SendCustom(LoadingSnapshotCompleteEvent,
                JsonSerializer.Serialize(new SnapshotComplete
                {
                    senderId = localPlayerId,
                    sceneName = sceneName,
                    snapshotId = _activeLoadingSnapshotId,
                    stateCount = _lastLoadingSnapshotStates.Count
                }, SnapshotJsonOptions));

            _nextHostSnapshotRetryTime = Time.unscaledTime + HostSnapshotRetryInterval;
            if (retransmit)
                DiagnosticLog.Verbose("WorldStoryObjectSync: retransmitted loading snapshot '" +
                                      _activeLoadingSnapshotId + "'.");
        }

        private bool TryBuildLoadingSnapshot(string sceneName,
                                             out List<WorldStoryObjectState> states)
        {
            states = new List<WorldStoryObjectState>();
            if (GameObject.Find("World") == null) return false;

            try
            {
                if (!_trackedCacheBuilt) RebuildTrackedCache();
                if (!_questCacheBuilt) RebuildQuestsCache();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("WorldStoryObjectSync: snapshot cache build failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
                return false;
            }

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < _cachedTrackedNodes.Count; i++)
                AddSnapshotState(sceneName, _cachedTrackedNodes[i].Key,
                                 _cachedTrackedNodes[i].Value, seen, states);
            for (int i = 0; i < _cachedQuestsNodes.Count; i++)
                AddSnapshotState(sceneName, _cachedQuestsNodes[i].Key,
                                 _cachedQuestsNodes[i].Value, seen, states);

            List<string> terminalPaths = new List<string>(_terminallyDisabledPaths);
            terminalPaths.Sort(StringComparer.Ordinal);
            for (int i = 0; i < terminalPaths.Count; i++)
            {
                string path = terminalPaths[i];
                if (IsLocalOnlyMultiplayerPath(path)) continue;
                if (!seen.Add(path)) continue;
                states.Add(new WorldStoryObjectState
                {
                    senderId = localPlayerId,
                    sceneName = sceneName,
                    path = path,
                    isActive = false,
                    isTerminalDisabled = true
                });
            }

            states.Sort((a, b) => string.CompareOrdinal(a.path, b.path));
            return true;
        }

        private void AddSnapshotState(string sceneName, string path, GameObject go,
                                      HashSet<string> seen,
                                      List<WorldStoryObjectState> states)
        {
            if (go == null || string.IsNullOrEmpty(path) ||
                IsLocalOnlyMultiplayerPath(path) || IsLocalOnlyInterfaceObject(go) ||
                MinigameSync.ShouldBypassWorldStorySync(path) || !seen.Add(path)) return;
            bool terminal = _terminallyDisabledPaths.Contains(path);
            states.Add(new WorldStoryObjectState
            {
                senderId = localPlayerId,
                sceneName = sceneName,
                path = path,
                isActive = terminal ? false : go.activeSelf,
                isTerminalDisabled = terminal,
                animators = IsWardrobeRootPath(path) || IsMitaFinalClickPath(path) ||
                            (IsScene5StartHorror() && IsScene5StreamRoot(path))
                    ? CaptureAnimatorStates(go)
                    : null,
                transforms = IsScene5BasementDoorRootPath(path)
                    ? CaptureHierarchyTransforms(go)
                    : null,
                audioSources = IsScene5BasementDoorRootPath(path)
                    ? CaptureAudioSources(go)
                    : null
            });
        }

        private void OnCustomMessage(string eventName, string json)
        {
            try
            {
                if (eventName == LoadingSnapshotRequestEvent)
                {
                    if (!MitaHost.IsHost) return;
                    SnapshotRequest request = JsonSerializer.Deserialize<SnapshotRequest>(
                        json, SnapshotJsonOptions);
                    if (request == null || string.IsNullOrWhiteSpace(request.playerId) ||
                        request.sceneName != SceneManager.GetActiveScene().name ||
                        request.sceneName != _loadingSnapshotScene || !_postLoadWorldReady)
                        return;

                    SendHostLoadingSnapshot(_hostLoadingSnapshotSent);
                    return;
                }

                if (eventName == LoadingSnapshotCompleteEvent)
                {
                    HandleLoadingSnapshotComplete(json);
                    return;
                }

                if (eventName == LoadingSnapshotAckEvent)
                {
                    HandleLoadingSnapshotAck(json);
                    return;
                }

                if (eventName == LoadingSnapshotReleaseEvent)
                {
                    HandleLoadingSnapshotRelease(json);
                    return;
                }

                if (eventName == Scene5BasementDoorEvent)
                {
                    ApplyRemoteBasementDoorEvent(json);
                    return;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("WorldStoryObjectSync: loading snapshot message '" +
                                      eventName + "' failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void BroadcastBasementDoorEvent(string path, int eventIndex)
        {
            if (dispatcher == null || !IsScene5StartHorror() ||
                !IsScene5BasementDoorPath(path) || !CanPublishBasementDoorState())
                return;

            dispatcher.SendCustom(Scene5BasementDoorEvent,
                JsonSerializer.Serialize(new BasementDoorEventMessage
                {
                    senderId = localPlayerId,
                    sceneName = SceneManager.GetActiveScene().name,
                    path = path,
                    eventIndex = eventIndex,
                    sequence = ++_scene5BasementDoorEventSequence
                }, SnapshotJsonOptions));
        }

        private void ApplyRemoteBasementDoorEvent(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || !IsScene5StartHorror()) return;
            BasementDoorEventMessage message = JsonSerializer.Deserialize<BasementDoorEventMessage>(
                json, SnapshotJsonOptions);
            if (message == null || string.IsNullOrWhiteSpace(message.senderId) ||
                message.senderId == localPlayerId ||
                message.sceneName != SceneManager.GetActiveScene().name ||
                !IsScene5BasementDoorPath(message.path) ||
                !CanAcceptBasementDoorStateFrom(message.senderId))
                return;

            long previousSequence;
            if (_lastBasementDoorEventSequenceBySender.TryGetValue(message.senderId,
                    out previousSequence) && message.sequence <= previousSequence)
                return;
            _lastBasementDoorEventSequenceBySender[message.senderId] = message.sequence;

            GameObject eventObject = FindByPath(message.path);
            Component eventsData = eventObject != null ? eventObject.GetComponent("Events_Data") : null;
            if (eventsData == null) return;

            try
            {
                System.Reflection.MethodInfo invoke = eventsData.GetType().GetMethod("EV",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(int) }, null);
                if (invoke == null) return;
                _applyingBasementDoorEventDepth++;
                invoke.Invoke(eventsData, new object[] { message.eventIndex });
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("WorldStoryObjectSync: failed to apply basement-door event " +
                                      message.eventIndex + " from '" + message.senderId + "': " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (_applyingBasementDoorEventDepth > 0)
                    _applyingBasementDoorEventDepth--;
            }
        }

        private void HandleLoadingSnapshotComplete(string json)
        {
            if (MitaHost.IsHost) return;
            SnapshotComplete complete = JsonSerializer.Deserialize<SnapshotComplete>(
                json, SnapshotJsonOptions);
            if (complete == null || string.IsNullOrWhiteSpace(complete.snapshotId) ||
                complete.senderId != MitaHost.HostPlayerId ||
                complete.sceneName != SceneManager.GetActiveScene().name ||
                complete.sceneName != _loadingSnapshotScene)
                return;

            if (_activeLoadingSnapshotId.Length == 0 && complete.stateCount == 0)
            {
                _activeLoadingSnapshotId = complete.snapshotId;
                _expectedLoadingSnapshotCount = 0;
            }

            if (_activeLoadingSnapshotId != complete.snapshotId ||
                _expectedLoadingSnapshotCount != complete.stateCount ||
                _receivedLoadingSnapshotRecords.Count != complete.stateCount ||
                _appliedLoadingSnapshotRecords.Count != complete.stateCount)
            {
                DiagnosticLog.Verbose("WorldStoryObjectSync: host snapshot completion arrived before " +
                                      "all hierarchy states were applied (received=" +
                                      _receivedLoadingSnapshotRecords.Count + "/" + complete.stateCount +
                                      ", applied=" + _appliedLoadingSnapshotRecords.Count + ").");
                _nextSnapshotRequestTime = Time.unscaledTime;
                return;
            }

            _loadingSnapshotCompleted = true;
            dispatcher.SendCustom(LoadingSnapshotAckEvent,
                JsonSerializer.Serialize(new SnapshotAck
                {
                    playerId = localPlayerId,
                    sceneName = complete.sceneName,
                    snapshotId = complete.snapshotId,
                    stateCount = complete.stateCount
                }, SnapshotJsonOptions));
            DiagnosticLog.Info("WorldStoryObjectSync: applied complete host hierarchy snapshot '" +
                               complete.snapshotId + "' (" + complete.stateCount + " state(s)); ACK sent.");
        }

        private void HandleLoadingSnapshotAck(string json)
        {
            if (!MitaHost.IsHost) return;
            SnapshotAck ack = JsonSerializer.Deserialize<SnapshotAck>(json, SnapshotJsonOptions);
            if (ack == null || string.IsNullOrWhiteSpace(ack.playerId) ||
                ack.playerId == localPlayerId ||
                ack.sceneName != SceneManager.GetActiveScene().name ||
                ack.snapshotId != _activeLoadingSnapshotId ||
                ack.stateCount != _expectedLoadingSnapshotCount)
                return;

            _loadingSnapshotAcks.Add(ack.playerId);
            DiagnosticLog.Info("WorldStoryObjectSync: loading snapshot ACK from '" + ack.playerId +
                               "' (" + _loadingSnapshotAcks.Count + "/" +
                               LoadingNetwork.ExpectedClientCount() + ").");
            if (_loadingSnapshotAcks.Count >= LoadingNetwork.ExpectedClientCount())
                ReleasePostLoadGate(true);
        }

        private void HandleLoadingSnapshotRelease(string json)
        {
            if (MitaHost.IsHost) return;
            SnapshotRelease release = JsonSerializer.Deserialize<SnapshotRelease>(
                json, SnapshotJsonOptions);
            if (release == null || release.senderId != MitaHost.HostPlayerId ||
                release.sceneName != SceneManager.GetActiveScene().name ||
                release.snapshotId != _activeLoadingSnapshotId ||
                !_loadingSnapshotCompleted)
                return;

            CompletePostLoadGate();
        }

        private void RecordLoadingSnapshotState(WorldStoryObjectState state, bool applied)
        {
            if (state == null || !state.isSnapshot ||
                string.IsNullOrEmpty(state.snapshotId) ||
                state.senderId != MitaHost.HostPlayerId ||
                state.sceneName != _loadingSnapshotScene)
                return;

            if (_activeLoadingSnapshotId != state.snapshotId)
            {
                _activeLoadingSnapshotId = state.snapshotId;
                _expectedLoadingSnapshotCount = Math.Max(0, state.snapshotCount);
                _receivedLoadingSnapshotRecords.Clear();
                _appliedLoadingSnapshotRecords.Clear();
                _loadingSnapshotCompleted = false;
            }

            if (state.snapshotCount != _expectedLoadingSnapshotCount ||
                state.snapshotIndex < 0 || state.snapshotIndex >= _expectedLoadingSnapshotCount ||
                string.IsNullOrEmpty(state.path))
                return;

            _receivedLoadingSnapshotRecords[state.snapshotIndex] = state.path;
            if (applied) _appliedLoadingSnapshotRecords.Add(state.snapshotIndex);
            else _appliedLoadingSnapshotRecords.Remove(state.snapshotIndex);
        }

        private void ReleasePostLoadGate(bool broadcast)
        {
            if (!_postLoadGateArmed) return;
            if (broadcast && MitaHost.IsHost && dispatcher != null && !_postLoadReleaseSent)
            {
                _postLoadReleaseSent = true;
                dispatcher.SendCustom(LoadingSnapshotReleaseEvent,
                    JsonSerializer.Serialize(new SnapshotRelease
                    {
                        senderId = localPlayerId,
                        sceneName = _loadingSnapshotScene,
                        snapshotId = _activeLoadingSnapshotId
                    }, SnapshotJsonOptions));
            }

            DiagnosticLog.Info("WorldStoryObjectSync: post-load snapshot barrier released for '" +
                               _loadingSnapshotScene + "'.");
            CompletePostLoadGate();
        }

        public void Tick()
        {
            if (dispatcher == null) return;

            string sceneName = SceneManager.GetActiveScene().name;
            _cachedActiveSceneName = sceneName ?? string.Empty;
            if (sceneName != lastSceneName)
            {
                lastSceneName = sceneName;
                syncedPaths.Clear();
                lastKnownActive.Clear();
                _cachedQuestsNodes.Clear();
                _questCacheBuilt = false;
                _trackedCacheBuilt = false;
                _cachedTrackedNodes.Clear();
                _cachedWardrobeRoots.Clear();
                _cachedBasementDoorFrame = null;
                _trackedPathByObjectId.Clear();
                _terminallyDisabledPaths.Clear();
                _lastTeleportByPath.Clear();
                _lastScene5SequenceBySender.Clear();
                _lastBasementDoorEventSequenceBySender.Clear();
                _lastSpecialStateFingerprintByPath.Clear();
                _nextSpecialStateHeartbeatByPath.Clear();
                _fullSnapshotPathsSent.Clear();
                _fullSnapshotTotalSentCount = 0;
                _fullSnapshotBudgetExceeded = false;
                _scene5QuestSequence = 0;
                _scene5BasementDoorEventSequence = 0;
                _nextScene5QuestSyncTime = 0f;
                _nextScene5BasementDoorSyncTime = 0f;
                _nextQuestsRecacheTime = 0f;
                _nextTrackedRecacheTime = 0f;
                nextFullResyncTime = 0f;
                _fullSnapshotPending = true;
                DiagnosticLog.Info("WorldStoryObjectSync: world-state snapshot queued for scene '" +
                                   sceneName + "'.");
            }

            TickPostLoadSnapshot();
            TickBedroomWardrobeAnimatorSync();
            TickMitaFinalClickAnimatorSync();
            TickScene5QuestSync();
            TickScene5BasementDoorSync();

            bool timeToSample = Time.unscaledTime >= nextSampleTime;
            bool sendFullSnapshot = _fullSnapshotPending ||
                                    Time.unscaledTime >= nextFullResyncTime;
            if (!timeToSample && !sendFullSnapshot) return;

            if (sendFullSnapshot && !_fullSnapshotPending)
            {
                _fullSnapshotPathsSent.Clear();
                _fullSnapshotTotalSentCount = 0;
            }
            if (timeToSample) nextSampleTime     = Time.unscaledTime + SampleInterval;
            if (sendFullSnapshot)
                nextFullResyncTime = Time.unscaledTime + FullResyncInterval;

            _fullSnapshotSentCount = 0;
            _fullSnapshotHadError = false;
            _fullSnapshotBudgetExceeded = false;
            _fullSnapshotPending = sendFullSnapshot;
            try
            {
                SyncTrackedFromCache();
                SyncQuestsFromCache();
                if (sendFullSnapshot && !_fullSnapshotBudgetExceeded)
                {
                    if (!IsMultiplayerSessionActive() || MitaHost.IsHost)
                        BroadcastTerminalDisabledStates();
                    DiagnosticLog.Info("WorldStoryObjectSync: full snapshot transmitted " +
                                       _fullSnapshotTotalSentCount + " state(s) in scene '" + sceneName +
                                       "'; object errors=" + _fullSnapshotHadError +
                                       "; next full resync in " + FullResyncInterval + "s.");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("WorldStoryObjectSync.Tick failed in scene '" + sceneName +
                                      "': " + ex.GetType().Name + ": " + ex.Message +
                                      ".");
            }
            finally
            {
                _fullSnapshotPending = sendFullSnapshot && _fullSnapshotBudgetExceeded;
            }
        }

        // ── Location*-registry feed ────────────────────────────────────────

        private void SyncTrackedFromCache()
        {
            if (!_trackedCacheBuilt ||
                Time.unscaledTime >= _nextTrackedRecacheTime)
            {
                try { RebuildTrackedCache(); }
                catch (Exception ex)
                {
                    if (_fullSnapshotPending) _fullSnapshotHadError = true;
                    DiagnosticLog.Warning("WorldStoryObjectSync: tracked-object cache rebuild failed: " +
                                          ex.GetType().Name + ": " + ex.Message);
                }
            }

            for (int i = 0; i < _cachedTrackedNodes.Count; i++)
            {
                if (_fullSnapshotPending && _fullSnapshotBudgetExceeded) break;
                var kv = _cachedTrackedNodes[i];
                if (kv.Value == null) continue;
                try { SyncObject(kv.Key, kv.Value); }
                catch (Exception ex)
                {
                    if (_fullSnapshotPending) _fullSnapshotHadError = true;
                    DiagnosticLog.Warning("WorldStoryObjectSync: tracked object sync failed for '" +
                                          kv.Key + "': " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        private void RebuildTrackedCache()
        {
            _nextTrackedRecacheTime = Time.unscaledTime + TrackedRecacheInterval;
            _cachedTrackedNodes.Clear();
            _cachedWardrobeRoots.Clear();
            _cachedBasementDoorFrame = null;
            HashSet<string> cachedPaths = new HashSet<string>(StringComparer.Ordinal);

            WorldStoryObjectRegistry.EnsureScannedForCurrentScene();

            foreach (KeyValuePair<string, GameObject> pair in WorldStoryObjectRegistry.AllTracked)
            {
                GameObject go = pair.Value;
                if (go == null || IsLocalOnlyMultiplayerPath(pair.Key) ||
                    IsLocalOnlyInterfaceObject(go)) continue;

                RegisterTrackedPath(pair.Key, go);
                if (SubtreeHasDialogue3DText(go)) continue;

                _cachedTrackedNodes.Add(new KeyValuePair<string, GameObject>(pair.Key, go));
                cachedPaths.Add(pair.Key);
            }

            AddBedroomWardrobeTree(cachedPaths);
            GameObject[] roots = SceneManager.GetActiveScene().GetRootGameObjects();
            for (int i = 0; roots != null && i < roots.Length; i++)
                ScanSpecialSyncTrees(roots[i] != null ? roots[i].transform : null, cachedPaths);
            _trackedCacheBuilt = true;
            DiagnosticLog.Info("WorldStoryObjectSync: cached " + _cachedTrackedNodes.Count +
                               " tracked Location*, wardrobe, and Scene 5 basement-door node(s).");
        }

        private void ScanSpecialSyncTrees(Transform node, HashSet<string> cachedPaths)
        {
            if (node == null) return;
            string path = LocalPlayerLocator.GetPath(node);
            if (IsLocalOnlyMultiplayerPath(path) || IsLocalOnlyInterfaceObject(node.gameObject)) return;
            if (string.IsNullOrEmpty(path) ||
                !path.StartsWith("World/House/", StringComparison.Ordinal))
            {
                for (int i = 0; i < node.childCount; i++)
                    ScanSpecialSyncTrees(node.GetChild(i), cachedPaths);
                return;
            }

            bool wardrobeRoot = IsWardrobeNodeName(node.name) && !HasWardrobeAncestor(node);
            bool basementDoorRoot = IsScene5StartHorror() && IsBasementDoorFrameCandidate(node);
            if (wardrobeRoot)
            {
                if (!_cachedWardrobeRoots.Contains(node.gameObject))
                    _cachedWardrobeRoots.Add(node.gameObject);
                AddBedroomWardrobeNode(node, cachedPaths);
                return;
            }
            if (basementDoorRoot)
            {
                _cachedBasementDoorFrame = node.gameObject;
                AddBedroomWardrobeNode(node, cachedPaths);
                return;
            }

            for (int i = 0; i < node.childCount; i++)
                ScanSpecialSyncTrees(node.GetChild(i), cachedPaths);
        }

        private static bool IsWardrobeNodeName(string name)
        {
            return !string.IsNullOrEmpty(name) &&
                   name.IndexOf("Wardrobe", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool HasWardrobeAncestor(Transform node)
        {
            for (Transform parent = node != null ? node.parent : null;
                 parent != null; parent = parent.parent)
            {
                if (string.Equals(parent.name, "World", StringComparison.OrdinalIgnoreCase))
                    break;
                if (IsWardrobeNodeName(parent.name)) return true;
            }
            return false;
        }

        private static bool IsBasementDoorFrameCandidate(Transform node)
        {
            if (node == null || !string.Equals(node.name, "BasementDoorFrame",
                    StringComparison.OrdinalIgnoreCase))
                return false;
            Transform door = node.Find("BasementDoor");
            if (door == null) return false;
            string path = LocalPlayerLocator.GetPath(node);
            return !string.IsNullOrEmpty(path) && path.StartsWith("World/House/",
                StringComparison.Ordinal);
        }

        private void AddBedroomWardrobeTree(HashSet<string> cachedPaths)
        {
            GameObject wardrobe = FindBedroomWardrobe();
            if (wardrobe == null) return;
            AddBedroomWardrobeNode(wardrobe.transform, cachedPaths);
        }

        private void AddBedroomWardrobeNode(Transform node, HashSet<string> cachedPaths)
        {
            if (node == null) return;
            string path = LocalPlayerLocator.GetPath(node);
            if (IsLocalOnlyMultiplayerPath(path) || IsLocalOnlyInterfaceObject(node.gameObject))
                return;
            if (!string.IsNullOrEmpty(path))
            {
                RegisterTrackedPath(path, node.gameObject);
                if (cachedPaths.Add(path))
                    _cachedTrackedNodes.Add(new KeyValuePair<string, GameObject>(path, node.gameObject));
            }

            for (int i = 0; i < node.childCount; i++)
                AddBedroomWardrobeNode(node.GetChild(i), cachedPaths);
        }

        private void TickBedroomWardrobeAnimatorSync()
        {
            if (!IsMultiplayerSessionActive() || !MitaHost.IsHost ||
                Time.unscaledTime < _nextWardrobeAnimatorSyncTime)
                return;

            _nextWardrobeAnimatorSyncTime =
                Time.unscaledTime + WardrobeAnimatorSyncInterval;
            if (!_trackedCacheBuilt || Time.unscaledTime >= _nextTrackedRecacheTime)
            {
                try { RebuildTrackedCache(); }
                catch (Exception ex)
                {
                    DiagnosticLog.Verbose("WorldStoryObjectSync: wardrobe cache refresh failed: " +
                                          ex.GetType().Name + ".");
                    return;
                }
            }

            string sceneName = SceneManager.GetActiveScene().name;
            for (int i = 0; i < _cachedWardrobeRoots.Count; i++)
            {
                GameObject wardrobe = _cachedWardrobeRoots[i];
                if (wardrobe == null) continue;
                string path = LocalPlayerLocator.GetPath(wardrobe.transform);
                WorldAnimatorState[] animators = CaptureAnimatorStates(wardrobe);
                if (string.IsNullOrEmpty(path) || animators == null || animators.Length == 0)
                    continue;

                WorldStoryObjectState state = new WorldStoryObjectState
                {
                    senderId = localPlayerId,
                    sceneName = sceneName,
                    path = path,
                    isActive = wardrobe.activeSelf,
                    animators = animators
                };
                if (ShouldSendSpecialState(state))
                    dispatcher.SendStoryObjectState(state);
            }
        }

        private void TickScene5BasementDoorSync()
        {
            if (!IsMultiplayerSessionActive() || !IsScene5StartHorror() ||
                Time.unscaledTime < _nextScene5BasementDoorSyncTime)
                return;

            _nextScene5BasementDoorSyncTime =
                Time.unscaledTime + Scene5BasementDoorSyncInterval;
            if (!_trackedCacheBuilt || Time.unscaledTime >= _nextTrackedRecacheTime)
            {
                try { RebuildTrackedCache(); }
                catch (Exception ex)
                {
                    DiagnosticLog.Verbose("WorldStoryObjectSync: basement-door cache refresh failed: " +
                                          ex.GetType().Name + ".");
                    return;
                }
            }

            GameObject frame = _cachedBasementDoorFrame ?? FindBasementDoorFrame();
            if (frame == null) return;
            string path = LocalPlayerLocator.GetPath(frame.transform);
            if (string.IsNullOrEmpty(path) || !CanPublishBasementDoorState()) return;

            WorldStoryObjectState state = new WorldStoryObjectState
            {
                senderId = localPlayerId,
                sceneName = _cachedActiveSceneName,
                path = path,
                isActive = frame.activeSelf,
                animators = CaptureAnimatorStates(frame),
                transforms = CaptureHierarchyTransforms(frame),
                audioSources = CaptureAudioSources(frame)
            };
            if (!ShouldSendSpecialState(state)) return;
            state.scene5Sequence = ++_scene5QuestSequence;
            dispatcher.SendStoryObjectState(state);
        }

        private void TickMitaFinalClickAnimatorSync()
        {
            if (!IsMultiplayerSessionActive() || !MitaHost.IsHost ||
                Time.unscaledTime < _nextMitaFinalClickAnimatorSyncTime)
                return;

            _nextMitaFinalClickAnimatorSyncTime =
                Time.unscaledTime + MitaFinalClickAnimatorSyncInterval;

            if (!_questCacheBuilt || Time.unscaledTime >= _nextQuestsRecacheTime)
            {
                try { RebuildQuestsCache(); }
                catch (Exception ex)
                {
                    DiagnosticLog.Verbose("WorldStoryObjectSync: AnimationMita cache refresh failed: " +
                                          ex.GetType().Name + ".");
                    return;
                }
            }

            string sceneName = SceneManager.GetActiveScene().name;
            for (int i = 0; i < _cachedQuestsNodes.Count; i++)
            {
                KeyValuePair<string, GameObject> pair = _cachedQuestsNodes[i];
                GameObject go = pair.Value;
                if (go == null || !IsMitaFinalClickPath(pair.Key) || !go.activeInHierarchy)
                    continue;

                WorldAnimatorState[] animators = CaptureAnimatorStates(go);
                if (animators == null || animators.Length == 0) continue;

                WorldStoryObjectState state = new WorldStoryObjectState
                {
                    senderId = localPlayerId,
                    sceneName = sceneName,
                    path = pair.Key,
                    isActive = go.activeSelf,
                    animators = animators
                };
                if (ShouldSendSpecialState(state))
                    dispatcher.SendStoryObjectState(state);
            }
        }

        private void TickScene5QuestSync()
        {
            if (!IsMultiplayerSessionActive() || !IsScene5StartHorror() ||
                Time.unscaledTime < _nextScene5QuestSyncTime)
                return;

            _nextScene5QuestSyncTime = Time.unscaledTime + Scene5QuestSyncInterval;
            if (!_questCacheBuilt || Time.unscaledTime >= _nextQuestsRecacheTime)
            {
                try { RebuildQuestsCache(); }
                catch (Exception ex)
                {
                    DiagnosticLog.Verbose("WorldStoryObjectSync: Scene 5 quest refresh failed: " +
                                          ex.GetType().Name + ".");
                    return;
                }
            }

            string sceneName = SceneManager.GetActiveScene().name;
            for (int i = 0; i < _cachedQuestsNodes.Count; i++)
            {
                KeyValuePair<string, GameObject> pair = _cachedQuestsNodes[i];
                GameObject root = pair.Value;
                if (root == null || !IsScene5StreamRoot(pair.Key) || !root.activeInHierarchy)
                    continue;

                WorldAnimatorState[] animators = CaptureAnimatorStates(root);
                WorldTransformState[] transforms = IsScene5MitaLegsPath(pair.Key)
                    ? CaptureMitaLegTransforms(root)
                    : null;
                if ((animators == null || animators.Length == 0) &&
                    (transforms == null || transforms.Length == 0))
                    continue;

                WorldStoryObjectState state = new WorldStoryObjectState
                {
                    senderId = localPlayerId,
                    sceneName = sceneName,
                    path = pair.Key,
                    isActive = root.activeSelf,
                    animators = animators,
                    transforms = transforms
                };
                if (!ShouldSendSpecialState(state)) continue;
                state.scene5Sequence = ++_scene5QuestSequence;
                dispatcher.SendStoryObjectState(state);
            }
        }

        // Special roots are sampled at 10 Hz so moving animation states stay
        // responsive. Avoid serializing and queuing the same static state on
        // every sample; emit a one-second heartbeat for recovery.
        private bool ShouldSendSpecialState(WorldStoryObjectState state)
        {
            if (state == null || string.IsNullOrEmpty(state.path)) return false;

            ulong fingerprint = ComputeSpecialStateFingerprint(state);
            ulong previous;
            float nextHeartbeat;
            bool hasPrevious = _lastSpecialStateFingerprintByPath.TryGetValue(state.path, out previous);
            bool heartbeatDue = !_nextSpecialStateHeartbeatByPath.TryGetValue(state.path, out nextHeartbeat) ||
                                Time.unscaledTime >= nextHeartbeat;
            if (hasPrevious && previous == fingerprint && !heartbeatDue) return false;

            _lastSpecialStateFingerprintByPath[state.path] = fingerprint;
            _nextSpecialStateHeartbeatByPath[state.path] =
                Time.unscaledTime + SpecialStateHeartbeatInterval;
            return true;
        }

        private static ulong ComputeSpecialStateFingerprint(WorldStoryObjectState state)
        {
            ulong hash = 14695981039346656037UL;
            AddFingerprint(ref hash, state.isActive);
            AddFingerprint(ref hash, state.animators);
            AddFingerprint(ref hash, state.transforms);
            AddFingerprint(ref hash, state.audioSources);
            return hash;
        }

        private static void AddFingerprint(ref ulong hash, WorldAnimatorState[] values)
        {
            AddFingerprint(ref hash, values != null ? values.Length : -1);
            if (values == null) return;
            for (int i = 0; i < values.Length; i++)
            {
                WorldAnimatorState value = values[i];
                if (value == null) { AddFingerprint(ref hash, -1); continue; }
                AddFingerprint(ref hash, value.animatorPath);
                AddFingerprint(ref hash, value.enabled);
                AddFingerprint(ref hash, value.speed);
                AddFingerprint(ref hash, value.layers != null ? value.layers.Length : -1);
                if (value.layers != null)
                    for (int j = 0; j < value.layers.Length; j++)
                    {
                        WorldAnimatorLayerState layer = value.layers[j];
                        if (layer == null) { AddFingerprint(ref hash, -1); continue; }
                        AddFingerprint(ref hash, layer.layerIndex);
                        AddFingerprint(ref hash, layer.stateHash);
                        AddFingerprint(ref hash, layer.normalizedTime);
                        AddFingerprint(ref hash, layer.weight);
                    }
                AddFingerprint(ref hash, value.parameters != null ? value.parameters.Length : -1);
                if (value.parameters != null)
                    for (int j = 0; j < value.parameters.Length; j++)
                    {
                        WorldAnimatorParameterState parameter = value.parameters[j];
                        if (parameter == null) { AddFingerprint(ref hash, -1); continue; }
                        AddFingerprint(ref hash, parameter.nameHash);
                        AddFingerprint(ref hash, parameter.type);
                        AddFingerprint(ref hash, parameter.boolValue);
                        AddFingerprint(ref hash, parameter.intValue);
                        AddFingerprint(ref hash, parameter.floatValue);
                    }
            }
        }

        private static void AddFingerprint(ref ulong hash, WorldTransformState[] values)
        {
            AddFingerprint(ref hash, values != null ? values.Length : -1);
            if (values == null) return;
            for (int i = 0; i < values.Length; i++)
            {
                WorldTransformState value = values[i];
                if (value == null) { AddFingerprint(ref hash, -1); continue; }
                AddFingerprint(ref hash, value.transformPath);
                AddFingerprint(ref hash, value.position.x);
                AddFingerprint(ref hash, value.position.y);
                AddFingerprint(ref hash, value.position.z);
                AddFingerprint(ref hash, value.rotation.x);
                AddFingerprint(ref hash, value.rotation.y);
                AddFingerprint(ref hash, value.rotation.z);
                AddFingerprint(ref hash, value.rotation.w);
                AddFingerprint(ref hash, value.localScale.x);
                AddFingerprint(ref hash, value.localScale.y);
                AddFingerprint(ref hash, value.localScale.z);
            }
        }

        private static void AddFingerprint(ref ulong hash, WorldAudioSourceState[] values)
        {
            AddFingerprint(ref hash, values != null ? values.Length : -1);
            if (values == null) return;
            for (int i = 0; i < values.Length; i++)
            {
                WorldAudioSourceState value = values[i];
                if (value == null) { AddFingerprint(ref hash, -1); continue; }
                AddFingerprint(ref hash, value.audioSourcePath);
                AddFingerprint(ref hash, value.clipName);
                AddFingerprint(ref hash, value.enabled);
                AddFingerprint(ref hash, value.isPlaying);
                AddFingerprint(ref hash, value.loop);
                AddFingerprint(ref hash, value.mute);
                AddFingerprint(ref hash, value.time);
                AddFingerprint(ref hash, value.volume);
                AddFingerprint(ref hash, value.pitch);
            }
        }

        private static void AddFingerprint(ref ulong hash, int value)
        {
            unchecked
            {
                hash ^= (uint)value;
                hash *= 1099511628211UL;
            }
        }

        private static void AddFingerprint(ref ulong hash, bool value)
        {
            AddFingerprint(ref hash, value ? 1 : 0);
        }

        private static void AddFingerprint(ref ulong hash, float value)
        {
            AddFingerprint(ref hash, BitConverter.SingleToInt32Bits(value));
        }

        private static void AddFingerprint(ref ulong hash, string value)
        {
            if (value == null)
            {
                AddFingerprint(ref hash, -1);
                return;
            }
            AddFingerprint(ref hash, value.Length);
            unchecked
            {
                for (int i = 0; i < value.Length; i++)
                {
                    hash ^= value[i];
                    hash *= 1099511628211UL;
                }
            }
        }

        private static WorldTransformState[] CaptureMitaLegTransforms(GameObject root)
        {
            if (root == null) return null;
            List<WorldTransformState> result = new List<WorldTransformState>();
            List<Transform> pending = new List<Transform> { root.transform };
            while (pending.Count > 0)
            {
                int last = pending.Count - 1;
                Transform current = pending[last];
                pending.RemoveAt(last);
                if (current == null) continue;

                bool hasPositionDriver = current == root.transform;
                if (!hasPositionDriver)
                {
                    try
                    {
                        Component[] components = current.GetComponents<Component>();
                        for (int i = 0; components != null && i < components.Length; i++)
                        {
                            Component component = components[i];
                            if (component != null &&
                                string.Equals(component.GetType().Name, "Transform_Position",
                                              StringComparison.Ordinal))
                            {
                                hasPositionDriver = true;
                                break;
                            }
                        }
                    }
                    catch { }
                }

                if (hasPositionDriver)
                {
                    string relativePath = GetRelativePath(root.transform, current);
                    if (relativePath != null)
                    {
                        result.Add(new WorldTransformState
                        {
                            transformPath = relativePath,
                            position = NetVector3.FromUnity(current.position),
                            rotation = NetQuaternion.FromUnity(current.rotation),
                            localScale = NetVector3.FromUnity(current.localScale)
                        });
                    }
                }

                for (int child = 0; child < current.childCount; child++)
                    pending.Add(current.GetChild(child));
            }
            return result.ToArray();
        }

        private static WorldTransformState[] CaptureHierarchyTransforms(GameObject root)
        {
            if (root == null) return null;
            List<WorldTransformState> result = new List<WorldTransformState>();
            List<Transform> pending = new List<Transform> { root.transform };
            while (pending.Count > 0)
            {
                int last = pending.Count - 1;
                Transform current = pending[last];
                pending.RemoveAt(last);
                if (current == null) continue;

                string relativePath = GetRelativePath(root.transform, current);
                if (relativePath != null)
                {
                    result.Add(new WorldTransformState
                    {
                        transformPath = relativePath,
                        position = NetVector3.FromUnity(current.position),
                        rotation = NetQuaternion.FromUnity(current.rotation),
                        localScale = NetVector3.FromUnity(current.localScale)
                    });
                }

                for (int child = 0; child < current.childCount; child++)
                    pending.Add(current.GetChild(child));
            }
            return result.ToArray();
        }

        private static WorldAudioSourceState[] CaptureAudioSources(GameObject root)
        {
            if (root == null) return null;
            AudioSource[] sources;
            try { sources = root.GetComponentsInChildren<AudioSource>(true); }
            catch { return null; }
            if (sources == null || sources.Length == 0) return null;

            List<WorldAudioSourceState> result = new List<WorldAudioSourceState>(sources.Length);
            for (int i = 0; i < sources.Length; i++)
            {
                AudioSource source = sources[i];
                if (source == null) continue;
                string relativePath = GetRelativePath(root.transform, source.transform);
                if (relativePath == null) continue;
                try
                {
                    result.Add(new WorldAudioSourceState
                    {
                        audioSourcePath = relativePath,
                        clipName = source.clip != null ? source.clip.name : string.Empty,
                        enabled = source.enabled,
                        isPlaying = source.isPlaying,
                        loop = source.loop,
                        mute = source.mute,
                        time = source.time,
                        volume = source.volume,
                        pitch = source.pitch
                    });
                }
                catch { }
            }
            return result.ToArray();
        }

        private static bool IsScene5StreamRoot(string path)
        {
            if (IsScene5BasementDoorRootPath(path)) return true;
            if (string.IsNullOrEmpty(path) ||
                !path.StartsWith("World/Quests/", StringComparison.Ordinal))
                return false;

            string lower = path.ToLowerInvariant();
            string lastSegment = lower.Substring(lower.LastIndexOf('/') + 1);
            bool quest1 = lower.Contains("quest 1") && lastSegment.Contains("starthorror");
            bool quest2 = lower.Contains("quest 2") &&
                          (lastSegment.Contains("toilet") || lastSegment.Contains("kitchen"));
            bool quest3Sit = lower.Contains("quest 3") && lower.Contains("findme") &&
                             lastSegment.Replace(" ", "").Contains("sitangle");
            bool quest4 = lower.Contains("quest 4") && lastSegment.Contains("keytake");
            return quest1 || quest2 || quest3Sit || quest4 ||
                   IsScene5MitaLegsPath(path);
        }

        private static bool IsScene5StreamedQuestPath(string path)
        {
            if (string.IsNullOrEmpty(path) ||
                !path.StartsWith("World/Quests/", StringComparison.Ordinal))
                return false;

            string lower = path.ToLowerInvariant();
            string compact = lower.Replace(" ", "");
            return (lower.Contains("quest 1") && lower.Contains("starthorror")) ||
                   (lower.Contains("quest 2") &&
                    (lower.Contains("toilet") || lower.Contains("kitchen"))) ||
                   (lower.Contains("quest 3") && lower.Contains("findme") &&
                    compact.Contains("sitangle")) ||
                   (lower.Contains("quest 4") && lower.Contains("keytake"));
        }

        private static bool IsScene5MitaLegsPath(string path)
        {
            if (string.IsNullOrEmpty(path) ||
                !path.StartsWith("World/Quests/", StringComparison.Ordinal))
                return false;
            return path.Substring(path.LastIndexOf('/') + 1)
                       .Replace(" ", "")
                       .IndexOf("MitaLegs", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsScene5StartHorror()
        {
            return string.Equals(_cachedActiveSceneName,
                                 "Scene 5 - StartHorror", StringComparison.Ordinal);
        }

        private static bool IsScene5BasementDoorPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string normalized = path.Replace('\\', '/');
            if (!normalized.StartsWith("World/House/", StringComparison.OrdinalIgnoreCase))
                return false;
            return normalized.IndexOf("/BasementDoorFrame", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsScene5BasementDoorRootPath(string path)
        {
            if (!IsScene5BasementDoorPath(path)) return false;
            string normalized = path.Replace('\\', '/');
            int separator = normalized.LastIndexOf('/');
            string leaf = separator >= 0 ? normalized.Substring(separator + 1) : normalized;
            return string.Equals(leaf, "BasementDoorFrame", StringComparison.OrdinalIgnoreCase);
        }

        private bool CanPublishBasementDoorState()
        {
            string ownerId = InventorySharingController.Scene5BasementKeyOwnerId;
            return string.IsNullOrEmpty(ownerId)
                ? MitaHost.IsHost
                : string.Equals(ownerId, localPlayerId, StringComparison.Ordinal);
        }

        private static bool CanAcceptBasementDoorStateFrom(string senderId)
        {
            if (string.IsNullOrWhiteSpace(senderId)) return false;
            string ownerId = InventorySharingController.Scene5BasementKeyOwnerId;
            return string.IsNullOrEmpty(ownerId)
                ? string.Equals(senderId, MitaHost.HostPlayerId, StringComparison.Ordinal)
                : string.Equals(senderId, ownerId, StringComparison.Ordinal);
        }

        private static bool IsWardrobeRootPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string normalized = path.Replace('\\', '/');
            if (!normalized.StartsWith("World/House/", StringComparison.OrdinalIgnoreCase))
                return false;
            int separator = normalized.LastIndexOf('/');
            string leaf = separator >= 0 ? normalized.Substring(separator + 1) : normalized;
            return IsWardrobeNodeName(leaf);
        }

        private static bool IsWardrobeSyncPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string normalized = path.Replace('\\', '/');
            return normalized.StartsWith("World/House/", StringComparison.OrdinalIgnoreCase) &&
                   normalized.IndexOf("Wardrobe", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static GameObject FindBasementDoorFrame()
        {
            for (int i = 0; i < BasementDoorFramePathCandidates.Length; i++)
            {
                GameObject frame = FindByPath(BasementDoorFramePathCandidates[i]);
                if (frame != null) return frame;
            }

            GameObject[] roots = SceneManager.GetActiveScene().GetRootGameObjects();
            List<Transform> pending = new List<Transform>();
            for (int i = 0; roots != null && i < roots.Length; i++)
                if (roots[i] != null) pending.Add(roots[i].transform);
            while (pending.Count > 0)
            {
                int last = pending.Count - 1;
                Transform current = pending[last];
                pending.RemoveAt(last);
                if (IsBasementDoorFrameCandidate(current)) return current.gameObject;
                for (int i = 0; current != null && i < current.childCount; i++)
                    pending.Add(current.GetChild(i));
            }
            return null;
        }

        private WorldAnimatorState[] CaptureAnimatorStates(GameObject root)
        {
            if (root == null) return null;
            Animator[] animators;
            try { animators = root.GetComponentsInChildren<Animator>(true); }
            catch { return null; }
            if (animators == null || animators.Length == 0) return null;

            List<WorldAnimatorState> captured = new List<WorldAnimatorState>(animators.Length);
            for (int i = 0; i < animators.Length; i++)
            {
                Animator animator = animators[i];
                if (animator == null) continue;

                string relativePath = GetRelativePath(root.transform, animator.transform);
                if (relativePath == null) continue;

                try
                {
                    int layerCount = Mathf.Min(animator.layerCount, 16);
                    WorldAnimatorLayerState[] layers = new WorldAnimatorLayerState[layerCount];
                    for (int layer = 0; layer < layerCount; layer++)
                    {
                        AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(layer);
                        float normalizedTime = info.normalizedTime;
                        if (float.IsNaN(normalizedTime) || float.IsInfinity(normalizedTime))
                            normalizedTime = 0f;

                        layers[layer] = new WorldAnimatorLayerState
                        {
                            layerIndex = layer,
                            stateHash = info.fullPathHash,
                            normalizedTime = normalizedTime,
                            weight = animator.GetLayerWeight(layer)
                        };
                    }

                    List<WorldAnimatorParameterState> parameters =
                        new List<WorldAnimatorParameterState>(animator.parameterCount);
                    for (int p = 0; p < animator.parameterCount; p++)
                    {
                        AnimatorControllerParameter parameter = animator.GetParameter(p);
                        if (parameter == null) continue;

                        WorldAnimatorParameterState value = new WorldAnimatorParameterState
                        {
                            nameHash = parameter.nameHash,
                            type = (int)parameter.type
                        };
                        switch (parameter.type)
                        {
                            case AnimatorControllerParameterType.Bool:
                                value.boolValue = animator.GetBool(parameter.nameHash);
                                break;
                            case AnimatorControllerParameterType.Int:
                                value.intValue = animator.GetInteger(parameter.nameHash);
                                break;
                            case AnimatorControllerParameterType.Float:
                                value.floatValue = animator.GetFloat(parameter.nameHash);
                                break;
                            default:
                                continue; // Trigger state is represented by the resulting layer state.
                        }
                        parameters.Add(value);
                    }

                    captured.Add(new WorldAnimatorState
                    {
                        animatorPath = relativePath,
                        enabled = animator.enabled,
                        speed = animator.speed,
                        layers = layers,
                        parameters = parameters.ToArray()
                    });
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Verbose("WorldStoryObjectSync: could not sample Animator '" +
                                          relativePath + "': " + ex.GetType().Name + ".");
                }
            }

            return captured.ToArray();
        }

        private static string GetRelativePath(Transform root, Transform child)
        {
            if (root == null || child == null) return null;
            if (root == child) return string.Empty;

            List<string> names = new List<string>();
            Transform current = child;
            while (current != null && current != root)
            {
                names.Add(current.name);
                current = current.parent;
            }
            if (current != root) return null;
            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        private static bool ApplyAnimatorStates(GameObject root,
                                                WorldAnimatorState[] states)
        {
            if (states == null || states.Length == 0) return true;
            if (root == null) return false;

            bool allApplied = true;
            for (int i = 0; i < states.Length; i++)
            {
                WorldAnimatorState state = states[i];
                if (state == null) continue;

                Transform target = string.IsNullOrEmpty(state.animatorPath)
                    ? root.transform
                    : root.transform.Find(state.animatorPath);
                Animator animator = target != null ? target.GetComponent<Animator>() : null;
                if (animator == null)
                {
                    allApplied = false;
                    continue;
                }

                try
                {
                    animator.speed = state.speed;
                    if (state.parameters != null)
                    {
                        for (int p = 0; p < state.parameters.Length; p++)
                        {
                            WorldAnimatorParameterState parameter = state.parameters[p];
                            if (parameter == null) continue;
                            switch ((AnimatorControllerParameterType)parameter.type)
                            {
                                case AnimatorControllerParameterType.Bool:
                                    animator.SetBool(parameter.nameHash, parameter.boolValue);
                                    break;
                                case AnimatorControllerParameterType.Int:
                                    animator.SetInteger(parameter.nameHash, parameter.intValue);
                                    break;
                                case AnimatorControllerParameterType.Float:
                                    animator.SetFloat(parameter.nameHash, parameter.floatValue);
                                    break;
                            }
                        }
                    }

                    if (state.layers != null)
                    {
                        for (int layer = 0; layer < state.layers.Length; layer++)
                        {
                            WorldAnimatorLayerState layerState = state.layers[layer];
                            if (layerState == null || layerState.layerIndex < 0 ||
                                layerState.layerIndex >= animator.layerCount)
                                continue;

                            animator.SetLayerWeight(layerState.layerIndex, layerState.weight);
                            if (layerState.stateHash == 0) continue;

                            AnimatorStateInfo current =
                                animator.GetCurrentAnimatorStateInfo(layerState.layerIndex);
                            if (current.fullPathHash != layerState.stateHash)
                            {
                                animator.CrossFadeInFixedTime(
                                    layerState.stateHash, 0.08f, layerState.layerIndex,
                                    layerState.normalizedTime);
                            }
                            else
                            {
                                float drift = Mathf.Abs(current.normalizedTime - layerState.normalizedTime);
                                if (current.loop)
                                {
                                    float currentPhase = Mathf.Repeat(current.normalizedTime, 1f);
                                    float targetPhase = Mathf.Repeat(layerState.normalizedTime, 1f);
                                    drift = Mathf.Abs(currentPhase - targetPhase);
                                    drift = Mathf.Min(drift, 1f - drift);
                                }

                                if (drift > 0.2f)
                                    animator.Play(layerState.stateHash, layerState.layerIndex,
                                                  layerState.normalizedTime);
                            }
                        }
                    }

                    animator.enabled = state.enabled;
                }
                catch (Exception ex)
                {
                    allApplied = false;
                    DiagnosticLog.Verbose("WorldStoryObjectSync: could not apply Animator '" +
                                          state.animatorPath + "': " + ex.GetType().Name + ".");
                }
            }

            return allApplied;
        }

        // ── Quests subtree feed ────────────────────────────────────────────

        private void SyncQuestsFromCache()
        {
            if (_fullSnapshotPending && _fullSnapshotBudgetExceeded) return;
            if (!_questCacheBuilt ||
                Time.unscaledTime >= _nextQuestsRecacheTime)
            {
                try { RebuildQuestsCache(); }
                catch (Exception ex)
                {
                    if (_fullSnapshotPending) _fullSnapshotHadError = true;
                    DiagnosticLog.Warning("WorldStoryObjectSync: quest cache rebuild failed: " +
                                          ex.GetType().Name + ": " + ex.Message);
                }
            }

            for (int i = 0; i < _cachedQuestsNodes.Count; i++)
            {
                if (_fullSnapshotPending && _fullSnapshotBudgetExceeded) break;
                var kv = _cachedQuestsNodes[i];
                if (kv.Value == null) continue;
                try { SyncObject(kv.Key, kv.Value); }
                catch (Exception ex)
                {
                    if (_fullSnapshotPending) _fullSnapshotHadError = true;
                    DiagnosticLog.Warning("WorldStoryObjectSync: quest object sync failed for '" +
                                          kv.Key + "': " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        private void RebuildQuestsCache()
        {
            _nextQuestsRecacheTime = Time.unscaledTime +
                (IsScene5StartHorror() ? Scene5QuestRecacheInterval : QuestsRecacheInterval);
            _cachedQuestsNodes.Clear();
            _questCacheBuilt = true;

            GameObject worldGO = GameObject.Find("World");
            if (worldGO == null)
            {
                WarnMissingQuestTree("World root GameObject was not found.");
                return;
            }

            Transform node = worldGO.transform.Find("Quests");
            if (node == null)
            {
                WarnMissingQuestTree("World/Quests was not found.");
                return;
            }

            CollectQuestsSubtree(node, _cachedQuestsNodes);

            DiagnosticLog.Info("WorldStoryObjectSync: cached " + _cachedQuestsNodes.Count +
                               " Quests node(s) including dialogue hierarchy state.");
        }

        private void WarnMissingQuestTree(string reason)
        {
            if (Time.unscaledTime < _nextMissingQuestTreeLogTime) return;
            _nextMissingQuestTreeLogTime = Time.unscaledTime + 5f;
            DiagnosticLog.Warning("WorldStoryObjectSync: cannot snapshot quest objects: " + reason);
        }

        private void CollectQuestsSubtree(Transform node,
                                          List<KeyValuePair<string, GameObject>> output)
        {
            if (node == null) return;

            string path = LocalPlayerLocator.GetPath(node);
            if (IsLocalOnlyMultiplayerPath(path) || IsLocalOnlyInterfaceObject(node.gameObject))
                return;
            if (!string.IsNullOrEmpty(path)) RegisterTrackedPath(path, node.gameObject);
            output.Add(new KeyValuePair<string, GameObject>(path, node.gameObject));

            for (int i = 0; i < node.childCount; i++)
                CollectQuestsSubtree(node.GetChild(i), output);
        }

        // ── Dialogue detection ─────────────────────────────────────────────

        private static bool SubtreeHasDialogue3DText(GameObject go)
        {
            if (go == null) return false;
            return SubtreeHasDialogue3DTextRecursive(go.transform);
        }

        private static bool SubtreeHasDialogue3DTextRecursive(Transform t)
        {
            if (t == null) return false;

            if (NodeHasDialogue3DText(t.gameObject)) return true;

            int childCount = t.childCount;
            for (int i = 0; i < childCount; i++)
            {
                Transform child = t.GetChild(i);
                if (child == null) continue;
                if (SubtreeHasDialogue3DTextRecursive(child)) return true;
            }

            return false;
        }

        private static bool NodeHasDialogue3DText(GameObject go)
        {
            if (go == null) return false;

            Component[] comps;
            try { comps = go.GetComponents<Component>(); }
            catch { return false; }
            if (comps == null) return false;

            for (int i = 0; i < comps.Length; i++)
            {
                Component c = comps[i];
                if (c == null) continue;
                if (c.GetType().Name == "Dialogue_3DText")
                    return true;
            }

            return false;
        }

        // ── Sync ───────────────────────────────────────────────────────────

        private void SyncObject(string path, GameObject go)
        {
            if (go == null || IsLocalOnlyMultiplayerPath(path) ||
                IsLocalOnlyInterfaceObject(go) ||
                MinigameSync.ShouldBypassWorldStorySync(path)) return;

            if (_fullSnapshotPending)
            {
                if (_fullSnapshotBudgetExceeded) return;
                if (_fullSnapshotPathsSent.Contains(path)) return;
                if (_fullSnapshotSentCount >= MaxFullSnapshotStatesPerFrame)
                {
                    _fullSnapshotBudgetExceeded = true;
                    return;
                }
            }

            if (_terminallyDisabledPaths.Contains(path))
            {
                if (go.activeSelf) SetActiveFromRemote(go, false);
                lastKnownActive[path] = false;
                return;
            }

            bool currentActive = go.activeSelf;

            bool known;
            bool hasKnown = lastKnownActive.TryGetValue(path, out known);
            bool changed  = !hasKnown || known != currentActive;

            if (!changed && !_fullSnapshotPending) return;

            bool localTransition = hasKnown && known != currentActive;

            if (IsScene5BasementDoorPath(path) && IsMultiplayerSessionActive() &&
                !CanPublishBasementDoorState())
            {
                lastKnownActive[path] = currentActive;
                return;
            }

            // Initial and periodic baselines come from the host. A client
            // learns its local defaults here but does not push them over the
            // host's authoritative loading snapshot.
            if (IsMultiplayerSessionActive() && !MitaHost.IsHost && !localTransition)
            {
                lastKnownActive[path] = currentActive;
                return;
            }

            // A real active -> inactive transition is terminal for synchronized
            // story objects. The Harmony hook normally captures it immediately;
            // this sampling path is the fallback for engine-side state changes.
            if (localTransition && !currentActive && IsMultiplayerSessionActive())
            {
                if (ExplicitKillPaths.Contains(path))
                    MarkTerminallyDisabled(path, go, true, "sampled inactive transition");
                else
                    BroadcastStoryObjectState(path, go, false, false);
                return;
            }

            Transform localPlayer = LocalPlayerLocator.FindHardcodedPlayerPath();
            NetVector3 senderPos = new NetVector3();
            if (localPlayer != null)
                senderPos = NetVector3.FromUnity(localPlayer.position);

            WorldStoryObjectState msg = new WorldStoryObjectState();
            msg.senderId       = localPlayerId;
            msg.sceneName      = lastSceneName;
            msg.path           = path;
            msg.isActive       = currentActive;
            msg.isTerminalDisabled = _terminallyDisabledPaths.Contains(path);
            msg.senderPosition = senderPos;
            if (IsWardrobeRootPath(path) || IsMitaFinalClickPath(path) ||
                (IsScene5StartHorror() && IsScene5StreamRoot(path)))
                msg.animators = CaptureAnimatorStates(go);
            if (IsScene5BasementDoorRootPath(path))
            {
                msg.transforms = CaptureHierarchyTransforms(go);
                msg.audioSources = CaptureAudioSources(go);
            }
            dispatcher.SendStoryObjectState(msg);
            lastKnownActive[path] = currentActive;
            if (_fullSnapshotPending)
            {
                _fullSnapshotSentCount++;
                _fullSnapshotTotalSentCount++;
                _fullSnapshotPathsSent.Add(path);
            }

            if (localTransition && sceneNameIsStartSecret())
                DiagnosticLog.Info("[WORLD-STORY-TX] scene='" + lastSceneName + "' path='" + path +
                                   "' activeSelf=" + known + "->" + currentActive +
                                   " activeInHierarchy=" + go.activeInHierarchy + ".");

            if (!loggedFirstSend)
            {
                loggedFirstSend = true;
                DiagnosticLog.Info(
                    "WorldStoryObjectSync: first send '" + path + "' = " + currentActive);
            }
        }

        private void OnRemoteStoryObjectState(WorldStoryObjectState state)
        {
            if (state == null || string.IsNullOrEmpty(state.path)) return;
            if (!string.IsNullOrEmpty(localPlayerId) && state.senderId == localPlayerId) return;
            if (state.sceneName != SceneManager.GetActiveScene().name) return;
            if (state.isSnapshot && (MitaHost.IsHost || state.senderId != MitaHost.HostPlayerId))
                return;
            if (IsScene5BasementDoorPath(state.path) &&
                !CanAcceptBasementDoorStateFrom(state.senderId))
                return;

            if (state.scene5Sequence > 0)
            {
                if (!IsScene5StartHorror() || !IsScene5StreamRoot(state.path)) return;
                long previousSequence;
                if (_lastScene5SequenceBySender.TryGetValue(state.senderId, out previousSequence) &&
                    state.scene5Sequence <= previousSequence)
                    return;
                _lastScene5SequenceBySender[state.senderId] = state.scene5Sequence;
            }

            // Count a host snapshot record as handled so older peers that
            // included a UI object cannot hold the loading barrier open.
            if (IsLocalOnlyMultiplayerPath(state.path))
            {
                RecordLoadingSnapshotState(state, true);
                return;
            }
            if (MinigameSync.ShouldBypassWorldStorySync(state.path)) return;
            if (!MinigameSync.ShouldAcceptRemoteWorldStoryState(state.path, state.senderId))
            {
                RecordLoadingSnapshotState(state, true);
                return;
            }

            try
            {
                if (_terminallyDisabledPaths.Contains(state.path))
                {
                    RecordLoadingSnapshotState(state, true);
                    return;
                }

                // Older builds may mark any deactivation as terminal. Keep
                // terminal semantics restricted to the two Scene 4 loading
                // objects that genuinely need to be retired on every peer.
                if (state.isTerminalDisabled && ExplicitKillPaths.Contains(state.path) &&
                    IsMultiplayerSessionActive())
                {
                    GameObject terminalGo = FindByPath(state.path);
                    MarkTerminallyDisabled(state.path, terminalGo, false, "remote terminal disable");
                    if (terminalGo != null && terminalGo.activeSelf)
                        SetActiveFromRemote(terminalGo, false);
                    lastKnownActive[state.path] = false;
                    RecordLoadingSnapshotState(state, true);
                    return;
                }

                GameObject go = FindByPath(state.path);
                if (go == null)
                {
                    RecordLoadingSnapshotState(state, false);
                    return;
                }

                if (go.activeSelf == state.isActive)
                {
                    lastKnownActive[state.path] = state.isActive;
                    bool animationApplied = ApplyRemoteAnimatorState(state, go);
                    bool transformsApplied = ApplyRemoteTransformState(state, go);
                    ApplyRemoteAudioState(state, go);
                    RecordLoadingSnapshotState(state, animationApplied && transformsApplied);
                    return;
                }

                bool beforeSelf = go.activeSelf;
                bool beforeHierarchy = go.activeInHierarchy;
                SetActiveFromRemote(go, state.isActive);
                bool afterSelf = go.activeSelf;
                bool afterHierarchy = go.activeInHierarchy;
                lastKnownActive[state.path] = state.isActive;
                bool animationsApplied = ApplyRemoteAnimatorState(state, go);
                bool transformsAppliedAfterActivation = ApplyRemoteTransformState(state, go);
                ApplyRemoteAudioState(state, go);
                RecordLoadingSnapshotState(state,
                    afterSelf == state.isActive && animationsApplied &&
                    transformsAppliedAfterActivation);
                DiagnosticLog.Verbose("WorldStoryObjectSync: applied remote state '" + state.path +
                                      "' active=" + state.isActive + ".");

                if (string.Equals(state.sceneName, "Scene 4 - StartSecret", StringComparison.Ordinal) ||
                    string.Equals(SceneManager.GetActiveScene().name, "Scene 4 - StartSecret", StringComparison.Ordinal))
                    DiagnosticLog.Info("[WORLD-STORY-APPLY] sender='" + state.senderId + "' scene='" +
                                       state.sceneName + "' path='" + state.path + "' resolved='" +
                                       MitaBoneSync.GetNetworkPath(go.transform) + "' activeSelf=" +
                                       beforeSelf + "->" + afterSelf + " activeInHierarchy=" +
                                       beforeHierarchy + "->" + afterHierarchy + " requested=" +
                                       state.isActive + ".");

                if (state.isActive && ShouldTeleportForPath(state.path))
                    TeleportLocalPlayerToSender(state);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning(
                    "WorldStoryObjectSync: failed to apply remote state '" + state.path + "': " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        private bool ApplyRemoteAnimatorState(WorldStoryObjectState state, GameObject go)
        {
            if (state == null || state.animators == null || state.animators.Length == 0)
                return true;

            bool scene5Stream = IsScene5StartHorror() && IsScene5StreamRoot(state.path);
            if (!scene5Stream && state.senderId != MitaHost.HostPlayerId)
                return true;
            if (!IsWardrobeRootPath(state.path) && !IsMitaFinalClickPath(state.path) &&
                !scene5Stream)
                return true;
            return ApplyAnimatorStates(go, state.animators);
        }

        private bool ApplyRemoteTransformState(WorldStoryObjectState state, GameObject go)
        {
            if (state == null || state.transforms == null || state.transforms.Length == 0 ||
                !IsScene5StartHorror() ||
                (!IsScene5MitaLegsPath(state.path) &&
                 !IsScene5BasementDoorRootPath(state.path)))
                return true;
            if (go == null) return false;

            bool allApplied = true;
            for (int i = 0; i < state.transforms.Length; i++)
            {
                WorldTransformState pose = state.transforms[i];
                if (pose == null) continue;
                Transform target = string.IsNullOrEmpty(pose.transformPath)
                    ? go.transform
                    : go.transform.Find(pose.transformPath);
                if (target == null)
                {
                    allApplied = false;
                    continue;
                }

                Vector3 position = pose.position.ToUnity();
                Quaternion rotation = pose.rotation.ToUnity();
                Vector3 scale = pose.localScale.ToUnity();
                if (!IsFinite(position) || !IsFinite(rotation) || !IsFinite(scale))
                {
                    allApplied = false;
                    continue;
                }

                try
                {
                    target.SetPositionAndRotation(position, rotation);
                    target.localScale = scale;
                }
                catch
                {
                    allApplied = false;
                }
            }
            return allApplied;
        }

        private bool ApplyRemoteAudioState(WorldStoryObjectState state, GameObject go)
        {
            if (state == null || state.audioSources == null || state.audioSources.Length == 0 ||
                !IsScene5StartHorror() || !IsScene5BasementDoorRootPath(state.path))
                return true;
            if (go == null) return false;

            bool allApplied = true;
            for (int i = 0; i < state.audioSources.Length; i++)
            {
                WorldAudioSourceState audioState = state.audioSources[i];
                if (audioState == null) continue;
                Transform target = string.IsNullOrEmpty(audioState.audioSourcePath)
                    ? go.transform
                    : go.transform.Find(audioState.audioSourcePath);
                AudioSource source = target != null ? target.GetComponent<AudioSource>() : null;
                if (source == null)
                {
                    allApplied = false;
                    continue;
                }

                try
                {
                    if (!string.IsNullOrEmpty(audioState.clipName) && source.clip != null &&
                        !string.Equals(source.clip.name, audioState.clipName, StringComparison.Ordinal))
                    {
                        allApplied = false;
                        continue;
                    }
                    source.enabled = audioState.enabled;
                    source.loop = audioState.loop;
                    source.mute = audioState.mute;
                    source.volume = audioState.volume;
                    source.pitch = audioState.pitch;
                    if (audioState.isPlaying)
                    {
                        if (!source.isPlaying)
                        {
                            source.time = Mathf.Max(0f, audioState.time);
                            source.Play();
                        }
                        else if (Mathf.Abs(source.time - audioState.time) > 0.2f)
                        {
                            source.time = Mathf.Max(0f, audioState.time);
                        }
                    }
                    else if (source.isPlaying)
                    {
                        source.Stop();
                    }
                }
                catch
                {
                    allApplied = false;
                }
            }
            return allApplied;
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                   !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
                   !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }

        private static bool IsFinite(Quaternion value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                   !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
                   !float.IsNaN(value.z) && !float.IsInfinity(value.z) &&
                   !float.IsNaN(value.w) && !float.IsInfinity(value.w) &&
                   (value.x * value.x + value.y * value.y +
                    value.z * value.z + value.w * value.w) > 0.0001f;
        }

        private static bool SetActivePrefix(GameObject __instance, bool value)
        {
            WorldStoryObjectSync sync = _active;
            if (sync == null || _applyingRemoteStateDepth > 0 ||
                !IsMultiplayerSessionActive() || __instance == null)
                return true;
            if (IsLocalOnlyInterfaceObject(__instance)) return true;

            int objectId;
            try { objectId = __instance.GetInstanceID(); }
            catch { return true; }

            string path;
            if (!sync._trackedPathByObjectId.TryGetValue(objectId, out path))
            {
                try { path = LocalPlayerLocator.GetPath(__instance.transform); }
                catch { return true; }
                if (IsLocalOnlyMultiplayerPath(path)) return true;
                if (!IsSynchronizedStoryPath(path))
                    return true;
                sync.RegisterTrackedPath(path, __instance);
            }
            if (IsLocalOnlyMultiplayerPath(path)) return true;
            if (MinigameSync.ShouldBypassWorldStorySync(path)) return true;

            if (value)
            {
                // Once the authoritative world has retired an object, stale
                // local game scripts and delayed peer messages cannot revive it.
                return !sync._terminallyDisabledPaths.Contains(path);
            }

            bool wasActive;
            try { wasActive = __instance.activeSelf; }
            catch { return true; }
            if (wasActive)
            {
                if (ExplicitKillPaths.Contains(path))
                    sync.MarkTerminallyDisabled(path, __instance, true, "GameObject.SetActive(false)");
                else
                    sync.BroadcastStoryObjectState(path, __instance, false, false);
            }

            return true;
        }

        private static void SetActivePostfix(GameObject __instance, bool value)
        {
            WorldStoryObjectSync sync = _active;
            if (sync == null || !value || _applyingRemoteStateDepth > 0 ||
                !IsMultiplayerSessionActive() || __instance == null)
                return;
            if (IsLocalOnlyInterfaceObject(__instance)) return;

            try
            {
                int objectId = __instance.GetInstanceID();
                string path;
                if (!sync._trackedPathByObjectId.TryGetValue(objectId, out path))
                {
                    path = LocalPlayerLocator.GetPath(__instance.transform);
                    if (IsLocalOnlyMultiplayerPath(path)) return;
                    if (!IsSynchronizedStoryPath(path))
                        return;
                    sync.RegisterTrackedPath(path, __instance);
                }

                if (IsLocalOnlyMultiplayerPath(path) ||
                    MinigameSync.ShouldBypassWorldStorySync(path) ||
                    !IsSynchronizedStoryPath(path) ||
                    sync._terminallyDisabledPaths.Contains(path) ||
                    !__instance.activeSelf)
                    return;

                bool previous;
                if (sync.lastKnownActive.TryGetValue(path, out previous) && previous)
                    return;

                sync.BroadcastStoryObjectState(path, __instance, true, false);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("WorldStoryObjectSync: failed to broadcast SetActive(true): " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void RegisterTrackedPath(string path, GameObject go)
        {
            if (string.IsNullOrEmpty(path) || go == null) return;
            try { _trackedPathByObjectId[go.GetInstanceID()] = path; }
            catch { }
        }

        private void MarkTerminallyDisabled(string path, GameObject go, bool broadcast, string reason)
        {
            if (string.IsNullOrEmpty(path) || IsLocalOnlyMultiplayerPath(path) ||
                IsLocalOnlyInterfaceObject(go)) return;

            bool added = _terminallyDisabledPaths.Add(path);
            lastKnownActive[path] = false;
            if (go != null) RegisterTrackedPath(path, go);

            if (added && broadcast && dispatcher != null)
            {
                Transform localPlayer = LocalPlayerLocator.FindHardcodedPlayerPath();
                WorldStoryObjectState msg = new WorldStoryObjectState();
                msg.senderId = localPlayerId;
                msg.sceneName = SceneManager.GetActiveScene().name;
                msg.path = path;
                msg.isActive = false;
                msg.isTerminalDisabled = true;
                if (localPlayer != null)
                    msg.senderPosition = NetVector3.FromUnity(localPlayer.position);
                dispatcher.SendStoryObjectState(msg);
            }

            if (added)
                DiagnosticLog.Info("WorldStoryObjectSync: terminally disabled '" + path +
                                    "' (" + reason + ").");

            KillExplicitOffender(path, go);
        }

        private void BroadcastStoryObjectState(string path, GameObject go, bool isActive, bool terminal)
        {
            if (string.IsNullOrEmpty(path) || IsLocalOnlyMultiplayerPath(path) ||
                IsLocalOnlyInterfaceObject(go) || dispatcher == null ||
                (IsScene5BasementDoorPath(path) && IsMultiplayerSessionActive() &&
                 !CanPublishBasementDoorState())) return;

            Transform localPlayer = LocalPlayerLocator.FindHardcodedPlayerPath();
            WorldStoryObjectState msg = new WorldStoryObjectState();
            msg.senderId = localPlayerId;
            msg.sceneName = SceneManager.GetActiveScene().name;
            msg.path = path;
            msg.isActive = isActive;
            msg.isTerminalDisabled = terminal;
            if (go != null && (IsWardrobeRootPath(path) || IsMitaFinalClickPath(path) ||
                               (IsScene5StartHorror() && IsScene5StreamRoot(path))))
                msg.animators = CaptureAnimatorStates(go);
            if (go != null && IsScene5BasementDoorRootPath(path))
            {
                msg.transforms = CaptureHierarchyTransforms(go);
                msg.audioSources = CaptureAudioSources(go);
            }
            if (localPlayer != null)
                msg.senderPosition = NetVector3.FromUnity(localPlayer.position);
            dispatcher.SendStoryObjectState(msg);
            lastKnownActive[path] = isActive;

            if (sceneNameIsStartSecret())
                DiagnosticLog.Info("[WORLD-STORY-TX] scene='" + lastSceneName + "' path='" + path +
                                   "' activeSelf=" + (go != null ? go.activeSelf.ToString() : "<missing>") +
                                   "->" + isActive + " terminal=" + terminal + ".");
        }

        private void BroadcastTerminalDisabledStates()
        {
            if (dispatcher == null || _terminallyDisabledPaths.Count == 0) return;

            Transform localPlayer = LocalPlayerLocator.FindHardcodedPlayerPath();
            NetVector3 senderPos = new NetVector3();
            if (localPlayer != null)
                senderPos = NetVector3.FromUnity(localPlayer.position);

            foreach (string path in _terminallyDisabledPaths)
            {
                if (IsLocalOnlyMultiplayerPath(path)) continue;
                WorldStoryObjectState msg = new WorldStoryObjectState();
                msg.senderId = localPlayerId;
                msg.sceneName = lastSceneName;
                msg.path = path;
                msg.isActive = false;
                msg.isTerminalDisabled = true;
                msg.senderPosition = senderPos;
                dispatcher.SendStoryObjectState(msg);
                _fullSnapshotSentCount++;
                _fullSnapshotTotalSentCount++;
            }
        }

        private void KillExplicitOffender(string path, GameObject go)
        {
            if (go == null || !IsMultiplayerSessionActive() ||
                !IsStartSecretSceneActive() || !ExplicitKillPaths.Contains(path)) return;

            GameObject target = go;
            if (target == null) return;
            try
            {
                if (target.activeSelf) SetActiveFromRemote(target, false);
                UnityEngine.Object.Destroy(target);
                DiagnosticLog.Warning("WorldStoryObjectSync: destroyed Scene 4 offender '" + path +
                                      "' (target='" + target.name + "').");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("WorldStoryObjectSync: could not destroy Scene 4 offender '" +
                                      path + "': " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void SetActiveFromRemote(GameObject go, bool value)
        {
            if (go == null) return;
            _applyingRemoteStateDepth++;
            try { go.SetActive(value); }
            finally { _applyingRemoteStateDepth--; }
        }

        private static bool IsMultiplayerSessionActive()
        {
            return SessionController.Active != null && MiSideMultiplayerPlugin.IsRelayConnected();
        }

        private static bool IsStartSecretSceneActive()
        {
            return string.Equals(SceneManager.GetActiveScene().name,
                                 "Scene 4 - StartSecret", StringComparison.Ordinal);
        }

        private static bool ShouldTeleportForPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            if (path.IndexOf("TriggerRoom", StringComparison.OrdinalIgnoreCase) < 0 &&
                path.IndexOf("SpawnPoint", StringComparison.OrdinalIgnoreCase) < 0 &&
                path.IndexOf("Teleport", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            return true;
        }

        private bool sceneNameIsStartSecret()
        {
            return string.Equals(lastSceneName, "Scene 4 - StartSecret", StringComparison.Ordinal);
        }

        private void TeleportLocalPlayerToSender(WorldStoryObjectState state)
        {
            Transform localPlayer = LocalPlayerLocator.FindHardcodedPlayerPath();
            if (localPlayer == null) return;

            float lastTime;
            if (_lastTeleportByPath.TryGetValue(state.path, out lastTime) &&
                Time.unscaledTime - lastTime < TeleportPathCooldown)
                return;

            Vector3 targetPos = state.senderPosition.ToUnity();
            if (targetPos == Vector3.zero) return;
            if (targetPos.y < -1f) return;

            float dist = Vector3.Distance(localPlayer.position, targetPos);
            if (dist < 15f) return;

            _lastTeleportByPath[state.path] = Time.unscaledTime;

            localPlayer.position = targetPos;
            DiagnosticLog.Info(
                "WorldStoryObjectSync: teleported local player to '" +
                state.senderId + "' for quest '" + state.path + "' (cooldown " +
                TeleportPathCooldown + "s)");
        }

        private static bool IsSynchronizedStoryPath(string path)
        {
            if (string.IsNullOrEmpty(path) || IsLocalOnlyMultiplayerPath(path)) return false;
            if (MinigameSync.ShouldBypassWorldStorySync(path)) return false;
            if (path.StartsWith("World/Quests/", StringComparison.Ordinal)) return true;
            if (IsWardrobeSyncPath(path) || IsScene5BasementDoorPath(path)) return true;
            for (int i = 0; i < BedroomWardrobePathCandidates.Length; i++)
            {
                string root = BedroomWardrobePathCandidates[i];
                if (string.Equals(path, root, StringComparison.Ordinal) ||
                    path.StartsWith(root + "/", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static bool IsMitaFinalClickPath(string path)
        {
            if (string.IsNullOrEmpty(path) ||
                !path.StartsWith("World/Quests/", StringComparison.Ordinal))
                return false;

            int separator = path.LastIndexOf('/');
            string nodeName = separator >= 0 ? path.Substring(separator + 1) : path;
            return string.Equals(nodeName, MitaFinalClickObjectName, StringComparison.Ordinal);
        }

        private static bool IsLocalOnlyMultiplayerPath(string path)
        {
            return IsLocalOnlyInterfacePath(path) ||
                   IsLocalOnlyPrologueQuestPath(path);
        }

        internal static bool IsLocalOnlyPrologueQuestPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            string normalized = path.Replace('\\', '/').Trim('/');
            const string questsPrefix = "World/Quests/";
            if (!normalized.StartsWith(questsPrefix, StringComparison.Ordinal))
                return false;

            int rootStart = questsPrefix.Length;
            int nextSeparator = normalized.IndexOf('/', rootStart);
            string questRoot = nextSeparator >= 0
                ? normalized.Substring(rootStart, nextSeparator - rootStart)
                : normalized.Substring(rootStart);
            return LocalOnlyPrologueQuestRoots.Contains(questRoot);
        }

        private static bool IsLocalOnlyInterfacePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            string normalized = path.Replace('\\', '/').Trim('/');
            string[] segments = normalized.Split('/');
            for (int i = 0; i + 1 < segments.Length; i++)
            {
                if (!string.Equals(segments[i], "GameController",
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                for (int rootIndex = 0; rootIndex < LocalOnlyInterfaceRoots.Length; rootIndex++)
                {
                    if (string.Equals(segments[i + 1], LocalOnlyInterfaceRoots[rootIndex],
                            StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        private static bool IsLocalOnlyInterfaceObject(GameObject go)
        {
            if (go == null) return false;

            try
            {
                Transform node = go.transform;
                while (node != null && node.parent != null)
                {
                    if (string.Equals(node.parent.name, "GameController",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        for (int i = 0; i < LocalOnlyInterfaceRoots.Length; i++)
                        {
                            if (string.Equals(node.name, LocalOnlyInterfaceRoots[i],
                                    StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                    }
                    node = node.parent;
                }

                return IsLocalOnlyInterfacePath(LocalPlayerLocator.GetPath(go.transform));
            }
            catch
            {
                return false;
            }
        }

        private static bool IsBedroomWardrobeRootPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            for (int i = 0; i < BedroomWardrobePathCandidates.Length; i++)
                if (string.Equals(path, BedroomWardrobePathCandidates[i], StringComparison.Ordinal))
                    return true;
            return false;
        }

        private static GameObject FindBedroomWardrobe()
        {
            for (int i = 0; i < BedroomWardrobePathCandidates.Length; i++)
            {
                GameObject wardrobe = FindByPath(BedroomWardrobePathCandidates[i]);
                if (wardrobe != null) return wardrobe;
            }
            return null;
        }

        private static GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            string[] parts = path.Split('/');
            if (parts.Length == 0) return null;

            Scene scene = SceneManager.GetActiveScene();
            GameObject[] roots = scene.GetRootGameObjects();

            for (int r = 0; r < roots.Length; r++)
            {
                if (roots[r].name == parts[0])
                {
                    Transform current = roots[r].transform;
                    bool found = true;
                    for (int i = 1; i < parts.Length; i++)
                    {
                        current = current.Find(parts[i]);
                        if (current == null)
                        {
                            found = false;
                            break;
                        }
                    }
                    if (found && current != null)
                        return current.gameObject;
                }
            }

            return null;
        }
    }
}
