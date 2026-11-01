using MiSideMultiplayer.API;
using MiSideMultiplayer.API.Factories;
using MiSideMultiplayer.Runtime.Lobby;
using MiSideMultiplayer.Runtime.UI;
using UnityEngine;

namespace MiSideMultiplayer.Runtime.Menu
{
    internal static class MultiplayerPrivateLobbyMenu
    {
        public const string PageName  = "Location Multiplayer Private Lobby";
        public const string PageTitle = "PRIVATE LOBBY";

        private const float BackY = -650f;

        private static GameMenu   _menu;
        private static GameMenu   _lobbyMenu;
        private static MenuOption _rowCode;

        private static string _code = "";

        private static string _editingKey;
        private static string _editingValue;
        private static int    _editingCursor;

        private enum PendingJoinStage
        {
            ConnectingToDirectory,
            ResolvingCode,
            ConnectingToRoom
        }

        private sealed class PendingJoin
        {
            public string Code;
            public string RoomId;
            public PendingJoinStage Stage;
            public float StageStartedAt;
            public System.Collections.Generic.HashSet<int> TriedEndpointIndices =
                new System.Collections.Generic.HashSet<int>();
        }

        private static readonly float CentralStageTimeoutSeconds =
            QBRelayFailover.HandshakeTimeoutSeconds * QBRelayFailover.EndpointCount + 8f;

        private static PendingJoin _pendingJoin;

        public static void AbortPendingConnection()
        {
            _pendingJoin = null;
        }

        public static GameMenu Build(GameMenu playMenu, GameMenu lobbyMenu)
        {
            if (MenuManager.Instance == null) return null;

            GameMenu existing = MenuManager.Instance.Find(PageName);
            if (existing != null)
            {
                _menu = existing;
                _lobbyMenu = lobbyMenu;
                return existing;
            }

            GameMenu menu = new MenuFactory()
                .SetObjectName(PageName)
                .SetTitle(PageTitle)
                .Build();
            if (menu == null) return null;
            _menu = menu;
            _lobbyMenu = lobbyMenu;

            _rowCode = BuildRow(menu, "CODE: --", OnCodeClicked);

            // JOIN: navigate to lobby on click.
            MenuOption join = new MenuOptionFactory()
                .SetName("JOIN")
                .SetParent(menu)
                .SetNextLocation(_lobbyMenu != null ? _lobbyMenu : menu)
                .SetOnClick(OnJoin)
                .Build();

            if (_lobbyMenu == null && join != null && join.OptionObject != null)
                Neutralize(join);

            if (playMenu != null)
            {
                MenuOption back = new MenuOptionFactory()
                    .SetName("BACK")
                    .SetParent(menu)
                    .SetNextLocation(playMenu)
                    .Build();
                if (back != null && back.OptionObject != null)
                {
                    RectTransform rt = back.OptionObject.GetComponent<RectTransform>();
                    if (rt != null) { Vector2 p = rt.anchoredPosition; p.y = BackY; rt.anchoredPosition = p; }
                }
            }

            RefreshLabels();
            DiagnosticLog.Info("MultiplayerPrivateLobbyMenu: built '" + PageName + "'.");
            return menu;
        }

        private static MenuOption BuildRow(GameMenu parent, string label, System.Action onClick)
        {
            try
            {
                MenuOption opt = new MenuOptionFactory()
                    .SetName(label)
                    .SetParent(parent)
                    .SetNextLocation(parent)
                    .SetOnClick(onClick)
                    .Build();
                Neutralize(opt);
                return opt;
            }
            catch (System.Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerPrivateLobbyMenu: row '" + label + "' failed: " + ex.Message);
                return null;
            }
        }

        private static void Neutralize(MenuOption opt)
        {
            if (opt == null || opt.OptionObject == null) return;
            MenuNextLocation next = opt.OptionObject.GetComponent<MenuNextLocation>();
            if (next != null) UnityEngine.Object.Destroy(next);
        }

        public static void Tick()
        {
            if (_menu == null) return;
            if (_editingKey != null) RefreshLabels();

            PendingJoin pending = _pendingJoin;
            if (pending == null) return;

            float elapsed = Time.unscaledTime - pending.StageStartedAt;
            if (pending.Stage == PendingJoinStage.ConnectingToDirectory &&
                MiSideMultiplayerPlugin.IsRelayReady())
            {
                int endpointIndex = MiSideMultiplayerPlugin.GetCurrentCentralEndpointIndex();
                if (endpointIndex >= 0 && !pending.TriedEndpointIndices.Add(endpointIndex))
                {
                    int nextEndpoint = FindUntestedEndpoint(pending);
                    if (nextEndpoint >= 0)
                    {
                        pending.StageStartedAt = Time.unscaledTime;
                        MiSideMultiplayerPlugin.ConnectToCentralAt(nextEndpoint, "default", false);
                    }
                    else
                    {
                        FinishPrivateCodeNotFound(pending);
                    }
                    return;
                }

                pending.Stage = PendingJoinStage.ResolvingCode;
                pending.StageStartedAt = Time.unscaledTime;
                DiagnosticLog.Info("MultiplayerPrivateLobbyMenu: directory relay " + endpointIndex +
                                   " ready; resolving code.");
                SessionDirectory.JoinByCode(pending.Code, response => OnCodeResolved(pending, response));
                return;
            }

            if (pending.Stage == PendingJoinStage.ConnectingToRoom &&
                MiSideMultiplayerPlugin.IsRelayReady())
            {
                _pendingJoin = null;
                MultiplayerConnectingOverlay.Hide();
                DiagnosticLog.Info("MultiplayerPrivateLobbyMenu: private-room relay ready; joining room '" +
                                   pending.RoomId + "'.");
                MultiplayerLobbyMenu.ConfigureAsClient(pending.RoomId, SessionVisibility.Private);
                return;
            }

            if (elapsed > CentralStageTimeoutSeconds)
            {
                DiagnosticLog.Warning("MultiplayerPrivateLobbyMenu: " + pending.Stage +
                                      " timed out after " + elapsed.ToString("F1") + "s.");
                _pendingJoin = null;
                if (pending.Stage == PendingJoinStage.ResolvingCode)
                    MultiplayerConnectionErrorHandler.HandleRelayFailure(RelayFailureKind.TimedOut, false);
                else
                    MultiplayerConnectingOverlay.Hide();
            }
        }

        private static void OnCodeResolved(PendingJoin pending, DirectoryJoinByCodeResponse response)
        {
            if (!ReferenceEquals(_pendingJoin, pending) || pending.Stage != PendingJoinStage.ResolvingCode)
                return;

            if (response == null || !response.found ||
                string.IsNullOrWhiteSpace(response.sessionId) ||
                response.visibility != SessionVisibility.Private)
            {
                int nextEndpoint = FindUntestedEndpoint(pending);
                if (nextEndpoint >= 0)
                {
                    pending.Stage = PendingJoinStage.ConnectingToDirectory;
                    pending.StageStartedAt = Time.unscaledTime;
                    DiagnosticLog.Info("MultiplayerPrivateLobbyMenu: code absent on this relay; checking relay " +
                                       nextEndpoint + ".");
                    if (!MiSideMultiplayerPlugin.ConnectToCentralAt(nextEndpoint, "default", false))
                        FinishPrivateCodeNotFound(pending);
                }
                else
                {
                    FinishPrivateCodeNotFound(pending);
                }
                return;
            }

            int hostEndpointIndex = response.relayEndpointIndex;
            if (hostEndpointIndex < 0)
            {
                DiagnosticLog.Warning("MultiplayerPrivateLobbyMenu: code response omitted its source server; using the active relay.");
                hostEndpointIndex = MiSideMultiplayerPlugin.GetCurrentCentralEndpointIndex();
            }
            pending.RoomId = response.sessionId;
            pending.Stage = PendingJoinStage.ConnectingToRoom;
            pending.StageStartedAt = Time.unscaledTime;

            string serverName = hostEndpointIndex >= 0
                ? MiSideMultiplayerPlugin.GetCentralEndpointName(hostEndpointIndex)
                : "unknown server";
            DiagnosticLog.Info("MultiplayerPrivateLobbyMenu: code resolved to private room '" + pending.RoomId +
                               "' on " + serverName +
                               "; reconnecting to that room before joining.");
            if (!MiSideMultiplayerPlugin.ConnectToCentralAt(
                    hostEndpointIndex, pending.RoomId, false))
            {
                _pendingJoin = null;
                MultiplayerConnectionErrorHandler.HandleRelayFailure(
                    RelayFailureKind.ConnectionError, false);
            }
        }

        private static int FindUntestedEndpoint(PendingJoin pending)
        {
            if (pending == null) return -1;
            for (int i = 0; i < MiSideMultiplayerPlugin.GetCentralEndpointCount(); i++)
                if (!pending.TriedEndpointIndices.Contains(i)) return i;
            return -1;
        }

        private static void FinishPrivateCodeNotFound(PendingJoin pending)
        {
            if (!ReferenceEquals(_pendingJoin, pending)) return;
            _pendingJoin = null;
            MultiplayerConnectingOverlay.Hide();
            DiagnosticLog.Warning("MultiplayerPrivateLobbyMenu: no active private lobby matches code '" +
                                  pending.Code + "'.");
            MultiplayerConnectionErrorHandler.ShowPrivateLobbyNotFound();
        }

        public static void DrawGui()
        {
            if (_editingKey == null) return;

            Event e = Event.current;
            if (e == null || e.type != EventType.KeyDown) return;

            if (e.keyCode == KeyCode.Escape || e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
            {
                CommitEdit();
                e.Use();
                return;
            }

            string value = _editingValue ?? "";

            if (e.keyCode == KeyCode.Backspace)
            {
                if (_editingCursor > 0) { value = value.Remove(_editingCursor - 1, 1); _editingCursor--; }
            }
            else if (e.keyCode == KeyCode.Delete)
            {
                if (_editingCursor < value.Length) value = value.Remove(_editingCursor, 1);
            }
            else if (e.keyCode == KeyCode.LeftArrow)
            {
                _editingCursor = Mathf.Max(0, _editingCursor - 1);
            }
            else if (e.keyCode == KeyCode.RightArrow)
            {
                _editingCursor = Mathf.Min(value.Length, _editingCursor + 1);
            }
            else if (e.character != '\0'
                     && !char.IsControl(e.character)
                     && value.Length < SessionDirectory.CodeLength
                     && SessionDirectory.IsValidCodeChar(e.character))
            {
                value = value.Insert(_editingCursor, char.ToUpperInvariant(e.character).ToString());
                _editingCursor++;
            }
            else
            {
                return;
            }

            _editingValue = value;
            e.Use();
        }

        private static void RefreshLabels()
        {
            if (_rowCode == null) return;

            if (_editingKey == "code")
                _rowCode.Text = "> CODE: " + Cursorize(_editingValue, _editingCursor);
            else
                _rowCode.Text = "CODE: " + (string.IsNullOrEmpty(_code) ? "--" : _code);
        }

        private static string Cursorize(string value, int cursor)
        {
            value = value ?? "";
            cursor = Mathf.Clamp(cursor, 0, value.Length);
            int blink = Mathf.FloorToInt(Time.unscaledTime * 2f) % 2;
            string caret = blink == 0 ? "_" : " ";
            return value.Insert(cursor, caret);
        }

        private static void BeginEdit(string key, string initial)
        {
            if (_editingKey != null && _editingKey != key) CommitEdit();

            string v = initial ?? "";
            if (key == "code" && v.Length > SessionDirectory.CodeLength)
                v = v.Substring(0, SessionDirectory.CodeLength);

            _editingKey    = key;
            _editingValue  = v;
            _editingCursor = _editingValue.Length;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
            RefreshLabels();
        }

        private static void CommitEdit()
        {
            if (_editingKey == null) return;

            if (_editingKey == "code")
            {
                string v = (_editingValue ?? "").ToUpperInvariant().Trim();
                if (v.Length > SessionDirectory.CodeLength)
                    v = v.Substring(0, SessionDirectory.CodeLength);
                _code = v;
            }

            _editingKey    = null;
            _editingValue  = null;
            _editingCursor = 0;

            RefreshLabels();
        }

        private static void OnCodeClicked()
        {
            if (_editingKey == "code") { CommitEdit(); return; }
            BeginEdit("code", _code);
        }

        private static void OnJoin()
        {
            if (_editingKey != null) CommitEdit();

            if (string.IsNullOrEmpty(_code))
            {
                DiagnosticLog.Warning("MultiplayerPrivateLobbyMenu: JOIN — no code entered.");
                return;
            }

            if (_lobbyMenu == null)
            {
                DiagnosticLog.Warning("MultiplayerPrivateLobbyMenu: no lobby target.");
                return;
            }

            MultiplayerConnectingOverlay.Show("CONNECTING", "PLEASE WAIT");

            // Resolve the code first. Resolution is a request to the directory
            // and needs a live transport, so connect to the central relay first.
            // After we know the room id, we'll re-open the transport with that
            // room and then attach the session.
            if (!MiSideMultiplayerPlugin.ConnectToCentral("default"))
            {
                DiagnosticLog.Warning("MultiplayerPrivateLobbyMenu: ConnectTo failed.");
                MultiplayerConnectionErrorHandler.HandleRelayFailure(
                    RelayFailureKind.ConnectionError, false);
                return;
            }

            _pendingJoin = new PendingJoin
            {
                Code = _code.Trim().ToUpperInvariant(),
                Stage = PendingJoinStage.ConnectingToDirectory,
                StageStartedAt = Time.unscaledTime
            };
        }
    }
}
