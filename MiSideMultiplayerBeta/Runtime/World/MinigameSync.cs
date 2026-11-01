using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using MiSideMultiplayer.Runtime.Lobby;

namespace MiSideMultiplayer
{
    /// <summary>
    /// Locks single-player minigames to one local player. Game Card gameplay
    /// stays local; peers receive only its ownership and completion signals.
    /// </summary>
    internal sealed class MinigameSync : IDisposable
    {
        private const string SignalEvent = "miside.minigame.signal";
        private const string GameCardId = "game-card";
        private const string GameAihastionId = "game-aihastion";
        private const string QuestOnePath = "World/Quests/Quest 1";
        private const string GameCardPath = QuestOnePath + "/Game Card";
        private const string GameCardEntryTriggerPath = QuestOnePath + "/Addon/Interactive Cards >>";
        private const string GameAihastionPath = QuestOnePath + "/Game Aihastion";
        private const string DynamicMinigameIdPrefix = "dynamic-minigame:";
        private const float ClaimRetrySeconds = 1.25f;
        private const float ClaimTimeoutSeconds = 12.0f;
        private const float CardStateIntervalSeconds = 0.20f;
        // Reassert suppression every Unity frame. SetActive hooks provide the
        // immediate barrier; this catches scripts that bypass that hook or
        // reactivate controls during their own Update methods.
        private const float SuppressionRefreshSeconds = 0.1f;
        private const float BusyMessageCooldownSeconds = 2.0f;

        private static readonly string[] SupportedGames = { GameCardId, GameAihastionId };
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true
        };

        private static MinigameSync _active;
        private static bool _setActivePatchInstalled;
        private static bool _inputPatchesInstalled;
        private static int _applyingSignalDepth;
        private static int _replayingInputDepth;
        private bool _ensuringGameCardOwnerControls;
        private bool _disabledRemoteGameCardSimulation;

        private RpcDispatcher _dispatcher;
        private string _localPlayerId = string.Empty;
        private string _activeSceneName = string.Empty;
        private float _nextCardStateTime;
        private float _nextCardStateSummaryTime;
        private float _nextSuppressionRefreshTime;
        private int _cardStateSequence;
        private int _lastAcceptedCardStateSequence;
        private int _cardMitaStateSequence;
        private int _lastAcceptedCardMitaSequence;
        private int _cardStatesRelayedSinceLog;
        private bool _cardGameStartNoticeSent;
        private GameCardState _latestCardState;

        private readonly Dictionary<string, string> _owners = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _revisions = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<string> _pendingClaims = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _pendingFinishes = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _completedGames = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _dynamicMinigameIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _pendingDynamicMinigameReleases =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<int, string> _minigameIdByControllerInstance =
            new Dictionary<int, string>();
        private readonly Dictionary<string, float> _nextClaimRetry = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> _claimStartedAt = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> _lastBusyNotice = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly HashSet<string> _loggedControlPaths = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _claimAttempts = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<GameObject>> _pendingControls =
            new Dictionary<string, List<GameObject>>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<PendingStartAction>> _pendingStartActions =
            new Dictionary<string, List<PendingStartAction>>(StringComparer.Ordinal);

        private sealed class PendingStartAction
        {
            public string key;
            public Action replay;
        }

        private sealed class MinigameSignal
        {
            public string kind;
            public string senderId;
            public string recipientId;
            public string sceneName;
            public string gameId;
            public string ownerId;
            public int revision;
            public bool completed;
            public int sequence;
            public GameCardState cardState;
            public MitaState mitaState;
        }

        [Serializable]
        private sealed class GameCardState
        {
            public int sequence;
            public bool play;
            public bool gameStop;
            public bool playerHoldCards;
            public bool mitaHoldCards;
            public bool showCards;
            public bool canShowCards;
            public bool cardMitaShow;
            public int indexCard;
            public int indexCardThrow;
            public int indexCardMitaThrow;
            public int indexCardMitaTakeForThrow;
            public int typeFishkaNow;
            public int countSteps;
            public int scorePlayer;
            public int scoreMita;
            public float stepGame;
            public float timeStepGame;
            public float stopGameTime;
            public int[] playerCardIndices;
            public int[] mitaCardIndices;
        }

        public static void Initialize(Harmony harmony)
        {
            if (harmony == null) return;

            if (!_setActivePatchInstalled)
            {
                try
                {
                    MethodInfo setActive = typeof(GameObject).GetMethod(
                        "SetActive", new[] { typeof(bool) });
                    MethodInfo prefix = typeof(MinigameSync).GetMethod(
                        nameof(SetActivePrefix), BindingFlags.Static | BindingFlags.NonPublic);
                    MethodInfo postfix = typeof(MinigameSync).GetMethod(
                        nameof(SetActivePostfix), BindingFlags.Static | BindingFlags.NonPublic);
                    if (setActive == null || prefix == null || postfix == null)
                    {
                        TraceWarning("could not install GameObject.SetActive enforcement hook; " +
                                     "SetActive=" + (setActive == null) + " prefix=" +
                                     (prefix == null) + " postfix=" + (postfix == null) + ".");
                        DiagnosticLog.Warning("MinigameSync: GameObject.SetActive patch target not found.");
                    }
                    else
                    {
                        harmony.Patch(setActive,
                            prefix: new HarmonyMethod(prefix),
                            postfix: new HarmonyMethod(postfix));
                        _setActivePatchInstalled = true;
                        Trace("SetActive enforcement hook installed.");
                    }
                }
                catch (Exception ex)
                {
                    TraceWarning("SetActive hook installation failed: " +
                                 ex.GetType().Name + ": " + ex.Message);
                    DiagnosticLog.Warning("MinigameSync: SetActive patch failed: " +
                                          ex.GetType().Name + ": " + ex.Message);
                }
            }

            if (_inputPatchesInstalled) return;
            try
            {
                bool installedAny = false;
                installedAny |= PatchInputMethod(harmony, typeof(Location4TableCardGame), "StartGame",
                    nameof(Location4TableCardGameStartPrefix));
                installedAny |= PatchInputPostfix(harmony, typeof(Location4TableCardGame), "StartGame",
                    nameof(Location4TableCardGameStartPostfix));
                installedAny |= PatchInputMethod(harmony, typeof(MinigamesController), "StartGame",
                    nameof(MinigamesControllerStartGamePrefix));
                installedAny |= PatchInputPostfix(harmony, typeof(MinigamesController), "StartGame",
                    nameof(MinigamesControllerStartGamePostfix));
                installedAny |= PatchInputMethod(harmony, typeof(MinigamesController), "GameFinish",
                    nameof(MinigamesControllerFinishPrefix));
                installedAny |= PatchInputPostfix(harmony, typeof(MinigamesController), "GameFinish",
                    nameof(MinigamesControllerFinishPostfix));
                installedAny |= PatchInputMethod(harmony, typeof(MinigamesController), "ExitGame",
                    nameof(MinigamesControllerExitPrefix));
                installedAny |= PatchInputPostfix(harmony, typeof(MinigamesController), "ExitGame",
                    nameof(MinigamesControllerExitPostfix));
                installedAny |= PatchInputMethod(harmony, typeof(MinigamesController), "Update",
                    nameof(MinigamesControllerUpdatePrefix));
                installedAny |= PatchInputMethod(harmony, typeof(Tamagotchi_Main), "MiniGamePlay",
                    nameof(TamagotchiMainMiniGamePlayPrefix));
                installedAny |= PatchInputPostfix(harmony, typeof(Tamagotchi_Main), "MiniGamePlay",
                    nameof(TamagotchiMainMiniGamePlayPostfix));
                installedAny |= PatchInputMethod(harmony, typeof(Tamagotchi_Main), "MiniGameStop",
                    nameof(TamagotchiMainMiniGameStopPrefix));
                installedAny |= PatchInputPostfix(harmony, typeof(Tamagotchi_Main), "MiniGameStop",
                    nameof(TamagotchiMainMiniGameStopPostfix));
                installedAny |= PatchInputMethod(harmony, typeof(Tamagotchi_MiniGame), "StartGame",
                    nameof(TamagotchiMiniGameStartPrefix));
                installedAny |= PatchInputMethod(harmony, typeof(Tamagotchi_MiniGame), "StopGame",
                    nameof(TamagotchiMiniGameStopPrefix));
                installedAny |= PatchInputPostfix(harmony, typeof(Tamagotchi_MiniGame), "StopGame",
                    nameof(TamagotchiMiniGameStopPostfix));
                installedAny |= PatchInputMethod(harmony, typeof(Tamagotchi_MiniGame), "Update",
                    nameof(TamagotchiMiniGameUpdatePrefix));
                installedAny |= PatchInputPostfix(harmony, typeof(Interface_KeyHint_Key), "Start",
                    nameof(InterfaceKeyHintStartPostfix));
                installedAny |= PatchInputPostfix(harmony, typeof(Interface_KeyHint_Key), "StartComponent",
                    nameof(InterfaceKeyHintStartPostfix));
                installedAny |= PatchInputMethod(harmony, typeof(ObjectInteractive), "Click",
                    nameof(ObjectInteractiveClickPrefix));
                installedAny |= PatchInputMethod(harmony, typeof(ButtonMouseClick), "OnPointerDown",
                    nameof(ButtonMouseClickPointerDownPrefix));
                installedAny |= PatchInputMethod(harmony, typeof(ButtonMouseClick), "PointerUp",
                    nameof(ButtonMouseClickPointerUpPrefix));
                installedAny |= PatchInputMethod(harmony, typeof(ButtonMouseClick), "keyDown",
                    nameof(ButtonMouseClickKeyDownPrefix));
                _inputPatchesInstalled = installedAny;
                Trace("input hook setup complete installedAny=" + installedAny +
                      "; Game Card, MinigamesController, and Tamagotchi entry/exit hooks are installed where available.");
            }
            catch (Exception ex)
            {
                TraceWarning("input hook setup failed: " + ex.GetType().Name + ": " + ex.Message);
                DiagnosticLog.Warning("MinigameSync: input patch setup failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool PatchInputMethod(Harmony harmony, Type ownerType,
                                             string methodName, string prefixName)
        {
            MethodInfo original = ownerType == null ? null : AccessTools.Method(ownerType, methodName);
            MethodInfo prefix = typeof(MinigameSync).GetMethod(
                prefixName, BindingFlags.Static | BindingFlags.NonPublic);
            if (original == null || prefix == null)
            {
                TraceWarning("input hook missing: " +
                             (ownerType == null ? "<null>" : ownerType.Name) + "." +
                             methodName + " originalMissing=" + (original == null) +
                             " prefixMissing=" + (prefix == null) + ".");
                DiagnosticLog.Warning("MinigameSync: input hook not found: " +
                                      (ownerType == null ? "<null>" : ownerType.Name) + "." + methodName);
                return false;
            }

            harmony.Patch(original, prefix: new HarmonyMethod(prefix));
            Trace("input hook installed: " + ownerType.Name + "." + methodName + "().");
            return true;
        }

        private static bool PatchInputPostfix(Harmony harmony, Type ownerType,
                                              string methodName, string postfixName)
        {
            MethodInfo original = ownerType == null ? null : AccessTools.Method(ownerType, methodName);
            MethodInfo postfix = typeof(MinigameSync).GetMethod(
                postfixName, BindingFlags.Static | BindingFlags.NonPublic);
            if (original == null || postfix == null)
            {
                TraceWarning("input postfix missing: " +
                             (ownerType == null ? "<null>" : ownerType.Name) + "." +
                             methodName + " originalMissing=" + (original == null) +
                             " postfixMissing=" + (postfix == null) + ".");
                DiagnosticLog.Warning("MinigameSync: input postfix not found: " +
                                      (ownerType == null ? "<null>" : ownerType.Name) + "." + methodName);
                return false;
            }

            harmony.Patch(original, postfix: new HarmonyMethod(postfix));
            Trace("input postfix installed: " + ownerType.Name + "." + methodName + "().");
            return true;
        }

        private static void Trace(string message)
        {
            Debug.Log("[MiSideMultiplayer][MINIGAME] " + message);
        }

        private static void TraceWarning(string message)
        {
            Debug.LogWarning("[MiSideMultiplayer][MINIGAME] " + message);
        }

        public void Configure(RpcDispatcher dispatcher, string playerId)
        {
            if (_dispatcher != null)
            {
                _dispatcher.CustomMessageReceived -= OnCustomMessage;
                _dispatcher.RemotePlayerLeft -= OnRemotePlayerLeft;
            }

            _dispatcher = dispatcher;
            _localPlayerId = playerId ?? string.Empty;
            _active = this;

            Trace("configured localId='" + _localPlayerId + "' dispatcher=" +
                  (_dispatcher == null ? "missing" : "ready") + ".");

            if (_dispatcher != null)
            {
                _dispatcher.CustomMessageReceived += OnCustomMessage;
                _dispatcher.RemotePlayerLeft += OnRemotePlayerLeft;
            }
        }

        internal static bool IsLocalGameCardOwner
        {
            get
            {
                MinigameSync sync = _active;
                if (sync == null || string.IsNullOrWhiteSpace(sync._localPlayerId)) return false;
                string owner;
                return sync._owners.TryGetValue(GameCardId, out owner) &&
                       string.Equals(owner, sync._localPlayerId, StringComparison.Ordinal);
            }
        }

        internal static bool HasRemoteGameCardOwner
        {
            get
            {
                MinigameSync sync = _active;
                if (sync == null) return false;
                string owner;
                return sync._owners.TryGetValue(GameCardId, out owner) &&
                       !string.IsNullOrEmpty(owner) && owner != sync._localPlayerId;
            }
        }

        internal static bool IsGameCardMitaPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            path = path.Replace('\\', '/');
            return IsSameOrDescendant(path, GameCardPath + "/MitaGame");
        }

        internal static bool IsLocalGameCardMitaPath(string path)
        {
            return IsLocalGameCardOwner && IsGameCardMitaPath(path);
        }

        internal static bool IsGameCardMitaStateAuthority(string senderId)
        {
            if (string.IsNullOrWhiteSpace(senderId)) return false;
            MinigameSync sync = _active;
            if (sync == null) return false;
            string owner;
            if (sync._owners.TryGetValue(GameCardId, out owner) &&
                !string.IsNullOrEmpty(owner))
                return string.Equals(senderId, owner, StringComparison.Ordinal);

            return string.Equals(senderId, MitaHost.HostPlayerId, StringComparison.Ordinal);
        }

        internal static void BroadcastGameCardMitaState(MitaState state)
        {
            MinigameSync sync = _active;
            if (sync == null || state == null || !IsLocalGameCardOwner ||
                string.IsNullOrWhiteSpace(sync._localPlayerId) ||
                !IsGameCardMitaPath(state.mitaPath)) return;
            state.senderId = sync._localPlayerId;
            sync.SendSignal(new MinigameSignal
            {
                kind = "card-mita-state",
                senderId = sync._localPlayerId,
                ownerId = sync._localPlayerId,
                sceneName = SceneManager.GetActiveScene().name,
                gameId = GameCardId,
                sequence = ++sync._cardMitaStateSequence,
                mitaState = state
            });
        }

        public static bool ShouldBypassWorldStorySync(string path)
        {
            string gameId;
            bool isControl;
            bool isFinishSignal;
            return TryClassifyPath(path, out gameId, out isControl, out isFinishSignal) &&
                   (gameId == GameCardId || isControl || isFinishSignal);
        }

        public static bool ShouldAcceptRemoteWorldStoryState(string path, string senderId)
        {
            string gameId;
            bool isControl;
            bool isFinishSignal;
            if (!TryClassifyPath(path, out gameId, out isControl, out isFinishSignal) ||
                gameId != GameCardId)
                return true;
            // Game Card runs its native quest/minigame scripts locally. Only
            // the minigame lock and finish signals are shared between peers.
            return false;
        }

        public void OnSceneLoaded(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return;
            if (!string.Equals(_activeSceneName, sceneName, StringComparison.Ordinal))
                ResetForScene(sceneName);

            if (string.Equals(sceneName, "SceneMenu", StringComparison.Ordinal) ||
                string.Equals(sceneName, "SceneLoading", StringComparison.Ordinal))
                return;

            SessionController session = SessionController.Active;
            Trace("scene='" + sceneName + "' role=" +
                  (session == null ? "no-session" : session.IsHost ? "host" : "client") +
                  " dispatcher=" + (_dispatcher == null ? "missing" : "ready") + ".");
            if (session == null || !session.IsHost || _dispatcher == null) return;

            // Establish an empty baseline for this scene. Revisions ensure a
            // delayed baseline cannot overwrite a newer claim or completion.
            for (int i = 0; i < SupportedGames.Length; i++)
                BroadcastState(SupportedGames[i], string.Empty, false, true);
            Trace("host sent empty minigame-lock baseline for scene='" + sceneName + "'.");
        }

        public void Tick()
        {
            string sceneName = SceneManager.GetActiveScene().name;
            if (!string.Equals(sceneName, _activeSceneName, StringComparison.Ordinal))
                OnSceneLoaded(sceneName);

            SessionController session = SessionController.Active;
            if (session == null || _dispatcher == null ||
                string.Equals(sceneName, "SceneMenu", StringComparison.Ordinal) ||
                string.Equals(sceneName, "SceneLoading", StringComparison.Ordinal))
                return;

            string currentOwner;
            if (_owners.TryGetValue(GameCardId, out currentOwner) &&
                !string.IsNullOrEmpty(currentOwner))
            {
                if (currentOwner != _localPlayerId)
                    DisableNonOwnerGameCardSimulation();
            }

            string[] trackedGames = GetTrackedGameIds();
            if (!session.IsHost)
            {
                for (int i = 0; i < trackedGames.Length; i++)
                {
                    string gameId = trackedGames[i];
                    if (!_pendingClaims.Contains(gameId)) continue;

                    float startedAt;
                    if (_claimStartedAt.TryGetValue(gameId, out startedAt) &&
                        Time.unscaledTime - startedAt >= ClaimTimeoutSeconds)
                    {
                        // Never fail open: a client remains unable to start until
                        // the host explicitly grants ownership. Keep retrying so a
                        // delayed host response can still authorize the queued input.
                        _claimStartedAt[gameId] = Time.unscaledTime;
                        DiagnosticLog.Warning("MinigameSync: still waiting for host approval for " +
                                              DisplayName(gameId) + " after " + ClaimTimeoutSeconds +
                                              "s; client remains blocked and will keep requesting.");
                        TraceWarning("host approval still pending game='" + gameId +
                                     "' local='" + _localPlayerId + "'; keeping input queued.");
                    }

                    float next;
                    if (_nextClaimRetry.TryGetValue(gameId, out next) &&
                        Time.unscaledTime < next)
                        continue;
                    SendClaim(gameId);
                }
            }

            if (Time.unscaledTime >= _nextSuppressionRefreshTime)
            {
                _nextSuppressionRefreshTime = Time.unscaledTime + SuppressionRefreshSeconds;
                trackedGames = GetTrackedGameIds();
                for (int i = 0; i < trackedGames.Length; i++)
                {
                    string owner;
                    string gameId = trackedGames[i];
                    bool hasOwner = _owners.TryGetValue(gameId, out owner) &&
                                    !string.IsNullOrEmpty(owner);
                    if (hasOwner && owner != _localPlayerId)
                        SuppressControls(gameId);
                    else if (hasOwner && owner == _localPlayerId && gameId == GameCardId)
                        EnsureGameCardOwnerControls();
                    else if (!hasOwner && IsBuiltInGame(gameId) &&
                             (session.IsHost || !_pendingClaims.Contains(gameId)))
                        HoldControlsUntilClaim(gameId);
                }
            }
        }

        /// <summary>
        /// The local player owns and runs their Game Card simulation. Only
        /// ownership is sent to peers; simulation remains entirely local.
        /// </summary>
        private void OnGameCardStarted(Location4TableCardGame game)
        {
            if (game == null) return;
            string owner;
            if (!_owners.TryGetValue(GameCardId, out owner) || owner != _localPlayerId)
                return;
            if (_cardGameStartNoticeSent) return;
            _cardGameStartNoticeSent = true;

            _nextCardStateTime = 0f;
            BindMinigamesControllerUpdate(GameCardId);
            EnsureGameCardOwnerControls();
        }

        private void TickOwnerGameCardState()
        {
            if (Time.unscaledTime < _nextCardStateTime) return;
            string owner;
            if (!_owners.TryGetValue(GameCardId, out owner) || owner != _localPlayerId)
                return;

            Location4TableCardGame game = FindCardGame();
            if (game == null || (!game.play && !game.gameStop)) return;
            _nextCardStateTime = Time.unscaledTime + CardStateIntervalSeconds;
            GameCardState state = CaptureCardGameState(game);
            if (state == null) return;
            state.sequence = ++_cardStateSequence;
            _latestCardState = state;
            SendSignal(new MinigameSignal
            {
                kind = "card-state",
                senderId = _localPlayerId,
                ownerId = _localPlayerId,
                sceneName = SceneManager.GetActiveScene().name,
                gameId = GameCardId,
                sequence = state.sequence,
                cardState = state
            });
        }

        private void RequestCardGameState(string ownerId)
        {
            if (_dispatcher == null || string.IsNullOrWhiteSpace(ownerId)) return;
            SendSignal(new MinigameSignal
            {
                kind = "card-request",
                senderId = _localPlayerId,
                recipientId = ownerId,
                ownerId = ownerId,
                sceneName = SceneManager.GetActiveScene().name,
                gameId = GameCardId
            });
        }

        private void RestoreLocalGameCardSimulation()
        {
            if (!_disabledRemoteGameCardSimulation) return;
            Location4TableCardGame game = FindCardGame();
            if (game != null) game.enabled = true;
            _disabledRemoteGameCardSimulation = false;
        }

        private void HandleCardGameStarted(MinigameSignal signal, SessionController session)
        {
            string owner;
            if (!IsKnownParticipant(session, signal.senderId) ||
                !_owners.TryGetValue(GameCardId, out owner) || owner != signal.senderId)
            {
                TraceWarning("rejected Game Card started notice from unapproved owner='" +
                             signal.senderId + "'.");
                return;
            }

            Trace("approved Game Card.StartGame() from owner='" + owner +
                  "'; requesting its initial game state.");
            RequestCardGameState(owner);
        }

        private void HandleCardGameSnapshot(MinigameSignal signal, SessionController session)
        {
            if (signal.cardState == null || !IsKnownParticipant(session, signal.senderId) ||
                signal.sequence <= 0 || signal.cardState.sequence != signal.sequence)
                return;
            string owner;
            if (!_owners.TryGetValue(GameCardId, out owner) || owner != signal.senderId)
            {
                TraceWarning("rejected Game Card state from non-owner='" + signal.senderId +
                             "' currentOwner='" + (owner ?? "") + "'.");
                return;
            }
            if (signal.sequence <= _lastAcceptedCardStateSequence) return;

            _lastAcceptedCardStateSequence = signal.sequence;
            signal.cardState.sequence = signal.sequence;
            _latestCardState = signal.cardState;
            DisableNonOwnerGameCardSimulation();
            ApplyCardGameState(FindCardGame(), signal.cardState);
            _cardStatesRelayedSinceLog++;

            // Re-envelope the client data as host-authorized state. Other
            // peers ignore the raw owner packet and only consume this copy.
            SendSignal(new MinigameSignal
            {
                kind = "card-state",
                senderId = _localPlayerId,
                ownerId = owner,
                sceneName = SceneManager.GetActiveScene().name,
                gameId = GameCardId,
                sequence = signal.sequence,
                cardState = signal.cardState
            });

            if (Time.unscaledTime >= _nextCardStateSummaryTime)
            {
                _nextCardStateSummaryTime = Time.unscaledTime + 5f;
                Trace("host relayed " + _cardStatesRelayedSinceLog +
                      " Game Card state update(s) from owner='" + owner +
                      "'; step=" + signal.cardState.stepGame +
                      " cards=" + ArrayLength(signal.cardState.playerCardIndices) +
                      ", score=" + signal.cardState.scorePlayer + "/" +
                      signal.cardState.scoreMita + ".");
                _cardStatesRelayedSinceLog = 0;
            }
        }

        private bool SetLocalGameCardOwner()
        {
            if (string.IsNullOrWhiteSpace(_localPlayerId))
            {
                TraceWarning("cannot start local Game Card: local player ID is empty.");
                return false;
            }
            if (_completedGames.Contains(GameCardId)) return false;

            string currentOwner;
            bool hasCurrentOwner = _owners.TryGetValue(GameCardId, out currentOwner) &&
                                   !string.IsNullOrEmpty(currentOwner);
            if (hasCurrentOwner && currentOwner != _localPlayerId) return false;
            if (hasCurrentOwner)
            {
                RestoreLocalGameCardSimulation();
                ReplayPendingControls(GameCardId);
                EnsureGameCardOwnerControls();
                return true;
            }

            bool changed = !hasCurrentOwner;
            _owners[GameCardId] = _localPlayerId;
            _pendingClaims.Remove(GameCardId);
            _nextClaimRetry.Remove(GameCardId);
            _claimStartedAt.Remove(GameCardId);
            _pendingFinishes.Remove(GameCardId);
            ClearPendingStartActions(GameCardId);
            if (changed)
            {
                _cardStateSequence = 0;
                _lastAcceptedCardStateSequence = 0;
                _cardMitaStateSequence = 0;
                _lastAcceptedCardMitaSequence = 0;
                _cardGameStartNoticeSent = false;
                _latestCardState = null;
            }

            int revision;
            _revisions.TryGetValue(GameCardId, out revision);
            revision++;
            _revisions[GameCardId] = revision;
            RestoreLocalGameCardSimulation();
            ReplayPendingControls(GameCardId);
            EnsureGameCardOwnerControls();
            SendSignal(new MinigameSignal
            {
                kind = "card-owner",
                senderId = _localPlayerId,
                ownerId = _localPlayerId,
                revision = revision,
                sceneName = SceneManager.GetActiveScene().name,
                gameId = GameCardId
            });
            BindMinigamesControllerUpdate(GameCardId);
            Trace("local Game Card owner set to '" + _localPlayerId + "' revision=" +
                  revision + "; local simulation starts without host approval.");
            return true;
        }

        private void ApplyPeerGameCardOwner(MinigameSignal signal,
                                            SessionController session)
        {
            if (!IsKnownParticipant(session, signal.senderId) ||
                !string.Equals(signal.ownerId, signal.senderId, StringComparison.Ordinal))
            {
                TraceWarning("rejected invalid peer Game Card owner from '" +
                             signal.senderId + "'.");
                return;
            }
            if (_completedGames.Contains(GameCardId)) return;

            string currentOwner;
            _owners.TryGetValue(GameCardId, out currentOwner);
            string winner = string.IsNullOrEmpty(currentOwner) ||
                            string.CompareOrdinal(signal.senderId, currentOwner) < 0
                ? signal.senderId : currentOwner;
            bool changed = !string.Equals(currentOwner, winner, StringComparison.Ordinal);
            if (changed)
            {
                _owners[GameCardId] = winner;
                _cardStateSequence = 0;
                _lastAcceptedCardStateSequence = 0;
                _cardMitaStateSequence = 0;
                _lastAcceptedCardMitaSequence = 0;
                _cardGameStartNoticeSent = false;
                _latestCardState = null;
            }

            int revision;
            _revisions.TryGetValue(GameCardId, out revision);
            _revisions[GameCardId] = Math.Max(revision, signal.revision);
            _pendingClaims.Remove(GameCardId);
            _nextClaimRetry.Remove(GameCardId);
            _claimStartedAt.Remove(GameCardId);
            ClearPendingStartActions(GameCardId);

            if (winner == _localPlayerId)
            {
                ReplayPendingControls(GameCardId);
                RestoreLocalGameCardSimulation();
                EnsureGameCardOwnerControls();
                Trace("peer Game Card claim from '" + signal.senderId +
                      "' resolved to local owner='" + winner + "'.");
            }
            else
            {
                ClearPendingControls(GameCardId);
                SuppressControls(GameCardId);
                DisableNonOwnerGameCardSimulation();
                ShowBusyMessage(GameCardId, winner);
                Trace("peer Game Card claim from '" + signal.senderId +
                      "' resolved to owner='" + winner + "'; local controls and simulation suppressed.");
            }
            BindMinigamesControllerUpdate(GameCardId);
        }

        private void ApplyPeerGameCardState(MinigameSignal signal,
                                            SessionController session)
        {
            GameCardState state = signal.cardState;
            if (state == null || signal.sequence <= 0 || state.sequence != signal.sequence ||
                !IsKnownParticipant(session, signal.senderId) ||
                !string.Equals(signal.ownerId, signal.senderId, StringComparison.Ordinal))
            {
                TraceWarning("rejected malformed or unauthorized peer Game Card state from '" +
                             signal.senderId + "'.");
                return;
            }
            if (_completedGames.Contains(GameCardId)) return;

            string currentOwner;
            _owners.TryGetValue(GameCardId, out currentOwner);
            if (!string.IsNullOrEmpty(currentOwner) && currentOwner != signal.senderId &&
                string.CompareOrdinal(signal.senderId, currentOwner) >= 0)
            {
                Trace("ignored Game Card snapshot from losing concurrent owner='" +
                      signal.senderId + "' winner='" + currentOwner + "'.");
                return;
            }
            if (string.IsNullOrEmpty(currentOwner) || currentOwner != signal.senderId)
            {
                ApplyPeerGameCardOwner(new MinigameSignal
                {
                    senderId = signal.senderId,
                    ownerId = signal.senderId,
                    revision = signal.revision,
                    sceneName = signal.sceneName,
                    gameId = GameCardId
                }, session);
            }

            string acceptedOwner;
            if (!_owners.TryGetValue(GameCardId, out acceptedOwner) ||
                acceptedOwner != signal.senderId ||
                signal.sequence <= _lastAcceptedCardStateSequence)
                return;

            _lastAcceptedCardStateSequence = signal.sequence;
            _latestCardState = state;
            _cardStatesRelayedSinceLog++;
            if (acceptedOwner != _localPlayerId)
            {
                DisableNonOwnerGameCardSimulation();
                ApplyCardGameState(FindCardGame(), state);
            }

            if (Time.unscaledTime >= _nextCardStateSummaryTime)
            {
                _nextCardStateSummaryTime = Time.unscaledTime + 5f;
                Trace("received " + _cardStatesRelayedSinceLog +
                      " direct Game Card state update(s) from owner='" + acceptedOwner +
                      "'; step=" + state.stepGame + " cards=" +
                      ArrayLength(state.playerCardIndices) + ", score=" +
                      state.scorePlayer + "/" + state.scoreMita + ".");
                _cardStatesRelayedSinceLog = 0;
            }
        }

        private void ApplyPeerGameCardFinish(MinigameSignal signal,
                                             SessionController session)
        {
            if (!IsKnownParticipant(session, signal.senderId) ||
                !string.Equals(signal.ownerId, signal.senderId, StringComparison.Ordinal))
            {
                TraceWarning("rejected invalid peer Game Card completion from '" +
                             signal.senderId + "'.");
                return;
            }

            string currentOwner;
            _owners.TryGetValue(GameCardId, out currentOwner);
            if (!string.IsNullOrEmpty(currentOwner) && currentOwner != signal.senderId &&
                string.CompareOrdinal(signal.senderId, currentOwner) >= 0)
            {
                Trace("ignored Game Card finish from losing concurrent owner='" +
                      signal.senderId + "' winner='" + currentOwner + "'.");
                return;
            }
            CompletePeerGameCard(signal.senderId, signal.sequence, signal.cardState);
        }

        private void ApplyPeerGameCardMitaState(MinigameSignal signal,
                                                SessionController session)
        {
            MitaState state = signal.mitaState;
            if (state == null || !IsKnownParticipant(session, signal.senderId) ||
                signal.sequence <= 0 ||
                !string.Equals(signal.ownerId, signal.senderId, StringComparison.Ordinal) ||
                !string.Equals(state.senderId, signal.senderId, StringComparison.Ordinal) ||
                !IsGameCardMitaPath(state.mitaPath))
            {
                TraceWarning("rejected malformed peer Game Card Mita state from '" +
                             signal.senderId + "'.");
                return;
            }
            if (!IsGameCardMitaStateAuthority(signal.senderId))
            {
                Trace("ignored Game Card Mita pose from non-owner='" + signal.senderId + "'.");
                return;
            }
            if (signal.sequence <= _lastAcceptedCardMitaSequence) return;
            _lastAcceptedCardMitaSequence = signal.sequence;
            MitaController.ReceiveGameCardMitaState(state);
        }

        private bool CompleteLocalGameCard()
        {
            string owner;
            if (!_owners.TryGetValue(GameCardId, out owner) || owner != _localPlayerId)
                return false;
            if (_completedGames.Contains(GameCardId)) return true;

            CompletePeerGameCard(_localPlayerId, 0, null);
            SendSignal(new MinigameSignal
            {
                kind = "card-finish",
                senderId = _localPlayerId,
                ownerId = _localPlayerId,
                sceneName = SceneManager.GetActiveScene().name,
                gameId = GameCardId,
                sequence = 0,
                completed = true
            });
            return true;
        }

        private void CompletePeerGameCard(string ownerId, int sequence,
                                           GameCardState finalState)
        {
            if (_completedGames.Contains(GameCardId)) return;
            _completedGames.Add(GameCardId);
            _owners[GameCardId] = string.Empty;
            _pendingClaims.Remove(GameCardId);
            _nextClaimRetry.Remove(GameCardId);
            _claimStartedAt.Remove(GameCardId);
            _pendingFinishes.Remove(GameCardId);
            ClearPendingControls(GameCardId);
            ClearPendingStartActions(GameCardId);
            if (finalState != null)
            {
                finalState.sequence = Math.Max(finalState.sequence, sequence);
                _latestCardState = finalState;
                _lastAcceptedCardStateSequence = Math.Max(_lastAcceptedCardStateSequence,
                                                           finalState.sequence);
                ApplyCardGameState(FindCardGame(), finalState);
            }
            int revision;
            _revisions.TryGetValue(GameCardId, out revision);
            _revisions[GameCardId] = revision + 1;
            DiagnosticLog.Info("MinigameSync: Game Card completed by '" + ownerId +
                               "'; broadcasting completion to every peer.");
            ApplyCompletion(GameCardId);
        }

        private void ApplyRelayedCardGameState(MinigameSignal signal)
        {
            if (signal.sequence <= 0 || signal.cardState == null ||
                signal.cardState.sequence != signal.sequence)
                return;
            string owner;
            if (!_owners.TryGetValue(GameCardId, out owner) || owner != signal.ownerId ||
                signal.cardState == null || signal.sequence <= _lastAcceptedCardStateSequence)
                return;

            _lastAcceptedCardStateSequence = signal.sequence;
            _latestCardState = signal.cardState;
            if (owner != _localPlayerId)
            {
                DisableNonOwnerGameCardSimulation();
                ApplyCardGameState(FindCardGame(), signal.cardState);
            }
        }

        private Location4TableCardGame FindCardGame()
        {
            GameObject root = FindPath(GameCardPath);
            return root != null ? root.GetComponentInChildren<Location4TableCardGame>(true) : null;
        }

        private static GameCardState CaptureCardGameState(Location4TableCardGame game)
        {
            if (game == null) return null;
            return new GameCardState
            {
                play = ReadCardValue(game, "play", false),
                gameStop = ReadCardValue(game, "gameStop", false),
                playerHoldCards = ReadCardValue(game, "playerHoldCards", false),
                mitaHoldCards = ReadCardValue(game, "mitaHoldCards", false),
                showCards = ReadCardValue(game, "showCards", false),
                canShowCards = ReadCardValue(game, "canShowCards", false),
                cardMitaShow = ReadCardValue(game, "cardMitaShow", false),
                indexCard = ReadCardValue(game, "indexCard", 0),
                indexCardThrow = ReadCardValue(game, "indexCardThrow", 0),
                indexCardMitaThrow = ReadCardValue(game, "indexCardMitaThrow", 0),
                indexCardMitaTakeForThrow = ReadCardValue(game, "indexCardMitaTakeForThrow", 0),
                typeFishkaNow = ReadCardValue(game, "typeFishkaNow", 0),
                countSteps = ReadCardValue(game, "countSteps", 0),
                scorePlayer = ReadCardValue(game, "scorePlayer", 0),
                scoreMita = ReadCardValue(game, "scoreMita", 0),
                stepGame = ReadCardValue(game, "stepGame", 0f),
                timeStepGame = ReadCardValue(game, "timeStepGame", 0f),
                stopGameTime = ReadCardValue(game, "StopGameTime", 0f),
                playerCardIndices = ReadCardIndices(game, "cardsPlayer"),
                mitaCardIndices = ReadCardIndices(game, "cardsMita")
            };
        }

        private void ApplyCardGameState(Location4TableCardGame game, GameCardState state)
        {
            if (game == null || state == null) return;
            WriteCardMember(game, "play", state.play);
            WriteCardMember(game, "gameStop", state.gameStop);
            WriteCardMember(game, "playerHoldCards", state.playerHoldCards);
            WriteCardMember(game, "mitaHoldCards", state.mitaHoldCards);
            WriteCardMember(game, "showCards", state.showCards);
            WriteCardMember(game, "canShowCards", state.canShowCards);
            WriteCardMember(game, "cardMitaShow", state.cardMitaShow);
            WriteCardMember(game, "indexCard", state.indexCard);
            WriteCardMember(game, "indexCardThrow", state.indexCardThrow);
            WriteCardMember(game, "indexCardMitaThrow", state.indexCardMitaThrow);
            WriteCardMember(game, "indexCardMitaTakeForThrow", state.indexCardMitaTakeForThrow);
            WriteCardMember(game, "typeFishkaNow", state.typeFishkaNow);
            WriteCardMember(game, "countSteps", state.countSteps);
            WriteCardMember(game, "scorePlayer", state.scorePlayer);
            WriteCardMember(game, "scoreMita", state.scoreMita);
            WriteCardMember(game, "stepGame", state.stepGame);
            WriteCardMember(game, "timeStepGame", state.timeStepGame);
            WriteCardMember(game, "StopGameTime", state.stopGameTime);
            WriteCardIndices(game, "cardsPlayer", state.playerCardIndices);
            WriteCardIndices(game, "cardsMita", state.mitaCardIndices);
            ApplyCardVisualIndices(game, "cardsPlayer", state.playerCardIndices);
            ApplyCardVisualIndices(game, "cardsMita", state.mitaCardIndices);
        }

        private void DisableNonOwnerGameCardSimulation()
        {
            string owner;
            if (!_owners.TryGetValue(GameCardId, out owner) || owner == _localPlayerId) return;
            Location4TableCardGame game = FindCardGame();
            if (game == null) return;
            if (game.enabled) game.enabled = false;
            _disabledRemoteGameCardSimulation = true;
        }

        private static T ReadCardValue<T>(object game, string name, T fallback)
        {
            object value = ReadCardMember(game, name);
            if (value == null) return fallback;
            try
            {
                if (value is T) return (T)value;
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch { return fallback; }
        }

        private static object ReadCardMember(object target, string name)
        {
            if (target == null || string.IsNullOrEmpty(name)) return null;
            try
            {
                Type type = target.GetType();
                PropertyInfo property = AccessTools.Property(type, name);
                if (property != null && property.GetGetMethod(true) != null)
                    return property.GetValue(target, null);
                FieldInfo field = AccessTools.Field(type, name);
                return field != null ? field.GetValue(target) : null;
            }
            catch { return null; }
        }

        private static void WriteCardMember(object target, string name, object value)
        {
            if (target == null || string.IsNullOrEmpty(name) || value == null) return;
            try
            {
                Type type = target.GetType();
                PropertyInfo property = AccessTools.Property(type, name);
                if (property != null)
                {
                    MethodInfo setter = property.GetSetMethod(true);
                    if (setter != null)
                    {
                        setter.Invoke(target, new[] { ConvertCardValue(value, property.PropertyType) });
                        return;
                    }
                }
                FieldInfo field = AccessTools.Field(type, name);
                if (field != null && !field.IsInitOnly)
                    field.SetValue(target, ConvertCardValue(value, field.FieldType));
            }
            catch { }
        }

        private static object ConvertCardValue(object value, Type targetType)
        {
            Type underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (underlying.IsInstanceOfType(value)) return value;
            if (underlying.IsEnum) return Enum.ToObject(underlying, value);
            return Convert.ChangeType(value, underlying);
        }

        private static int[] ReadCardIndices(object game, string name)
        {
            object values = ReadCardMember(game, name);
            int count = GetCollectionLength(values);
            if (count <= 0) return Array.Empty<int>();
            int[] result = new int[count];
            for (int i = 0; i < count; i++)
            {
                object slot = GetCollectionItem(values, i);
                object cardObject = ReadCardMember(slot, "objectCard");
                result[i] = cardObject == null || (cardObject is UnityEngine.Object &&
                    (UnityEngine.Object)cardObject == null)
                    ? -1 : ReadCardValue(slot, "indexMemory", -1);
            }
            return result;
        }

        private static void ApplyCardVisualIndices(object game, string slotsName, int[] indices)
        {
            if (game == null || indices == null) return;
            object slots = ReadCardMember(game, slotsName);
            object generalCards = ReadCardMember(game, "cardsGeneral");
            int count = Math.Min(GetCollectionLength(slots), indices.Length);
            int generalCount = GetCollectionLength(generalCards);
            for (int i = 0; i < count; i++)
            {
                object slot = GetCollectionItem(slots, i);
                GameObject cardObject = ReadCardMember(slot, "objectCard") as GameObject;
                if (cardObject == null) continue;
                int index = indices[i];
                if (index < 0 || index >= generalCount)
                {
                    if (cardObject.activeSelf) cardObject.SetActive(false);
                    continue;
                }

                object cardData = GetCollectionItem(generalCards, index);
                Mesh mesh = ReadCardMember(cardData, "mesh") as Mesh;
                MeshFilter meshFilter = cardObject.GetComponent<MeshFilter>();
                if (mesh != null && meshFilter != null && meshFilter.mesh != mesh)
                    meshFilter.mesh = mesh;

                Location4TableCardGame_Card cardScript =
                    cardObject.GetComponent<Location4TableCardGame_Card>();
                if (cardScript != null)
                {
                    SetCardText(cardScript, "textHeart", ReadCardValue(cardData, "heart", 0));
                    SetCardText(cardScript, "textDamage", ReadCardValue(cardData, "damage", 0));
                    SetCardText(cardScript, "textShield", ReadCardValue(cardData, "shield", 0));
                }
                if (!cardObject.activeSelf) cardObject.SetActive(true);
            }
        }

        private static void SetCardText(object card, string memberName, int value)
        {
            UnityEngine.UI.Text text = ReadCardMember(card, memberName) as UnityEngine.UI.Text;
            string rendered = value.ToString();
            if (text != null && text.text != rendered) text.text = rendered;
        }

        private static void WriteCardIndices(object game, string name, int[] indices)
        {
            if (indices == null) return;
            object cards = ReadCardMember(game, name);
            int count = Math.Min(GetCollectionLength(cards), indices.Length);
            for (int i = 0; i < count; i++)
                WriteCardMember(GetCollectionItem(cards, i), "indexMemory", indices[i]);
        }

        private static int GetCollectionLength(object collection)
        {
            if (collection == null) return 0;
            try
            {
                Array array = collection as Array;
                if (array != null) return array.Length;
                object length = ReadCardMember(collection, "Length") ?? ReadCardMember(collection, "Count");
                return length == null ? 0 : Convert.ToInt32(length);
            }
            catch { return 0; }
        }

        private static object GetCollectionItem(object collection, int index)
        {
            if (collection == null || index < 0) return null;
            try
            {
                Array array = collection as Array;
                if (array != null) return index < array.Length ? array.GetValue(index) : null;
                PropertyInfo item = collection.GetType().GetProperty("Item",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return item != null ? item.GetValue(collection, new object[] { index }) : null;
            }
            catch { return null; }
        }

        private static int ArrayLength(Array array)
        {
            return array == null ? 0 : array.Length;
        }

        public void Dispose()
        {
            if (_dispatcher != null)
            {
                _dispatcher.CustomMessageReceived -= OnCustomMessage;
                _dispatcher.RemotePlayerLeft -= OnRemotePlayerLeft;
            }
            _dispatcher = null;
            if (_active == this) _active = null;
        }

        private static bool SetActivePrefix(GameObject __instance, bool value)
        {
            MinigameSync sync = _active;
            if (sync == null || _applyingSignalDepth > 0 || __instance == null)
                return true;

            try { return sync.BeforeSetActive(__instance, value); }
            catch (Exception ex)
            {
                TraceWarning("SetActive prefix failed: " + ex.GetType().Name + ": " + ex.Message);
                DiagnosticLog.Warning("MinigameSync: SetActive prefix failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
                return true;
            }
        }

        private static bool ObjectInteractiveClickPrefix(ObjectInteractive __instance)
        {
            MinigameSync sync = _active;
            if (sync == null || _replayingInputDepth > 0 || __instance == null)
                return true;

            try
            {
                ObjectInteractive captured = __instance;
                return sync.BeforePlayerInput(captured.gameObject,
                    "ObjectInteractive.Click", "interactive:" + captured.GetInstanceID(),
                    () => { if (captured != null) captured.Click(); }, true);
            }
            catch (Exception ex)
            {
                TraceWarning("ObjectInteractive.Click guard failed: " +
                             ex.GetType().Name + ": " + ex.Message);
                DiagnosticLog.Warning("MinigameSync: ObjectInteractive.Click guard failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
                return true;
            }
        }

        private static bool Location4TableCardGameStartPrefix(Location4TableCardGame __instance)
        {
            MinigameSync sync = _active;
            if (sync == null || _replayingInputDepth > 0 || __instance == null)
                return true;

            try
            {
                Location4TableCardGame captured = __instance;
                string path = LocalPlayerLocator.GetPath(captured.transform);
                Trace("Game Card StartGame() invoked at path='" + path + "' scene='" +
                      SceneManager.GetActiveScene().name + "'.");
                return sync.BeforePlayerInput(captured.gameObject,
                    "Location4TableCardGame.StartGame",
                    "game-card:start:" + captured.GetInstanceID(),
                    () => { if (captured != null) captured.StartGame(); }, false,
                    GameCardId);
            }
            catch (Exception ex)
            {
                TraceWarning("Game Card StartGame hook failed: " +
                             ex.GetType().Name + ": " + ex.Message);
                DiagnosticLog.Warning("MinigameSync: Game Card StartGame guard failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
                return true;
            }
        }

        private static void Location4TableCardGameStartPostfix(Location4TableCardGame __instance)
        {
            MinigameSync sync = _active;
            if (sync == null || __instance == null) return;

            string owner;
            if (!sync._owners.TryGetValue(GameCardId, out owner) ||
                !string.Equals(owner, sync._localPlayerId, StringComparison.Ordinal))
                return;

            try
            {
                sync.OnGameCardStarted(__instance);
                sync.EnsureGameCardOwnerControls();
            }
            catch (Exception ex)
            {
                TraceWarning("Game Card StartGame postfix control bind failed: " +
                             ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool MinigamesControllerStartGamePrefix(
            MinigamesController __instance, MinigamesAutomate __0)
        {
            MinigameSync sync = _active;
            if (sync == null || _replayingInputDepth > 0 || __instance == null)
                return true;

            SessionController session = SessionController.Active;
            if (session == null) return true;

            string automatePath = __0 != null
                ? LocalPlayerLocator.GetPath(__0.transform)
                : string.Empty;
            if (automatePath != null) automatePath = automatePath.Replace('\\', '/');
            bool isGameCard = IsSameOrDescendant(automatePath, GameCardPath);
            string gameId = isGameCard
                ? GameCardId
                : sync.CreateDynamicMinigameId("controller",
                    __0 != null ? __0.transform : null);
            if (string.IsNullOrEmpty(gameId))
            {
                TraceWarning("MinigamesController.StartGame received no MinigamesAutomate path; " +
                             "blocking the local start until it can be identified safely.");
                return false;
            }

            int instanceId = __instance.GetInstanceID();
            if (IsDynamicMinigameId(gameId)) sync._dynamicMinigameIds.Add(gameId);
            sync._minigameIdByControllerInstance[instanceId] = gameId;
            MinigamesController capturedController = __instance;
            MinigamesAutomate capturedAutomate = __0;
            return sync.BeforePlayerInput(capturedAutomate.gameObject,
                "MinigamesController.StartGame",
                "controller-start:" + instanceId + ":" + gameId,
                () =>
                {
                    if (capturedController != null && capturedAutomate != null)
                        capturedController.StartGame(capturedAutomate);
                }, true, IsDynamicMinigameId(gameId) ? gameId : null);
        }

        private static void MinigamesControllerStartGamePostfix(
            MinigamesController __instance, MinigamesAutomate __0)
        {
            MinigameSync sync = _active;
            if (sync == null || __instance == null || __0 == null) return;
            string gameId;
            if (!sync._minigameIdByControllerInstance.TryGetValue(
                    __instance.GetInstanceID(), out gameId))
                return;
            string owner;
            if (sync._owners.TryGetValue(gameId, out owner) && owner == sync._localPlayerId)
            {
                Trace("MinigamesController.StartGame ran under local owner for id='" +
                      gameId + "' automate='" + __0.name + "'.");
                if (gameId == GameCardId)
                {
                    sync.OnGameCardStarted(sync.FindCardGame());
                    sync.EnsureGameCardOwnerControls();
                }
            }
        }

        private static bool MinigamesControllerFinishPrefix(MinigamesController __instance)
        {
            MinigameSync sync = _active;
            return sync == null || _replayingInputDepth > 0 || __instance == null ||
                   sync.AllowControllerFinish(__instance.GetInstanceID(), "GameFinish");
        }

        private static void MinigamesControllerFinishPostfix(MinigamesController __instance)
        {
            MinigameSync sync = _active;
            if (sync != null && __instance != null)
                sync.ReleaseControllerMinigame(__instance.GetInstanceID(), "GameFinish");
        }

        private static bool MinigamesControllerExitPrefix(MinigamesController __instance)
        {
            MinigameSync sync = _active;
            return sync == null || _replayingInputDepth > 0 || __instance == null ||
                   sync.AllowControllerFinish(__instance.GetInstanceID(), "ExitGame");
        }

        private static void MinigamesControllerExitPostfix(MinigamesController __instance)
        {
            MinigameSync sync = _active;
            if (sync != null && __instance != null)
                sync.ReleaseControllerMinigame(__instance.GetInstanceID(), "ExitGame");
        }

        private static bool MinigamesControllerUpdatePrefix(MinigamesController __instance)
        {
            MinigameSync sync = _active;
            if (sync == null || __instance == null) return true;
            string gameId;
            if (!sync._minigameIdByControllerInstance.TryGetValue(
                    __instance.GetInstanceID(), out gameId))
                return true;
            return sync.CanRunOwnedMinigameUpdate(gameId);
        }

        private static bool TamagotchiMainMiniGamePlayPrefix(
            Tamagotchi_Main __instance, Tamagotchi_MiniGame __0)
        {
            MinigameSync sync = _active;
            if (sync == null || _replayingInputDepth > 0 || __instance == null)
                return true;
            SessionController session = SessionController.Active;
            if (session == null) return true;

            string gameId = sync.CreateDynamicMinigameId("tamagotchi",
                __0 != null ? __0.transform : null);
            if (string.IsNullOrEmpty(gameId))
            {
                TraceWarning("Tamagotchi_Main.MiniGamePlay received no minigame path; " +
                             "blocking the local start until it can be identified safely.");
                return false;
            }

            int instanceId = __instance.GetInstanceID();
            sync._dynamicMinigameIds.Add(gameId);
            sync._minigameIdByControllerInstance[instanceId] = gameId;
            Tamagotchi_Main capturedMain = __instance;
            Tamagotchi_MiniGame capturedGame = __0;
            return sync.BeforePlayerInput(capturedGame.gameObject,
                "Tamagotchi_Main.MiniGamePlay",
                "tamagotchi-start:" + instanceId + ":" + gameId,
                () =>
                {
                    if (capturedMain != null && capturedGame != null)
                        capturedMain.MiniGamePlay(capturedGame);
                }, true, gameId);
        }

        private static void TamagotchiMainMiniGamePlayPostfix(
            Tamagotchi_Main __instance, Tamagotchi_MiniGame __0)
        {
            MinigameSync sync = _active;
            if (sync == null || __instance == null || __0 == null) return;
            string gameId;
            if (sync._minigameIdByControllerInstance.TryGetValue(
                    __instance.GetInstanceID(), out gameId))
                Trace("Tamagotchi minigame entered via MiniGamePlay id='" + gameId +
                      "' object='" + __0.name + "'.");
        }

        private static bool TamagotchiMainMiniGameStopPrefix(Tamagotchi_Main __instance)
        {
            MinigameSync sync = _active;
            return sync == null || _replayingInputDepth > 0 || __instance == null ||
                   sync.AllowControllerFinish(__instance.GetInstanceID(), "MiniGameStop");
        }

        private static void TamagotchiMainMiniGameStopPostfix(Tamagotchi_Main __instance)
        {
            MinigameSync sync = _active;
            if (sync != null && __instance != null)
                sync.ReleaseControllerMinigame(__instance.GetInstanceID(), "MiniGameStop");
        }

        private static bool TamagotchiMiniGameStartPrefix(
            Tamagotchi_MiniGame __instance, Transform __0)
        {
            MinigameSync sync = _active;
            if (sync == null || _replayingInputDepth > 0 || __instance == null)
                return true;
            SessionController session = SessionController.Active;
            if (session == null) return true;

            string gameId = sync.CreateDynamicMinigameId("tamagotchi", __instance.transform);
            if (string.IsNullOrEmpty(gameId)) return false;
            int instanceId = __instance.GetInstanceID();
            sync._dynamicMinigameIds.Add(gameId);
            sync._minigameIdByControllerInstance[instanceId] = gameId;
            Tamagotchi_MiniGame capturedGame = __instance;
            Transform capturedCamera = __0;
            return sync.BeforePlayerInput(capturedGame.gameObject,
                "Tamagotchi_MiniGame.StartGame",
                "tamagotchi-start:" + instanceId + ":" + gameId,
                () =>
                {
                    if (capturedGame != null)
                        capturedGame.StartGame(capturedCamera);
                }, true, gameId);
        }

        private static bool TamagotchiMiniGameStopPrefix(Tamagotchi_MiniGame __instance)
        {
            MinigameSync sync = _active;
            return sync == null || _replayingInputDepth > 0 || __instance == null ||
                   sync.AllowDynamicMinigameStop(
                       sync.CreateDynamicMinigameId("tamagotchi", __instance.transform),
                       "Tamagotchi_MiniGame.StopGame");
        }

        private static void TamagotchiMiniGameStopPostfix(Tamagotchi_MiniGame __instance)
        {
            MinigameSync sync = _active;
            if (sync != null && __instance != null)
                sync.ReleaseDynamicMinigame(
                    sync.CreateDynamicMinigameId("tamagotchi", __instance.transform),
                    "Tamagotchi_MiniGame.StopGame");
        }

        private static bool TamagotchiMiniGameUpdatePrefix(Tamagotchi_MiniGame __instance)
        {
            MinigameSync sync = _active;
            return sync == null || __instance == null || sync.CanRunOwnedMinigameUpdate(
                sync.CreateDynamicMinigameId("tamagotchi", __instance.transform));
        }

        private static void InterfaceKeyHintStartPostfix(Interface_KeyHint_Key __instance)
        {
            MinigameSync sync = _active;
            if (sync == null || __instance == null) return;

            string owner;
            if (!sync._owners.TryGetValue(GameCardId, out owner) ||
                !string.Equals(owner, sync._localPlayerId, StringComparison.Ordinal))
                return;

            try
            {
                string path = LocalPlayerLocator.GetPath(__instance.transform);
                if (!IsSameOrDescendant(path, GameCardPath + "/Game Mechanic") &&
                    !IsSameOrDescendant(path, GameCardPath + "/Interface Game"))
                    return;

                Transform localPlayer = LocalPlayerLocator.FindHardcodedPlayerPath();
                PlayerMove localMove = localPlayer != null
                    ? localPlayer.GetComponent<PlayerMove>()
                    : null;
                if (localMove != null && __instance.scrpm != localMove)
                    __instance.scrpm = localMove;
            }
            catch (Exception ex)
            {
                TraceWarning("Game Card key-hint startup rebind failed: " +
                             ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool ButtonMouseClickPointerUpPrefix(ButtonMouseClick __instance)
        {
            return GuardButtonInput(__instance, "PointerUp", "pointer-up");
        }

        private static bool ButtonMouseClickPointerDownPrefix(ButtonMouseClick __instance,
                                                               UnityEngine.EventSystems.PointerEventData eventData)
        {
            MinigameSync sync = _active;
            if (sync == null || _replayingInputDepth > 0 || __instance == null)
                return true;

            try
            {
                ButtonMouseClick captured = __instance;
                UnityEngine.EventSystems.PointerEventData capturedEvent = eventData;
                return sync.BeforePlayerInput(captured.gameObject,
                    "ButtonMouseClick.OnPointerDown",
                    "button:pointer-down:" + captured.GetInstanceID(),
                    () =>
                    {
                        if (captured != null)
                            captured.OnPointerDown(capturedEvent);
                    }, false);
            }
            catch (Exception ex)
            {
                TraceWarning("ButtonMouseClick.OnPointerDown guard failed: " +
                             ex.GetType().Name + ": " + ex.Message);
                DiagnosticLog.Warning("MinigameSync: ButtonMouseClick.OnPointerDown guard failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
                return true;
            }
        }

        private static bool ButtonMouseClickKeyDownPrefix(ButtonMouseClick __instance)
        {
            return GuardButtonInput(__instance, "keyDown", "key-down");
        }

        private static bool GuardButtonInput(ButtonMouseClick button, string methodName,
                                             string actionKey)
        {
            MinigameSync sync = _active;
            if (sync == null || _replayingInputDepth > 0 || button == null)
                return true;

            try
            {
                ButtonMouseClick captured = button;
                return sync.BeforePlayerInput(captured.gameObject,
                    "ButtonMouseClick." + methodName,
                    "button:" + actionKey + ":" + captured.GetInstanceID(),
                    () =>
                    {
                        if (captured == null) return;
                        if (methodName == "PointerUp") captured.PointerUp();
                        else if (captured.eventClick != null) captured.eventClick.Invoke();
                        else captured.keyDown();
                    }, false);
            }
            catch (Exception ex)
            {
                TraceWarning("ButtonMouseClick." + methodName + " guard failed: " +
                             ex.GetType().Name + ": " + ex.Message);
                DiagnosticLog.Warning("MinigameSync: ButtonMouseClick." + methodName +
                                      " guard failed: " + ex.GetType().Name + ": " + ex.Message);
                return true;
            }
        }

        private bool BeforePlayerInput(GameObject target, string source, string actionKey,
                                       Action replay, bool objectInteraction,
                                       string forcedGameId = null)
        {
            if (target == null) return true;
            string path = LocalPlayerLocator.GetPath(target.transform);
            string gameId;
            bool isControl;
            bool isFinishSignal;
            bool classified;
            if (!string.IsNullOrEmpty(forcedGameId) &&
                (IsDynamicMinigameId(forcedGameId) || forcedGameId == GameCardId))
            {
                gameId = forcedGameId;
                isControl = false;
                isFinishSignal = false;
                classified = true;
                if (IsDynamicMinigameId(gameId)) _dynamicMinigameIds.Add(gameId);
            }
            else
            {
                classified = TryClassifyPath(path, out gameId, out isControl,
                    out isFinishSignal);
            }
            if (!classified)
            {
                if (source == "Location4TableCardGame.StartGame")
                    TraceWarning("StartGame() path is outside registered minigame roots: '" + path +
                                 "'. No lock was claimed; expected '" + GameCardPath + "'.");
                return true;
            }
            if (!IsStartInputPath(path, gameId, isControl, objectInteraction, source))
            {
                if (source == "Location4TableCardGame.StartGame")
                    TraceWarning("StartGame() classified as '" + gameId +
                                 "' but path is not an accepted start path: '" + path + "'.");
                return true;
            }

            SessionController session = SessionController.Active;
            if (session == null)
                return true;
            if (gameId == GameCardId && _dispatcher == null)
            {
                TraceWarning("Game Card start blocked: multiplayer session is active but " +
                             "the peer-lock transport is unavailable; refusing to start " +
                             "without broadcasting ownership.");
                ShowBusyMessage(GameCardId, null);
                return false;
            }
            if (_dispatcher == null && gameId != GameCardId)
            {
                TraceWarning("input at '" + path + "' blocked: multiplayer session is active " +
                             "but the host approval channel is unavailable.");
                return false;
            }

            string owner;
            bool hasOwner = _owners.TryGetValue(gameId, out owner) &&
                            !string.IsNullOrEmpty(owner);
            if (IsDynamicMinigameId(gameId) &&
                _pendingDynamicMinigameReleases.Contains(gameId))
            {
                Trace("input blocked while release is awaiting host confirmation for game='" +
                      gameId + "' source=" + source + ".");
                return false;
            }
            if (hasOwner)
            {
                if (owner == _localPlayerId)
                {
                    Trace("input allowed: game='" + gameId + "' local player owns lock; source=" +
                          source + " path='" + path + "'.");
                    return true;
                }

                DiagnosticLog.Info("MinigameSync: blocked " + source + " at '" + path +
                                   "'; " + DisplayName(gameId) + " is owned by '" + owner + "'.");
                Trace("input blocked: game='" + gameId + "' owner='" + owner +
                      "' contender='" + _localPlayerId + "' source=" + source +
                      " path='" + path + "'.");
                ShowBusyMessage(gameId, owner);
                return false;
            }

            if (_completedGames.Contains(gameId))
            {
                Trace("input blocked: game='" + gameId + "' is marked complete; source=" +
                      source + " path='" + path + "'.");
                DiagnosticLog.Info("MinigameSync: blocked " + source + " at '" + path +
                                   "'; " + DisplayName(gameId) + " is already complete.");
                return false;
            }

            if (gameId == GameCardId)
            {
                if (!SetLocalGameCardOwner())
                {
                    string currentOwner;
                    _owners.TryGetValue(GameCardId, out currentOwner);
                    Trace("local Game Card start denied through " + source +
                          " at path='" + path + "'; current owner='" +
                          (currentOwner ?? "none") + "'.");
                    ShowBusyMessage(GameCardId, currentOwner);
                    return false;
                }
                Trace("local Game Card started immediately through " + source +
                      " at path='" + path + "'; broadcasting only the peer lock, with no host approval or gameplay-state replication.");
                return true;
            }

            if (session.IsHost)
            {
                SetOwnerAndBroadcast(gameId, _localPlayerId);
                Trace("local host claimed game='" + gameId + "' through " + source +
                      " at path='" + path + "'.");
                DiagnosticLog.Info("MinigameSync: player input claim from " + source +
                                   " at '" + path + "'; host acquired " + DisplayName(gameId) + ".");
                return true;
            }

            QueuePendingStartAction(gameId, actionKey, replay);
            RequestHostApproval(gameId, source, path, session);
            return false;
        }

        private string CreateDynamicMinigameId(string scope, Transform target)
        {
            if (target == null || string.IsNullOrWhiteSpace(scope)) return null;
            string path = LocalPlayerLocator.GetPath(target);
            if (string.IsNullOrWhiteSpace(path)) return null;
            path = path.Replace('\\', '/');
            string scene = SceneManager.GetActiveScene().name;
            if (string.IsNullOrWhiteSpace(scene)) return null;
            return DynamicMinigameIdPrefix + scope + "|" + scene + "|" + path;
        }

        private bool CanRunOwnedMinigameUpdate(string gameId)
        {
            if (gameId != GameCardId && !IsDynamicMinigameId(gameId)) return true;
            SessionController session = SessionController.Active;
            if (session == null) return true;
            if (_pendingClaims.Contains(gameId) ||
                _pendingDynamicMinigameReleases.Contains(gameId))
                return false;
            string owner;
            return !_owners.TryGetValue(gameId, out owner) ||
                   string.IsNullOrEmpty(owner) || owner == _localPlayerId;
        }

        private bool AllowControllerFinish(int instanceId, string source)
        {
            string gameId;
            if (!_minigameIdByControllerInstance.TryGetValue(instanceId, out gameId))
                return true;
            if (gameId == GameCardId)
            {
                string owner;
                if (!_owners.TryGetValue(GameCardId, out owner) || string.IsNullOrEmpty(owner) ||
                    owner == _localPlayerId)
                    return true;
                ShowBusyMessage(GameCardId, owner);
                return false;
            }
            return AllowDynamicMinigameStop(gameId, source);
        }

        private bool AllowDynamicMinigameStop(string gameId, string source)
        {
            SessionController session = SessionController.Active;
            if (session == null || !IsDynamicMinigameId(gameId)) return true;
            string owner;
            if (!_owners.TryGetValue(gameId, out owner) || string.IsNullOrEmpty(owner))
                return true;
            if (owner == _localPlayerId) return true;
            Trace("blocked non-owner " + source + " for minigame='" + gameId +
                  "' currentOwner='" + owner + "'.");
            ShowBusyMessage(gameId, owner);
            return false;
        }

        private void ReleaseControllerMinigame(int instanceId, string source)
        {
            string gameId;
            if (!_minigameIdByControllerInstance.TryGetValue(instanceId, out gameId))
                return;
            ReleaseDynamicMinigame(gameId, source);
        }

        private void ReleaseDynamicMinigame(string gameId, string source)
        {
            if (!IsDynamicMinigameId(gameId)) return;
            SessionController session = SessionController.Active;
            if (session == null) return;
            string owner;
            if (!_owners.TryGetValue(gameId, out owner) || owner != _localPlayerId)
                return;
            if (!_pendingDynamicMinigameReleases.Add(gameId)) return;

            Trace(source + " released minigame='" + gameId + "' locally; notifying host.");
            if (session.IsHost)
            {
                ReleaseDynamicMinigameOnHost(gameId, _localPlayerId);
                return;
            }

            SendSignal(new MinigameSignal
            {
                kind = "release",
                senderId = _localPlayerId,
                ownerId = _localPlayerId,
                sceneName = SceneManager.GetActiveScene().name,
                gameId = gameId
            });
        }

        private void ReleaseDynamicMinigameOnHost(string gameId, string senderId)
        {
            string owner;
            if (!_owners.TryGetValue(gameId, out owner) || string.IsNullOrEmpty(owner) ||
                !string.Equals(owner, senderId, StringComparison.Ordinal))
            {
                Trace("ignored release for minigame='" + gameId + "' sender='" +
                      senderId + "' currentOwner='" + (owner ?? "none") + "'.");
                return;
            }

            _dynamicMinigameIds.Add(gameId);
            _owners[gameId] = string.Empty;
            _pendingDynamicMinigameReleases.Remove(gameId);
            _pendingClaims.Remove(gameId);
            _nextClaimRetry.Remove(gameId);
            _claimStartedAt.Remove(gameId);
            ClearPendingControls(gameId);
            ClearPendingStartActions(gameId);
            ClearDynamicMinigameControllerMappings(gameId);
            BroadcastState(gameId, string.Empty, false, true);
            DiagnosticLog.Info("MinigameSync: released minigame '" + DisplayName(gameId) +
                               "' after owner '" + senderId + "' exited.");
        }

        private void ClearDynamicMinigameControllerMappings(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return;
            List<int> remove = null;
            foreach (KeyValuePair<int, string> pair in _minigameIdByControllerInstance)
            {
                if (!string.Equals(pair.Value, gameId, StringComparison.Ordinal)) continue;
                if (remove == null) remove = new List<int>();
                remove.Add(pair.Key);
            }
            if (remove == null) return;
            for (int i = 0; i < remove.Count; i++)
                _minigameIdByControllerInstance.Remove(remove[i]);
        }

        private void BindMinigamesControllerUpdate(string gameId)
        {
            if (!IsControllerMinigameId(gameId)) return;
            try
            {
                MinigamesController controller = UnityEngine.Object
                    .FindObjectOfType<MinigamesController>();
                if (controller != null)
                    _minigameIdByControllerInstance[controller.GetInstanceID()] = gameId;
            }
            catch (Exception ex)
            {
                TraceWarning("could not bind MinigamesController.Update to owner lock '" +
                             gameId + "': " + ex.GetType().Name + ".");
            }
        }

        private static void SetActivePostfix(GameObject __instance, bool value)
        {
            MinigameSync sync = _active;
            if (sync == null || _applyingSignalDepth > 0 || !value || __instance == null)
                return;

            try { sync.AfterSetActive(__instance); }
            catch (Exception ex)
            {
                TraceWarning("SetActive postfix failed: " + ex.GetType().Name + ": " + ex.Message);
                DiagnosticLog.Warning("MinigameSync: SetActive postfix failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
        }

        private bool BeforeSetActive(GameObject go, bool value)
        {
            if (!value) return true;

            string path = LocalPlayerLocator.GetPath(go.transform);
            string gameId;
            bool isControl;
            bool isFinishSignal;
            if (!TryClassifyPath(path, out gameId, out isControl, out isFinishSignal))
                return true;

            SessionController session = SessionController.Active;
            if (session == null) return true;
            if (_dispatcher == null)
            {
                if (gameId == GameCardId)
                {
                    if (isFinishSignal) return CompleteLocalGameCard();
                    if (!isControl) return true;
                    string localOwner;
                    return _owners.TryGetValue(GameCardId, out localOwner) &&
                           localOwner == _localPlayerId;
                }
                return !isControl && !isFinishSignal;
            }

            if (isFinishSignal)
                return HandleFinishActivation(gameId, go, session);
            if (!isControl) return true;

            string owner;
            bool hasOwner = _owners.TryGetValue(gameId, out owner) &&
                            !string.IsNullOrEmpty(owner);
            if (hasOwner)
            {
                if (owner == _localPlayerId)
                    return true;

                TraceControlOnce("owned:" + owner, path,
                    "blocked control reactivation for game='" + gameId + "' owner='" + owner +
                    "' path='" + path + "'.");
                if (go.activeSelf)
                {
                    SetActiveFromSignal(go, false);
                }
                return false;
            }

            if (_completedGames.Contains(gameId)) return false;

            // Keep the controls hidden until the host grants the claim. The
            // accepted owner state restores the queued controls on that client.
            if (!session.IsHost && _pendingClaims.Contains(gameId))
            {
                if (gameId != GameCardId) QueuePendingControl(gameId, go);
                if (go.activeSelf) SetActiveFromSignal(go, false);
                TraceControlOnce("pending-owner", path,
                    "held local control while waiting for host approval; game='" +
                    gameId + "' path='" + path + "'.");
                return false;
            }

            // Activation is quest/game setup, not proof that a player chose
            // to start. Keep the actual interface hidden until an input hook
            // claims ownership; otherwise whichever client initializes first
            // can silently own the minigame before either player touches it.
            if (gameId != GameCardId) QueuePendingControl(gameId, go);
            if (go.activeSelf) SetActiveFromSignal(go, false);
            TraceControlOnce("unowned", path,
                "held unowned control activation for game='" + gameId +
                "' at path='" + path + "'; waiting for player input.");
            return false;
        }

        private void RequestHostApproval(string gameId, string source, string path,
                                         SessionController session)
        {
            string localId = _localPlayerId;
            if (string.IsNullOrWhiteSpace(localId) && session != null)
                localId = session.LocalPlayerId;
            if (string.IsNullOrWhiteSpace(localId))
            {
                TraceWarning("cannot claim game='" + gameId + "' from " + source +
                             "; local player ID is empty.");
                return;
            }
            if (string.IsNullOrWhiteSpace(_localPlayerId))
                _localPlayerId = localId;

            if (session != null && !string.IsNullOrWhiteSpace(session.LocalPlayerId) &&
                !string.Equals(session.LocalPlayerId, localId, StringComparison.Ordinal))
                TraceWarning("minigame claimant ID mismatch: transport localId='" + localId +
                             "' session localId='" + session.LocalPlayerId +
                             "'; using transport identity for the host claim.");

            if (!_pendingClaims.Add(gameId))
            {
                Trace("host approval already pending game='" + gameId + "' source='" +
                      source + "' path='" + path + "'.");
                return;
            }

            _claimStartedAt[gameId] = Time.unscaledTime;
            Trace("requested host approval before starting game='" + gameId +
                  "' playerId='" + localId + "' source='" + source +
                  "' path='" + path + "'. Local game remains blocked.");
            SendClaim(gameId);
        }

        private void AfterSetActive(GameObject go)
        {
            string path = LocalPlayerLocator.GetPath(go.transform);
            string gameId;
            bool isControl;
            bool isFinishSignal;
            if (!TryClassifyPath(path, out gameId, out isControl, out isFinishSignal)) return;

            if (gameId == GameCardId)
            {
                string owner;
                if (_owners.TryGetValue(GameCardId, out owner) &&
                    string.Equals(owner, _localPlayerId, StringComparison.Ordinal) &&
                    isControl)
                    EnsureGameCardOwnerControls();
            }

            if (!isFinishSignal || !string.Equals(gameId, GameCardId, StringComparison.Ordinal))
                return;

            if (_completedGames.Contains(GameCardId))
                ActivateQuestTwo();
        }

        private bool HandleFinishActivation(string gameId, GameObject go,
                                            SessionController session)
        {
            if (gameId == GameCardId)
                return CompleteLocalGameCard();

            string owner;
            if (!_owners.TryGetValue(gameId, out owner) ||
                string.IsNullOrEmpty(owner) || owner != _localPlayerId)
                return false;

            if (session.IsHost)
            {
                CompleteOnHost(gameId, _localPlayerId, go);
                return true;
            }

            // A client waits for the host's accepted completion signal. This
            // prevents its local story scripts from starting Quest 2 alone.
            if (_pendingFinishes.Add(gameId))
                SendSignal(new MinigameSignal
                {
                    kind = "finish",
                    senderId = _localPlayerId,
                    sceneName = SceneManager.GetActiveScene().name,
                    gameId = gameId,
                    ownerId = _localPlayerId
                });
            return false;
        }

        private void OnCustomMessage(string eventName, string json)
        {
            if (!string.Equals(eventName, SignalEvent, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(json))
                return;

            try
            {
                MinigameSignal signal = JsonSerializer.Deserialize<MinigameSignal>(json, JsonOptions);
                if (signal == null)
                {
                    TraceWarning("received an empty/unreadable minigame signal.");
                    return;
                }

                string activeScene = SceneManager.GetActiveScene().name;
                if (string.IsNullOrWhiteSpace(signal.kind) ||
                    string.IsNullOrWhiteSpace(signal.senderId) ||
                    !IsSupportedGame(signal.gameId))
                {
                    TraceWarning("rejected malformed/unsupported signal: kind='" + signal.kind +
                                 "' sender='" + signal.senderId + "' game='" + signal.gameId +
                                 "' scene='" + signal.sceneName + "'.");
                    return;
                }

                if (!string.Equals(signal.sceneName, activeScene, StringComparison.Ordinal))
                {
                    TraceWarning("ignored stale/cross-scene signal: kind='" + signal.kind +
                                 "' sender='" + signal.senderId + "' game='" + signal.gameId +
                                 "' messageScene='" + signal.sceneName + "' activeScene='" +
                                 activeScene + "'.");
                    return;
                }

                if (signal.senderId == _localPlayerId) return;

                if (IsDynamicMinigameId(signal.gameId))
                    _dynamicMinigameIds.Add(signal.gameId);

                SessionController session = SessionController.Active;
                if (session == null)
                {
                    TraceWarning("received " + signal.kind + " from '" + signal.senderId +
                                 "' for '" + signal.gameId + "' but no active session exists.");
                    return;
                }

                if (signal.gameId == GameCardId)
                {
                    if (signal.kind == "card-owner")
                    {
                        ApplyPeerGameCardOwner(signal, session);
                        return;
                    }
                    if (signal.kind == "card-finish")
                    {
                        ApplyPeerGameCardFinish(signal, session);
                        return;
                    }
                    if (signal.kind == "card-state" || signal.kind == "card-mita-state" ||
                        signal.kind == "card-request" || signal.kind == "card-snapshot" ||
                        signal.kind == "started")
                    {
                        Trace("ignored legacy Game Card gameplay-state signal kind='" +
                              signal.kind + "' from '" + signal.senderId +
                              "'; Game Card simulation is local-only.");
                        return;
                    }
                }

                if (signal.kind != "card-snapshot" && signal.kind != "card-state" &&
                    signal.kind != "card-mita-state")
                    Trace("rx kind='" + signal.kind + "' game='" + signal.gameId +
                          "' sender='" + signal.senderId + "' recipient='" +
                          (signal.recipientId ?? "") + "' owner='" + (signal.ownerId ?? "") +
                          "' revision=" + signal.revision + " completed=" + signal.completed +
                          " scene='" + signal.sceneName + "' role=" +
                          (session.IsHost ? "host" : "client") + ".");

                if (session.IsHost)
                {
                    if (signal.kind == "claim") HandleClaimRequest(signal, session);
                    else if (signal.kind == "release" &&
                             IsDynamicMinigameId(signal.gameId))
                        HandleDynamicMinigameReleaseRequest(signal, session);
                    else if (signal.kind == "finish") HandleFinishRequest(signal, session);
                    else if (signal.kind == "started" && signal.gameId == GameCardId)
                        HandleCardGameStarted(signal, session);
                    else if (signal.kind == "card-snapshot" && signal.gameId == GameCardId)
                        HandleCardGameSnapshot(signal, session);
                    else TraceWarning("host ignored unexpected signal kind='" + signal.kind +
                                      "' from '" + signal.senderId + "'.");
                    return;
                }

                if (signal.kind == "claim")
                {
                    Trace("client ignored peer claim; only host owner state can approve game='" +
                          signal.gameId + "' sender='" + signal.senderId + "'.");
                    return;
                }

                if (!IsSessionHost(session, signal.senderId))
                {
                    TraceWarning("client rejected non-host signal kind='" + signal.kind +
                                 "' from '" + signal.senderId + "' for game='" +
                                 signal.gameId + "'.");
                    return;
                }
                if (signal.kind == "state") ApplyAuthoritativeState(signal);
                else if (signal.kind == "denied") ApplyDenial(signal);
                else TraceWarning("client ignored unexpected host signal kind='" +
                                  signal.kind + "' for game='" + signal.gameId + "'.");
            }
            catch (Exception ex)
            {
                TraceWarning("signal receive failed: " + ex.GetType().Name + ": " + ex.Message);
                DiagnosticLog.Warning("MinigameSync: signal receive failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void HandleClaimRequest(MinigameSignal request, SessionController session)
        {
            if (IsDynamicMinigameId(request.gameId))
                _dynamicMinigameIds.Add(request.gameId);
            if (!IsKnownParticipant(session, request.senderId))
            {
                TraceWarning("host rejected claim from unknown participant='" + request.senderId +
                             "' game='" + request.gameId + "'.");
                DiagnosticLog.Warning("MinigameSync: rejected " + DisplayName(request.gameId) +
                                      " claim from unknown session participant '" +
                                      request.senderId + "'.");
                SendDenied(request.gameId, request.senderId);
                return;
            }

            if (_completedGames.Contains(request.gameId))
            {
                Trace("host denied claim game='" + request.gameId + "' requester='" +
                      request.senderId + "' reason=completed.");
                DiagnosticLog.Info("MinigameSync: rejected " + DisplayName(request.gameId) +
                                   " claim from '" + request.senderId + "' because it is complete.");
                SendDenied(request.gameId, request.senderId);
                return;
            }

            string owner;
            if (!_owners.TryGetValue(request.gameId, out owner) || string.IsNullOrEmpty(owner))
            {
                Trace("host granting claim game='" + request.gameId + "' to requester='" +
                      request.senderId + "' (previous owner empty).");
                SetOwnerAndBroadcast(request.gameId, request.senderId);
                DiagnosticLog.Info("MinigameSync: granted " + DisplayName(request.gameId) +
                                   " to player '" + request.senderId + "'.");
                return;
            }

            if (owner == request.senderId)
            {
                Trace("host reaffirming claim game='" + request.gameId + "' owner='" + owner +
                      "' (duplicate request).");
                BroadcastState(request.gameId, owner, false, false);
                return;
            }

            Trace("host denying claim game='" + request.gameId + "' requester='" +
                  request.senderId + "' currentOwner='" + owner + "'.");
            DiagnosticLog.Info("MinigameSync: denied " + DisplayName(request.gameId) +
                               " claim from '" + request.senderId + "'; owner is '" + owner + "'.");
            BroadcastState(request.gameId, owner, false, false);
            SendDenied(request.gameId, request.senderId);
        }

        private void HandleDynamicMinigameReleaseRequest(MinigameSignal request,
                                                          SessionController session)
        {
            if (!IsDynamicMinigameId(request.gameId) ||
                !IsKnownParticipant(session, request.senderId))
            {
                TraceWarning("host rejected malformed/unrecognized minigame release from '" +
                             request.senderId + "'.");
                return;
            }

            string owner;
            if (!_owners.TryGetValue(request.gameId, out owner) ||
                !string.Equals(owner, request.senderId, StringComparison.Ordinal))
            {
                Trace("host rejected release game='" + request.gameId + "' sender='" +
                      request.senderId + "' currentOwner='" + (owner ?? "none") + "'.");
                SendDenied(request.gameId, request.senderId);
                return;
            }

            Trace("host accepted release game='" + request.gameId + "' from owner='" +
                  request.senderId + "'.");
            ReleaseDynamicMinigameOnHost(request.gameId, request.senderId);
        }

        private void HandleFinishRequest(MinigameSignal request, SessionController session)
        {
            if (!IsKnownParticipant(session, request.senderId))
            {
                TraceWarning("host rejected finish request from unknown participant='" +
                             request.senderId + "' game='" + request.gameId + "'.");
                return;
            }
            string owner;
            if (!_owners.TryGetValue(request.gameId, out owner) ||
                string.IsNullOrEmpty(owner) || owner != request.senderId)
            {
                Trace("host rejected finish request game='" + request.gameId + "' requester='" +
                      request.senderId + "' currentOwner='" + (owner ?? "none") + "'.");
                SendDenied(request.gameId, request.senderId);
                return;
            }

            Trace("host accepted finish request game='" + request.gameId + "' from='" +
                  request.senderId + "'.");
            CompleteOnHost(request.gameId, request.senderId, null);
        }

        private void ApplyAuthoritativeState(MinigameSignal signal)
        {
            // The host's initial empty baseline can arrive after a peer has
            // already started Game Card locally. A peer owner packet outranks
            // that stale baseline regardless of transport timing.
            if (signal.gameId == GameCardId && !signal.completed &&
                string.IsNullOrEmpty(signal.ownerId))
            {
                string peerOwner;
                if (_owners.TryGetValue(GameCardId, out peerOwner) &&
                    !string.IsNullOrEmpty(peerOwner))
                {
                    Trace("ignored stale empty host baseline for Game Card; peer owner='" +
                          peerOwner + "'.");
                    return;
                }
            }

            if (IsDynamicMinigameId(signal.gameId))
                _dynamicMinigameIds.Add(signal.gameId);
            int currentRevision;
            bool hasRevision = _revisions.TryGetValue(signal.gameId, out currentRevision);
            if (hasRevision && signal.revision < currentRevision)
            {
                TraceWarning("ignored older state game='" + signal.gameId + "' revision=" +
                             signal.revision + " current=" + currentRevision + ".");
                return;
            }

            string oldOwner;
            bool hasOwner = _owners.TryGetValue(signal.gameId, out oldOwner);
            bool ownerChanged = !hasOwner ||
                !string.Equals(oldOwner ?? string.Empty, signal.ownerId ?? string.Empty,
                    StringComparison.Ordinal);
            if (hasRevision && signal.revision == currentRevision && hasOwner &&
                !string.Equals(oldOwner ?? string.Empty, signal.ownerId ?? string.Empty,
                    StringComparison.Ordinal))
            {
                TraceWarning("ignored conflicting same-revision state game='" + signal.gameId +
                             "' revision=" + signal.revision + " currentOwner='" +
                             (oldOwner ?? string.Empty) + "' incomingOwner='" +
                             (signal.ownerId ?? string.Empty) + "'.");
                return;
            }
            if (hasRevision && signal.revision == currentRevision && hasOwner &&
                string.Equals(oldOwner ?? string.Empty, signal.ownerId ?? string.Empty,
                    StringComparison.Ordinal) &&
                (!signal.completed || _completedGames.Contains(signal.gameId)))
                return;

            _revisions[signal.gameId] = signal.revision;
            _owners[signal.gameId] = signal.ownerId ?? string.Empty;
            if (IsControllerMinigameId(signal.gameId) &&
                !string.IsNullOrEmpty(signal.ownerId))
                BindMinigamesControllerUpdate(signal.gameId);
            if (IsDynamicMinigameId(signal.gameId) && ownerChanged && hasOwner &&
                !string.IsNullOrEmpty(oldOwner) && string.IsNullOrEmpty(signal.ownerId))
            {
                _pendingDynamicMinigameReleases.Remove(signal.gameId);
                ClearDynamicMinigameControllerMappings(signal.gameId);
            }
            if (signal.gameId == GameCardId && ownerChanged)
            {
                _lastAcceptedCardStateSequence = 0;
                _lastAcceptedCardMitaSequence = 0;
                if (signal.ownerId == _localPlayerId) _cardStateSequence = 0;
                if (signal.ownerId == _localPlayerId) _cardMitaStateSequence = 0;
                _cardGameStartNoticeSent = false;
                _latestCardState = null;
            }
            if (signal.gameId == GameCardId && signal.cardState != null)
            {
                _latestCardState = signal.cardState;
                _lastAcceptedCardStateSequence = Math.Max(_lastAcceptedCardStateSequence,
                                                           signal.cardState.sequence);
                ApplyCardGameState(FindCardGame(), signal.cardState);
            }
            if (signal.completed && string.Equals(signal.gameId, GameCardId, StringComparison.Ordinal))
                _completedGames.Add(signal.gameId);

            if (signal.completed && signal.gameId == GameCardId && signal.cardState != null)
                ApplyCardGameState(FindCardGame(), signal.cardState);

            if (signal.completed)
            {
                _claimAttempts.Remove(signal.gameId);
                _pendingClaims.Remove(signal.gameId);
                _nextClaimRetry.Remove(signal.gameId);
                _claimStartedAt.Remove(signal.gameId);
                _pendingFinishes.Remove(signal.gameId);
                ClearPendingControls(signal.gameId);
                ClearPendingStartActions(signal.gameId);
            }

            string owner = signal.ownerId ?? string.Empty;
            if (owner == _localPlayerId)
            {
                _claimAttempts.Remove(signal.gameId);
                _pendingClaims.Remove(signal.gameId);
                _nextClaimRetry.Remove(signal.gameId);
                _claimStartedAt.Remove(signal.gameId);
                _pendingFinishes.Remove(signal.gameId);
                if (signal.gameId == GameCardId)
                {
                    ClearPendingControls(signal.gameId);
                    RestoreLocalGameCardSimulation();
                }
                else
                {
                    ReplayPendingControls(signal.gameId);
                }
                ReplayPendingStartActions(signal.gameId);
            }
            else if (!string.IsNullOrEmpty(owner))
            {
                _pendingClaims.Remove(signal.gameId);
                _nextClaimRetry.Remove(signal.gameId);
                _claimStartedAt.Remove(signal.gameId);
                ClearPendingControls(signal.gameId);
                ClearPendingStartActions(signal.gameId);
                SuppressControls(signal.gameId);
            }
            else if (_pendingClaims.Contains(signal.gameId))
            {
                SendClaim(signal.gameId);
            }

            if (signal.completed)
                ApplyCompletion(signal.gameId);

            Trace("applied state game='" + signal.gameId + "' owner='" +
                  (string.IsNullOrEmpty(owner) ? "none" : owner) + "' revision=" +
                  signal.revision + " completed=" + signal.completed +
                  " local='" + _localPlayerId + "'.");
            DiagnosticLog.Info("MinigameSync: " + DisplayName(signal.gameId) +
                               " owner is '" + (string.IsNullOrEmpty(owner) ? "none" : owner) +
                               "' (revision " + signal.revision + ").");
        }

        private void ApplyDenial(MinigameSignal signal)
        {
            if (signal.recipientId != _localPlayerId)
            {
                Trace("ignored denial for another player recipient='" +
                      (signal.recipientId ?? "") + "' local='" + _localPlayerId +
                      "' game='" + signal.gameId + "'.");
                return;
            }

            if (IsDynamicMinigameId(signal.gameId))
                _pendingDynamicMinigameReleases.Remove(signal.gameId);

            Trace("applying claim denial game='" + signal.gameId + "' owner='" +
                  (signal.ownerId ?? "") + "' revision=" + signal.revision +
                  " completed=" + signal.completed + ".");
            _claimAttempts.Remove(signal.gameId);

            int currentRevision;
            if (!_revisions.TryGetValue(signal.gameId, out currentRevision) ||
                signal.revision >= currentRevision)
            {
                _revisions[signal.gameId] = signal.revision;
                _owners[signal.gameId] = signal.ownerId ?? string.Empty;
            }

            _pendingClaims.Remove(signal.gameId);
            _nextClaimRetry.Remove(signal.gameId);
            _claimStartedAt.Remove(signal.gameId);
            _pendingFinishes.Remove(signal.gameId);
            ClearPendingStartActions(signal.gameId);
            if (signal.completed && string.Equals(signal.gameId, GameCardId, StringComparison.Ordinal))
                _completedGames.Add(signal.gameId);
            string owner;
            _owners.TryGetValue(signal.gameId, out owner);
            if (!string.IsNullOrEmpty(owner) && owner != _localPlayerId)
            {
                ClearPendingControls(signal.gameId);
                SuppressControls(signal.gameId);
            }
            else
            {
                ClearPendingControls(signal.gameId);
                SuppressControls(signal.gameId);
            }

            if (!string.IsNullOrEmpty(owner) && owner != _localPlayerId)
                ShowBusyMessage(signal.gameId, owner);
            else if (!signal.completed)
                ShowBusyMessage(signal.gameId, null);
        }

        private void CompleteOnHost(string gameId, string senderId, GameObject localSignalObject)
        {
            if (_completedGames.Contains(gameId) && gameId == GameCardId)
            {
                if (localSignalObject == null) ApplyCompletion(gameId);
                return;
            }

            string owner;
            if (!_owners.TryGetValue(gameId, out owner) ||
                string.IsNullOrEmpty(owner) || owner != senderId)
            {
                SendDenied(gameId, senderId);
                return;
            }

            _owners[gameId] = string.Empty;
            _pendingClaims.Remove(gameId);
            _claimStartedAt.Remove(gameId);
            _pendingFinishes.Remove(gameId);
            ClearPendingControls(gameId);
            ClearPendingStartActions(gameId);
            if (gameId == GameCardId) _completedGames.Add(gameId);

            BroadcastState(gameId, string.Empty, true, true);
            DiagnosticLog.Info("MinigameSync: " + DisplayName(gameId) +
                               " completed by '" + senderId + "'.");

            if (localSignalObject == null)
                ApplyCompletion(gameId);
        }

        private void ApplyCompletion(string gameId)
        {
            string signalName = gameId == GameCardId
                ? "DialogueStopGame"
                : "AnimationPlayerStop";
            GameObject questOne = FindPath(QuestOnePath);
            GameObject stopSignal = FindDescendantByName(questOne, signalName);
            if (stopSignal != null && !stopSignal.activeSelf)
                SetActiveFromSignal(stopSignal, true);

            if (gameId == GameCardId)
                ActivateQuestTwo();
        }

        private void ActivateQuestTwo()
        {
            GameObject questTwo = FindPath("World/Quests/Quest 2");
            if (questTwo == null)
            {
                TraceWarning("Game Card completion could not activate Quest 2: path not found.");
                DiagnosticLog.Warning("MinigameSync: Game Card completed, but 'World/Quests/Quest 2' was not found; keeping the synchronized DialogueStopGame signal active.");
                return;
            }

            if (!questTwo.activeSelf)
            {
                SetActiveFromSignal(questTwo, true);
                DiagnosticLog.Info("MinigameSync: activated Quest 2 on this player after Game Card completion.");
            }
        }

        private void SetOwnerAndBroadcast(string gameId, string ownerId)
        {
            string previousOwner;
            bool ownerChanged = !_owners.TryGetValue(gameId, out previousOwner) ||
                !string.Equals(previousOwner ?? string.Empty, ownerId ?? string.Empty,
                    StringComparison.Ordinal);
            _owners[gameId] = ownerId ?? string.Empty;
            if (gameId == GameCardId && ownerChanged)
            {
                _cardStateSequence = 0;
                _lastAcceptedCardStateSequence = 0;
                _cardMitaStateSequence = 0;
                _lastAcceptedCardMitaSequence = 0;
                _cardGameStartNoticeSent = false;
                _latestCardState = null;
            }
            _pendingClaims.Remove(gameId);
            _nextClaimRetry.Remove(gameId);
            _claimStartedAt.Remove(gameId);
            BroadcastState(gameId, ownerId, false, true);
            if (IsControllerMinigameId(gameId) && !string.IsNullOrEmpty(ownerId))
                BindMinigamesControllerUpdate(gameId);
            if (string.Equals(ownerId, _localPlayerId, StringComparison.Ordinal))
            {
                if (gameId == GameCardId)
                {
                    ClearPendingControls(gameId);
                    RestoreLocalGameCardSimulation();
                }
                else ReplayPendingControls(gameId);
                if (gameId == GameCardId) EnsureGameCardOwnerControls();
            }
            else if (!string.IsNullOrEmpty(ownerId))
            {
                SuppressControls(gameId);
            }
        }

        private void BroadcastState(string gameId, string ownerId, bool completed, bool incrementRevision)
        {
            if (_dispatcher == null || string.IsNullOrEmpty(_localPlayerId)) return;

            int revision;
            _revisions.TryGetValue(gameId, out revision);
            if (incrementRevision) revision++;
            _revisions[gameId] = revision;
            _owners[gameId] = ownerId ?? string.Empty;
            SendSignal(new MinigameSignal
            {
                kind = "state",
                senderId = _localPlayerId,
                sceneName = SceneManager.GetActiveScene().name,
                gameId = gameId,
                ownerId = ownerId ?? string.Empty,
                revision = revision,
                completed = completed,
                sequence = completed && gameId == GameCardId && _latestCardState != null
                    ? _latestCardState.sequence : 0,
                cardState = completed && gameId == GameCardId ? _latestCardState : null
            });
        }

        private void SendDenied(string gameId, string recipientId)
        {
            int revision;
            _revisions.TryGetValue(gameId, out revision);
            string owner;
            _owners.TryGetValue(gameId, out owner);
            SendSignal(new MinigameSignal
            {
                kind = "denied",
                senderId = _localPlayerId,
                recipientId = recipientId,
                sceneName = SceneManager.GetActiveScene().name,
                gameId = gameId,
                ownerId = owner ?? string.Empty,
                revision = revision,
                completed = _completedGames.Contains(gameId)
            });
        }

        private void SendClaim(string gameId)
        {
            if (_dispatcher == null) return;
            int attempts;
            _claimAttempts.TryGetValue(gameId, out attempts);
            attempts++;
            _claimAttempts[gameId] = attempts;
            _nextClaimRetry[gameId] = Time.unscaledTime + ClaimRetrySeconds;
            if (!_claimStartedAt.ContainsKey(gameId))
                _claimStartedAt[gameId] = Time.unscaledTime;
            SendSignal(new MinigameSignal
            {
                kind = "claim",
                senderId = _localPlayerId,
                sceneName = SceneManager.GetActiveScene().name,
                gameId = gameId
            });
            Trace("tx claim attempt=" + attempts + " game='" + gameId + "' sender='" +
                  _localPlayerId + "' scene='" + SceneManager.GetActiveScene().name +
                  "' dispatcher=ready.");
        }

        private void SendSignal(MinigameSignal signal)
        {
            if (_dispatcher == null || signal == null) return;
            try
            {
                string json = JsonSerializer.Serialize(signal, JsonOptions);
                _dispatcher.SendCustom(SignalEvent, json);
                if (signal.kind != "claim" && signal.kind != "card-snapshot" &&
                    signal.kind != "card-state" && signal.kind != "card-mita-state")
                    Trace("tx kind='" + signal.kind + "' game='" + signal.gameId +
                          "' sender='" + signal.senderId + "' recipient='" +
                          (signal.recipientId ?? "") + "' owner='" +
                          (signal.ownerId ?? "") + "' revision=" + signal.revision +
                          " completed=" + signal.completed + " scene='" +
                          signal.sceneName + "'.");
            }
            catch (Exception ex)
            {
                TraceWarning("signal send failed kind='" + signal.kind + "' game='" +
                             signal.gameId + "': " + ex.GetType().Name + ": " + ex.Message);
                DiagnosticLog.Warning("MinigameSync: signal send failed: " + ex.Message);
            }
        }

        private void OnRemotePlayerLeft(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return;
            Trace("remote player left id='" + playerId + "'.");
            SessionController session = SessionController.Active;
            string[] trackedGames = GetTrackedGameIds();
            for (int i = 0; i < trackedGames.Length; i++)
            {
                string gameId = trackedGames[i];
                string owner;
                if (!_owners.TryGetValue(gameId, out owner) || owner != playerId) continue;

                if (session != null && session.IsHost)
                {
                    _owners[gameId] = string.Empty;
                    BroadcastState(gameId, string.Empty, false, true);
                }
                else
                {
                    _owners.Remove(gameId);
                }
                ClearDynamicMinigameControllerMappings(gameId);
                _pendingDynamicMinigameReleases.Remove(gameId);
                DiagnosticLog.Info("MinigameSync: released " + DisplayName(gameId) +
                                   " because owner '" + playerId + "' left.");
            }
        }

        private void SuppressControls(string gameId)
        {
            if (IsDynamicMinigameId(gameId)) return;
            GameObject questOne = FindPath(QuestOnePath);
            if (questOne == null) return;

            GameObject gameRoot = FindDescendantByName(questOne,
                gameId == GameCardId ? "Game Card" : "Game Aihastion");
            if (gameRoot == null) return;
            SuppressControlsRecursive(gameRoot.transform,
                LocalPlayerLocator.GetPath(gameRoot.transform), gameId, false);
        }

        private void HoldControlsUntilClaim(string gameId)
        {
            if (IsDynamicMinigameId(gameId)) return;
            GameObject questOne = FindPath(QuestOnePath);
            if (questOne == null) return;

            GameObject gameRoot = FindDescendantByName(questOne,
                gameId == GameCardId ? "Game Card" : "Game Aihastion");
            if (gameRoot == null) return;
            SuppressControlsRecursive(gameRoot.transform,
                LocalPlayerLocator.GetPath(gameRoot.transform), gameId, true);
        }

        private void SuppressControlsRecursive(Transform node, string path,
                                               string lockedGameId, bool queueForOwner)
        {
            if (node == null) return;
            string classifiedGame;
            bool isControl;
            bool isFinishSignal;
            if (TryClassifyPath(path, out classifiedGame, out isControl, out isFinishSignal) &&
                isControl && classifiedGame == lockedGameId && node.gameObject.activeSelf)
            {
                if (queueForOwner)
                    QueuePendingControl(lockedGameId, node.gameObject);
                TraceControlOnce(queueForOwner ? "unowned" : "suppressed", path,
                    (queueForOwner ? "held" : "suppressed") + " active control for game='" +
                    lockedGameId + "' at path='" + path + "'.");
                SetActiveFromSignal(node.gameObject, false);
            }

            int childCount = node.childCount;
            for (int i = 0; i < childCount; i++)
            {
                Transform child = node.GetChild(i);
                if (child == null) continue;
                SuppressControlsRecursive(child, path + "/" + child.name,
                    lockedGameId, queueForOwner);
            }
        }

        private void QueuePendingControl(string gameId, GameObject go)
        {
            List<GameObject> pending;
            if (!_pendingControls.TryGetValue(gameId, out pending))
            {
                pending = new List<GameObject>();
                _pendingControls[gameId] = pending;
            }
            if (!pending.Contains(go)) pending.Add(go);
        }

        private void QueuePendingStartAction(string gameId, string key, Action replay)
        {
            if (replay == null) return;
            List<PendingStartAction> pending;
            if (!_pendingStartActions.TryGetValue(gameId, out pending))
            {
                pending = new List<PendingStartAction>();
                _pendingStartActions[gameId] = pending;
            }

            for (int i = 0; i < pending.Count; i++)
                if (string.Equals(pending[i].key, key, StringComparison.Ordinal)) return;

            pending.Add(new PendingStartAction { key = key, replay = replay });
        }

        private void ReplayPendingStartActions(string gameId)
        {
            List<PendingStartAction> pending;
            if (!_pendingStartActions.TryGetValue(gameId, out pending)) return;
            _pendingStartActions.Remove(gameId);

            _replayingInputDepth++;
            try
            {
                for (int i = 0; i < pending.Count; i++)
                {
                    try
                    {
                        if (pending[i] != null && pending[i].replay != null)
                            pending[i].replay();
                    }
                    catch (Exception ex)
                    {
                        TraceWarning("replaying accepted " + DisplayName(gameId) +
                                     " input failed: " + ex.GetType().Name + ": " + ex.Message);
                        DiagnosticLog.Warning("MinigameSync: replaying the accepted " +
                                              DisplayName(gameId) + " input failed: " +
                                              ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }
            finally { _replayingInputDepth--; }
            EnsureGameCardOwnerControls();
        }

        private void ClearPendingStartActions(string gameId)
        {
            _pendingStartActions.Remove(gameId);
        }

        private void ReplayPendingControls(string gameId)
        {
            List<GameObject> pending;
            if (!_pendingControls.TryGetValue(gameId, out pending)) return;
            _pendingControls.Remove(gameId);
            Trace("restoring " + pending.Count + " held control object(s) for game='" +
                  gameId + "' owner='" + _localPlayerId + "'.");
            for (int i = 0; i < pending.Count; i++)
            {
                GameObject go = pending[i];
                if (go != null) SetActiveFromSignal(go, true);
            }
            if (gameId == GameCardId) EnsureGameCardOwnerControls();
        }

        private void EnsureGameCardOwnerControls()
        {
            if (_ensuringGameCardOwnerControls) return;
            _ensuringGameCardOwnerControls = true;
            try { EnsureGameCardOwnerControlsCore(); }
            finally { _ensuringGameCardOwnerControls = false; }
        }

        private void EnsureGameCardOwnerControlsCore()
        {
            string owner;
            if (!_owners.TryGetValue(GameCardId, out owner) || owner != _localPlayerId)
                return;

            Location4TableCardGame cardGame = FindCardGame();
            if (cardGame == null || !cardGame.play || cardGame.gameStop)
                return;

            GameObject mechanicRoot = FindGameCardControlRoot("Game Mechanic");
            GameObject interfaceRoot = FindGameCardControlRoot("Interface Game");

            // HoldControlsUntilClaim hides these roots while nobody owns the
            // minigame. Re-enable them only for the client that owns an active
            // game; otherwise the native start path can run with its HUD still
            // disabled and leave the owner unable to play.
            if (mechanicRoot != null && !mechanicRoot.activeSelf)
            {
                SetActiveFromSignal(mechanicRoot, true);
                TraceControlOnce("owner-hud", GameCardPath + "/Game Mechanic",
                    "restored Game Mechanic for active local Game Card owner='" +
                    _localPlayerId + "'.");
            }
            if (interfaceRoot != null && !interfaceRoot.activeSelf)
            {
                SetActiveFromSignal(interfaceRoot, true);
                TraceControlOnce("owner-hud", GameCardPath + "/Interface Game",
                    "restored Interface Game for active local Game Card owner='" +
                    _localPlayerId + "'.");
            }

            Transform localPlayer = LocalPlayerLocator.FindHardcodedPlayerPath();
            PlayerMove localMove = localPlayer != null ? localPlayer.GetComponent<PlayerMove>() : null;
            if (localMove == null) return;

            bool reboundAny = false;
            HashSet<int> visited = new HashSet<int>();
            Interface_KeyHint_Key[] gameButtons =
            {
                cardGame.buttonShowCards,
                cardGame.buttonHideCards,
                cardGame.buttonThrowCard,
                cardGame.buttonRightCard,
                cardGame.buttonLeftCard
            };
            for (int i = 0; i < gameButtons.Length; i++)
                RebindGameCardKeyHint(gameButtons[i], localMove, visited, ref reboundAny);

            GameObject[] controlRoots = { mechanicRoot, interfaceRoot };
            for (int rootIndex = 0; rootIndex < controlRoots.Length; rootIndex++)
            {
                GameObject controlRoot = controlRoots[rootIndex];
                if (controlRoot == null) continue;
                Interface_KeyHint_Key[] keyHints;
                try { keyHints = controlRoot.GetComponentsInChildren<Interface_KeyHint_Key>(true); }
                catch { continue; }
                if (keyHints == null) continue;

                for (int i = 0; i < keyHints.Length; i++)
                {
                    Interface_KeyHint_Key keyHint = keyHints[i];
                    if (keyHint == null) continue;
                    RebindGameCardKeyHint(keyHint, localMove, visited, ref reboundAny);
                }
            }

            if (reboundAny)
                TraceControlOnce("owner-rebind", GameCardPath,
                    "rebound Game Card key-hint controls to the local PlayerMove for owner='" +
                    _localPlayerId + "'.");
        }

        private void RebindGameCardKeyHint(Interface_KeyHint_Key keyHint, PlayerMove localMove,
                                           HashSet<int> visited, ref bool reboundAny)
        {
            if (keyHint == null || localMove == null || visited == null) return;
            int instanceId;
            try { instanceId = keyHint.GetInstanceID(); }
            catch { return; }
            if (!visited.Add(instanceId)) return;

            try
            {
                if (keyHint.scrpm != localMove)
                {
                    keyHint.scrpm = localMove;
                    reboundAny = true;
                }
            }
            catch (Exception ex)
            {
                TraceWarning("could not bind Game Card key hint '" + keyHint.name +
                             "' to the local PlayerMove: " + ex.GetType().Name + ".");
            }
        }

        private void ClearPendingControls(string gameId)
        {
            _pendingControls.Remove(gameId);
        }

        private void TraceControlOnce(string category, string path, string message)
        {
            if (_loggedControlPaths.Add(category + "|" + path)) Trace(message);
        }

        private void ShowBusyMessage(string gameId, string ownerId)
        {
            string ownerName = ResolvePlayerName(ownerId);
            string noticeKey = gameId + ":" + (ownerId ?? string.Empty);
            float last;
            if (_lastBusyNotice.TryGetValue(noticeKey, out last) &&
                Time.unscaledTime - last < BusyMessageCooldownSeconds)
                return;
            _lastBusyNotice[noticeKey] = Time.unscaledTime;
            MiSideMultiplayer.API.QBLib.Notifications.ShowMinigameInProgress(ownerName, gameId);
        }

        private string ResolvePlayerName(string playerId)
        {
            if (string.IsNullOrWhiteSpace(playerId)) return "A player";
            if (string.Equals(playerId, _localPlayerId, StringComparison.Ordinal))
                return MiSideMultiplayerPlugin.GetDisplayName();

            SessionController session = SessionController.Active;
            if (session != null)
            {
                foreach (KeyValuePair<string, SessionPlayer> pair in session.Players)
                {
                    SessionPlayer player = pair.Value;
                    if (player == null ||
                        (pair.Key != playerId && player.playerId != playerId))
                        continue;
                    if (!string.IsNullOrWhiteSpace(player.displayName))
                        return player.displayName.Trim();
                }
            }
            return playerId;
        }

        private void ResetForScene(string sceneName)
        {
            _activeSceneName = sceneName;
            _owners.Clear();
            _revisions.Clear();
            _pendingClaims.Clear();
            _pendingFinishes.Clear();
            _completedGames.Clear();
            _dynamicMinigameIds.Clear();
            _pendingDynamicMinigameReleases.Clear();
            _minigameIdByControllerInstance.Clear();
            _nextClaimRetry.Clear();
            _claimStartedAt.Clear();
            _pendingControls.Clear();
            _pendingStartActions.Clear();
            _lastBusyNotice.Clear();
            _loggedControlPaths.Clear();
            _claimAttempts.Clear();
            _nextCardStateTime = Time.unscaledTime;
            _nextCardStateSummaryTime = Time.unscaledTime + 5f;
            _nextSuppressionRefreshTime = Time.unscaledTime;
            _cardStateSequence = 0;
            _lastAcceptedCardStateSequence = 0;
            _cardMitaStateSequence = 0;
            _lastAcceptedCardMitaSequence = 0;
            _cardStatesRelayedSinceLog = 0;
            _cardGameStartNoticeSent = false;
            _latestCardState = null;
            _disabledRemoteGameCardSimulation = false;
        }

        private static void SetActiveFromSignal(GameObject go, bool active)
        {
            if (go == null) return;
            _applyingSignalDepth++;
            try { go.SetActive(active); }
            finally { _applyingSignalDepth--; }
        }

        private static bool TryClassifyPath(string path, out string gameId,
                                            out bool isControl,
                                            out bool isFinishSignal)
        {
            gameId = null;
            isControl = false;
            isFinishSignal = false;
            if (string.IsNullOrEmpty(path)) return false;

            path = path.Replace('\\', '/');

            if (IsSameOrDescendant(path, GameCardEntryTriggerPath))
            {
                gameId = GameCardId;
                return true;
            }

            if (IsSameOrDescendant(path, GameCardPath))
            {
                gameId = GameCardId;
                string leaf = LeafName(path);
                isControl = IsSameOrDescendant(path, GameCardPath + "/Game Mechanic") ||
                            IsSameOrDescendant(path, GameCardPath + "/Interface Game");
                isFinishSignal = string.Equals(leaf, "DialogueStopGame",
                    StringComparison.OrdinalIgnoreCase);
                return true;
            }

            if (IsSameOrDescendant(path, GameAihastionPath))
            {
                gameId = GameAihastionId;
                string leaf = LeafName(path);
                string animationsPath = GameAihastionPath + "/Animations/";
                isControl = IsSameOrDescendant(path, GameAihastionPath + "/TargetCamera") ||
                            IsSameOrDescendant(path, GameAihastionPath + "/AudioMovePlayer") ||
                            IsSameOrDescendant(path, GameAihastionPath + "/AudioMoveMita") ||
                            (path.StartsWith(animationsPath, StringComparison.OrdinalIgnoreCase) &&
                             (IsSameOrDescendant(path, animationsPath + "AnimationPlayerStart") ||
                              IsSameOrDescendant(path, animationsPath + "AnimationPlayerInspect")));
                isFinishSignal = path.StartsWith(animationsPath, StringComparison.OrdinalIgnoreCase) &&
                                 string.Equals(leaf, "AnimationPlayerStop",
                                     StringComparison.OrdinalIgnoreCase);
                return true;
            }

            return false;
        }

        private static bool IsStartInputPath(string path, string gameId,
                                             bool isControl, bool objectInteraction,
                                             string source)
        {
            if (gameId == GameCardId)
            {
                if (objectInteraction)
                    return IsSameOrDescendant(path, GameCardEntryTriggerPath) ||
                           IsSameOrDescendant(path, GameCardPath + "/Start") ||
                           IsSameOrDescendant(path, GameCardPath + "/Interface Start");

                // StartGame is the real game entry point; its component may be
                // attached to the Game Card root rather than a Start child.
                // Button callbacks and control input are also game entry input.
                return isControl || string.Equals(source,
                    "Location4TableCardGame.StartGame", StringComparison.Ordinal) ||
                    string.Equals(source,
                        "MinigamesController.StartGame", StringComparison.Ordinal) ||
                    IsSameOrDescendant(path, GameCardPath + "/Start") ||
                    IsSameOrDescendant(path, GameCardPath + "/Interface Start");
            }
            return isControl || objectInteraction;
        }

        private static bool IsSameOrDescendant(string path, string root)
        {
            return string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static string LeafName(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash < 0 ? path : path.Substring(slash + 1);
        }

        private static GameObject FindGameCardControlRoot(string name)
        {
            GameObject control = FindPath(GameCardPath + "/" + name);
            if (control != null) return control;

            GameObject gameCard = FindPath(GameCardPath);
            if (gameCard != null) return FindDescendantByName(gameCard, name);

            GameObject questOne = FindPath(QuestOnePath);
            return questOne != null ? FindDescendantByName(questOne, name) : null;
        }

        private static bool IsSupportedGame(string gameId)
        {
            return string.Equals(gameId, GameCardId, StringComparison.Ordinal) ||
                   string.Equals(gameId, GameAihastionId, StringComparison.Ordinal) ||
                   IsDynamicMinigameId(gameId);
        }

        private static bool IsDynamicMinigameId(string gameId)
        {
            return !string.IsNullOrWhiteSpace(gameId) &&
                   gameId.StartsWith(DynamicMinigameIdPrefix, StringComparison.Ordinal) &&
                   gameId.Length <= 1024 &&
                   gameId.IndexOf('|', DynamicMinigameIdPrefix.Length) >= 0;
        }

        private static bool IsControllerMinigameId(string gameId)
        {
            return gameId == GameCardId ||
                   (!string.IsNullOrWhiteSpace(gameId) &&
                    gameId.StartsWith(DynamicMinigameIdPrefix + "controller|",
                        StringComparison.Ordinal));
        }

        private static bool IsBuiltInGame(string gameId)
        {
            return string.Equals(gameId, GameCardId, StringComparison.Ordinal) ||
                   string.Equals(gameId, GameAihastionId, StringComparison.Ordinal);
        }

        private string[] GetTrackedGameIds()
        {
            List<string> ids = new List<string>(SupportedGames.Length +
                                                _dynamicMinigameIds.Count);
            for (int i = 0; i < SupportedGames.Length; i++)
                ids.Add(SupportedGames[i]);
            foreach (string gameId in _dynamicMinigameIds)
                if (!ids.Contains(gameId)) ids.Add(gameId);
            return ids.ToArray();
        }

        private static string DisplayName(string gameId)
        {
            if (gameId == GameCardId) return "Game Card";
            if (gameId == GameAihastionId) return "Game Aihastion";
            if (IsDynamicMinigameId(gameId))
            {
                int pathStart = gameId.IndexOf('|', DynamicMinigameIdPrefix.Length);
                string path = pathStart >= 0 && pathStart + 1 < gameId.Length
                    ? gameId.Substring(pathStart + 1) : gameId;
                int scopeSeparator = path.IndexOf('|');
                if (scopeSeparator >= 0 && scopeSeparator + 1 < path.Length)
                    path = path.Substring(scopeSeparator + 1);
                string leaf = LeafName(path);
                return string.IsNullOrWhiteSpace(leaf) ? "Minigame" : leaf;
            }
            return "Minigame";
        }

        private static bool IsSessionHost(SessionController session, string senderId)
        {
            if (session == null || string.IsNullOrWhiteSpace(senderId)) return false;
            foreach (KeyValuePair<string, SessionPlayer> pair in session.Players)
            {
                SessionPlayer player = pair.Value;
                if (player != null && player.isHost &&
                    (player.playerId == senderId || pair.Key == senderId))
                    return true;
            }
            return false;
        }

        private static bool IsKnownParticipant(SessionController session, string playerId)
        {
            if (session == null || string.IsNullOrWhiteSpace(playerId)) return false;
            if (session.LocalPlayerId == playerId) return true;
            foreach (KeyValuePair<string, SessionPlayer> pair in session.Players)
            {
                SessionPlayer player = pair.Value;
                if (player != null && (player.playerId == playerId || pair.Key == playerId))
                    return true;
            }
            return false;
        }

        private static GameObject FindPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded) return null;

            string[] parts = path.Split('/');
            if (parts.Length == 0) return null;
            GameObject[] roots = scene.GetRootGameObjects();
            for (int r = 0; r < roots.Length; r++)
            {
                GameObject root = roots[r];
                if (root == null || root.name != parts[0]) continue;
                Transform current = root.transform;
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
                if (found && current != null) return current.gameObject;
            }
            return null;
        }

        private static GameObject FindDescendantByName(GameObject root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name)) return null;
            if (root.name == name) return root;
            Transform transform = root.transform;
            for (int i = 0; i < transform.childCount; i++)
            {
                GameObject found = FindDescendantByName(transform.GetChild(i).gameObject, name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
