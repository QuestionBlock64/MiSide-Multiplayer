using MiSideMultiplayer.API;
using MiSideMultiplayer.API.Factories;
using UnityEngine;

namespace MiSideMultiplayer.Runtime.Menu
{
    internal static class ModeSelectMenu
    {
        public const string PageName  = "Location Mode Select";
        public const string PageTitle = "SELECT MODE";

        public const string SinglePlayerLabel = "SINGLEPLAYER";
        public const string MultiplayerLabel  = "MULTIPLAYER";
        public const string ExitLabel         = "EXIT";

        public const string VanillaMainMenu     = "Location Menu";
        public const string MultiplayerMenuName = "Location Multiplayer";
        public const string ExitMenu            = "Location ExitGame";

        private const float ExitY = -350f;

        public static GameMenu Build()
        {
            if (MenuManager.Instance == null)
            {
                DiagnosticLog.Warning("ModeSelectMenu: MenuManager not initialized.");
                return null;
            }

            GameMenu existing = MenuManager.Instance.Find(PageName);
            if (existing != null)
                return existing;

            GameMenu menu = new MenuFactory()
                .SetObjectName(PageName)
                .SetTitle(PageTitle)
                .Build();

            if (menu == null)
            {
                DiagnosticLog.Warning("ModeSelectMenu: MenuFactory.Build returned null.");
                return null;
            }

            GameMenu vanillaMenu = null;
            try { vanillaMenu = MenuManager.Instance.Find(VanillaMainMenu); }
            catch (System.Exception ex) { DiagnosticLog.Warning("ModeSelectMenu: find vanilla menu failed: " + ex.Message); }
            if (vanillaMenu == null)
                DiagnosticLog.Warning("ModeSelectMenu: could not find '" + VanillaMainMenu + "'.");

            GameMenu exitGameMenu = null;
            try { exitGameMenu = MenuManager.Instance.Find(ExitMenu); }
            catch (System.Exception ex) { DiagnosticLog.Warning("ModeSelectMenu: find exit menu failed: " + ex.Message); }
            if (exitGameMenu == null)
                DiagnosticLog.Warning("ModeSelectMenu: could not find '" + ExitMenu + "'.");

            GameMenu multiplayerMenu = null;
            try
            {
                multiplayerMenu = MultiplayerMenu.Build(menu);
            }
            catch (System.Exception ex)
            {
                DiagnosticLog.Warning("ModeSelectMenu: MultiplayerMenu.Build threw:\n" + ex.ToString());
                multiplayerMenu = null;
            }

            if (vanillaMenu != null)
            {
                try { MenuTweaks.ConvertExitToBack(vanillaMenu, menu); }
                catch (System.Exception ex) { DiagnosticLog.Warning("ModeSelectMenu: ConvertExitToBack failed: " + ex.Message); }
            }

            if (exitGameMenu != null)
            {
                try { MenuTweaks.PointExitGameNoButtonTo(menu); }
                catch (System.Exception ex) { DiagnosticLog.Warning("ModeSelectMenu: PointExitGameNoButtonTo failed: " + ex.Message); }
            }

            if (vanillaMenu != null)
            {
                try
                {
                    new MenuOptionFactory()
                        .SetName(SinglePlayerLabel)
                        .SetParent(menu)
                        .SetNextLocation(vanillaMenu)
                        .Build();
                }
                catch (System.Exception ex)
                {
                    DiagnosticLog.Warning("ModeSelectMenu: SINGLEPLAYER option failed: " + ex.Message);
                }
            }

            if (multiplayerMenu != null)
            {
                try
                {
                    new MenuOptionFactory()
                        .SetName(MultiplayerLabel)
                        .SetParent(menu)
                        .SetNextLocation(multiplayerMenu)
                        .SetOnClick(() =>
                        {
                            if (MultiplayerMenu.CurrentState != MultiplayerMenu.LoginState.LoggedIn)
                                MultiplayerMenu.SetNotLoggedIn();
                        })
                        .Build();
                }
                catch (System.Exception ex)
                {
                    DiagnosticLog.Warning("ModeSelectMenu: MULTIPLAYER option failed: " + ex.Message);
                }
            }
            else
            {
                DiagnosticLog.Warning("ModeSelectMenu: multiplayer menu unavailable this session.");
            }

            if (exitGameMenu != null)
            {
                try
                {
                    MenuOption exit = new MenuOptionFactory()
                        .SetName(ExitLabel)
                        .SetParent(menu)
                        .SetNextLocation(exitGameMenu)
                        .Build();

                    if (exit != null && exit.OptionObject != null)
                    {
                        RectTransform rt = exit.OptionObject.GetComponent<RectTransform>();
                        if (rt != null)
                        {
                            Vector2 pos = rt.anchoredPosition;
                            pos.y = ExitY;
                            rt.anchoredPosition = pos;
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    DiagnosticLog.Warning("ModeSelectMenu: EXIT option failed: " + ex.Message);
                }
            }

            DiagnosticLog.Info("ModeSelectMenu: built '" + PageName + "'.");
            return menu;
        }
    }
}