using System;
using System.Collections.Generic;
using HarmonyLib;
using MiSideMultiplayer.Runtime.Menu;
using MiSideMultiplayer.Runtime.Menu.UI;
using UnityEngine;

namespace MiSideMultiplayer.Runtime.Menu.Patches
{
    /// <summary>
    /// Postfix on MenuLocation.Active(bool). Whenever the game opens or
    /// closes a menu, we:
    ///   1. Look up any registered CanvasGroupFader and tell it to fade.
    ///   2. If the menu is the multiplayer login menu, forward the open/close
    ///      event to MultiplayerMenu so it can fade its prompt text.
    /// </summary>
    [HarmonyPatch(typeof(MenuLocation), nameof(MenuLocation.Active))]
    internal static class MenuLocationActiveFadePatch
    {
        private const string MultiplayerLoginMenuName = "Location Multiplayer";

        private static readonly Dictionary<IntPtr, CanvasGroupFader> _faders
            = new Dictionary<IntPtr, CanvasGroupFader>();

        public static void Register(MenuLocation menu, CanvasGroupFader fader)
        {
            if (menu == null || fader == null) return;
            IntPtr key = menu.Pointer;
            if (key == IntPtr.Zero) return;

            _faders[key] = fader;
            DiagnosticLog.Info("MenuLocationActiveFadePatch: registered fader for MenuLocation 0x" +
                               key.ToString("X") + ".");
        }

        public static void Unregister(MenuLocation menu)
        {
            if (menu == null) return;
            IntPtr key = menu.Pointer;
            if (key == IntPtr.Zero) return;
            _faders.Remove(key);
        }

        private static void Postfix(MenuLocation __instance, bool x)
        {
            if (__instance == null) return;

            try
            {
                IntPtr key = __instance.Pointer;
                if (key == IntPtr.Zero) return;

                CanvasGroupFader fader;
                if (_faders.TryGetValue(key, out fader) && fader != null)
                {
                    fader.SetVisible(x);
                }

                // Forward to MultiplayerMenu if this is the login menu.
                try
                {
                    string goName = __instance.gameObject != null ? __instance.gameObject.name : null;
                    if (goName == MultiplayerLoginMenuName)
                    {
                        if (x) MultiplayerMenu.OnMenuOpened();
                        else   MultiplayerMenu.OnMenuClosed();
                    }
                    else if (goName == MultiplayerLobbyMenu.PageName ||
                             (goName != null && goName.StartsWith(
                                 MultiplayerLobbyMenu.PageName + "(", StringComparison.Ordinal)))
                    {
                        MultiplayerLobbyMenu.OnLobbyMenuVisibilityChanged(x);
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warning("MenuLocationActiveFadePatch: forward to MultiplayerMenu failed: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MenuLocationActiveFadePatch: Postfix threw: " + ex.Message);
            }
        }
    }
}
