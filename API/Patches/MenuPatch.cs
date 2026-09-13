using HarmonyLib;

namespace MiSideMultiplayer.API.Patches
{
    [HarmonyPatch(typeof(Menu))]
    public class MenuPatch
    {
        [HarmonyPostfix]
        [HarmonyPatch(nameof(Menu.Start))]
        public static void StartPatch()
        {
            MultiplayerMenuBootstrap.Build();
        }
    }
}