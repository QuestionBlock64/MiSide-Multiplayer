using System.Reflection;
using HarmonyLib;
using MiSideMultiplayer.API.Players;
using UnityEngine;

namespace MiSideMultiplayer
{
    [HarmonyPatch(typeof(Location5_MitaFollowPlayer))]
    public static class MitaMultiPlayerWander
    {
        private static readonly FieldInfo _targetField =
            AccessTools.Field(typeof(Location5_MitaFollowPlayer), "target");

        [HarmonyPrefix]
        [HarmonyPatch("Update")]
        public static void Prefix(Location5_MitaFollowPlayer __instance)
        {
            if (__instance == null || _targetField == null) return;
            if (!MitaHost.IsHost) return;
            if (MitaPlayerResolver.IsGameCardMita(__instance.transform)) return;

            Transform closest = MitaPlayerResolver.ClosestTo(__instance.transform.position);
            if (closest != null)
                _targetField.SetValue(__instance, closest);
        }
    }
}
