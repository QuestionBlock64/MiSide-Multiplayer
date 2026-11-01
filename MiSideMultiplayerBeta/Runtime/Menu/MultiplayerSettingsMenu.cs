using MiSideMultiplayer.API;
using MiSideMultiplayer.API.Factories;
using MiSideMultiplayer.Runtime.Settings;
using UnityEngine;

namespace MiSideMultiplayer.Runtime.Menu
{
    internal static class MultiplayerSettingsMenu
    {
        public const string PageName  = "Location Multiplayer Settings";
        public const string PageTitle = "SETTINGS";

        private const float BackY = -650f;

        private static MultiplayerSettingsController _controller;

        public static GameMenu Build(GameMenu homeMenu)
        {
            if (MenuManager.Instance == null)
            {
                DiagnosticLog.Warning("MultiplayerSettingsMenu: MenuManager not initialized.");
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
                DiagnosticLog.Warning("MultiplayerSettingsMenu: MenuFactory.Build returned null.");
                return null;
            }

            // Populate rows.
            try
            {
                _controller = new MultiplayerSettingsController();
                _controller.Populate(menu);
            }
            catch (System.Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerSettingsMenu: populate failed:\n" + ex.ToString());
                _controller = null;
            }

            // BACK → Home
            if (homeMenu != null)
            {
                try
                {
                    MenuOption back = new MenuOptionFactory()
                        .SetName("BACK")
                        .SetParent(menu)
                        .SetNextLocation(homeMenu)
                        .SetOnClick(() =>
                        {
                            try { _controller?.Dispose(); } catch { }
                            _controller = null;
                        })
                        .Build();

                    if (back != null && back.OptionObject != null)
                    {
                        RectTransform rt = back.OptionObject.GetComponent<RectTransform>();
                        if (rt != null)
                        {
                            Vector2 pos = rt.anchoredPosition;
                            pos.y = BackY;
                            rt.anchoredPosition = pos;
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    DiagnosticLog.Warning("MultiplayerSettingsMenu: BACK option failed: " + ex.Message);
                }
            }

            DiagnosticLog.Info("MultiplayerSettingsMenu: built '" + PageName + "'.");
            return menu;
        }

        public static void Tick()
        {
            _controller?.Tick();
        }

        public static void DrawGui()
        {
            _controller?.OnGui();
        }
    }
}