using MiSideMultiplayer.API;
using MiSideMultiplayer.API.Factories;
using MiSideMultiplayer.Runtime.Lobby;
using MiSideMultiplayer.Runtime.UI;
using UnityEngine;

namespace MiSideMultiplayer.Runtime.Menu
{
    internal static class MultiplayerHostSetupMenu
    {
        public const string PageName  = "Location Multiplayer Host Setup";
        public const string PageTitle = "HOST GAME";

        private const float BackY = -650f;

        private static GameMenu _menu;
        private static GameMenu _lobbyMenu;

        private static MenuOption _rowMaxPlayers;
        private static MenuOption _rowVisibility;
        private static MenuOption _rowSessionName;
        private static MenuOption _rowRandomName;
        private static MenuOption _rowCode;
        private static MenuOption _rowCustomDirectEndpoint;
        private static MenuOption _rowDirectIp;
        private static MenuOption _rowDirectPort;

        private static int               _maxPlayers  = 4;
        private static SessionVisibility _visibility  = SessionVisibility.Public;
        private static string            _sessionName = "";
        private static string            _code        = "";
        private static bool              _useCustomDirectEndpoint;
        private static string            _directIp       = "127.0.0.1";
        private static string            _directPortText = "24805";

        private static string _editingKey;
        private static string _editingValue;
        private static int    _editingCursor;

        // Pending host registration. Set when START HOSTING is clicked;
        // cleared once the transport is up and we've sent the register message.
        private static PendingHostSetup _pendingHostSetup;
        private static readonly float CentralConnectTimeoutSeconds =
            QBRelayFailover.HandshakeTimeoutSeconds * QBRelayFailover.EndpointCount + 8f;

        public static void AbortPendingConnection()
        {
            _pendingHostSetup = null;
        }

        private sealed class PendingHostSetup
        {
            public string RoomId;
            public string Host;
            public int Port;
            public string SessionName;
            public SessionVisibility Visibility;
            public int MaxPlayers;
            public string Code;
            public float StartedAt;
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

            if (string.IsNullOrEmpty(_sessionName))
            {
                string dn = MiSideMultiplayerPlugin.GetDisplayName();
                _sessionName = (string.IsNullOrEmpty(dn) ? "Player" : dn) + "'s Game";
            }

            _rowMaxPlayers  = BuildRow(menu, "MAX PLAYERS: " + _maxPlayers, OnMaxPlayers);
            _rowVisibility  = BuildRow(menu, "SESSION TYPE: " + _visibility.DisplayName(), OnVisibility);
            _rowSessionName = BuildRow(menu, "NAME: " + _sessionName, OnSessionNameClicked);
            _rowRandomName  = BuildRow(menu, "RANDOM NAME", OnRandomName);
            _rowCode        = BuildRow(menu, "", OnCodeClicked);
            _rowCustomDirectEndpoint = BuildRow(menu, "CUSTOM SERVER ADDRESS: OFF", OnCustomDirectEndpoint);
            _rowDirectIp    = BuildRow(menu, "SERVER IP: " + _directIp, OnDirectIpClicked);
            _rowDirectPort  = BuildRow(menu, "SERVER PORT: " + _directPortText, OnDirectPortClicked);

            // START HOSTING: navigate to the lobby.
            MenuOption start = new MenuOptionFactory()
                .SetName("START HOSTING")
                .SetParent(menu)
                .SetNextLocation(_lobbyMenu != null ? _lobbyMenu : menu)
                .SetOnClick(() => OnStartHosting(menu))
                .Build();

            if (_lobbyMenu == null)
                Neutralize(start);

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
            DiagnosticLog.Info("MultiplayerHostSetupMenu: built '" + PageName + "'.");
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
                DiagnosticLog.Warning("MultiplayerHostSetupMenu: row '" + label + "' failed: " + ex.Message);
                return null;
            }
        }

        private static void Neutralize(MenuOption opt)
        {
            if (opt == null || opt.OptionObject == null) return;
            MenuNextLocation next = opt.OptionObject.GetComponent<MenuNextLocation>();
            if (next != null) UnityEngine.Object.Destroy(next);
        }

        // ── Tick / Gui ──────────────────────────────────────────────────────

        public static void Tick()
        {
            if (_menu == null) return;
            if (_editingKey != null) RefreshLabels();

            if (_pendingHostSetup != null)
            {
                bool relayReady = MiSideMultiplayerPlugin.IsRelayReady();
                if (relayReady)
                {
                    var p = _pendingHostSetup;
                    _pendingHostSetup = null;

                    DiagnosticLog.Info("MultiplayerHostSetupMenu: relay ready at " + p.Host + ":" + p.Port +
                                       ", registering " + p.Visibility + " host in room '" + p.RoomId + "'.");

                    SessionDirectory.RegisterHostDetailed(
                        p.RoomId,
                        p.SessionName,
                        p.Visibility,
                        p.MaxPlayers,
                        p.Code,
                        (ok, reason) =>
                        {
                            if (!ok)
                            {
                                DiagnosticLog.Warning("MultiplayerHostSetupMenu: registration rejected: " + reason);
                                MultiplayerConnectionErrorHandler.ShowRegistrationRejected(
                                    reason, p.Visibility == SessionVisibility.DirectConnect);
                                return;
                            }
                            MultiplayerConnectingOverlay.Hide();
                            MultiplayerLobbyMenu.ConfigureAsHost(
                                p.RoomId, p.SessionName, p.Visibility, p.MaxPlayers, p.Code);
                        });
                }
                else if (Time.unscaledTime - _pendingHostSetup.StartedAt > CentralConnectTimeoutSeconds)
                {
                    PendingHostSetup failed = _pendingHostSetup;
                    _pendingHostSetup = null;
                    DiagnosticLog.Warning("MultiplayerHostSetupMenu: relay did not become ready before the " +
                                          "connection window expired.");
                    if (failed.Visibility == SessionVisibility.DirectConnect)
                        MultiplayerConnectionErrorHandler.HandleRelayFailure(
                            RelayFailureKind.TimedOut, true);
                    else
                        MultiplayerConnectingOverlay.Hide();
                }
            }
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
                if (_editingCursor > 0)
                {
                    value = value.Remove(_editingCursor - 1, 1);
                    _editingCursor--;
                }
            }
            else if (e.keyCode == KeyCode.Delete)
            {
                if (_editingCursor < value.Length)
                    value = value.Remove(_editingCursor, 1);
            }
            else if (e.keyCode == KeyCode.LeftArrow)
            {
                _editingCursor = Mathf.Max(0, _editingCursor - 1);
            }
            else if (e.keyCode == KeyCode.RightArrow)
            {
                _editingCursor = Mathf.Min(value.Length, _editingCursor + 1);
            }
            else if (e.character != '\0' && !char.IsControl(e.character))
            {
                if (_editingKey == "sessionName")
                {
                    if (value.Length < 32)
                    {
                        value = value.Insert(_editingCursor, e.character.ToString());
                        _editingCursor++;
                    }
                }
                else if (_editingKey == "directIp" && value.Length < 64 &&
                         (char.IsLetterOrDigit(e.character) || e.character == '.' || e.character == ':' || e.character == '-'))
                {
                    value = value.Insert(_editingCursor, e.character.ToString());
                    _editingCursor++;
                }
                else if (_editingKey == "directPort" && char.IsDigit(e.character) && value.Length < 5)
                {
                    value = value.Insert(_editingCursor, e.character.ToString());
                    _editingCursor++;
                }
                else if (_editingKey == "code")
                {
                    if (value.Length < SessionDirectory.CodeLength
                        && SessionDirectory.IsValidCodeChar(e.character))
                    {
                        value = value.Insert(_editingCursor, char.ToUpperInvariant(e.character).ToString());
                        _editingCursor++;
                    }
                }
            }
            else
            {
                return;
            }

            _editingValue = value;
            e.Use();
        }

        // ── Refresh ─────────────────────────────────────────────────────────

        private static void RefreshLabels()
        {
            if (_rowMaxPlayers != null) _rowMaxPlayers.Text = "MAX PLAYERS: " + _maxPlayers;
            if (_rowVisibility != null) _rowVisibility.Text = "SESSION TYPE: " + _visibility.DisplayName();

            if (_rowSessionName != null)
            {
                if (_editingKey == "sessionName")
                    _rowSessionName.Text = "> NAME: " + Cursorize(_editingValue, _editingCursor);
                else
                    _rowSessionName.Text = "NAME: " + _sessionName;
            }

            if (_rowCode != null)
            {
                bool privateSession = _visibility == SessionVisibility.Private;
                if (_rowCode.OptionObject != null) _rowCode.OptionObject.SetActive(privateSession);
                if (privateSession)
                {
                    if (string.IsNullOrEmpty(_code))
                        _code = SessionDirectory.GenerateCode();

                    if (_editingKey == "code")
                        _rowCode.Text = "> CODE: " + Cursorize(_editingValue, _editingCursor);
                    else
                        _rowCode.Text = "CODE: " + _code;
                }
                else
                {
                    _code = "";
                    _rowCode.Text = "";
                }
            }

            bool directSession = _visibility == SessionVisibility.DirectConnect;
            if (_rowCustomDirectEndpoint != null)
            {
                if (_rowCustomDirectEndpoint.OptionObject != null)
                    _rowCustomDirectEndpoint.OptionObject.SetActive(directSession);
                _rowCustomDirectEndpoint.Text = "CUSTOM SERVER ADDRESS: " +
                    (_useCustomDirectEndpoint ? "ON" : "OFF");
            }
            if (_rowDirectIp != null)
            {
                if (_rowDirectIp.OptionObject != null)
                    _rowDirectIp.OptionObject.SetActive(directSession && _useCustomDirectEndpoint);
                _rowDirectIp.Text = _editingKey == "directIp"
                    ? "> SERVER IP: " + Cursorize(_editingValue, _editingCursor)
                    : "SERVER IP: " + _directIp;
            }
            if (_rowDirectPort != null)
            {
                if (_rowDirectPort.OptionObject != null)
                    _rowDirectPort.OptionObject.SetActive(directSession && _useCustomDirectEndpoint);
                _rowDirectPort.Text = _editingKey == "directPort"
                    ? "> SERVER PORT: " + Cursorize(_editingValue, _editingCursor)
                    : "SERVER PORT: " + _directPortText;
            }
        }

        private static string Cursorize(string value, int cursor)
        {
            value = value ?? "";
            cursor = Mathf.Clamp(cursor, 0, value.Length);
            int blink = Mathf.FloorToInt(Time.unscaledTime * 2f) % 2;
            string caret = blink == 0 ? "_" : " ";
            return value.Insert(cursor, caret);
        }

        // ── Editing ─────────────────────────────────────────────────────────

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

            if (_editingKey == "sessionName")
            {
                string v = _editingValue ?? "";
                if (!string.IsNullOrWhiteSpace(v)) _sessionName = v;
            }
            else if (_editingKey == "code")
            {
                string v = (_editingValue ?? "").ToUpperInvariant().Trim();
                if (v.Length > SessionDirectory.CodeLength)
                    v = v.Substring(0, SessionDirectory.CodeLength);
                if (!string.IsNullOrWhiteSpace(v)) _code = v;
            }
            else if (_editingKey == "directIp")
            {
                string v = (_editingValue ?? "").Trim();
                System.Net.IPAddress parsed;
                if (System.Net.IPAddress.TryParse(v, out parsed)) _directIp = v;
                else DiagnosticLog.Warning("MultiplayerHostSetupMenu: invalid Direct Connect IP '" + v + "'.");
            }
            else if (_editingKey == "directPort")
            {
                if (int.TryParse(_editingValue, out int p) && p > 0 && p < 65536)
                    _directPortText = p.ToString();
                else
                    DiagnosticLog.Warning("MultiplayerHostSetupMenu: invalid Direct Connect port '" +
                                          (_editingValue ?? "") + "'.");
            }

            _editingKey    = null;
            _editingValue  = null;
            _editingCursor = 0;

            RefreshLabels();
        }

        // ── Row click handlers ──────────────────────────────────────────────

        private static void OnMaxPlayers()
        {
            if (_editingKey != null) CommitEdit();
            _maxPlayers++;
            if (_maxPlayers > 4) _maxPlayers = 2;
            RefreshLabels();
        }

        private static void OnVisibility()
        {
            if (_editingKey != null) CommitEdit();
            int next = ((int)_visibility + 1) % 3;
            _visibility = (SessionVisibility)next;
            RefreshLabels();
        }

        private static void OnSessionNameClicked()
        {
            if (_editingKey == "sessionName") { CommitEdit(); return; }
            BeginEdit("sessionName", _sessionName);
        }

        private static void OnRandomName()
        {
            if (_editingKey != null) CommitEdit();
            _sessionName = GenerateRandomName();
            RefreshLabels();
        }

        private static void OnCodeClicked()
        {
            if (_visibility != SessionVisibility.Private) return;
            if (_editingKey == "code") { CommitEdit(); return; }
            BeginEdit("code", _code);
        }

        private static void OnCustomDirectEndpoint()
        {
            if (_editingKey != null) CommitEdit();
            _useCustomDirectEndpoint = !_useCustomDirectEndpoint;
            RefreshLabels();
        }

        private static void OnDirectIpClicked()
        {
            if (_editingKey == "directIp") { CommitEdit(); return; }
            BeginEdit("directIp", _directIp);
        }

        private static void OnDirectPortClicked()
        {
            if (_editingKey == "directPort") { CommitEdit(); return; }
            BeginEdit("directPort", _directPortText);
        }

        // ── Random name generator ───────────────────────────────────────────

        private static readonly string[] Adjectives =
        {
            "Purple", "Silent", "Cosmic", "Pale", "Neon", "Frozen", "Hollow", "Wild",
            "Broken", "Distant", "Crimson", "Lonely", "Radiant", "Ghostly", "Bitter"
        };

        private static readonly string[] Nouns =
        {
            "Aihasto", "Signal", "Echo", "Harbor", "Vault", "Mirror", "Garden", "Circuit",
            "Rain", "Static", "Beacon", "Cabin", "Relay", "Dream", "Aperture"
        };

        private static string GenerateRandomName()
        {
            var rng = new System.Random();
            string adj  = Adjectives[rng.Next(Adjectives.Length)];
            string noun = Nouns[rng.Next(Nouns.Length)];
            return adj + " " + noun;
        }

        // ── Start hosting ───────────────────────────────────────────────────

        private static void OnStartHosting(GameMenu menu)
        {
            if (_pendingHostSetup != null)
            {
                DiagnosticLog.Warning("MultiplayerHostSetupMenu: host connection is already pending.");
                return;
            }
            if (_editingKey != null) CommitEdit();

            bool directConnect = _visibility == SessionVisibility.DirectConnect;
            string roomId = directConnect ? "default" : SessionDirectory.GenerateRoomId();
            string host = MiSideMultiplayerPlugin.GetRelayHost();
            int port = MiSideMultiplayerPlugin.GetRelayPort();
            if (directConnect)
            {
                host = _useCustomDirectEndpoint ? _directIp : "127.0.0.1";
                if (_useCustomDirectEndpoint)
                {
                    System.Net.IPAddress parsedAddress;
                    if (!System.Net.IPAddress.TryParse(_directIp, out parsedAddress))
                    {
                        DiagnosticLog.Warning("MultiplayerHostSetupMenu: invalid custom Direct Connect IP '" +
                                              _directIp + "'.");
                        MultiplayerConnectionErrorHandler.ShowInvalidDirectEndpoint(
                            "Enter the Direct Connect relay's valid IP address.");
                        return;
                    }
                    if (!int.TryParse(_directPortText, out port) || port <= 0 || port > 65535)
                    {
                        DiagnosticLog.Warning("MultiplayerHostSetupMenu: invalid custom Direct Connect port.");
                        MultiplayerConnectionErrorHandler.ShowInvalidDirectEndpoint(
                            "Enter a port between 1 and 65535.");
                        return;
                    }
                }
            }

            DiagnosticLog.Info("MultiplayerHostSetupMenu: START HOSTING — endpoint=" + host + ":" + port +
                               " room=" + roomId +
                               " type=" + _visibility +
                               " name='" + _sessionName + "' max=" + _maxPlayers +
                               (_visibility == SessionVisibility.Private ? " code='" + _code + "'" : ""));

            MultiplayerConnectingOverlay.Show("CONNECTING", "PLEASE WAIT");

            bool connected = directConnect
                ? MiSideMultiplayerPlugin.ConnectTo(host, port, roomId)
                : MiSideMultiplayerPlugin.ConnectToCentral(roomId);
            if (!connected)
            {
                DiagnosticLog.Warning("MultiplayerHostSetupMenu: ConnectTo failed.");
                MultiplayerConnectionErrorHandler.HandleRelayFailure(
                    RelayFailureKind.ConnectionError, directConnect);
                return;
            }

            _pendingHostSetup = new PendingHostSetup
            {
                RoomId      = roomId,
                Host        = host,
                Port        = port,
                SessionName = _sessionName,
                Visibility  = _visibility,
                MaxPlayers  = _maxPlayers,
                Code        = _code,
                StartedAt   = Time.unscaledTime
            };
        }
    }
}
