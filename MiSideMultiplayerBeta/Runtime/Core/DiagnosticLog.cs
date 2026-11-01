using UnityEngine;

namespace MiSideMultiplayer
{
    internal static class DiagnosticLog
    {
        private const string Prefix = "[MiSideMultiplayer] ";
        private const string VerbosePrefix = "[MiSideMultiplayer][V] ";

        public static bool Enabled = true;
        public static bool VerboseEnabled = false;

        public static void Info(string message)
        {
            if (Enabled)
                Debug.Log(Prefix + message);
        }

        public static void Verbose(string message)
        {
            if (Enabled && VerboseEnabled)
                Debug.Log(VerbosePrefix + message);
        }

        public static void Warning(string message)
        {
            Debug.LogWarning(Prefix + message);
        }

        public static void Error(string message)
        {
            Debug.LogError(Prefix + message);
        }
    }
}