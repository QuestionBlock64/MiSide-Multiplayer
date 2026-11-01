using System;
using System.Collections.Generic;
using System.Text.Json;
using MiSideMultiplayer.Runtime.Lobby;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer
{
    /// <summary>
    /// Exchanges the exact session participant IDs during loading. Mita's host
    /// AI uses this roster so it does not choose from whichever puppets happen
    /// to have spawned first.
    /// </summary>
    internal static class MitaPlayerRosterSync
    {
        private const string RosterEvent = "miside.mita.playerRoster";

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true
        };

        private static RpcDispatcher _dispatcher;
        private static string _loadingScene = "";
        private static string _snapshotScene = "";
        private static string _snapshotHostId = "";
        private static string[] _snapshotPlayerIds = new string[0];
        private static bool _hasSnapshot;
        private static float _nextHostPublishTime;
        private static float _nextRosterWaitLogTime;
        private const float HostPublishInterval = 0.75f;

        public static void Bind(RpcDispatcher dispatcher)
        {
            if (ReferenceEquals(_dispatcher, dispatcher)) return;

            if (_dispatcher != null)
                _dispatcher.CustomMessageReceived -= OnCustomMessage;

            _dispatcher = dispatcher;
            if (_dispatcher != null)
                _dispatcher.CustomMessageReceived += OnCustomMessage;
        }

        public static void Unbind()
        {
            if (_dispatcher != null)
            {
                try { _dispatcher.CustomMessageReceived -= OnCustomMessage; }
                catch { }
            }
            _dispatcher = null;
        }

        public static void PrepareForLoading()
        {
            SessionController session = SessionController.Active;
            _loadingScene = session != null && !string.IsNullOrWhiteSpace(session.SelectedScene)
                ? session.SelectedScene
                : SceneManager.GetActiveScene().name;
            _snapshotScene = "";
            _snapshotHostId = "";
            _snapshotPlayerIds = new string[0];
            _hasSnapshot = false;
            _nextHostPublishTime = 0f;

            if (session == null)
            {
                string localId = MiSideMultiplayerPlugin.GetLocalPlayerId();
                if (!string.IsNullOrWhiteSpace(localId))
                {
                    _snapshotScene = _loadingScene;
                    _snapshotHostId = localId;
                    _snapshotPlayerIds = new[] { localId };
                    _hasSnapshot = true;
                }
            }
        }

        public static bool HasSnapshotForLoading
        {
            get
            {
                return SessionController.Active == null ||
                       (_hasSnapshot && string.Equals(_snapshotScene, _loadingScene,
                           StringComparison.Ordinal));
            }
        }

        public static bool HasRosterForScene(string sceneName)
        {
            return SessionController.Active == null ||
                   (_hasSnapshot && !string.IsNullOrWhiteSpace(sceneName) &&
                    string.Equals(_snapshotScene, sceneName, StringComparison.Ordinal) &&
                    _snapshotPlayerIds.Length > 0);
        }

        public static void PublishHostRosterIfDue(bool force)
        {
            SessionController session = SessionController.Active;
            if (session == null || !session.IsHost || _dispatcher == null) return;
            if (!force && UnityEngine.Time.unscaledTime < _nextHostPublishTime) return;

            _nextHostPublishTime = UnityEngine.Time.unscaledTime + HostPublishInterval;
            string[] ids = BuildRoster(session);
            int expectedCount = MiSideMultiplayer.Runtime.Loading.LoadingStageManager.ExpectedClientCount();
            if (ids.Length == 0 || ids.Length != expectedCount)
            {
                if (UnityEngine.Time.unscaledTime >= _nextRosterWaitLogTime)
                {
                    _nextRosterWaitLogTime = UnityEngine.Time.unscaledTime + 5f;
                    DiagnosticLog.Warning("MitaPlayerRosterSync: waiting for a complete loading roster " +
                                          "(" + ids.Length + "/" + expectedCount + " participant IDs).");
                }
                return;
            }

            string hostId = session.LocalPlayerId;
            _snapshotScene = _loadingScene;
            _snapshotHostId = hostId;
            _snapshotPlayerIds = ids;
            _hasSnapshot = true;

            try
            {
                string json = JsonSerializer.Serialize(new RosterMessage
                {
                    senderId = hostId,
                    sceneName = _loadingScene,
                    playerIds = ids,
                    playerCount = ids.Length
                }, JsonOpts);
                _dispatcher.SendCustom(RosterEvent, json);
                DiagnosticLog.Info("MitaPlayerRosterSync: host published " + ids.Length +
                                   " participant(s) for '" + _loadingScene + "': " +
                                   string.Join(", ", ids) + ".");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MitaPlayerRosterSync: roster publish failed: " + ex.Message);
            }
        }

        public static List<string> GetKnownPlayerIds(string sceneName)
        {
            SessionController session = SessionController.Active;
            if (session == null)
            {
                List<string> solo = new List<string>();
                string localId = MiSideMultiplayerPlugin.GetLocalPlayerId();
                if (!string.IsNullOrWhiteSpace(localId)) solo.Add(localId);
                return solo;
            }

            if (!_hasSnapshot || !string.Equals(sceneName, _snapshotScene, StringComparison.Ordinal))
                return new List<string>();

            List<SessionPlayer> players = new List<SessionPlayer>();
            foreach (KeyValuePair<string, SessionPlayer> pair in session.Players)
            {
                SessionPlayer player = pair.Value;
                if (player == null) continue;
                if (string.IsNullOrWhiteSpace(player.playerId)) player.playerId = pair.Key;
                string id = player.playerId;
                if (string.IsNullOrWhiteSpace(id)) continue;
                players.Add(player);
            }
            players.Sort(ComparePlayers);

            List<string> current = new List<string>(players.Count);
            HashSet<string> currentIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < players.Count; i++)
            {
                string id = players[i].playerId;
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (currentIds.Add(id)) current.Add(id);
            }

            // The loading snapshot is the authoritative initial set. Intersect
            // it with the live session roster to drop leavers, then append
            // players who joined after that loading cycle.
            if (_hasSnapshot && string.Equals(sceneName, _snapshotScene, StringComparison.Ordinal))
            {
                List<string> merged = new List<string>(current.Count);
                HashSet<string> liveIds = new HashSet<string>(current, StringComparer.Ordinal);
                HashSet<string> emitted = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < _snapshotPlayerIds.Length; i++)
                {
                    string id = _snapshotPlayerIds[i];
                    if (!string.IsNullOrWhiteSpace(id) && liveIds.Contains(id) && emitted.Add(id))
                        merged.Add(id);
                }
                for (int i = 0; i < current.Count; i++)
                    if (emitted.Add(current[i])) merged.Add(current[i]);
                return merged;
            }

            return current;
        }

        private static string[] BuildRoster(SessionController session)
        {
            List<SessionPlayer> players = new List<SessionPlayer>();
            foreach (KeyValuePair<string, SessionPlayer> pair in session.Players)
            {
                SessionPlayer player = pair.Value;
                if (player == null) continue;
                if (string.IsNullOrWhiteSpace(player.playerId)) player.playerId = pair.Key;
                if (!string.IsNullOrWhiteSpace(player.playerId) &&
                    (player.isReady || player.isHost || player.playerId == session.LocalPlayerId))
                    players.Add(player);
            }
            players.Sort(ComparePlayers);

            List<string> ids = new List<string>(players.Count);
            HashSet<string> unique = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < players.Count; i++)
                if (unique.Add(players[i].playerId)) ids.Add(players[i].playerId);

            if (!unique.Contains(session.LocalPlayerId) && !string.IsNullOrWhiteSpace(session.LocalPlayerId))
                ids.Insert(0, session.LocalPlayerId);
            return ids.ToArray();
        }

        private static int ComparePlayers(SessionPlayer a, SessionPlayer b)
        {
            int order = a.joinOrder.CompareTo(b.joinOrder);
            return order != 0 ? order : string.CompareOrdinal(a.playerId, b.playerId);
        }

        private static void OnCustomMessage(string eventName, string json)
        {
            if (!string.Equals(eventName, RosterEvent, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(json)) return;

            try
            {
                RosterMessage message = JsonSerializer.Deserialize<RosterMessage>(json, JsonOpts);
                if (message == null || string.IsNullOrWhiteSpace(message.senderId) ||
                    string.IsNullOrWhiteSpace(message.sceneName) || message.playerIds == null ||
                    message.playerCount != message.playerIds.Length)
                    return;

                SessionController session = SessionController.Active;
                if (session == null || session.IsHost || !IsSessionHost(session, message.senderId)) return;
                if (!string.Equals(message.sceneName, _loadingScene, StringComparison.Ordinal)) return;
                if (message.playerIds.Length == 0 || message.playerIds.Length > 4) return;

                HashSet<string> unique = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < message.playerIds.Length; i++)
                    if (string.IsNullOrWhiteSpace(message.playerIds[i]) || !unique.Add(message.playerIds[i]))
                        return;
                if (!unique.Contains(session.LocalPlayerId)) return;

                _snapshotScene = message.sceneName;
                _snapshotHostId = message.senderId;
                _snapshotPlayerIds = message.playerIds;
                _hasSnapshot = true;
                DiagnosticLog.Info("MitaPlayerRosterSync: received exact loading roster " +
                                   message.playerCount + "/" + message.playerIds.Length +
                                   " for '" + message.sceneName + "' from host '" +
                                   message.senderId + "': " + string.Join(", ", message.playerIds) + ".");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MitaPlayerRosterSync: roster receive failed: " + ex.Message);
            }
        }

        private static bool IsSessionHost(SessionController session, string senderId)
        {
            if (session == null || string.IsNullOrWhiteSpace(senderId)) return false;
            foreach (SessionPlayer player in session.Players.Values)
                if (player != null && player.isHost && player.playerId == senderId)
                    return true;
            return false;
        }

        [Serializable]
        private sealed class RosterMessage
        {
            public string senderId;
            public string sceneName;
            public string[] playerIds;
            public int playerCount;
        }
    }
}
