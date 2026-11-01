using System;
using System.Reflection;
using HarmonyLib;
using MiSideMultiplayer.API;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer
{
    /// <summary>
    /// Hooks World.Awake() — the moment a new "world" (Backrooms, Cappie's world,
    /// any location) activates within a scene. On world change we drop every
    /// cache keyed by scene paths, clear the local inventory, and log.
    /// </summary>
    public static class WorldTransitionSync
    {
        private static bool _initialized;
        private static Harmony _harmony;
        private static string _lastWorldName;

        public static void Initialize(Harmony harmony)
        {
            if (_initialized) return;
            _initialized = true;
            _harmony = harmony;

            try
            {
                Type worldType = FindType("World");
                if (worldType == null)
                {
                    DiagnosticLog.Warning("WorldTransitionSync: World type NOT FOUND.");
                    return;
                }

                MethodInfo awake = worldType.GetMethod(
                    "Awake",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);

                if (awake == null)
                {
                    DiagnosticLog.Warning("WorldTransitionSync: World.Awake NOT FOUND.");
                    return;
                }

                MethodInfo postfix = typeof(WorldTransitionSync).GetMethod(
                    nameof(WorldAwakePostfix), BindingFlags.Static | BindingFlags.NonPublic);

                _harmony.Patch(awake, postfix: new HarmonyMethod(postfix));
                DiagnosticLog.Info("WorldTransitionSync: patched World.Awake.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("WorldTransitionSync: init failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void WorldAwakePostfix(Component __instance)
        {
            try
            {
                string name = __instance != null ? __instance.gameObject.name : "?";
                _lastWorldName = name;

                DiagnosticLog.Info("WorldTransitionSync: world activated — '" + name +
                                   "' in scene '" + SceneManager.GetActiveScene().name + "'.");

                try { MitaPlayerOverride.RestoreSwap(); }
                catch (Exception ex) { DiagnosticLog.Warning("WorldTransitionSync: RestoreSwap failed: " + ex.Message); }

                try { MitaSceneCache.Invalidate(); }
                catch (Exception ex) { DiagnosticLog.Warning("WorldTransitionSync: MitaSceneCache.Invalidate failed: " + ex.Message); }

                try { WorldOccupancyGuard.Invalidate(); }
                catch (Exception ex) { DiagnosticLog.Warning("WorldTransitionSync: WorldOccupancyGuard.Invalidate failed: " + ex.Message); }

                try { MitaGazeState.ClearAll(); }
                catch (Exception ex) { DiagnosticLog.Warning("WorldTransitionSync: MitaGazeState.ClearAll failed: " + ex.Message); }

                try { MitaSampler.ForceRescan(); }
                catch (Exception ex) { DiagnosticLog.Warning("WorldTransitionSync: MitaSampler.ForceRescan failed: " + ex.Message); }

                try { MitaController.ForceRescan(); }
                catch (Exception ex) { DiagnosticLog.Warning("WorldTransitionSync: MitaController.ForceRescan failed: " + ex.Message); }

                try { WorldObjectRegistry.ForceRescan(); }
                catch (Exception ex) { DiagnosticLog.Warning("WorldTransitionSync: WorldObjectRegistry.ForceRescan failed: " + ex.Message); }

                try { WorldStoryObjectRegistry.ForceRescan(); }
                catch (Exception ex) { DiagnosticLog.Warning("WorldTransitionSync: WorldStoryObjectRegistry.ForceRescan failed: " + ex.Message); }

                try { WorldDoorSync.ResetCachesForWorldChange(); }
                catch (Exception ex) { DiagnosticLog.Warning("WorldTransitionSync: WorldDoorSync reset failed: " + ex.Message); }

                try { WorldStoryObjectSync.ResetCachesForWorldChange(); }
                catch (Exception ex) { DiagnosticLog.Warning("WorldTransitionSync: WorldStoryObjectSync reset failed: " + ex.Message); }

                try { InventorySharingController.ForceCacheClear(); }
                catch (Exception ex) { DiagnosticLog.Warning("WorldTransitionSync: InventorySharingController.ForceCacheClear failed: " + ex.Message); }

                try { ClearLocalInventory(); }
                catch (Exception ex) { DiagnosticLog.Warning("WorldTransitionSync: ClearLocalInventory failed: " + ex.Message); }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("WorldTransitionSync postfix failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Clears the local player's inventory on world change. Uses
        /// FindWithTag("GameController") — no GlobalTag reads or writes.
        ///
        /// `keyItems` is [HideInInspector] and got stripped from the managed
        /// view of the IL2CPP build, so we read it via QBLib.Fields's native
        /// accessor instead of touching the C# field directly.
        /// </summary>
        private static void ClearLocalInventory()
        {
            GameObject gcGO = null;
            try { gcGO = GameObject.FindWithTag("GameController"); } catch { }
            if (gcGO == null)
            {
                try { gcGO = GameObject.Find("GameController"); } catch { }
            }
            if (gcGO == null) return;

            GameController gc = gcGO.GetComponent<GameController>();
            if (gc == null) return;

            bool exists = false;
            try { exists = QBLib.Fields.Exists(gc, "keyItems"); }
            catch (Exception ex) { DiagnosticLog.Warning("ClearLocalInventory: Fields.Exists failed: " + ex.Message); }

            if (!exists)
            {
                DiagnosticLog.Warning(
                    "WorldTransitionSync: GameController.keyItems field not found natively — " +
                    "cannot clear inventory via RemoveKeyItem.");
                return;
            }

            Il2CppSystem.Collections.Generic.List<GameObject> keyItemsList = null;
            try
            {
                keyItemsList = QBLib.Fields.ReadRef<Il2CppSystem.Collections.Generic.List<GameObject>>(
                    gc, "keyItems");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("ClearLocalInventory: Fields.ReadRef threw: " + ex.Message);
            }

            if (keyItemsList == null)
            {
                DiagnosticLog.Warning(
                    "WorldTransitionSync: keyItems present but ReadRef returned null — " +
                    "cannot clear inventory via RemoveKeyItem.");
                return;
            }

            int count = 0;
            try { count = keyItemsList.Count; }
            catch (Exception ex) { DiagnosticLog.Warning("ClearLocalInventory: Count threw: " + ex.Message); }

            if (count == 0) return;

            InventorySharingController.BulkClearingInventory = true;
            try
            {
                for (int i = count - 1; i >= 0; i--)
                {
                    GameObject item = null;
                    try { item = keyItemsList[i]; }
                    catch (Exception ex)
                    {
                        DiagnosticLog.Warning("ClearLocalInventory: indexer threw at " + i + ": " + ex.Message);
                        continue;
                    }

                    if (item == null) continue;

                    try { gc.RemoveKeyItem(item); }
                    catch (Exception ex) { DiagnosticLog.Warning("ClearLocalInventory: RemoveKeyItem threw: " + ex.Message); }
                }
            }
            finally
            {
                InventorySharingController.BulkClearingInventory = false;
            }

            DiagnosticLog.Info("WorldTransitionSync: cleared " + count + " inventory item(s) on world change.");
        }

        private static Type FindType(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(name, false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }
    }
}