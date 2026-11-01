using System;
using System.Collections.Generic;
using MiSideMultiplayer.API;
using MiSideMultiplayer.API.Factories;
using MiSideMultiplayer.Runtime.Loading;
using MiSideMultiplayer.Runtime.Lobby;
using MiSideMultiplayer.Runtime.Menu.UI;
using UnityEngine;
using UnityEngine.UI;

namespace MiSideMultiplayer.Runtime.Menu
{
    internal static class MultiplayerLobbyMenu
    {
        public const string PageName  = "Location Multiplayer Lobby";
        public const string PageTitle = "LOBBY";

        private const float BackY     = -650f;
        private const string GreenHex = "#00FF66";
        private const string RedHex   = "#FF3355";
        private const float PlayerStatusFadeDuration = 0.32f;

        private const int MaxPlayerRows = 4;

        private static GameMenu          _menu;
        private static GameMenu          _exitTarget;
        private static SessionController _session;
        private static bool              _lobbyMenuVisible;

        private static readonly MenuOption[] _playerRows = new MenuOption[MaxPlayerRows];
        private static readonly PlayerRowStatus[] _playerRowStatuses = new PlayerRowStatus[MaxPlayerRows];

        private sealed class PlayerRowStatus
        {
            public Text RowText;
            public CanvasGroupFader Fader;
        }

        private static MenuOption _rowScene;
        private static MenuOption _rowPrivateCode;
        private static MenuOption _rowAction;
        private static MenuOption _rowExit;
        private static string _privateCode = string.Empty;

        private static bool _exitConfirmArmed;
        private static bool _handlingHostLeft;

        private static bool _sessionStartHandled;
        private static RpcDispatcher _hostLeftDispatcher;
        private static string _roomId;
        private static bool _hostDepartureNotified;

        public static SessionController Session { get { return _session; } }

        public static void AbortForConnectionFailure()
        {
            ReleaseSessionOnly();
        }

        public static GameMenu Build(GameMenu exitTarget)
        {
            if (MenuManager.Instance == null)
            {
                DiagnosticLog.Warning("MultiplayerLobbyMenu: MenuManager not initialized.");
                return null;
            }

            try { MultiplayerErrorWindowMenu.Build(); }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerLobbyMenu: error window build failed: " + ex.Message);
            }

            GameMenu existing = MenuManager.Instance.Find(PageName);
            if (existing != null)
            {
                _menu = existing;
                _exitTarget = exitTarget;
                EnsureScenePickerBuilt();
                return existing;
            }

            GameMenu menu = new MenuFactory()
                .SetObjectName(PageName)
                .SetTitle(PageTitle)
                .Build();
            if (menu == null)
            {
                DiagnosticLog.Warning("MultiplayerLobbyMenu: MenuFactory.Build returned null.");
                return null;
            }
            _menu = menu;
            _exitTarget = exitTarget;

            for (int i = 0; i < MaxPlayerRows; i++)
            {
                _playerRows[i] = BuildRow(menu, "", OnHeaderClicked);
                InitializePlayerRowFader(i, _playerRows[i]);
            }

            _rowPrivateCode = BuildRow(menu, "", OnPrivateCodeClicked);
            _rowScene  = BuildRow(menu, "SCENE: " + MultiplayerScenePickerMenu.DefaultSceneDisplay, OnSceneClicked, keepNavigation: true);
            _rowAction = BuildRow(menu, "READY UP!", OnActionClicked);
            _rowExit   = BuildRow(menu, "LEAVE LOBBY", OnExitClicked);

            EnsureScenePickerBuilt();

            DiagnosticLog.Info("MultiplayerLobbyMenu: built '" + PageName + "'.");
            return menu;
        }

        public static void OnLobbyMenuVisibilityChanged(bool visible)
        {
            _lobbyMenuVisible = visible;
            for (int i = 0; i < MaxPlayerRows; i++)
            {
                PlayerRowStatus status = _playerRowStatuses[i];
                if (status == null || status.Fader == null) continue;
                try { status.Fader.SetVisible(visible); } catch { }
            }
        }

        public static void OnMenuNavigationStarted(GameObject destination)
        {
            bool enteringLobby = destination != null &&
                (destination.name == PageName || destination.name.StartsWith(PageName + "(", StringComparison.Ordinal));
            OnLobbyMenuVisibilityChanged(enteringLobby);
        }

        private static void InitializePlayerRowFader(int index, MenuOption row)
        {
            if (index < 0 || index >= MaxPlayerRows || row == null) return;
            try
            {
                GameObject rowObject = row.OptionObject;
                if (rowObject == null) return;

                Text text = rowObject.GetComponentInChildren<Text>(true);
                if (text == null) return;

                CanvasGroup group = text.GetComponent<CanvasGroup>();
                if (group == null) group = text.gameObject.AddComponent<CanvasGroup>();
                group.alpha = _lobbyMenuVisible ? 1f : 0f;

                CanvasGroupFader fader = text.GetComponent<CanvasGroupFader>();
                if (fader == null) fader = text.gameObject.AddComponent<CanvasGroupFader>();
                fader.FadeDuration = PlayerStatusFadeDuration;
                fader.Initialize(group, _lobbyMenuVisible);

                _playerRowStatuses[index] = new PlayerRowStatus
                {
                    RowText = text,
                    Fader = fader
                };
            }
            catch
            {
                // Rows may be destroyed during a menu scene transition. The
                // normal text/button behavior remains available without fade.
            }
        }

        private static void EnsureScenePickerBuilt()
        {
            try { MultiplayerScenePickerMenu.Build(_menu); }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerLobbyMenu: scene picker build failed: " + ex.Message);
            }
        }

        private static MenuOption BuildRow(GameMenu parent, string label, System.Action onClick, bool keepNavigation = false)
        {
            try
            {
                MenuOption opt = new MenuOptionFactory()
                    .SetName(label)
                    .SetParent(parent)
                    .SetNextLocation(parent)
                    .SetOnClick(onClick)
                    .Build();
                Neutralize(opt, keepNavigation);
                return opt;
            }
            catch (System.Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerLobbyMenu: row '" + label + "' failed: " + ex.Message);
                return null;
            }
        }

        private static void Neutralize(MenuOption opt, bool keepNavigation)
        {
            if (opt == null || opt.OptionObject == null) return;
            if (!keepNavigation)
            {
                // Match the backup behavior: action rows do not navigate.
                // Removing the component also avoids PointerDown invoking a
                // disabled MenuNextLocation with a null destination.
                MenuNextLocation next = opt.OptionObject.GetComponent<MenuNextLocation>();
                if (next != null) UnityEngine.Object.Destroy(next);
            }

            try
            {
                Text t = opt.OptionObject.GetComponentInChildren<Text>();
                if (t != null) t.supportRichText = true;
            }
            catch { }
        }

        public static void ConfigureAsHost(string roomId, string sessionName, SessionVisibility vis, int maxPlayers, string code)
        {
            var dispatcher = MiSideMultiplayerPlugin.GetRpcDispatcher();
            if (dispatcher == null)
            {
                DiagnosticLog.Warning("MultiplayerLobbyMenu: no dispatcher.");
                return;
            }

            var session = new SessionController(
                dispatcher,
                MiSideMultiplayerPlugin.GetLocalPlayerId(),
                MiSideMultiplayerPlugin.GetDisplayName(),
                MiSideMultiplayerPlugin.PluginVersion,
                new[] { "SceneAihasto" },
                vis, sessionName, code, maxPlayers,
                isLocalHost: true);

            _privateCode = vis == SessionVisibility.Private
                ? (code ?? string.Empty).Trim().ToUpperInvariant()
                : string.Empty;

            try { session.SelectScene(MultiplayerScenePickerMenu.DefaultSceneInternal); } catch { }

            AttachSession(session);
            _roomId = roomId;
            _hostDepartureNotified = false;
            Refresh();
        }

        public static void ConfigureAsClient(string roomId, SessionVisibility vis)
        {
            _privateCode = string.Empty;
            var dispatcher = MiSideMultiplayerPlugin.GetRpcDispatcher();
            if (dispatcher == null)
            {
                DiagnosticLog.Warning("MultiplayerLobbyMenu: no dispatcher.");
                return;
            }

            AttachSession(new SessionController(
                dispatcher,
                MiSideMultiplayerPlugin.GetLocalPlayerId(),
                MiSideMultiplayerPlugin.GetDisplayName(),
                MiSideMultiplayerPlugin.PluginVersion,
                new[] { "SceneAihasto" },
                vis, "Remote Session", "", 4,
                isLocalHost: false));

            _roomId = roomId;
            _hostDepartureNotified = false;
            SessionDirectory.SendJoin(roomId);
        }

        /// <summary>
        /// Announces a deliberate host departure before MiSide begins its
        /// SceneMenu fade. The server can therefore eject every joiner at the
        /// moment the host leaves instead of waiting for the post-fade socket
        /// close to be detected.
        /// </summary>
        public static void NotifyLocalHostLeaving()
        {
            if (_hostDepartureNotified || _session == null || !_session.IsLocalHost ||
                string.IsNullOrEmpty(_roomId))
                return;

            LoadingStageManager.AbortForSessionExit();
            _hostDepartureNotified = true;
            DiagnosticLog.Info("MultiplayerLobbyMenu: host departure announced for room '" +
                               _roomId + "' before menu transition.");
            try { SessionDirectory.UnregisterHost(_roomId); }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerLobbyMenu: host unregister send failed: " + ex.Message);
            }
        }

        public static void AttachSession(SessionController session)
        {
            ReleaseSessionOnly();

            _sessionStartHandled = false;

            _session = session;
            if (_session != null)
            {
                SubscribeHostLeftFallback();
                _session.OnRosterChanged   += OnRosterChanged;
                _session.OnSessionStarting += OnSessionStarting;
                _session.OnSceneSelected   += OnSceneSelectedChanged;
                _session.OnHostLeft        += OnHostLeft;
                _session.BroadcastHello();
                Refresh();
            }
        }

        private static void ReleaseSessionOnly()
        {
            // Any path that ends a session also disarms the loading overlay,
            // so subsequent singleplayer transitions are untouched. Log the
            // full exception if this throws so we can see it, instead of
            // swallowing it silently.
            try { LoadingStageManager.AbortForSessionExit(); }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerLobbyMenu: multiplayer load cleanup threw:\n" +
                                      ex.ToString());
            }

            UnsubscribeHostLeftFallback();

            if (_session != null)
            {
                try
                {
                    _session.OnRosterChanged   -= OnRosterChanged;
                    _session.OnSessionStarting -= OnSessionStarting;
                    _session.OnSceneSelected   -= OnSceneSelectedChanged;
                    _session.OnHostLeft        -= OnHostLeft;
                    _session.Dispose();
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warning("MultiplayerLobbyMenu: session dispose threw:\n" +
                                          ex.ToString());
                }
                _session = null;
            }
            _exitConfirmArmed = false;
        }

        public static void Tick()
        {
            _session?.Tick();
        }

        private static void OnRosterChanged() { Refresh(); }
        private static void OnSceneSelectedChanged(string sceneName) { Refresh(); }

        private static void Refresh()
        {
            if (_menu == null) return;

            try
            {
                var playerList = new List<SessionPlayer>();
                if (_session != null)
                {
                    foreach (var kvp in _session.Players)
                    {
                        if (kvp.Value != null) playerList.Add(kvp.Value);
                    }
                }

                playerList.Sort((a, b) =>
                {
                    if (a.isHost != b.isHost) return a.isHost ? -1 : 1;
                    return string.CompareOrdinal(a.playerId ?? "", b.playerId ?? "");
                });

                for (int i = 0; i < MaxPlayerRows; i++)
                {
                    var row = _playerRows[i];
                    if (row == null) continue;

                    string label = string.Empty;
                    if (i < playerList.Count)
                    {
                        var p = playerList[i];
                        string playerName = p.displayName ?? string.Empty;
                        if (!string.IsNullOrEmpty(playerName))
                        {
                            string color = p.isReady ? GreenHex : RedHex;
                            string state = p.isReady ? "Ready" : "Not Ready";
                            label = playerName + "   <color=" + color + ">" + state + "</color>";
                        }
                    }

                    SetPlayerRowText(i, row, label);
                }

                if (_rowPrivateCode != null)
                {
                    bool showPrivateCode = _session != null && _session.IsLocalHost &&
                        _session.Visibility == SessionVisibility.Private &&
                        !string.IsNullOrWhiteSpace(_privateCode);
                    if (_rowPrivateCode.OptionObject != null)
                        _rowPrivateCode.OptionObject.SetActive(showPrivateCode);
                    if (showPrivateCode)
                        _rowPrivateCode.Text = "PRIVATE CODE: " + _privateCode + "  (CLICK TO COPY)";
                }

                if (_rowScene != null)
                {
                    string internalName = null;
                    if (_session != null) internalName = _session.SelectedScene;

                    string display = MultiplayerScenePickerMenu.GetDisplayName(
                        string.IsNullOrEmpty(internalName)
                            ? MultiplayerScenePickerMenu.DefaultSceneInternal
                            : internalName);

                    try { _rowScene.Text = "SCENE: " + display; } catch { }
                }

                if (_rowAction != null && _session != null)
                {
                    if (_session.IsLocalHost)
                    {
                        string text = _session.AllReady
                            ? "START SESSION"
                            : "WAITING FOR PLAYERS...";
                        try { _rowAction.Text = text; } catch { }
                    }
                    else
                    {
                        bool ready = false;
                        SessionPlayer self;
                        if (_session.Players.TryGetValue(_session.LocalPlayerId, out self) && self != null)
                            ready = self.isReady;

                        string text = ready ? "UNREADY" : "READY UP!";
                        try { _rowAction.Text = text; } catch { }
                    }
                }

                if (_rowExit != null)
                {
                    try { _rowExit.Text = _exitConfirmArmed ? "PRESS AGAIN TO EXIT" : "EXIT"; } catch { }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerLobbyMenu: Refresh threw:\n" + ex.ToString());
            }
        }

        private static void SetPlayerRowText(int index, MenuOption row, string label)
        {
            if (index < 0 || index >= MaxPlayerRows || row == null) return;
            try
            {
                PlayerRowStatus status = _playerRowStatuses[index];
                if (status != null && status.RowText != null)
                    status.RowText.text = label ?? string.Empty;
                else if (row.OptionObject != null)
                    row.Text = label ?? string.Empty;
            }
            catch
            {
                // During teardown the button may already be destroyed; don't
                // turn a stale roster refresh into repeated Unity exceptions.
            }
        }

        private static void OnHeaderClicked() { }

        private static void OnPrivateCodeClicked()
        {
            if (_session == null || !_session.IsLocalHost ||
                _session.Visibility != SessionVisibility.Private ||
                string.IsNullOrWhiteSpace(_privateCode))
                return;

            try
            {
                GUIUtility.systemCopyBuffer = _privateCode;
                DiagnosticLog.Info("MultiplayerLobbyMenu: private lobby code copied to clipboard.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerLobbyMenu: couldn't copy private lobby code: " + ex.Message);
            }
        }

        private static void OnSceneClicked()
        {
            _exitConfirmArmed = false;

            if (_session == null)
            {
                DiagnosticLog.Warning("MultiplayerLobbyMenu: scene row clicked with no session.");
                return;
            }

            if (!_session.IsLocalHost)
            {
                DiagnosticLog.Info("MultiplayerLobbyMenu: scene row clicked by non-host; ignored.");
                return;
            }

            GameMenu picker = MultiplayerScenePickerMenu.Menu;
            if (picker == null)
            {
                DiagnosticLog.Warning("MultiplayerLobbyMenu: scene picker not built.");
                return;
            }

            DiagnosticLog.Info("MultiplayerLobbyMenu: host opening scene picker.");

            try
            {
                // The button's native navigation runs after its action callback.
                // Retarget that route to the picker without changing the click flow.
                if (_rowScene != null && _rowScene.OptionObject != null)
                {
                    MenuNextLocation next = _rowScene.OptionObject.GetComponent<MenuNextLocation>();
                    if (next != null) next.nextLocation = picker.MenuObject;
                    else DiagnosticLog.Warning("MultiplayerLobbyMenu: scene row lost MenuNextLocation.");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerLobbyMenu: scene picker navigation failed: " + ex.Message);
            }
        }

        private static void OnActionClicked()
        {
            if (_session == null)
            {
                DiagnosticLog.Warning("MultiplayerLobbyMenu: OnActionClicked with no session.");
                return;
            }
            _exitConfirmArmed = false;

            try
            {
                if (_session.IsLocalHost)
                {
                    DiagnosticLog.Info("MultiplayerLobbyMenu: START SESSION clicked. AllReady=" +
                                       _session.AllReady + ".");
                    if (_session.AllReady) _session.StartSession();
                }
                else
                {
                    _session.ToggleReady();
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerLobbyMenu: OnActionClicked threw:\n" + ex.ToString());
            }
        }

        private static void OnExitClicked()
        {
            if (_session == null) return;

            if (!_exitConfirmArmed)
            {
                _exitConfirmArmed = true;
                if (_rowExit != null) _rowExit.Text = "PRESS AGAIN TO EXIT";
                return;
            }

            DiagnosticLog.Info("MultiplayerLobbyMenu: exit confirmed.");
            _exitConfirmArmed = false;

            NotifyLocalHostLeaving();
            ReleaseSessionOnly();
            try { MiSideMultiplayerPlugin.DisconnectRelay(); } catch { }

            NavigateToExitTarget();
        }

        private static void NavigateToExitTarget()
        {
            if (_exitTarget == null || _menu == null) return;

            try
            {
                MenuOption exit = _rowExit;
                if (exit != null && exit.OptionObject != null)
                {
                    var nl = exit.OptionObject.GetComponent<MenuNextLocation>();
                    if (nl != null)
                    {
                        nl.nextLocation = _exitTarget.MenuObject;
                        nl.enabled = true;
                        nl.Click();
                    }
                }
            }
            catch (System.Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerLobbyMenu: exit navigation failed: " + ex.Message);
            }
        }

        // ── Host-left handling ──────────────────────────────────────────────

        private static void SubscribeHostLeftFallback()
        {
            RpcDispatcher dispatcher = MiSideMultiplayerPlugin.GetRpcDispatcher();
            if (dispatcher == null || ReferenceEquals(_hostLeftDispatcher, dispatcher)) return;

            UnsubscribeHostLeftFallback();
            dispatcher.CustomMessageReceived += OnDispatcherCustomMessage;
            _hostLeftDispatcher = dispatcher;
        }

        private static void UnsubscribeHostLeftFallback()
        {
            if (_hostLeftDispatcher == null) return;
            _hostLeftDispatcher.CustomMessageReceived -= OnDispatcherCustomMessage;
            _hostLeftDispatcher = null;
        }

        private static void OnDispatcherCustomMessage(string eventName, string json)
        {
            if (eventName != SessionEvents.HostLeft || _handlingHostLeft ||
                _session == null || _session.IsLocalHost)
                return;

            DiagnosticLog.Warning("MultiplayerLobbyMenu: fallback received host-left; forcing lobby exit.");
            OnHostLeft("");
        }

        private static void OnHostLeft(string hostName)
        {
            if (_handlingHostLeft || _session == null) return;
            _handlingHostLeft = true;

            DiagnosticLog.Info("MultiplayerLobbyMenu: host left the session. Showing error.");

            ReleaseSessionOnly();
            MultiplayerConnectionErrorHandler.ShowHostLeft(hostName);

            _handlingHostLeft = false;
        }

        private static void OnSessionStarting(string sceneName, int expectedPlayers)
        {
            if (_sessionStartHandled)
            {
                DiagnosticLog.Verbose(
                    "MultiplayerLobbyMenu: ignoring redundant SessionStart delivery for scene '" +
                    sceneName + "' (already started).");
                return;
            }
            _sessionStartHandled = true;

            DiagnosticLog.Info("MultiplayerLobbyMenu: session starting → scene '" + sceneName + "'.");

            int expected = Mathf.Max(1, expectedPlayers);

            bool isHost = false;
            try { isHost = _session != null && _session.IsLocalHost; } catch { }

            // Arm the loading overlay. If this throws for any reason, log the
            // full stack and keep going, so LoadingStageManager.Begin still
            // runs and the pipeline still starts. The button appearing to do
            // nothing is worse than an overlay that fails to arm.
            try
            {
                MultiplayerLoadingScene.Begin(sceneName);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerLobbyMenu: MultiplayerLoadingScene.Begin threw:\n" +
                                      ex.ToString());
            }

            try
            {
                LoadingStageManager.Begin(isHost, expected);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerLobbyMenu: LoadingStageManager.Begin threw:\n" +
                                      ex.ToString());
            }
        }
    }
}
