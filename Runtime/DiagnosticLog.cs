using UnityEngine;

namespace MiSideMultiplayer
{
    internal static class DiagnosticLog
    {
        public static bool Enabled = true;

        public static void Info(string message)
        {
            Debug.Log("[MiSideMultiplayer] " + message);
        }

        public static void Warning(string message)
        {
            Debug.LogWarning("[MiSideMultiplayer] " + message);
        }

        public static void Error(string message)
        {
            Debug.LogError("[MiSideMultiplayer] " + message);
        }
    }
}
