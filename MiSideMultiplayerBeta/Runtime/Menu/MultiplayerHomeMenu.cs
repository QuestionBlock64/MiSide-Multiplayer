using System;
using System.Collections.Generic;
using MiSideMultiplayer.API;
using MiSideMultiplayer.API.Credits;
using MiSideMultiplayer.API.Factories;
using MiSideMultiplayer.Runtime.Credits;
using UnityEngine;

namespace MiSideMultiplayer.Runtime.Menu
{
    internal static class MultiplayerHomeMenu
    {
        public const string PageName   = "Location Multiplayer Home";
        public const string PageTitle  = "MULTIPLAYER";

        public const string PlayLabel     = "PLAY";
        public const string SettingsLabel = "SETTINGS";
        public const string DiscordLabel  = "DISCORD";
        public const string CreditsLabel  = "CREDITS";

        private const string DiscordInvite = "https://discord.gg/yyDVE3rTzQ";

        private const float BackY = -650f;

        public static GameMenu Build(GameMenu backTarget)
        {
            if (MenuManager.Instance == null)
            {
                DiagnosticLog.Warning("MultiplayerHomeMenu: MenuManager not initialized.");
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
                DiagnosticLog.Warning("MultiplayerHomeMenu: MenuFactory.Build returned null.");
                return null;
            }

            GameMenu settingsPage = null;
            try { settingsPage = MultiplayerSettingsMenu.Build(menu); }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerHomeMenu: MultiplayerSettingsMenu.Build failed:\n" + ex.ToString());
            }

            GameMenu playMenu = null;
            try { playMenu = MultiplayerPlayMenu.Build(menu); }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerHomeMenu: MultiplayerPlayMenu.Build failed:\n" + ex.ToString());
            }

            GameMenu creditsMenu = null;
            try { creditsMenu = MultiplayerAuthorMenu.Build(menu); }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerHomeMenu: MultiplayerAuthorMenu.Build failed:\n" + ex.ToString());
            }

            BuildOption(menu, PlayLabel,     playMenu     != null ? playMenu     : menu);
            BuildOption(menu, SettingsLabel, settingsPage != null ? settingsPage : menu);
            BuildOptionWithAction(menu, DiscordLabel, menu, () => OnDiscordClicked());

            // CREDITS now points at our own menu. No more vanilla-button
            // round-trip, no more CreditsInjectionHost.
            BuildOption(menu, CreditsLabel, creditsMenu != null ? creditsMenu : menu);

            if (backTarget != null)
            {
                try
                {
                    MenuOption back = new MenuOptionFactory()
                        .SetName("BACK")
                        .SetParent(menu)
                        .SetNextLocation(backTarget)
                        .SetOnClick(() => MultiplayerMenu.SetNotLoggedIn())
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
                catch (Exception ex)
                {
                    DiagnosticLog.Warning("MultiplayerHomeMenu: BACK option failed: " + ex.Message);
                }
            }

            DiagnosticLog.Info("MultiplayerHomeMenu: built '" + PageName + "'.");
            return menu;
        }

        private static void BuildOption(GameMenu parent, string label, GameMenu destination)
        {
            try
            {
                new MenuOptionFactory()
                    .SetName(label)
                    .SetParent(parent)
                    .SetNextLocation(destination)
                    .Build();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerHomeMenu: option '" + label + "' failed: " + ex.Message);
            }
        }

        private static void BuildOptionWithAction(GameMenu parent, string label, GameMenu destination, Action onClick)
        {
            try
            {
                new MenuOptionFactory()
                    .SetName(label)
                    .SetParent(parent)
                    .SetNextLocation(destination)
                    .SetOnClick(onClick)
                    .Build();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MultiplayerHomeMenu: option '" + label + "' failed: " + ex.Message);
            }
        }

        private static void OnDiscordClicked()
        {
            DiagnosticLog.Info("MultiplayerHomeMenu: DISCORD clicked.");
            try { Application.OpenURL(DiscordInvite); }
            catch (Exception ex) { DiagnosticLog.Warning("MultiplayerHomeMenu: OpenURL failed: " + ex.Message); }
        }
    }
}