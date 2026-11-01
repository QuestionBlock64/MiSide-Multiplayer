using System.Collections.Generic;
using MiSideMultiplayer.API;
using MiSideMultiplayer.API.Factories;
using MiSideMultiplayer.Runtime.Lobby;
using MiSideMultiplayer.Runtime.UI;
using UnityEngine;

namespace MiSideMultiplayer.Runtime.Menu
{
    internal static class MultiplayerPublicLobbyMenu
    {
        public const string PageName  = "Location Multiplayer Public Lobby";
        public const string PageTitle = "PUBLIC LOBBIES";

        private const float RowSpacing = 55f;

        private static GameMenu _menu;
        private static GameMenu _lobbyMenu;
        private static MenuOption _refreshRow;
        private static MenuOption _backRow;

        private sealed class SessionRow
        {
            public MenuOption Option;
            public DirectorySessionEntry Entry;
        }

        // Reuse row objects across refreshes. Destroying and rebuilding menu-template
        // buttons during quick list replies leaves duplicate visual children behind.
        private static readonly List<SessionRow> _sessionRows = new List<SessionRow>();
        private static int _visibleSessionRowCount;

        // Transport bring-up state.
        private static bool  _transportRequested;
        private static bool  _transportUp;
        private static float _transportRequestedAt;
        // A central connection may quietly probe both relay endpoints before
        // reporting a real failure to the player.
        private static readonly float TransportTimeoutSeconds =
            QBRelayFailover.HandshakeTimeoutSeconds * QBRelayFailover.EndpointCount + 6f;
        private const  float RetryDelaySeconds = 1f;

        // List-request state.
        private static bool  _listRequestPending;
        private static float _listRequestedAt;
        private const  float ListTimeoutSeconds = 5f;
        private static bool  _refreshRequested;
        private static float _nextListRetryAt;
        private static bool  _pageWasActive;
        private static readonly HashSet<int> _visitedDirectoryEndpoints = new HashSet<int>();
        private static readonly List<DirectorySessionEntry> _directoryResults =
            new List<DirectorySessionEntry>();
        private static bool _directoryScanActive;
        private static int _requestedDirectoryEndpointIndex = -1;

        private sealed class PendingJoin
        {
            public string RoomId;
            public SessionVisibility Visibility;
            public float StartedAt;
        }

        private static PendingJoin _pendingJoin;

        public static void AbortPendingConnection()
        {
            _pendingJoin = null;
            _transportRequested = false;
            _transportUp = false;
            _listRequestPending = false;
            SessionDirectory.CancelPendingListRequest();
            _refreshRequested = false;
            _directoryScanActive = false;
            _visitedDirectoryEndpoints.Clear();
            _directoryResults.Clear();
            _requestedDirectoryEndpointIndex = -1;
            ClearSessionRows();
        }

        public static GameMenu Build(GameMenu playMenu, GameMenu lobbyMenu)
        {
            if (MenuManager.Instance == null) return null;

            GameMenu existing = MenuManager.Instance.Find(PageName);
            if (existing != null)
            {
                _menu = existing;
                _lobbyMenu = lobbyMenu;
                _refreshRow = _menu.Find("REFRESH");
                _backRow = _menu.Find("BACK");
                _pageWasActive = false;
                return existing;
            }

            _menu = new MenuFactory()
                .SetObjectName(PageName)
                .SetTitle(PageTitle)
                .Build();
            if (_menu == null) return null;
            _lobbyMenu = lobbyMenu;

            _refreshRow = BuildRow("REFRESH", OnRefresh);

            if (playMenu != null)
            {
                _backRow = new MenuOptionFactory()
                    .SetName("BACK")
                    .SetParent(_menu)
                    .SetNextLocation(playMenu)
                    .Build();
            }

            LayoutRows();
            DiagnosticLog.Info("MultiplayerPublicLobbyMenu: built '" + PageName + "'.");
            return _menu;
        }

        public static void DrawGui() { }

        private static MenuOption BuildRow(string label, System.Action onClick, bool navigate = false)
        {
            try
            {
                var f = new MenuOptionFactory()
                    .SetName(label)
                    .SetParent(_menu)
                    .SetNextLocation(navigate && _lobbyMenu != null ? _lobbyMenu : _menu);

                if (onClick != null) f = f.SetOnClick(onClick);

                MenuOption opt = f.Build();

                if (opt != null && opt.OptionObject != null && !navigate)
                {
                    MenuNextLocation next = opt.OptionObject.GetComponent<MenuNextLocation>();
                    if (next != null) UnityEngine.Object.Destroy(next);
                }

                return opt;
            }
            catch (System.Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerPublicLobbyMenu: row '" + label + "' failed: " + ex.Message);
                return null;
            }
        }

        // ── Transport lifecycle ─────────────────────────────────────────────

        private static bool IsPageActive()
        {
            if (_menu == null || _menu.MenuObject == null) return false;

            bool fallback = _menu.MenuObject.activeInHierarchy;
            try
            {
                Component location = _menu.MenuObject.GetComponent("MenuLocation");
                return location == null
                    ? fallback
                    : QBLib.Fields.ReadBool(location, "active", fallback);
            }
            catch
            {
                return fallback;
            }
        }

        private static void RequestTransport()
        {
            if (_transportUp && MiSideMultiplayerPlugin.IsRelayReady()) return;
            if (_transportRequested) return;

            _transportRequested   = true;
            _transportRequestedAt = Time.unscaledTime;

            DiagnosticLog.Info("MultiplayerPublicLobbyMenu: requesting transport to central relay.");

            if (!MiSideMultiplayerPlugin.ConnectToCentral("default"))
            {
                DiagnosticLog.Warning("MultiplayerPublicLobbyMenu: ConnectTo failed.");
                MultiplayerConnectionErrorHandler.HandleRelayFailure(
                    RelayFailureKind.ConnectionError, false);
            }
        }

        // ── Tick ────────────────────────────────────────────────────────────

        public static void Tick()
        {
            if (_menu == null) return;

            bool pageIsActive = IsPageActive();
            MultiplayerConnectingOverlay.SetLobbyScanVisible(pageIsActive && _directoryScanActive);
            if (pageIsActive && !_pageWasActive)
            {
                _pageWasActive = true;
                DiagnosticLog.Info("MultiplayerPublicLobbyMenu: entered public-lobby page; refreshing.");
                RequestRefresh();
            }
            else if (!pageIsActive)
            {
                _pageWasActive = false;
                TickPendingJoin();
                return;
            }

            bool relayConnected = MiSideMultiplayerPlugin.IsRelayReady();
            if (relayConnected && !_transportUp)
            {
                _transportUp = true;
                _transportRequested = false;
                DiagnosticLog.Info("MultiplayerPublicLobbyMenu: transport up.");
            }
            else if (!relayConnected && _transportUp)
            {
                _transportUp = false;
                _listRequestPending = false;
                _refreshRequested = true;
                _nextListRetryAt = Time.unscaledTime;
                DiagnosticLog.Warning("MultiplayerPublicLobbyMenu: transport disconnected while listing; retrying.");
            }

            if (_transportRequested && !_transportUp &&
                Time.unscaledTime - _transportRequestedAt > TransportTimeoutSeconds)
            {
                DiagnosticLog.Warning("MultiplayerPublicLobbyMenu: transport never came up within " +
                                       TransportTimeoutSeconds + "s.");
                _transportRequested = false;
                _directoryScanActive = false;
                _refreshRequested = false;
                MultiplayerConnectingOverlay.SetLobbyScanVisible(false);
            }

            if (_listRequestPending &&
                Time.unscaledTime - _listRequestedAt > ListTimeoutSeconds)
            {
                DiagnosticLog.Warning("MultiplayerPublicLobbyMenu: list request timed out.");
                _listRequestPending = false;
                SessionDirectory.CancelPendingListRequest();
                int currentEndpoint = MiSideMultiplayerPlugin.GetCurrentCentralEndpointIndex();
                if (currentEndpoint >= 0) _visitedDirectoryEndpoints.Add(currentEndpoint);
                if (_requestedDirectoryEndpointIndex >= 0)
                    _visitedDirectoryEndpoints.Add(_requestedDirectoryEndpointIndex);
                _requestedDirectoryEndpointIndex = -1;
                ContinueDirectoryScan();
            }

            if (_refreshRequested && Time.unscaledTime >= _nextListRetryAt)
            {
                if (_transportUp)
                    SendListRequest();
                else if (!_transportRequested)
                    RequestTransport();
            }

            TickPendingJoin();
        }

        private static void TickPendingJoin()
        {
            if (_pendingJoin != null)
            {
                if (MiSideMultiplayerPlugin.IsRelayReady())
                {
                    var p = _pendingJoin;
                    _pendingJoin = null;
                    MultiplayerConnectingOverlay.Hide();
                    MultiplayerLobbyMenu.ConfigureAsClient(p.RoomId, p.Visibility);
                }
                else if (Time.unscaledTime - _pendingJoin.StartedAt > TransportTimeoutSeconds)
                {
                    DiagnosticLog.Warning("MultiplayerPublicLobbyMenu: transport never came up; giving up.");
                    _pendingJoin = null;
                    MultiplayerConnectingOverlay.Hide();
                }
            }
        }

        // ── Refresh ─────────────────────────────────────────────────────────

        private static void OnRefresh()
        {
            RequestRefresh();
        }

        private static void RequestRefresh()
        {
            if (_menu == null) return;

            if (!_listRequestPending)
            {
                _visitedDirectoryEndpoints.Clear();
                _directoryResults.Clear();
                _directoryScanActive = true;
            }

            _refreshRequested = true;
            _nextListRetryAt = Time.unscaledTime;

            // SessionDirectory has one list callback slot. Do not issue a second
            // request until the active request has completed or timed out.
            if (_listRequestPending)
            {
                DiagnosticLog.Verbose("MultiplayerPublicLobbyMenu: refresh queued behind active list request.");
                return;
            }

            ClearSessionRows();

            if (MiSideMultiplayerPlugin.IsRelayReady())
            {
                _transportUp = true;
                SendListRequest();
            }
            else if (!_transportRequested)
            {
                _transportUp = false;
                RequestTransport();
            }
        }

        private static void SendListRequest()
        {
            if (!_refreshRequested || _listRequestPending) return;

            // A new request invalidates the prior snapshot. Rows are recreated only
            // from the authoritative response, so departed sessions never linger.
            ClearSessionRows();
            _refreshRequested = false;
            _listRequestPending = true;
            _listRequestedAt = Time.unscaledTime;
            if (_requestedDirectoryEndpointIndex < 0)
                _requestedDirectoryEndpointIndex = MiSideMultiplayerPlugin.GetCurrentCentralEndpointIndex();
            DiagnosticLog.Info("MultiplayerPublicLobbyMenu: requesting public-session list.");

            SessionDirectory.ListPublicSessions(sessions =>
            {
                _listRequestPending = false;
                int currentEndpoint = MiSideMultiplayerPlugin.GetCurrentCentralEndpointIndex();
                if (currentEndpoint >= 0) _visitedDirectoryEndpoints.Add(currentEndpoint);
                if (sessions != null)
                {
                    foreach (DirectorySessionEntry entry in sessions)
                    {
                        if (entry != null && entry.relayEndpointIndex >= 0)
                            _visitedDirectoryEndpoints.Add(entry.relayEndpointIndex);
                    }
                }
                if (_requestedDirectoryEndpointIndex >= 0)
                    _visitedDirectoryEndpoints.Add(_requestedDirectoryEndpointIndex);
                _requestedDirectoryEndpointIndex = -1;
                if (sessions != null) _directoryResults.AddRange(sessions);
                ContinueDirectoryScan();
            });
        }

        private static void ContinueDirectoryScan()
        {
            if (!_directoryScanActive) return;

            int endpointCount = MiSideMultiplayerPlugin.GetCentralEndpointCount();
            for (int i = 0; i < endpointCount; i++)
            {
                if (_visitedDirectoryEndpoints.Contains(i)) continue;

                _refreshRequested = true;
                _nextListRetryAt = Time.unscaledTime;
                _transportUp = false;
                _transportRequested = true;
                _transportRequestedAt = Time.unscaledTime;
                _requestedDirectoryEndpointIndex = i;
                DiagnosticLog.Info("MultiplayerPublicLobbyMenu: scanning " +
                    MiSideMultiplayerPlugin.GetCentralEndpointName(i) + " for public lobbies.");
                if (!MiSideMultiplayerPlugin.ConnectToCentralAt(i, "default", true))
                {
                    _transportRequested = false;
                    _visitedDirectoryEndpoints.Add(i);
                    _requestedDirectoryEndpointIndex = -1;
                    ContinueDirectoryScan();
                }
                return;
            }

            _directoryScanActive = false;
            _refreshRequested = false;
            MultiplayerConnectingOverlay.SetLobbyScanVisible(false);
            RenderSessions(_directoryResults);
        }

        private static void RenderSessions(List<DirectorySessionEntry> sessions)
        {
            var seenSessionIds = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            var visibleEntries = new List<DirectorySessionEntry>();
            if (sessions != null)
            {
                foreach (DirectorySessionEntry entry in sessions)
                {
                    if (entry == null || entry.visibility != SessionVisibility.Public ||
                        string.IsNullOrWhiteSpace(entry.sessionId) ||
                        !seenSessionIds.Add(entry.sessionId))
                        continue;

                    visibleEntries.Add(entry);
                }
            }

            EnsureRowCapacity(visibleEntries.Count);
            _visibleSessionRowCount = 0;

            for (int i = 0; i < visibleEntries.Count && i < _sessionRows.Count; i++)
            {
                SessionRow row = _sessionRows[i];
                if (row == null || row.Option == null || row.Option.OptionObject == null) continue;

                row.Entry = visibleEntries[i];
                row.Option.Text = FormatSessionLabel(row.Entry);
                row.Option.OptionObject.SetActive(true);
                _visibleSessionRowCount++;
            }

            for (int i = _visibleSessionRowCount; i < _sessionRows.Count; i++)
            {
                SessionRow row = _sessionRows[i];
                if (row == null) continue;
                row.Entry = null;
                try
                {
                    if (row.Option != null && row.Option.OptionObject != null)
                        row.Option.OptionObject.SetActive(false);
                }
                catch { }
            }

            LayoutRows();
            DiagnosticLog.Info("MultiplayerPublicLobbyMenu: rendered " + _visibleSessionRowCount + " public session(s).");
        }

        private static void ClearSessionRows()
        {
            _visibleSessionRowCount = 0;
            foreach (SessionRow row in _sessionRows)
            {
                try
                {
                    if (row == null) continue;
                    row.Entry = null;
                    if (row.Option != null && row.Option.OptionObject != null)
                        row.Option.OptionObject.SetActive(false);
                }
                catch { }
            }
            LayoutRows();
        }

        private static void EnsureRowCapacity(int requiredCount)
        {
            for (int i = _sessionRows.Count - 1; i >= 0; i--)
            {
                SessionRow row = _sessionRows[i];
                if (row == null || row.Option == null || row.Option.OptionObject == null)
                    _sessionRows.RemoveAt(i);
            }

            while (_sessionRows.Count < requiredCount)
            {
                SessionRow binding = new SessionRow();
                binding.Option = BuildRow("", () => OnJoinSession(binding.Entry), navigate: _lobbyMenu != null);
                if (binding.Option == null || binding.Option.OptionObject == null) break;

                binding.Option.OptionObject.SetActive(false);
                _sessionRows.Add(binding);
            }
        }

        private static string FormatSessionLabel(DirectorySessionEntry entry)
        {
            string sessionName = string.IsNullOrWhiteSpace(entry.sessionName) ? "Session" : entry.sessionName;
            string hostName = string.IsNullOrWhiteSpace(entry.hostDisplayName) ? "Player" : entry.hostDisplayName;
            int maxPlayers = Mathf.Max(1, entry.maxPlayers);
            int playerCount = Mathf.Clamp(entry.playerCount, 0, maxPlayers);
            return sessionName + "  [" + playerCount + "/" + maxPlayers + "]  by " + hostName;
        }

        private static void LayoutRows()
        {
            float refreshY = -100f;
            if (_refreshRow != null && _refreshRow.OptionObject != null)
            {
                RectTransform refreshRt = _refreshRow.OptionObject.GetComponent<RectTransform>();
                if (refreshRt != null) refreshY = refreshRt.anchoredPosition.y;
            }

            for (int i = 0; i < _visibleSessionRowCount; i++)
            {
                SessionRow row = _sessionRows[i];
                if (row == null || row.Option == null || row.Option.OptionObject == null) continue;

                RectTransform rt = row.Option.OptionObject.GetComponent<RectTransform>();
                if (rt != null)
                {
                    Vector2 position = rt.anchoredPosition;
                    position.y = refreshY - RowSpacing * (i + 1);
                    rt.anchoredPosition = position;
                }
            }

            if (_backRow != null && _backRow.OptionObject != null)
            {
                RectTransform backRt = _backRow.OptionObject.GetComponent<RectTransform>();
                if (backRt != null)
                {
                    Vector2 position = backRt.anchoredPosition;
                    position.y = refreshY - RowSpacing * (_visibleSessionRowCount + 2);
                    backRt.anchoredPosition = position;
                }
            }
        }

        private static void OnJoinSession(DirectorySessionEntry entry)
        {
            if (entry == null || _lobbyMenu == null) return;

            string serverName = entry.relayEndpointIndex >= 0
                ? MiSideMultiplayerPlugin.GetCentralEndpointName(entry.relayEndpointIndex)
                : "unknown server";
            DiagnosticLog.Info("PublicLobby: joining '" + entry.sessionName +
                                "' room='" + entry.sessionId + "' on " + serverName + ".");

            MultiplayerConnectingOverlay.Show("CONNECTING", "PLEASE WAIT");

            if (!MiSideMultiplayerPlugin.ConnectToCentralAt(
                    entry.relayEndpointIndex, entry.sessionId, false))
            {
                DiagnosticLog.Warning("PublicLobby: ConnectTo failed.");
                MultiplayerConnectionErrorHandler.HandleRelayFailure(
                    RelayFailureKind.ConnectionError, false);
                return;
            }

            _pendingJoin = new PendingJoin
            {
                RoomId     = entry.sessionId,
                Visibility = SessionVisibility.Public,
                StartedAt  = Time.unscaledTime
            };
        }
    }
}
