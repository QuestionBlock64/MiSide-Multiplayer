using HarmonyLib;

namespace MiSideMultiplayer
{
    public static class MitaPlayerOverride
    
    {
        private static string localPlayerId = "";
        public static string ConfiguredHostId { get; set; } = "";

        public static void Configure(string configuredLocalPlayerId)
        {
            localPlayerId = configuredLocalPlayerId ?? "";
        }

        public static void Initialize(Harmony harmony)
        {
            if (harmony == null) return;

            int patched = 0;
            patched += InstallPatch(harmony, typeof(MitaMultiPlayerWander));
            patched += InstallPatch(harmony, typeof(MitaMultiPlayerGaze));

            DiagnosticLog.Info("MitaPlayerOverride: installed " + patched + " AI patch class(es).");
        }

        private static int InstallPatch(Harmony harmony, System.Type patchClass)
        {
            try
            {
                harmony.CreateClassProcessor(patchClass).Patch();
                return 1;
            }
            catch (System.Exception ex)
            {
                DiagnosticLog.Warning("MitaPlayerOverride: failed to install " + patchClass.Name + ": " + ex.Message);
                return 0;
            }
        }

        public static string CurrentAttentionPlayerId
        {
            get { return ""; }
        }

        public static void ForceAttention(string _) { }

        public static void RestoreSwap() { }
    }
}