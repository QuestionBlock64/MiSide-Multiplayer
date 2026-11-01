using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using MiSideMultiplayer.Runtime.Lobby;

namespace MiSideMultiplayer.Runtime.Menu
{
    /// <summary>
    /// Keeps the world running while the vanilla FastMenu is open in a live
    /// multiplayer session. Loading pauses and single-player behavior remain
    /// owned by the game's normal GameController.Pause implementation.
    /// </summary>
    internal static class FastMenuMultiplayerPatch
    {
        private static bool _initialized;
        private static int _fastMenuOpenDepth;
        private static bool _loggedMenuModeWorldRunning;
        private static bool _cameraInputLocked;
        private static bool _loggedMissingMouseLock;
        private static Component _lockedPlayerMove;
        private static PropertyInfo _stopMouseMoveProperty;
        private static FieldInfo _stopMouseMoveField;
        private static bool _originalStopMouseMove;

        public static void Initialize(Harmony harmony)
        {
            if (_initialized || harmony == null) return;

            try
            {
                Type gameControllerType = AccessTools.TypeByName("GameController");
                if (gameControllerType == null)
                {
                    DiagnosticLog.Warning("FastMenuMultiplayerPatch: GameController type not found.");
                    return;
                }

                MethodInfo open = AccessTools.Method(gameControllerType,
                    "FastMenuOpen", Type.EmptyTypes);
                MethodInfo close = AccessTools.Method(gameControllerType,
                    "FastMenuClose", Type.EmptyTypes);
                MethodInfo pause = AccessTools.Method(gameControllerType,
                    "Pause", new[] { typeof(bool), typeof(bool) });

                MethodInfo openPrefix = AccessTools.Method(typeof(FastMenuMultiplayerPatch),
                    nameof(FastMenuOpenPrefix));
                MethodInfo openPostfix = AccessTools.Method(typeof(FastMenuMultiplayerPatch),
                    nameof(FastMenuOpenPostfix));
                MethodInfo openFinalizer = AccessTools.Method(typeof(FastMenuMultiplayerPatch),
                    nameof(FastMenuOpenFinalizer));
                MethodInfo closePostfix = AccessTools.Method(typeof(FastMenuMultiplayerPatch),
                    nameof(FastMenuClosePostfix));
                MethodInfo pausePostfix = AccessTools.Method(typeof(FastMenuMultiplayerPatch),
                    nameof(PausePostfix));

                bool installed = false;
                if (open != null)
                {
                    harmony.Patch(open,
                        prefix: new HarmonyMethod(openPrefix),
                        postfix: new HarmonyMethod(openPostfix),
                        finalizer: new HarmonyMethod(openFinalizer));
                    installed = true;
                    DiagnosticLog.Info("FastMenuMultiplayerPatch: patched GameController.FastMenuOpen().");
                }
                else
                {
                    DiagnosticLog.Warning("FastMenuMultiplayerPatch: GameController.FastMenuOpen() not found.");
                }

                if (close != null)
                {
                    harmony.Patch(close, postfix: new HarmonyMethod(closePostfix));
                    installed = true;
                    DiagnosticLog.Info("FastMenuMultiplayerPatch: patched GameController.FastMenuClose().");
                }
                else
                {
                    DiagnosticLog.Warning("FastMenuMultiplayerPatch: GameController.FastMenuClose() not found.");
                }

                if (pause != null)
                {
                    harmony.Patch(pause, postfix: new HarmonyMethod(pausePostfix));
                    installed = true;
                    DiagnosticLog.Info("FastMenuMultiplayerPatch: patched GameController.Pause(bool, bool).");
                }
                else
                {
                    DiagnosticLog.Warning("FastMenuMultiplayerPatch: GameController.Pause(bool, bool) not found.");
                }

                _initialized = installed;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("FastMenuMultiplayerPatch: initialization failed: " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void FastMenuOpenPrefix(Component __instance, ref bool __state)
        {
            __state = IsMultiplayerSessionActive();
            if (__state) _fastMenuOpenDepth++;
        }

        private static void FastMenuOpenPostfix(Component __instance, ref bool __state)
        {
            if (!__state) return;

            _fastMenuOpenDepth = Math.Max(0, _fastMenuOpenDepth - 1);
            KeepWorldRunning(__instance);
            LockCameraInput(__instance);
            __state = false;
        }

        private static void FastMenuClosePostfix(Component __instance)
        {
            _fastMenuOpenDepth = 0;
            RestoreCameraInput();
        }

        private static Exception FastMenuOpenFinalizer(Exception __exception,
                                                       ref bool __state)
        {
            // Postfix clears the state after doing the recovery. This finalizer
            // handles an exception thrown before the postfix could run.
            if (__state)
                _fastMenuOpenDepth = Math.Max(0, _fastMenuOpenDepth - 1);
            return __exception;
        }

        private static void PausePostfix(Component __instance, bool __0, bool __1)
        {
            if (!__0)
            {
                if (!IsFastMenuActive(__instance)) RestoreCameraInput();
                return;
            }
            if (__1 || !IsMultiplayerSessionActive()) return;
            if (_fastMenuOpenDepth <= 0 && !IsFastMenuActive(__instance)) return;

            // The game's pause routine has already enabled menu mode, shown
            // the cursor, and locked player controls. Undo only the world
            // freeze so the menu remains modal while the scene keeps running.
            KeepWorldRunning(__instance);
            LockCameraInput(__instance);
            if (!_loggedMenuModeWorldRunning)
            {
                _loggedMenuModeWorldRunning = true;
                DiagnosticLog.Info("FastMenuMultiplayerPatch: FastMenu keeps normal pause/menu mode; world simulation and audio continue.");
            }
        }

        private static bool IsFastMenuActive(Component gameController)
        {
            if (gameController == null) return false;

            try
            {
                Transform interfaceRoot = gameController.transform.Find("Interface");
                Transform menu = interfaceRoot != null
                    ? interfaceRoot.Find("FastMenu(Clone)")
                    : null;
                if (menu != null && menu.gameObject.activeSelf) return true;
            }
            catch { }

            try
            {
                PropertyInfo property = AccessTools.Property(gameController.GetType(), "fastMenuObject");
                GameObject menu = property != null
                    ? property.GetValue(gameController, null) as GameObject
                    : null;
                return menu != null && menu.activeSelf;
            }
            catch
            {
                return false;
            }
        }

        private static void KeepWorldRunning(Component gameController)
        {
            try { Time.timeScale = 1f; }
            catch { }

            try
            {
                AudioSource[] sources = UnityEngine.Object.FindObjectsOfType<AudioSource>(true);
                if (sources != null)
                {
                    for (int i = 0; i < sources.Length; i++)
                    {
                        if (sources[i] != null) sources[i].UnPause();
                    }
                }
            }
            catch { }
        }

        private static void LockCameraInput(Component gameController)
        {
            if (gameController == null) return;

            Component playerMove = null;
            try
            {
                PropertyInfo moveProperty = AccessTools.Property(gameController.GetType(), "scrpm");
                if (moveProperty != null)
                    playerMove = moveProperty.GetValue(gameController, null) as Component;
            }
            catch { }

            if (playerMove == null)
            {
                try
                {
                    Component[] components = gameController.GetComponentsInChildren<Component>(true);
                    for (int i = 0; i < components.Length; i++)
                    {
                        if (components[i] != null && components[i].GetType().Name == "PlayerMove")
                        {
                            playerMove = components[i];
                            break;
                        }
                    }
                }
                catch { }
            }

            if (playerMove == null)
            {
                LogMissingMouseLock("GameController.scrpm / PlayerMove was not found.");
                return;
            }

            if (_cameraInputLocked && _lockedPlayerMove == playerMove)
            {
                WriteStopMouseMove(true);
                return;
            }

            RestoreCameraInput();
            PropertyInfo property = AccessTools.Property(playerMove.GetType(), "stopMouseMove");
            FieldInfo field = property == null
                ? AccessTools.Field(playerMove.GetType(), "stopMouseMove")
                : null;
            try
            {
                if (property != null && property.PropertyType == typeof(bool) && property.CanRead && property.CanWrite)
                    _originalStopMouseMove = (bool)property.GetValue(playerMove, null);
                else if (field != null && field.FieldType == typeof(bool))
                    _originalStopMouseMove = (bool)field.GetValue(playerMove);
                else
                {
                    LogMissingMouseLock("PlayerMove.stopMouseMove was not readable and writable.");
                    return;
                }

                _lockedPlayerMove = playerMove;
                _stopMouseMoveProperty = property;
                _stopMouseMoveField = field;
                _cameraInputLocked = true;
                WriteStopMouseMove(true);
                DiagnosticLog.Info("FastMenuMultiplayerPatch: suppressed mouse-look while FastMenu is open.");
            }
            catch (Exception ex)
            {
                RestoreCameraInput();
                LogMissingMouseLock("could not set PlayerMove.stopMouseMove: " + ex.Message);
            }
        }

        private static void WriteStopMouseMove(bool value)
        {
            if (!_cameraInputLocked || _lockedPlayerMove == null) return;
            try
            {
                if (_stopMouseMoveProperty != null)
                    _stopMouseMoveProperty.SetValue(_lockedPlayerMove, value, null);
                else if (_stopMouseMoveField != null)
                    _stopMouseMoveField.SetValue(_lockedPlayerMove, value);
            }
            catch { }
        }

        private static void RestoreCameraInput()
        {
            if (_cameraInputLocked && _lockedPlayerMove != null)
                WriteStopMouseMove(_originalStopMouseMove);
            _cameraInputLocked = false;
            _lockedPlayerMove = null;
            _stopMouseMoveProperty = null;
            _stopMouseMoveField = null;
        }

        private static void LogMissingMouseLock(string reason)
        {
            if (_loggedMissingMouseLock) return;
            _loggedMissingMouseLock = true;
            DiagnosticLog.Warning("FastMenuMultiplayerPatch: " + reason);
        }

        private static bool IsMultiplayerSessionActive()
        {
            return SessionController.Active != null &&
                   MiSideMultiplayerPlugin.IsRelayConnected();
        }
    }
}
