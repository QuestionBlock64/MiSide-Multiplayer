using MiSideMultiplayer.API;
using MiSideMultiplayer.API.Factories;
using MiSideMultiplayer.Runtime.Lobby;
using MiSideMultiplayer.Runtime.UI;
using UnityEngine;

namespace MiSideMultiplayer.Runtime.Menu
{
    internal static class MultiplayerDirectConnectMenu
    {
        public const string PageName  = "Location Multiplayer Direct Connect";
        public const string PageTitle = "DIRECT CONNECT";

        private const float BackY = -650f;

        private static GameMenu _menu;
        private static GameMenu _lobbyMenu;
        private static MenuOption _rowIp;
        private static MenuOption _rowPort;

        private static string _ip       = "";
        private static string _portText = "24805";

        private static string _editingKey;
        private static string _editingValue;
        private static int    _editingCursor;

        private sealed class PendingConnect
        {
            public string Host;
            public int    Port;
            public float  StartedAt;
        }

        private static PendingConnect _pendingConnect;

        public static void AbortPendingConnection()
        {
            _pendingConnect = null;
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

            _rowIp   = BuildRow(menu, "IP: " + _ip, OnIpClicked);
            _rowPort = BuildRow(menu, "PORT: " + _portText, OnPortClicked);

            // CONNECT: navigate to lobby on click.
            MenuOption connect = new MenuOptionFactory()
                .SetName("CONNECT")
                .SetParent(menu)
                .SetNextLocation(_lobbyMenu != null ? _lobbyMenu : menu)
                .SetOnClick(OnConnect)
                .Build();

            if (_lobbyMenu == null && connect != null && connect.OptionObject != null)
                Neutralize(connect);

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
            DiagnosticLog.Info("MultiplayerDirectConnectMenu: built '" + PageName + "'.");
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
                DiagnosticLog.Warning("MultiplayerDirectConnectMenu: row '" + label + "' failed: " + ex.Message);
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

            if (_pendingConnect != null)
            {
                if (MiSideMultiplayerPlugin.IsRelayReady())
                {
                    var p = _pendingConnect;
                    _pendingConnect = null;
                    DiagnosticLog.Info("MultiplayerDirectConnectMenu: relay handshake complete at " +
                                       p.Host + ":" + p.Port + "; joining room 'default'.");
                    MultiplayerConnectingOverlay.Hide();
                    MultiplayerLobbyMenu.ConfigureAsClient("default", SessionVisibility.DirectConnect);
                }
                else if (Time.unscaledTime - _pendingConnect.StartedAt > 15f)
                {
                    DiagnosticLog.Warning("MultiplayerDirectConnectMenu: transport never came up; giving up.");
                    MultiplayerConnectionErrorHandler.HandleRelayFailure(
                        RelayFailureKind.TimedOut, true);
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
            else if (e.character != '\0' && !char.IsControl(e.character) && value.Length < 64 &&
                     ((_editingKey == "ip" && (char.IsLetterOrDigit(e.character) || e.character == '.' || e.character == ':')) ||
                      (_editingKey == "port" && char.IsDigit(e.character) && value.Length < 5)))
            {
                value = value.Insert(_editingCursor, e.character.ToString());
                _editingCursor++;
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
            if (_rowIp != null)
            {
                if (_editingKey == "ip")
                    _rowIp.Text = "> IP: " + Cursorize(_editingValue, _editingCursor);
                else
                    _rowIp.Text = "IP: " + _ip;
            }

            if (_rowPort != null)
            {
                if (_editingKey == "port")
                    _rowPort.Text = "> PORT: " + Cursorize(_editingValue, _editingCursor);
                else
                    _rowPort.Text = "PORT: " + _portText;
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
            _editingKey    = key;
            _editingValue  = initial ?? "";
            _editingCursor = _editingValue.Length;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
            RefreshLabels();
        }

        private static void CommitEdit()
        {
            if (_editingKey == null) return;

            if (_editingKey == "ip")
            {
                string v = (_editingValue ?? "").Trim();
                System.Net.IPAddress parsed;
                if (System.Net.IPAddress.TryParse(v, out parsed)) _ip = v;
                else DiagnosticLog.Warning("MultiplayerDirectConnectMenu: invalid IP address '" + v + "'.");
            }
            else if (_editingKey == "port")
            {
                string v = (_editingValue ?? "").Trim();
                if (int.TryParse(v, out int p) && p > 0 && p < 65536)
                    _portText = p.ToString();
            }

            _editingKey    = null;
            _editingValue  = null;
            _editingCursor = 0;

            RefreshLabels();
        }

        // ── Row click handlers ──────────────────────────────────────────────

        private static void OnIpClicked()
        {
            if (_editingKey == "ip") { CommitEdit(); return; }
            BeginEdit("ip", _ip);
        }

        private static void OnPortClicked()
        {
            if (_editingKey == "port") { CommitEdit(); return; }
            BeginEdit("port", _portText);
        }

        // ── Connect ─────────────────────────────────────────────────────────

        private static void OnConnect()
        {
            if (_editingKey != null) CommitEdit();

            int port;
            if (!int.TryParse(_portText, out port) || port <= 0 || port > 65535)
            {
                DiagnosticLog.Warning("MultiplayerDirectConnectMenu: invalid port '" + _portText + "'.");
                MultiplayerConnectionErrorHandler.ShowInvalidDirectEndpoint(
                    "Enter a port between 1 and 65535.");
                return;
            }

            System.Net.IPAddress parsedAddress;
            if (!System.Net.IPAddress.TryParse(_ip, out parsedAddress))
            {
                DiagnosticLog.Warning("MultiplayerDirectConnectMenu: invalid IP address '" + _ip + "'.");
                MultiplayerConnectionErrorHandler.ShowInvalidDirectEndpoint("Enter a valid IP address.");
                return;
            }

            DiagnosticLog.Info("MultiplayerDirectConnectMenu: CONNECT → " + _ip + ":" + port);

            MultiplayerConnectingOverlay.Show("CONNECTING", "PLEASE WAIT");

            if (!MiSideMultiplayerPlugin.ConnectTo(_ip, port, "default"))
            {
                DiagnosticLog.Warning("MultiplayerDirectConnectMenu: connect request rejected.");
                MultiplayerConnectionErrorHandler.HandleRelayFailure(
                    RelayFailureKind.ConnectionError, true);
                return;
            }

            _pendingConnect = new PendingConnect
            {
                Host      = _ip,
                Port      = port,
                StartedAt = Time.unscaledTime
            };
        }
    }
}
