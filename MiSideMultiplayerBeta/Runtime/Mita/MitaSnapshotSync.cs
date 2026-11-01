using System;
using System.Collections.Generic;
using System.Text.Json;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer
{
    /// <summary>
    /// Join-in-progress baseline transfer for host-owned Mita state. The relay
    /// routes requests to the session host and broadcasts the host's snapshot;
    /// it never reads or interprets game state.
    /// </summary>
    internal static class MitaSnapshotSync
    {
        public const string RequestEvent = "miside.mita.sync.request";
        public const string CompleteEvent = "miside.mita.sync.complete";

        private sealed class RequestMessage
        {
            public string playerId;
            public string sceneName;
        }

        private sealed class CompleteMessage
        {
            public string senderId;
            public string sceneName;
            public string snapshotId;
            public int stateCount;
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true
        };

        private static RpcDispatcher _dispatcher;
        private static readonly object _sync = new object();
        private static readonly Dictionary<int, string> _receivedPaths =
            new Dictionary<int, string>();
        private static string _activeSnapshotId = "";
        private static string _sceneName = "";
        private static int _expectedStateCount = -1;
        private static bool _completed;

        public static bool HasCompletedSnapshot
        {
            get { lock (_sync) return _completed; }
        }

        public static float Progress
        {
            get
            {
                lock (_sync)
                {
                    if (_completed) return 1f;
                    if (_expectedStateCount <= 0) return 0f;
                    return (float)_receivedPaths.Count / _expectedStateCount;
                }
            }
        }

        public static void Configure(RpcDispatcher dispatcher)
        {
            if (ReferenceEquals(_dispatcher, dispatcher)) return;

            if (_dispatcher != null)
            {
                _dispatcher.CustomMessageReceived -= OnCustomMessage;
                _dispatcher.RemoteMitaStateReceived -= OnRemoteMitaStateReceived;
            }

            _dispatcher = dispatcher;
            if (_dispatcher != null)
            {
                _dispatcher.CustomMessageReceived += OnCustomMessage;
                _dispatcher.RemoteMitaStateReceived += OnRemoteMitaStateReceived;
            }
        }

        public static void Reset(string sceneName)
        {
            MitaEventSync.ResetAppliedSequences();
            lock (_sync)
            {
                _sceneName = sceneName ?? "";
                _activeSnapshotId = "";
                _expectedStateCount = -1;
                _receivedPaths.Clear();
                _completed = false;
            }
        }

        public static void RequestSnapshot()
        {
            RpcDispatcher dispatcher = _dispatcher;
            if (dispatcher == null || MitaHost.IsHost) return;

            try
            {
                string sceneName = SceneManager.GetActiveScene().name;
                DiagnosticLog.Verbose("MitaSnapshotSync: requesting host snapshot for '" +
                                      sceneName + "' as player '" + MitaHost.LocalPlayerId + "'.");
                dispatcher.SendCustom(RequestEvent, JsonSerializer.Serialize(new RequestMessage
                {
                    playerId = MitaHost.LocalPlayerId,
                    sceneName = sceneName
                }, JsonOptions));
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MitaSnapshotSync: request failed: " + ex.Message);
            }
        }

        public static void SendComplete(string snapshotId, string sceneName, int stateCount)
        {
            RpcDispatcher dispatcher = _dispatcher;
            if (dispatcher == null || !MitaHost.IsHost) return;

            try
            {
                dispatcher.SendCustom(CompleteEvent, JsonSerializer.Serialize(new CompleteMessage
                {
                    senderId = MitaHost.LocalPlayerId,
                    sceneName = sceneName ?? "",
                    snapshotId = snapshotId ?? "",
                    stateCount = Math.Max(0, stateCount)
                }, JsonOptions));
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MitaSnapshotSync: completion send failed: " + ex.Message);
            }
        }

        private static void OnRemoteMitaStateReceived(MitaState state)
        {
            if (state == null || !state.isFullSnapshot || MitaHost.IsHost ||
                string.IsNullOrEmpty(state.snapshotId) ||
                state.senderId != MitaHost.HostPlayerId ||
                state.sceneName != SceneManager.GetActiveScene().name)
                return;

            int receivedCount;
            bool newSnapshot = false;
            lock (_sync)
            {
                if (_sceneName != state.sceneName)
                {
                    _sceneName = state.sceneName;
                    _activeSnapshotId = "";
                    _expectedStateCount = -1;
                    _receivedPaths.Clear();
                    _completed = false;
                }

                if (_activeSnapshotId != state.snapshotId)
                {
                    _activeSnapshotId = state.snapshotId;
                    _expectedStateCount = Math.Max(0, state.snapshotCount);
                    _receivedPaths.Clear();
                    _completed = false;
                    newSnapshot = true;
                }

                if (state.snapshotCount != _expectedStateCount ||
                    state.snapshotIndex < 0 || state.snapshotIndex >= _expectedStateCount ||
                    string.IsNullOrEmpty(state.mitaPath))
                    return;

                _receivedPaths[state.snapshotIndex] = state.mitaPath;
                receivedCount = _receivedPaths.Count;
            }

            if (newSnapshot)
                DiagnosticLog.Info("MitaSnapshotSync: receiving host snapshot '" + state.snapshotId +
                                   "' with " + state.snapshotCount + " Mita record(s).");
            DiagnosticLog.Verbose("MitaSnapshotSync: received record " + (state.snapshotIndex + 1) + "/" +
                                  state.snapshotCount + " for '" + state.mitaPath + "' (" +
                                  receivedCount + " distinct record(s) received).");
        }

        private static void OnCustomMessage(string eventName, string json)
        {
            if (eventName == RequestEvent)
            {
                HandleRequest(json);
                return;
            }

            if (eventName == CompleteEvent)
                HandleComplete(json);
        }

        private static void HandleRequest(string json)
        {
            if (!MitaHost.IsHost) return;

            try
            {
                RequestMessage request = JsonSerializer.Deserialize<RequestMessage>(json, JsonOptions);
                if (request == null || string.IsNullOrWhiteSpace(request.playerId) ||
                    request.sceneName != SceneManager.GetActiveScene().name)
                    return;

                DiagnosticLog.Info("MitaSnapshotSync: player '" + request.playerId +
                                   "' requested the current host snapshot for '" + request.sceneName + "'.");
                MitaSampler.SendFullSnapshot();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MitaSnapshotSync: request handling failed: " + ex.Message);
            }
        }

        private static void HandleComplete(string json)
        {
            if (MitaHost.IsHost) return;

            try
            {
                CompleteMessage complete = JsonSerializer.Deserialize<CompleteMessage>(json, JsonOptions);
                if (complete == null || string.IsNullOrWhiteSpace(complete.snapshotId) ||
                    complete.senderId != MitaHost.HostPlayerId ||
                    complete.sceneName != SceneManager.GetActiveScene().name)
                    return;

                List<KeyValuePair<int, string>> received;
                lock (_sync)
                {
                    if (complete.stateCount == 0 && _activeSnapshotId.Length == 0 &&
                        _receivedPaths.Count == 0)
                    {
                        _activeSnapshotId = complete.snapshotId;
                        _expectedStateCount = 0;
                    }

                    if (_sceneName != complete.sceneName ||
                        _activeSnapshotId != complete.snapshotId ||
                        _expectedStateCount != complete.stateCount ||
                        _receivedPaths.Count != complete.stateCount)
                    {
                        DiagnosticLog.Verbose("MitaSnapshotSync: completion marker for '" +
                                              complete.snapshotId + "' does not match current download " +
                                              "(received=" + _receivedPaths.Count + ", expected=" +
                                              _expectedStateCount + ", marker=" + complete.stateCount + ").");
                        return;
                    }

                    received = new List<KeyValuePair<int, string>>(_receivedPaths);
                }

                for (int i = 0; i < received.Count; i++)
                {
                    KeyValuePair<int, string> item = received[i];
                    if (item.Key < 0 || item.Key >= complete.stateCount ||
                        !MitaController.HasAppliedSnapshotState(item.Value, complete.snapshotId))
                    {
                        DiagnosticLog.Warning("MitaSnapshotSync: host snapshot '" + complete.snapshotId +
                                              "' is incomplete locally; waiting for a retry.");
                        return;
                    }
                }

                lock (_sync) _completed = true;
                DiagnosticLog.Info("MitaSnapshotSync: downloaded and applied " + complete.stateCount +
                                   " host Mita state record(s) from snapshot '" + complete.snapshotId +
                                   "' for '" + complete.sceneName + "'.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MitaSnapshotSync: completion handling failed: " + ex.Message);
            }
        }
    }
}
