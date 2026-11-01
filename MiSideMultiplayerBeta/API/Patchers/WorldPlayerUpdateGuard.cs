using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MiSideMultiplayer
{
    /// <summary>
    /// The game's WorldPlayer.Update assumes its global player, controller,
    /// and camera references are already valid. During scene teardown/loading
    /// those globals are temporarily null, which otherwise throws every frame
    /// and floods the log while Unity is trying to transition scenes.
    /// </summary>
    internal static class WorldPlayerUpdateGuard
    {
        private static bool _initialized;
        private static bool _waitingLogged;

        public static void Initialize(Harmony harmony)
        {
            if (_initialized || harmony == null) return;
            _initialized = true;

            try
            {
                MethodInfo update = typeof(WorldPlayer).GetMethod(
                    "Update",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null);
                if (update == null)
                {
                    DiagnosticLog.Warning("WorldPlayerUpdateGuard: WorldPlayer.Update() not found.");
                    return;
                }

                MethodInfo prefixMethod = typeof(WorldPlayerUpdateGuard).GetMethod(
                    nameof(UpdatePrefix), BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(update, prefix: new HarmonyMethod(prefixMethod));
                DiagnosticLog.Info("WorldPlayerUpdateGuard: patched WorldPlayer.Update() to skip frames without a bound local player/camera.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("WorldPlayerUpdateGuard: patch failed: " +
                                      ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool UpdatePrefix()
        {
            try
            {
                if (GlobalTag.gameController != null &&
                    GlobalTag.player != null &&
                    GlobalTag.cameraPlayer != null)
                {
                    _waitingLogged = false;
                    return true;
                }

                if (!_waitingLogged)
                {
                    DiagnosticLog.Verbose("WorldPlayerUpdateGuard: skipped WorldPlayer.Update() while scene globals are being cleared or rebound.");
                    _waitingLogged = true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}
