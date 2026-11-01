using System;
using System.Collections.Generic;
using System.Text.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer.Runtime.Lobby
{
    public sealed class SessionController : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true
        };

        // NEW — static accessor for PlayerDisplayName. Set in ctor, cleared in Dispose.
        public static SessionController Active { get; private set; }

        private readonly RpcDispatcher dispatcher;
        private readonly string localPlayerId;
        private readonly string localDisplayName;
        private readonly string pluginVersion;
        private readonly string[] availableScenes;
        private readonly bool   isLocalHost;

        private readonly Dictionary<string, SessionPlayer> players =
            new Dictionary<string, SessionPlayer>();

        private string            selectedScene;
        private bool              sessionStarted;
        private SessionVisibility visibility = SessionVisibility.DirectConnect;
        private string            sessionName = "";
        private string            sessionCode = "";
        private int               maxPlayers  = 4;

        private float nextHelloBroadcastTime;
        private float nextStalePruneTime;

        private const float HelloIntervalSeconds = 2.0f;
        private const float PlayerStaleSeconds   = 8.0f;

        private bool  sessionStartPending;
        private float nextSessionStartResendTime;
        private int   sessionStartResendsLeft;
        private const float SessionStartResendIntervalSeconds = 1.0f;
        private const int   SessionStartResendCount            = 4;

        // NEW — next joinOrder the host will hand out. 0 is reserved for the host.
        private int nextJoinOrder = 1;

        public event Action                OnRosterChanged;
        public event Action<string, int>   OnSessionStarting;
        public event Action<string>        OnSceneSelected;
        public event Action<string>        OnHostLeft;

        public IReadOnlyDictionary<string, SessionPlayer> Players { get { return players; } }
        public string            SelectedScene   { get { return selectedScene; } }
        public bool              SessionStarted  { get { return sessionStarted; } }
        public string            LocalPlayerId   { get { return localPlayerId; } }
        public string[]          AvailableScenes { get { return availableScenes; } }
        public SessionVisibility Visibility      { get { return visibility; } }
        public string            SessionName     { get { return sessionName; } }
        public string            SessionCode     { get { return sessionCode; } }
        public int               MaxPlayers      { get { return maxPlayers; } }
        public bool              IsLocalHost     { get { return isLocalHost; } }
        public bool              IsHost          { get { return isLocalHost; } }

        public bool AllReady
        {
            get
            {
                if (players.Count == 0) return false;
                foreach (var kvp in players)
                {
                    if (kvp.Value == null) continue;
                    if (!kvp.Value.isReady) return false;
                }
                return true;
            }
        }

        public SessionController(
            RpcDispatcher dispatcher,
            string localPlayerId,
            string localDisplayName,
            string pluginVersion,
            string[] availableScenes,
            SessionVisibility visibility,
            string sessionName,
            string sessionCode,
            int maxPlayers,
            bool   isLocalHost)
        {
            this.dispatcher       = dispatcher;
            this.localPlayerId    = localPlayerId;
            this.localDisplayName = string.IsNullOrEmpty(localDisplayName) ? "Player" : localDisplayName;
            this.pluginVersion    = pluginVersion;
            this.availableScenes  = availableScenes ?? new string[0];
            this.visibility       = visibility;
            this.sessionName      = string.IsNullOrEmpty(sessionName) ? "Session" : sessionName;
            this.sessionCode      = sessionCode ?? "";
            this.maxPlayers       = Mathf.Clamp(maxPlayers, 1, 4);
            this.isLocalHost      = isLocalHost;

            var self = new SessionPlayer(localPlayerId, localDisplayName, pluginVersion);
            self.lastSeenUtc = SessionPlayer.NowUtcSeconds();
            self.isHost      = isLocalHost;
            self.joinOrder   = 0;                                                    // NEW — host is always slot 0
            if (isLocalHost) self.isReady = true;
            players[localPlayerId] = self;

            Active = this;                                                           // NEW

            Subscribe();
        }

        private void Subscribe()
        {
            if (dispatcher != null)
            {
                dispatcher.CustomMessageReceived += OnCustomMessage;
                DiagnosticLog.Verbose(
                    "[HOST-LEFT TRACE 5/5] SessionController[" + localPlayerId +
                    "] subscribed OnCustomMessage. isLocalHost=" + isLocalHost + ".");
            }
            else
            {
                DiagnosticLog.Warning(
                    "[HOST-LEFT TRACE 5/5] SessionController[" + localPlayerId +
                    "] was created without an RpcDispatcher.");
            }
        }

        public void Dispose()
        {
            if (dispatcher != null)
                dispatcher.CustomMessageReceived -= OnCustomMessage;
            players.Clear();

            if (ReferenceEquals(Active, this)) Active = null;                       // NEW
        }

        public void Tick()
        {
            float now = SessionPlayer.NowUtcSeconds();

            if (now >= nextHelloBroadcastTime)
            {
                nextHelloBroadcastTime = now + HelloIntervalSeconds;
                BroadcastHello();
            }

            if (now >= nextStalePruneTime)
            {
                nextStalePruneTime = now + 2.0f;
                PruneStale(now);
            }

            SessionPlayer self;
            if (players.TryGetValue(localPlayerId, out self) && self != null)
            {
                self.lastSeenUtc = now;
                if (isLocalHost) self.isReady = true;
            }

            if (sessionStartPending && now >= nextSessionStartResendTime)
            {
                if (sessionStartResendsLeft > 0)
                {
                    sessionStartResendsLeft--;
                    nextSessionStartResendTime = now + SessionStartResendIntervalSeconds;

                    DiagnosticLog.Verbose("SessionController[" + localPlayerId +
                                          "]: re-broadcasting SessionStart (" +
                                          sessionStartResendsLeft + " resend(s) left) in case a " +
                                          "joiner missed the original packet.");
                    Send(SessionEvents.SessionStart, new SessionStartMessage
                    {
                        sceneName         = selectedScene,
                        startedByPlayerId = localPlayerId,
                        expectedPlayerCount = 0
                    });
                }
                else
                {
                    sessionStartPending = false;
                }
            }
        }

        public void BroadcastHello()
        {
            DiagnosticLog.Verbose("SessionController[" + localPlayerId +
                                   "]: broadcasting hello (scene='" +
                                   (selectedScene ?? "<null>") + "').");
            Send(SessionEvents.PlayerHello, new SessionHelloMessage
            {
                playerId      = localPlayerId,
                displayName   = localDisplayName,
                pluginVersion = pluginVersion,
                isHost        = isLocalHost,
                isReady       = isLocalHost ? true : GetLocalReady(),
                selectedScene = selectedScene
            });
        }

        public void ToggleReady()
        {
            if (isLocalHost) return;

            SessionPlayer self;
            if (!players.TryGetValue(localPlayerId, out self) || self == null) return;

            self.isReady = !self.isReady;
            Send(SessionEvents.PlayerReady, new SessionReadyMessage
            {
                playerId = localPlayerId,
                isReady  = self.isReady
            });
            RaiseRosterChanged();
        }

        public void SelectScene(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return;
            if (!isLocalHost) return;

            selectedScene = sceneName;

            DiagnosticLog.Info("SessionController[" + localPlayerId +
                               "]: host selected scene '" + sceneName +
                               "'. Broadcasting.");

            Send(SessionEvents.SceneSelected, new SessionSceneSelectedMessage
            {
                sceneName          = sceneName,
                selectedByPlayerId = localPlayerId
            });
            var h = OnSceneSelected; if (h != null) h(sceneName);
        }

        public void StartSession()
        {
            if (!isLocalHost) return;
            if (!AllReady) return;
            if (string.IsNullOrEmpty(selectedScene)) return;

            sessionStarted = true;
            Send(SessionEvents.SessionStart, new SessionStartMessage
            {
                sceneName         = selectedScene,
                startedByPlayerId = localPlayerId,
                expectedPlayerCount = 0
            });

            sessionStartPending        = true;
            sessionStartResendsLeft    = SessionStartResendCount;
            nextSessionStartResendTime = SessionPlayer.NowUtcSeconds() + SessionStartResendIntervalSeconds;
        }

        private void OnCustomMessage(string eventName, string json)
        {
            DiagnosticLog.Verbose("SessionController[" + localPlayerId + "]: OnCustomMessage '" + eventName + "'.");

            if (eventName == SessionEvents.HostLeft)
            {
                DiagnosticLog.Verbose(
                    "[HOST-LEFT TRACE 5/5] SessionController[" + localPlayerId +
                    "] received host-left in OnCustomMessage; selecting HandleHostLeft.");
            }

            try
            {
                switch (eventName)
                {
                    case SessionEvents.PlayerHello:   HandleHello(json);         break;
                    case SessionEvents.PlayerReady:   HandleReady(json);         break;
                    case SessionEvents.SceneSelected: HandleSceneSelected(json); break;
                    case SessionEvents.SessionStart:  HandleSessionStart(json);  break;
                    case SessionEvents.PlayerJoined:  HandlePlayerJoined(json);  break;
                    case SessionEvents.PlayerLeft:    HandlePlayerLeft(json);    break;
                    case SessionEvents.HostLeft:      HandleHostLeft(json);      break;
                    case SessionEvents.ColorAssign:   HandleColorAssign(json);   break;   // NEW
                    default:
                        DiagnosticLog.Verbose("SessionController[" + localPlayerId +
                                              "]: unhandled event '" + eventName + "'.");
                        break;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("SessionController: handling '" + eventName +
                                      "' failed:\n" + ex.ToString());
            }
        }

        private void HandleHello(string json)
        {
            var msg = JsonSerializer.Deserialize<SessionHelloMessage>(json, JsonOpts);
            if (msg == null || string.IsNullOrEmpty(msg.playerId)) return;
            if (msg.playerId == localPlayerId) return;

            SessionPlayer p;
            if (!players.TryGetValue(msg.playerId, out p) || p == null)
            {
                p = new SessionPlayer(msg.playerId, msg.displayName, msg.pluginVersion);
                players[msg.playerId] = p;
            }
            if (!string.IsNullOrEmpty(msg.displayName)) p.displayName = msg.displayName;
            if (!string.IsNullOrEmpty(msg.pluginVersion)) p.pluginVersion = msg.pluginVersion;
            p.lastSeenUtc = SessionPlayer.NowUtcSeconds();

            p.isHost = msg.isHost;
            if (msg.isHost) p.isReady = true;
            else            p.isReady = msg.isReady;

            if (msg.isHost && !isLocalHost && !string.IsNullOrEmpty(msg.selectedScene))
            {
                if (selectedScene != msg.selectedScene)
                {
                    selectedScene = msg.selectedScene;
                    DiagnosticLog.Info("SessionController[" + localPlayerId +
                                       "]: adopted host scene '" + selectedScene +
                                       "' from hello.");
                    var h = OnSceneSelected;
                    if (h != null)
                    {
                        try { h(selectedScene); }
                        catch (Exception ex)
                        {
                            DiagnosticLog.Warning("SessionController: OnSceneSelected threw: " + ex.ToString());
                        }
                    }
                }
            }

            // NEW — host-side joinOrder assignment, first time we see this player.
            // Guarded by joinOrder == 0 which the host never assigns to non-hosts,
            // so this fires exactly once per joining player. Existing behavior
            // above is untouched.
            if (isLocalHost && p.joinOrder == 0)
            {
                p.joinOrder = nextJoinOrder++;
                Send(SessionEvents.ColorAssign, new SessionColorAssignMessage
                {
                    playerId  = p.playerId,
                    joinOrder = p.joinOrder
                });
                DiagnosticLog.Info("SessionController[" + localPlayerId +
                                   "]: assigned joinOrder=" + p.joinOrder +
                                   " to '" + p.playerId + "'.");
            }

            RaiseRosterChanged();
        }

        private void HandleReady(string json)
        {
            var msg = JsonSerializer.Deserialize<SessionReadyMessage>(json, JsonOpts);
            if (msg == null || string.IsNullOrEmpty(msg.playerId)) return;

            SessionPlayer p;
            if (!players.TryGetValue(msg.playerId, out p) || p == null)
            {
                p = new SessionPlayer(msg.playerId, "", "?");
                players[msg.playerId] = p;
            }
            p.isReady     = msg.isReady;
            p.lastSeenUtc = SessionPlayer.NowUtcSeconds();
            RaiseRosterChanged();
        }

        private void HandleSceneSelected(string json)
        {
            var msg = JsonSerializer.Deserialize<SessionSceneSelectedMessage>(json, JsonOpts);
            if (msg == null || string.IsNullOrEmpty(msg.sceneName)) return;

            DiagnosticLog.Info("SessionController[" + localPlayerId +
                               "]: received scene selection '" + msg.sceneName + "'.");

            selectedScene = msg.sceneName;
            var h = OnSceneSelected;
            if (h != null)
            {
                try { h(msg.sceneName); }
                catch (Exception ex)
                {
                    DiagnosticLog.Warning("SessionController: OnSceneSelected threw: " + ex.ToString());
                }
            }
        }

        private void HandleSessionStart(string json)
        {
            var msg = JsonSerializer.Deserialize<SessionStartMessage>(json, JsonOpts);
            if (msg == null || string.IsNullOrEmpty(msg.sceneName)) return;
            sessionStarted = true;
            if (isLocalHost) sessionStartPending = false;
            selectedScene  = msg.sceneName;
            var h = OnSessionStarting;
            if (h != null)
            {
                try { h(selectedScene, Math.Max(1, msg.expectedPlayerCount)); }
                catch (Exception ex)
                {
                    DiagnosticLog.Warning("SessionController: OnSessionStarting threw: " + ex.ToString());
                }
            }
        }

        private void HandlePlayerJoined(string json)
        {
            DiagnosticLog.Verbose("SessionController[" + localPlayerId + "]: player joined; re-hello.");
            BroadcastHello();
        }

        private void HandlePlayerLeft(string json)
        {
            SessionPlayerLeftNotice msg;
            try { msg = JsonSerializer.Deserialize<SessionPlayerLeftNotice>(json, JsonOpts); }
            catch { msg = null; }

            string leavingId = msg != null ? msg.playerId : null;
            if (string.IsNullOrEmpty(leavingId)) return;

            SessionPlayer leavingPlayer;
            players.TryGetValue(leavingId, out leavingPlayer);
            string displayName = msg != null ? msg.playerName : null;
            if (string.IsNullOrWhiteSpace(displayName) && leavingPlayer != null)
                displayName = leavingPlayer.displayName;

            if (players.Remove(leavingId))
            {
                string sceneName = SceneManager.GetActiveScene().name;
                if (sessionStarted && !string.Equals(sceneName, "SceneMenu", StringComparison.Ordinal))
                    MiSideMultiplayer.API.QBLib.Notifications.ShowPlayerLeft(
                        string.IsNullOrWhiteSpace(displayName) ? leavingId : displayName);

                DiagnosticLog.Info("SessionController[" + localPlayerId +
                                   "]: removed player '" + leavingId + "' from roster.");
                RaiseRosterChanged();
            }
        }

        private void HandleHostLeft(string json)
        {
            DiagnosticLog.Info("SessionController[" + localPlayerId +
                               "]: [HOST-LEFT TRACE 5/5] HandleHostLeft ENTER. isLocalHost=" + isLocalHost +
                               " subscribers=" + (OnHostLeft == null ? 0 : OnHostLeft.GetInvocationList().Length));

            if (isLocalHost) return;

            SessionHostLeftNotice msg;
            try { msg = JsonSerializer.Deserialize<SessionHostLeftNotice>(json, JsonOpts); }
            catch { msg = null; }

            string hostName = msg != null ? msg.playerName : null;
            if (string.IsNullOrEmpty(hostName) && msg != null && !string.IsNullOrEmpty(msg.playerId))
            {
                SessionPlayer p;
                if (players.TryGetValue(msg.playerId, out p) && p != null)
                    hostName = p.displayName;
            }

            DiagnosticLog.Info("SessionController[" + localPlayerId +
                               "]: host left ('" + (hostName ?? "?") + "'). Firing OnHostLeft.");

            var h = OnHostLeft;
            if (h == null)
            {
                DiagnosticLog.Warning("SessionController: OnHostLeft has NO subscribers.");
                return;
            }

            try
            {
                h(hostName ?? "");
                DiagnosticLog.Verbose(
                    "[HOST-LEFT TRACE 5/5] All OnHostLeft handlers returned for SessionController[" +
                    localPlayerId + "].");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("SessionController: OnHostLeft handler threw: " + ex.ToString());
            }
        }

        // NEW — joiner receives "your joinOrder is N" from the host.
        private void HandleColorAssign(string json)
        {
            SessionColorAssignMessage msg;
            try { msg = JsonSerializer.Deserialize<SessionColorAssignMessage>(json, JsonOpts); }
            catch { msg = null; }
            if (msg == null) return;
            if (string.IsNullOrEmpty(msg.playerId) || msg.playerId != localPlayerId) return;

            SessionPlayer self;
            if (players.TryGetValue(localPlayerId, out self) && self != null)
            {
                self.joinOrder = msg.joinOrder;
                DiagnosticLog.Info("SessionController[" + localPlayerId +
                                   "]: received joinOrder=" + msg.joinOrder + " from host.");
            }
        }

        private bool GetLocalReady()
        {
            SessionPlayer self;
            if (players.TryGetValue(localPlayerId, out self) && self != null)
                return self.isReady;
            return false;
        }

        private void PruneStale(float now)
        {
            List<string> toRemove = null;
            foreach (var kvp in players)
            {
                if (kvp.Key == localPlayerId) continue;
                if (kvp.Value == null) continue;
                if (now - kvp.Value.lastSeenUtc > PlayerStaleSeconds)
                {
                    if (toRemove == null) toRemove = new List<string>();
                    toRemove.Add(kvp.Key);
                }
            }
            if (toRemove == null) return;

            foreach (var id in toRemove)
            {
                players.Remove(id);
                DiagnosticLog.Verbose("SessionController[" + localPlayerId + "]: pruned stale '" + id + "'.");
            }
            RaiseRosterChanged();
        }

        private void RaiseRosterChanged()
        {
            var h = OnRosterChanged;
            if (h == null) return;

            var list = h.GetInvocationList();
            for (int i = 0; i < list.Length; i++)
            {
                try
                {
                    ((Action)list[i]).Invoke();
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warning("SessionController: OnRosterChanged subscriber " +
                                          i + " threw:\n" + ex.ToString());
                }
            }
        }

        private void Send<T>(string eventName, T msg)
        {
            if (dispatcher == null) return;
            try
            {
                string json = JsonSerializer.Serialize(msg, JsonOpts);

                if (json == "{}" && msg != null)
                {
                    DiagnosticLog.Warning("SessionController: '" + eventName +
                                          "' serialized to an empty object.");
                }

                dispatcher.SendCustom(eventName, json);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("SessionController: send '" + eventName + "' failed: " + ex.Message);
            }
        }
    }
}
