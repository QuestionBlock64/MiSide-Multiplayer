using UnityEngine;

namespace MiSideMultiplayer
{
    /// <summary>
    /// Central logging wrapper that always routes to the BepInEx console
    /// via Unity's Debug.Log / Debug.LogWarning / Debug.LogError.
    ///
    /// All messages are prefixed with [MiSideMultiplayer] for easy filtering
    /// in the BepInEx console with the keyword "MiSideMultiplayer".
    /// </summary>
    internal static class DiagnosticLog
    {
        private const string Prefix = "[MiSideMultiplayer] ";

        /// <summary>
        /// When false, Info messages are suppressed. Warning and Error always print.
        /// Toggle via the Diagnostics.DebugLogging config key.
        /// </summary>
        public static bool Enabled = true;

        public static void Info(string message)
        {
            if (Enabled)
                Debug.Log(Prefix + message);
        }

        public static void Warning(string message)
        {
            // Warnings always print regardless of Enabled flag.
            Debug.LogWarning(Prefix + message);
        }

        public static void Error(string message)
        {
            // Errors always print regardless of Enabled flag.
            Debug.LogError(Prefix + message);
        }
    }
}
