using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using MiSideMultiplayer.Runtime.Menu;
using MiSideMultiplayer.Runtime.Loading;

namespace MiSideMultiplayer
{
    internal sealed class RuntimeServices : IDisposable
    {
        public static readonly string CentralRelayHost = QBRelayFailover.InitialHost;
        public static readonly int    CentralRelayPort = QBRelayFailover.InitialPort;

        private RpcDispatcher        rpcDispatcher;
        private NetworkManager       networkManager;
        private LocalPlayerSampler   localPlayerSampler;
        private TcpRelayTransport    relayTransport;
        private MitaSampler          mitaSampler;
        private MitaController       mitaController;
        private WorldDoorSync        worldDoorSync;
        private WorldStoryObjectSync worldStoryObjectSync;
        private MinigameSync         minigameSync;
        private BoneSync             boneSync;
        private ChatController       chatController;
        private SharedLifeController sharedLifeController;
        private readonly string      localPlayerId;
        private readonly string      roomName;
        private readonly string      displayName;
        private PlayerDisplayName playerDisplayName;
        private InventorySharingController inventorySharingController;
        private PuppetProximityFade puppetProximityFade;
        private HeldItemSync heldItemSync;
        private bool wasLowConnection;
        private float nextLowConnectionRefreshTime;

        // Remember the last endpoint we successfully connected to so we can
        // short-circuit repeated ConnectTo calls to the same target.
        private string _currentHost = "";
        private int    _currentPort;
        private string _currentRoom = "";
        private bool   _currentIsCentral;

        // Relay selection ends when a candidate returns its protocol
        // heartbeat. Each lobby lives on exactly one server; once joined or
        // hosted, reconnects must retain that server because session state is
        // held locally by each relay.
        private bool   _centralFailoverPending;
        private int    _centralCandidateIndex = -1;
        private int    _centralNextCandidateIndex = -1;
        private int    _preferredCentralCandidateIndex = -1;
        private float  _centralHandshakeDeadline;
        private string _centralRoom = "";
        private bool   _centralEnable;
        private bool   _centralRememberSuccess;
        private readonly List<int> _centralRemainingCandidates = new List<int>();
        private RelayFailureKind _centralLastFailure = RelayFailureKind.ConnectionError;

        private readonly UnityEngine.Events.UnityAction<
            UnityEngine.SceneManagement.Scene,
            UnityEngine.SceneManagement.LoadSceneMode> _sceneLoadedHandler;

        public RpcDispatcher Dispatcher { get { return rpcDispatcher; } }
        public bool IsRelayConnected { get { return relayTransport != null && relayTransport.IsConnected; } }
        public bool IsRelayReady { get { return relayTransport != null && relayTransport.IsServerReady; } }
        public bool IsLowConnection { get { return relayTransport != null && relayTransport.IsLowConnection; } }
        public int CurrentCentralEndpointIndex
        {
            get { return _currentIsCentral ? QBRelayFailover.GetIndex(_currentHost, _currentPort) : -1; }
        }

        public RuntimeServices(
            Transform runtimeRoot,
            string    localPlayerId,
            string    displayName,
            string[]  visualRootCandidates,
            string[]  playerRootCandidates,
            float     snapshotSendRate,
            float     snapshotHeartbeatSeconds,
            bool      emitLocalSnapshots,
            bool      enableNetworking,
            string    serverHost,
            int       serverPort,
            string    roomName,
            Func<string> displayNameProvider)
        {
            this.localPlayerId = localPlayerId;
            this.displayName   = displayName;
            this.roomName      = roomName;

            CustomModelBridge.TryInitialize();
            PlayerFallGuard.Initialize();

            rpcDispatcher        = new RpcDispatcher();
            networkManager       = new NetworkManager(runtimeRoot);
            localPlayerSampler   = new LocalPlayerSampler();
            mitaSampler          = new MitaSampler();
            mitaController       = new MitaController();
            worldDoorSync        = new WorldDoorSync();
            worldStoryObjectSync = new WorldStoryObjectSync();
            minigameSync         = new MinigameSync();
            boneSync             = new BoneSync();
            chatController       = new ChatController(
                rpcDispatcher,
                networkManager,
                localPlayerId,
                displayNameProvider);

            sharedLifeController = new SharedLifeController(rpcDispatcher, localPlayerId);
            inventorySharingController = new InventorySharingController(rpcDispatcher, localPlayerId);
            playerDisplayName = new PlayerDisplayName();
            puppetProximityFade = new PuppetProximityFade();
            heldItemSync = new HeldItemSync();

            mitaController.Configure(localPlayerId);
            mitaSampler.Configure(rpcDispatcher, localPlayerId, snapshotSendRate * 0.5f);

            worldDoorSync.Configure(rpcDispatcher, localPlayerId);
            worldStoryObjectSync.Configure(rpcDispatcher, localPlayerId);
            StorySceneTransitionSync.Configure(rpcDispatcher, localPlayerId);
            minigameSync.Configure(rpcDispatcher, localPlayerId);
            boneSync.Configure(rpcDispatcher, localPlayerId);

            networkManager.Configure(
                rpcDispatcher,
                visualRootCandidates,
                playerRootCandidates,
                localPlayerId,
                mitaController);

            localPlayerSampler.Configure(
                rpcDispatcher,
                networkManager,
                localPlayerId,
                displayName,
                snapshotSendRate,
                snapshotHeartbeatSeconds,
                emitLocalSnapshots);

            try
            {
                _sceneLoadedHandler =
                    Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<
                        UnityEngine.Events.UnityAction<UnityEngine.SceneManagement.Scene,
                                                       UnityEngine.SceneManagement.LoadSceneMode>>(OnSceneLoaded);

                UnityEngine.SceneManagement.SceneManager.sceneLoaded += _sceneLoadedHandler;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("RuntimeServices: failed to subscribe sceneLoaded: " +
                                      ex.GetType().Name + ": " + ex.Message);
                _sceneLoadedHandler = null;
            }

            DiagnosticLog.Info(
                "RuntimeServices started. LocalPlayerId='" + localPlayerId +
                "'  DisplayName='" + displayName +
                "'  Relay idle (use ConnectTo to open a connection).");
        }

        // ── On-demand relay connection ──────────────────────────────────────

        public bool ConnectTo(string host, int port, string roomName = null, bool enable = true)
        {
            try
            {
                string effectiveHost = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host;
                int    effectivePort = port <= 0 ? 7777 : port;
                string effectiveRoom = string.IsNullOrWhiteSpace(roomName) ? this.roomName : roomName;

                // Short-circuit: already connected to the same endpoint + room.
                if (relayTransport != null
                    && relayTransport.IsConnected
                    && !_centralFailoverPending
                    && string.Equals(_currentHost, effectiveHost, StringComparison.OrdinalIgnoreCase)
                    && _currentPort == effectivePort
                    && string.Equals(_currentRoom, effectiveRoom, StringComparison.OrdinalIgnoreCase))
                {
                    DiagnosticLog.Info("RuntimeServices: ConnectTo skipped — already connected to " +
                                        effectiveHost + ":" + effectivePort + "  room='" + effectiveRoom + "'.");
                    return true;
                }

                // Tear down any previous connection.
                Disconnect();

                return StartTransport(effectiveHost, effectivePort, effectiveRoom, enable, false);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("RuntimeServices.ConnectTo failed: " + ex.Message);
                return false;
            }
        }

        public bool ConnectToCentral(string roomName = null, bool enable = true)
        {
            string effectiveRoom = string.IsNullOrWhiteSpace(roomName) ? this.roomName : roomName;

            if (_centralFailoverPending &&
                string.Equals(_centralRoom, effectiveRoom, StringComparison.OrdinalIgnoreCase))
                return true;

            if (relayTransport != null && _currentIsCentral && relayTransport.IsServerReady &&
                string.Equals(_currentRoom, effectiveRoom, StringComparison.OrdinalIgnoreCase))
            {
                DiagnosticLog.Info("RuntimeServices: central relay already ready at " +
                                   _currentHost + ":" + _currentPort + " room='" + effectiveRoom + "'.");
                return true;
            }

            int startIndex = _preferredCentralCandidateIndex >= 0
                ? _preferredCentralCandidateIndex
                : QBRelayFailover.GetStableStartIndex(localPlayerId);
            return ConnectToCentralAt(startIndex, effectiveRoom, enable, true, true);
        }

        public bool ConnectToCentralAt(int endpointIndex, string roomName = null, bool enable = true,
                                       bool tryOtherEndpoints = true, bool rememberSuccess = false)
        {
            if (endpointIndex < 0 || endpointIndex >= QBRelayFailover.EndpointCount)
                return false;

            string effectiveRoom = string.IsNullOrWhiteSpace(roomName) ? this.roomName : roomName;
            if (_centralFailoverPending && _centralCandidateIndex == endpointIndex &&
                string.Equals(_centralRoom, effectiveRoom, StringComparison.OrdinalIgnoreCase))
                return true;

            if (relayTransport != null && _currentIsCentral && relayTransport.IsServerReady &&
                QBRelayFailover.GetIndex(_currentHost, _currentPort) == endpointIndex &&
                string.Equals(_currentRoom, effectiveRoom, StringComparison.OrdinalIgnoreCase))
                return true;

            var remaining = new List<int>();
            if (tryOtherEndpoints)
            {
                for (int offset = 1; offset < QBRelayFailover.EndpointCount; offset++)
                    remaining.Add((endpointIndex + offset) % QBRelayFailover.EndpointCount);
            }

            return StartCentralCandidate(effectiveRoom, enable, endpointIndex, remaining, rememberSuccess);
        }

        private bool StartCentralCandidate(string effectiveRoom, bool enable, int candidateIndex,
                                           List<int> remainingCandidates, bool rememberSuccess)
        {
            string host = QBRelayFailover.GetHost(candidateIndex);
            int port = QBRelayFailover.GetPort(candidateIndex);

            Disconnect();

            _centralFailoverPending = true;
            _centralCandidateIndex = candidateIndex;
            _centralNextCandidateIndex = remainingCandidates.Count > 0 ? remainingCandidates[0] : -1;
            _centralRemainingCandidates.Clear();
            for (int i = 1; i < remainingCandidates.Count; i++)
                _centralRemainingCandidates.Add(remainingCandidates[i]);
            _centralHandshakeDeadline = Time.realtimeSinceStartup + QBRelayFailover.HandshakeTimeoutSeconds;
            _centralRoom = effectiveRoom;
            _centralEnable = enable;
            _centralRememberSuccess = rememberSuccess;
            _centralLastFailure = RelayFailureKind.ConnectionError;

            try
            {
                bool started = StartTransport(host, port, effectiveRoom, enable, true);
                if (!started)
                {
                    TryNextCentralCandidate(RelayFailureKind.ConnectionError);
                    return true;
                }

                DiagnosticLog.Info("RuntimeServices: connecting to central relay " +
                                   QBRelayFailover.GetName(candidateIndex) + " at " + host + ":" + port +
                                   "; waiting for server heartbeat.");
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("RuntimeServices: central relay candidate start failed: " + ex.Message);
                TryNextCentralCandidate(RelayFailureKind.ConnectionError);
                return true;
            }
        }

        private bool StartTransport(string host, int port, string effectiveRoom, bool enable, bool isCentral)
        {
            relayTransport = new TcpRelayTransport(
                rpcDispatcher,
                localPlayerId,
                effectiveRoom,
                host,
                port,
                enable,
                () => displayName);

            int candidateIndex = _centralCandidateIndex;
            relayTransport.ConnectionFailed += failure =>
            {
                if (isCentral && _centralFailoverPending && candidateIndex == _centralCandidateIndex)
                    TryNextCentralCandidate(failure);
                else
                    MultiplayerConnectionErrorHandler.HandleRelayFailure(failure, !isCentral);
            };

            _currentHost = host;
            _currentPort = port;
            _currentRoom = effectiveRoom;
            _currentIsCentral = isCentral;
            wasLowConnection = false;
            relayTransport.Start();

            DiagnosticLog.Info("RuntimeServices: ConnectTo " + host + ":" + port +
                               "  room='" + effectiveRoom + "'  enabled=" + enable);
            return true;
        }

        private void TryNextCentralCandidate(RelayFailureKind failure)
        {
            if (!_centralFailoverPending) return;

            _centralLastFailure = failure;
            string room = _centralRoom;
            bool enable = _centralEnable;
            int nextIndex = _centralNextCandidateIndex;
            bool rememberSuccess = _centralRememberSuccess;

            if (nextIndex >= 0)
            {
                var remaining = new List<int>(_centralRemainingCandidates);
                DiagnosticLog.Info("RuntimeServices: central relay " +
                                   QBRelayFailover.GetName(_centralCandidateIndex) +
                                   " did not become healthy; silently trying " +
                                   QBRelayFailover.GetName(nextIndex) + ".");
                StartCentralCandidate(room, enable, nextIndex, remaining, rememberSuccess);
                return;
            }

            _centralFailoverPending = false;
            _centralCandidateIndex = -1;
            _centralNextCandidateIndex = -1;
            _centralRemainingCandidates.Clear();
            _centralRoom = "";
            Disconnect();
            DiagnosticLog.Warning("RuntimeServices: all " + QBRelayFailover.EndpointCount +
                                  " central relay endpoints failed their connection health check.");
            MultiplayerConnectionErrorHandler.HandleRelayFailure(_centralLastFailure, false);
        }

        private void TickCentralRelayFailover()
        {
            if (!_centralFailoverPending) return;

            if (relayTransport != null && relayTransport.IsServerReady)
            {
                if (_centralRememberSuccess)
                    _preferredCentralCandidateIndex = _centralCandidateIndex;
                _centralFailoverPending = false;
                _centralCandidateIndex = -1;
                _centralNextCandidateIndex = -1;
                _centralRemainingCandidates.Clear();
                _centralRoom = "";
                DiagnosticLog.Info("RuntimeServices: central relay heartbeat received; selected endpoint " +
                                   _currentHost + ":" + _currentPort + ".");
                return;
            }

            if (Time.realtimeSinceStartup >= _centralHandshakeDeadline)
            {
                DiagnosticLog.Info("RuntimeServices: central relay " +
                                   QBRelayFailover.GetName(_centralCandidateIndex) +
                                   " did not return a heartbeat within " +
                                   QBRelayFailover.HandshakeTimeoutSeconds + "s.");
                TryNextCentralCandidate(RelayFailureKind.TimedOut);
            }
        }

        public void Disconnect()
        {
            _centralFailoverPending = false;
            _centralCandidateIndex = -1;
            _centralNextCandidateIndex = -1;
            _centralRemainingCandidates.Clear();
            _centralRoom = "";
            MiSideMultiplayer.API.QBLib.Notifications.HideLowConnection();
            wasLowConnection = false;
            try
            {
                if (relayTransport != null)
                {
                    relayTransport.Dispose();
                    relayTransport = null;
                    _currentHost = "";
                    _currentPort = 0;
                    _currentRoom = "";
                    _currentIsCentral = false;
                    DiagnosticLog.Info("RuntimeServices: relay disconnected.");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("RuntimeServices.Disconnect failed: " + ex.Message);
            }
        }

        // ── Scene / world hooks ─────────────────────────────────────────────

        public static void InitializeWorldGuard(Harmony harmony)
        {
            WorldPlayerUpdateGuard.Initialize(harmony);
            WorldOccupancyGuard.Initialize(harmony);
            WorldStoryObjectSync.Initialize(harmony);
            MinigameSync.Initialize(harmony);
            FastMenuMultiplayerPatch.Initialize(harmony);
        }

        private void OnSceneLoaded(
            UnityEngine.SceneManagement.Scene scene,
            UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            try { MultiplayerMenuBranding.OnSceneLoaded(scene.name); } catch { }
            try { MitaSceneCache.Invalidate(); } catch { }
            try { WorldOccupancyGuard.Invalidate(); } catch { }
            try { SceneManagerHook.OnSceneLoaded(scene.name); } catch { }
            try { MiSideMultiplayer.Runtime.Loading.SceneLoadingHook.OnSceneLoaded(scene.name); } catch { }
            try { WorldStoryObjectSync.OnDestinationSceneLoaded(scene.name); } catch { }
            try { StorySceneTransitionSync.OnSceneLoaded(scene.name); } catch { }
            try { minigameSync.OnSceneLoaded(scene.name); } catch { }
            UpdateConnectionWarning(true);

            if (string.Equals(scene.name, "SceneMenu", StringComparison.Ordinal) &&
                relayTransport != null && MultiplayerLobbyMenu.Session != null)
            {
                // A multiplayer participant has returned to the game menu.
                // Closing the relay makes a host departure immediately
                // authoritative to the server and ejects every joiner.
                DiagnosticLog.Info("RuntimeServices: returned to SceneMenu; closing active multiplayer relay.");
                Disconnect();
            }
        }

        // ── Tick ────────────────────────────────────────────────────────────

        public void Tick()
        {
            PrologueLocalQuestSupport.Tick();

            if (relayTransport != null)
            {
                PerfProbe.Begin(); relayTransport.Tick(); PerfProbe.End("relay");
            }
            TickCentralRelayFailover();
            UpdateConnectionWarning();
            SceneManagerHook.Tick();

            PerfProbe.Begin(); rpcDispatcher.Tick();              PerfProbe.End("rpc");
            StorySceneTransitionSync.Tick();
            PerfProbe.Begin(); networkManager.Tick();             PerfProbe.End("network");
            PerfProbe.Begin(); localPlayerSampler.Tick();         PerfProbe.End("localPlayer");
            PerfProbe.Begin(); mitaSampler.Tick();                PerfProbe.End("mitaSampler");
            PerfProbe.Begin(); mitaController.Tick();             PerfProbe.End("mitaController");
            PerfProbe.Begin(); worldDoorSync.Tick();              PerfProbe.End("doors");
            PerfProbe.Begin(); worldStoryObjectSync.Tick();       PerfProbe.End("story");
            minigameSync.Tick();
            PerfProbe.Begin(); boneSync.Tick();                   PerfProbe.End("boneSync");
            PerfProbe.Begin(); chatController.Tick();             PerfProbe.End("chat");
            PerfProbe.Begin(); inventorySharingController.Tick(); PerfProbe.End("inventory");
            PerfProbe.Begin(); WorldOccupancyGuard.Tick();        PerfProbe.End("occupancy");
            PerfProbe.Begin(); PlayerFallGuard.Tick();            PerfProbe.End("fallGuard");
            PerfProbe.Begin(); playerDisplayName.Tick(); PerfProbe.End("displayName");
            PerfProbe.Begin(); puppetProximityFade.Tick(); PerfProbe.End("proximityFade"); PuppetShaderProbe.Tick(); HeldItemProbe.Tick();
            PerfProbe.Begin(); heldItemSync.Tick(); PerfProbe.End("heldItem");
            PerfProbe.FrameDone();
        }

        public void LateTick()
        {
            PerfProbe.Begin(); networkManager.LateTick(); PerfProbe.End("networkLate");
            PerfProbe.Begin(); boneSync.LateTick();       PerfProbe.End("boneLate");
        }

        public void OnGui()
        {
            chatController.OnGui();
        }

        private void UpdateConnectionWarning(bool forceRefresh = false)
        {
            bool low = relayTransport != null && relayTransport.IsConnected &&
                       relayTransport.IsLowConnection;
            bool gameplay = IsGameplayScene(
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
            bool shouldShow = low && gameplay;
            float now = Time.realtimeSinceStartup;

            if (shouldShow && (!wasLowConnection || forceRefresh ||
                               now >= nextLowConnectionRefreshTime))
            {
                MiSideMultiplayer.API.QBLib.Notifications.ShowLowConnection();
                nextLowConnectionRefreshTime = now + 1f;
            }
            else if (!shouldShow && (wasLowConnection || forceRefresh))
            {
                MiSideMultiplayer.API.QBLib.Notifications.HideLowConnection();
                nextLowConnectionRefreshTime = 0f;
            }

            // Track the state that should be visible in gameplay. Tracking the
            // raw transport state here loses the warning if lag starts in a
            // menu/loading scene and continues after gameplay becomes active.
            wasLowConnection = shouldShow;
        }

        private static bool IsGameplayScene(string sceneName)
        {
            return !string.IsNullOrEmpty(sceneName) &&
                   !string.Equals(sceneName, "SceneMenu", StringComparison.Ordinal) &&
                   !string.Equals(sceneName, "SceneLoading", StringComparison.Ordinal);
        }

        public void Dispose()
        {
            if (_sceneLoadedHandler != null)
            {
                try
                {
                    UnityEngine.SceneManagement.SceneManager.sceneLoaded -= _sceneLoadedHandler;
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warning("RuntimeServices: failed to unsubscribe sceneLoaded: " +
                                          ex.GetType().Name + ": " + ex.Message);
                }
            }

            if (rpcDispatcher != null)
                rpcDispatcher.SendPlayerLeft(localPlayerId);

            Disconnect();

            if (playerDisplayName != null) playerDisplayName.Dispose();
            if (puppetProximityFade != null) puppetProximityFade.Dispose();
            if (heldItemSync != null) heldItemSync.Dispose();
            if (minigameSync != null) minigameSync.Dispose();

            sharedLifeController.Dispose();
            chatController.Dispose();
            inventorySharingController.Dispose();
            networkManager.Dispose();
            boneSync.Dispose();
        }
    }
}
