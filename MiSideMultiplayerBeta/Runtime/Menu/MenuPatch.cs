using HarmonyLib;
using MiSideMultiplayer.API;
using MiSideMultiplayer.Runtime.Menu;
using UnityEngine;

namespace MiSideMultiplayer.Runtime.Menu
{
    [HarmonyPatch(typeof(global::Menu))]
    public class MenuPatch
    {
        private static GameObject _originalFirstLocation;

        [HarmonyPrefix]
        [HarmonyPatch(nameof(global::Menu.Start))]
        public static void StartPrefix(global::Menu __instance)
        {
            try
            {
                GameMenu modeSelect = ModeSelectMenu.Build();
                if (modeSelect == null || modeSelect.MenuObject == null)
                {
                    DiagnosticLog.Warning("MenuPatch: mode select not built; using vanilla first location.");
                    return;
                }

                if (_originalFirstLocation == null)
                    _originalFirstLocation = __instance.firstLocation;

                __instance.firstLocation = modeSelect.MenuObject;

                DiagnosticLog.Info("MenuPatch: redirected firstLocation to '" +
                                   modeSelect.MenuObject.name + "'.");
            }
            catch (System.Exception ex)
            {
                DiagnosticLog.Warning("MenuPatch: StartPrefix failed:\n" + ex.ToString());
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(global::Menu.OpenNextLocation))]
        public static void OpenNextLocationPrefix(GameObject objectLocation)
        {
            try { MultiplayerLobbyMenu.OnMenuNavigationStarted(objectLocation); }
            catch { }
        }

    }
}
