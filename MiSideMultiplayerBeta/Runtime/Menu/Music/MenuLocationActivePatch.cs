using HarmonyLib;
using MiSideMultiplayer.Runtime.Menu;

namespace MiSideMultiplayer.Runtime.Menu.Music
{
    /// <summary>
    /// Postfix on MenuLocation.Active(bool) that routes every menu
    /// transition through MultiplayerMenuMusic. If the menu becoming active
    /// is part of the Multiplayer flow (including Mode Select), we switch to
    /// the custom track; otherwise we restore the game's original track.
    /// </summary>
    [HarmonyPatch(typeof(MenuLocation), nameof(MenuLocation.Active))]
    internal static class MenuLocationActivePatch
    {
        [HarmonyPostfix]
        private static void Postfix(MenuLocation __instance, bool x)
        {
            if (__instance == null) return;

            string name = __instance.gameObject != null ? __instance.gameObject.name : null;
            if (string.IsNullOrEmpty(name)) return;

            if (!x) return;   // only care about menus becoming active

            bool multiplayerMenu = MultiplayerMenuBranding.IsMultiplayerMenuName(name);
            MultiplayerMenuBranding.OnLocationActivated(__instance);

            if (multiplayerMenu)
                MultiplayerMenuMusic.EnterMultiplayer();
            else
                MultiplayerMenuMusic.ExitMultiplayer();
        }
    }
}
