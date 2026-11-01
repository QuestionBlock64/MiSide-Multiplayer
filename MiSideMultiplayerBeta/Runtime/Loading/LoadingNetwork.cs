using System;
using System.Text.Json;

namespace MiSideMultiplayer.Runtime.Loading
{
    /// <summary>
    /// Thin wrapper around RpcDispatcher for loading pipeline messages.
    /// Owns the client-ready, stage-finished, phase, and done messages.
    /// </summary>
    internal static class LoadingNetwork
    {
        public const string ClientReadyEvent   = "miside.loading.clientReady";
        public const string SyncDoneAckEvent   = "miside.loading.ackSyncDone";
        public const string StageFinishedEvent = "miside.loading.stageFinished";
        public const string PhaseEvent         = "miside.loading.phase";
        public const string DoneEvent          = "miside.loading.done";
        public const string ParticipantDroppedEvent = "miside.loading.participantDropped";

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true
        };

        private static RpcDispatcher _subscribedDispatcher;
        private static LoadingStageManager _manager;

        // ── Wiring ───────────────────────────────────────────────────────

        public static void Bind(LoadingStageManager manager)
        {
            _manager = manager;

            var dispatcher = MiSideMultiplayerPlugin.GetRpcDispatcher();
            MitaPlayerRosterSync.Bind(dispatcher);
            if (dispatcher == null) return;
            if (ReferenceEquals(_subscribedDispatcher, dispatcher)) return;

            if (_subscribedDispatcher != null)
                _subscribedDispatcher.CustomMessageReceived -= OnCustomMessage;

            dispatcher.CustomMessageReceived += OnCustomMessage;
            _subscribedDispatcher = dispatcher;
            DiagnosticLog.Info("LoadingNetwork: subscribed to RpcDispatcher.");
        }

        /// <summary>
        /// LoadingStageManager is created before RuntimeServices during plugin
        /// startup. Its Awake therefore runs before a dispatcher exists. Bind
        /// again at the actual beginning of a multiplayer load so clients do
        /// not miss every host phase/ready message for the whole game run.
        /// </summary>
        public static void EnsureBound()
        {
            if (_manager != null) Bind(_manager);
        }

        public static void Unbind()
        {
            if (_subscribedDispatcher != null)
            {
                try { _subscribedDispatcher.CustomMessageReceived -= OnCustomMessage; }
                catch { }
                _subscribedDispatcher = null;
            }
            _manager = null;
            MitaPlayerRosterSync.Unbind();
        }

        // ── Helpers used by stages ──────────────────────────────────────

        public static bool IsHost()
        {
            return LoadingStageManager.IsLocalHost;
        }

        public static int ExpectedClientCount()
        {
            return LoadingStageManager.ExpectedClientCount();
        }

        // ── Outbound ─────────────────────────────────────────────────────

        public static void SendClientReady(int stageIndex)
        {
            var msg = new ClientReadyMessage
            {
                playerId   = MiSideMultiplayerPlugin.GetLocalPlayerId(),
                stageIndex = stageIndex
            };
            Send(ClientReadyEvent, msg);
            DiagnosticLog.Info("LoadingNetwork: client-ready for stage " + stageIndex + ".");
        }

        public static void SendStageFinished(int stageIndex)
        {
            var msg = new StageFinishedMessage { stageIndex = stageIndex };
            Send(StageFinishedEvent, msg);
            DiagnosticLog.Info("LoadingNetwork: host finished stage " + stageIndex + ".");
        }

        public static void SendSyncDoneAck(int stageIndex)
        {
            var msg = new ClientReadyMessage
            {
                playerId = MiSideMultiplayerPlugin.GetLocalPlayerId(),
                stageIndex = stageIndex
            };
            Send(SyncDoneAckEvent, msg);
            DiagnosticLog.Info("LoadingNetwork: ACK_SYNC_DONE for validation stage " + stageIndex + ".");
        }

        public static void SendPhase(int    stageIndex,
                                     string phaseId,
                                     float  progress,
                                     int    readyCount,
                                     int    expectedCount,
                                     string friendlyName,
                                     string statusName)
        {
            var msg = new LoadingPhaseBroadcast
            {
                stageIndex    = stageIndex,
                phaseId       = phaseId,
                progress      = progress,
                readyCount    = readyCount,
                expectedCount = expectedCount,
                friendlyName  = friendlyName,
                statusName    = statusName
            };
            Send(PhaseEvent, msg);
        }

        public static void SendDone()
        {
            Send(DoneEvent, new LoadingDoneMessage());
        }

        // ── Inbound ──────────────────────────────────────────────────────

        private static void OnCustomMessage(string eventName, string json)
        {
            if (_manager == null) return;

            try
            {
                switch (eventName)
                {
                    case ClientReadyEvent:
                    {
                        var msg = JsonSerializer.Deserialize<ClientReadyMessage>(json, JsonOpts);
                        if (msg == null || string.IsNullOrEmpty(msg.playerId)) return;
                        if (!IsHost()) return;
                        _manager.OnClientReady(msg.playerId, msg.stageIndex);
                        return;
                    }
                    case SyncDoneAckEvent:
                    {
                        var msg = JsonSerializer.Deserialize<ClientReadyMessage>(json, JsonOpts);
                        if (msg == null || string.IsNullOrEmpty(msg.playerId)) return;
                        if (!IsHost()) return;
                        _manager.OnClientReady(msg.playerId, msg.stageIndex);
                        return;
                    }
                    case StageFinishedEvent:
                    {
                        var msg = JsonSerializer.Deserialize<StageFinishedMessage>(json, JsonOpts);
                        if (msg == null) return;
                        if (IsHost()) return;
                        _manager.OnServerStageFinished(msg.stageIndex);
                        return;
                    }
                    case PhaseEvent:
                    {
                        var msg = JsonSerializer.Deserialize<LoadingPhaseBroadcast>(json, JsonOpts);
                        if (msg == null) return;
                        _manager.OnPhaseBroadcast(
                            msg.stageIndex,
                            msg.phaseId,
                            msg.progress,
                            msg.readyCount,
                            msg.expectedCount,
                            msg.friendlyName,
                            msg.statusName);
                        return;
                    }
                    case DoneEvent:
                    {
                        _manager.OnDone();
                        return;
                    }
                    case ParticipantDroppedEvent:
                    {
                        _manager.OnParticipantDropped();
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("LoadingNetwork: handle '" + eventName +
                                      "' failed: " + ex.Message);
            }
        }

        // ── Serialization ────────────────────────────────────────────────

        private static void Send<T>(string eventName, T payload)
        {
            var dispatcher = MiSideMultiplayerPlugin.GetRpcDispatcher();
            if (dispatcher == null) return;

            try
            {
                string json = JsonSerializer.Serialize(payload, JsonOpts);
                dispatcher.SendCustom(eventName, json);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("LoadingNetwork: send '" + eventName +
                                      "' failed: " + ex.Message);
            }
        }

        [Serializable] private sealed class ClientReadyMessage
        {
            public string playerId;
            public int    stageIndex;
        }

        [Serializable] private sealed class StageFinishedMessage
        {
            public int stageIndex;
        }

        [Serializable] private sealed class LoadingDoneMessage { }
    }
}
