using MiSideMultiplayer.API;
using MiSideMultiplayer.API.Factories;
using UnityEngine;

namespace MiSideMultiplayer.Runtime.Menu
{
    internal static class MultiplayerPlayMenu
    {
        public const string PageName  = "Location Multiplayer Play";
        public const string PageTitle = "PLAY";

        private const float BackY = -650f;

        public static GameMenu Build(GameMenu homeMenu)
        {
            if (MenuManager.Instance == null) return null;

            GameMenu existing = MenuManager.Instance.Find(PageName);
            if (existing != null) return existing;

            GameMenu menu = new MenuFactory()
                .SetObjectName(PageName)
                .SetTitle(PageTitle)
                .Build();
            if (menu == null) return null;

            // Build the Lobby FIRST so every submenu can navigate to it.
            GameMenu lobby = null;
            try { lobby = MultiplayerLobbyMenu.Build(menu); }
            catch (System.Exception ex) { DiagnosticLog.Warning("PlayMenu: lobby build failed: " + ex.Message); }

            // Sub-menus. Each accepts the lobby so its action button can SetNextLocation to it.
            GameMenu hostSetup     = null;
            GameMenu publicLobby   = null;
            GameMenu privateLobby  = null;
            GameMenu directConnect = null;

            try { hostSetup     = MultiplayerHostSetupMenu.Build(menu, lobby); }       catch (System.Exception ex) { DiagnosticLog.Warning("PlayMenu: host setup failed: " + ex.Message); }
            try { publicLobby   = MultiplayerPublicLobbyMenu.Build(menu, lobby); }     catch (System.Exception ex) { DiagnosticLog.Warning("PlayMenu: public lobby failed: " + ex.Message); }
            try { privateLobby  = MultiplayerPrivateLobbyMenu.Build(menu, lobby); }    catch (System.Exception ex) { DiagnosticLog.Warning("PlayMenu: private lobby failed: " + ex.Message); }
            try { directConnect = MultiplayerDirectConnectMenu.Build(menu, lobby); }   catch (System.Exception ex) { DiagnosticLog.Warning("PlayMenu: direct connect failed: " + ex.Message); }

            BuildOption(menu, "HOST GAME",      hostSetup     != null ? hostSetup     : menu);
            BuildOption(menu, "PUBLIC LOBBY",   publicLobby   != null ? publicLobby   : menu);
            BuildOption(menu, "PRIVATE LOBBY",  privateLobby  != null ? privateLobby  : menu);
            BuildOption(menu, "DIRECT CONNECT", directConnect != null ? directConnect : menu);

            if (homeMenu != null)
            {
                MenuOption back = new MenuOptionFactory()
                    .SetName("BACK")
                    .SetParent(menu)
                    .SetNextLocation(homeMenu)
                    .Build();
                if (back != null && back.OptionObject != null)
                {
                    RectTransform rt = back.OptionObject.GetComponent<RectTransform>();
                    if (rt != null) { Vector2 p = rt.anchoredPosition; p.y = BackY; rt.anchoredPosition = p; }
                }
            }

            DiagnosticLog.Info("MultiplayerPlayMenu: built '" + PageName + "'.");
            return menu;
        }

        private static void BuildOption(GameMenu parent, string label, GameMenu dest)
        {
            try
            {
                new MenuOptionFactory()
                    .SetName(label)
                    .SetParent(parent)
                    .SetNextLocation(dest)
                    .Build();
            }
            catch (System.Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerPlayMenu: option '" + label + "' failed: " + ex.Message);
            }
        }
    }
}