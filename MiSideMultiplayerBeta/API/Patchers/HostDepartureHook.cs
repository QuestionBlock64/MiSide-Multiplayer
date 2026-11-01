using System;
using System.Reflection;
using HarmonyLib;
using MiSideMultiplayer.Runtime.Menu;

namespace MiSideMultiplayer.API.Patchers
{
    /// <summary>
    /// Notifies the relay at the start of MiSide's native return-to-menu fade.
    /// Waiting for SceneMenu.sceneLoaded delays the notification until after the
    /// fade and leaves joiners in the game longer than necessary.
    /// </summary>
    internal static class HostDepartureHook
    {
        private static bool _initialized;

        public static void Initialize(Harmony harmony)
        {
            if (_initialized || harmony == null) return;
            _initialized = true;

            try
            {
                MethodInfo nextLevel = typeof(BlackScreen).GetMethod(
                    "NextLevel",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new Type[] { typeof(string) },
                    null);
                if (nextLevel == null)
                {
                    DiagnosticLog.Warning("HostDepartureHook: BlackScreen.NextLevel(string) not found.");
                    return;
                }

                HarmonyMethod prefix = new HarmonyMethod(typeof(HostDepartureHook).GetMethod(
                    nameof(NextLevelPrefix),
                    BindingFlags.Static | BindingFlags.NonPublic));
                harmony.Patch(nextLevel, prefix, null);
                DiagnosticLog.Info("HostDepartureHook: patched BlackScreen.NextLevel(string).");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("HostDepartureHook: patch failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void NextLevelPrefix(string level)
        {
            if (!string.Equals(level, "SceneMenu", StringComparison.Ordinal)) return;
            MultiplayerLobbyMenu.NotifyLocalHostLeaving();
        }
    }
}
