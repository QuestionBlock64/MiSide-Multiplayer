using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer
{
    /// <summary>
    /// Scans the active scene for known stateful world-object component types
    /// and indexes them by a STABLE PATH (GameController/World/.../ObjectName)
    /// so both players' clients can refer to the SAME object with the SAME
    /// string identifier — they run the same scene layout, so the path is
    /// deterministic on both ends without any explicit ID system.
    ///
    /// SCOPE (first pass): "ObjectDoor" only — confirmed via IL2CPP dump as a
    /// generic, reusable component with a single authoritative bool ("open")
    /// that the component's own Update()/LateUpdate() reads to drive the
    /// smooth rotation/sound animation. We only need to feed the correct
    /// value in; MiSide's own code handles the visual entirely, the same
    /// philosophy that already worked for animator parameters.
    ///
    /// ObjectDoor is part of the base game assembly (not a separate mod DLL),
    /// so unlike CustomModelBridge we don't need assembly-level reflection —
    /// GameObject.GetComponent(string) finds it directly by type name.
    /// </summary>
    public static class WorldObjectRegistry
    {
        public const string DoorTypeName = "ObjectDoor";

        private static readonly Dictionary<string, Component> doorsByPath =
            new Dictionary<string, Component>();

        private static string scannedForScene;

        public static int DoorCount { get { return doorsByPath.Count; } }

        /// <summary>
        /// Rescans the active scene if it has changed since the last scan.
        /// Cheap to call every tick — only does real work on a scene change.
        /// </summary>
        public static void EnsureScannedForCurrentScene()
        {
            string sceneName = SceneManager.GetActiveScene().name;
            if (sceneName == scannedForScene)
                return;

            RescanNow(sceneName);
        }

        private static void RescanNow(string sceneName)
        {
            scannedForScene = sceneName;
            doorsByPath.Clear();

            try
            {
                GameObject[] roots = SceneManager.GetActiveScene().GetRootGameObjects();
                int doorsFound = 0;

                for (int i = 0; i < roots.Length; i++)
                {
                    if (roots[i] == null) continue;
                    doorsFound += ScanRecursive(roots[i].transform);
                }

                DiagnosticLog.Info(
                    "WorldObjectRegistry: scanned scene '" + sceneName + "' — found " +
                    doorsFound + " door(s).");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning(
                    "WorldObjectRegistry: scan failed for scene '" + sceneName + "': " + ex.Message);
            }
        }

        private static int ScanRecursive(Transform node)
        {
            if (node == null) return 0;
            int found = 0;

            try
            {
                Component door = node.GetComponent(DoorTypeName);
                if (door != null)
                {
                    string path = LocalPlayerLocator.GetPath(node);
                    if (!string.IsNullOrEmpty(path))
                    {
                        doorsByPath[path] = door;
                        found = 1;
                    }
                }
            }
            catch (Exception)
            {
                // A single bad node shouldn't abort the whole scan.
            }

            int childCount = node.childCount;
            for (int i = 0; i < childCount; i++)
                found += ScanRecursive(node.GetChild(i));

            return found;
        }

        /// <summary>Path → Component for every known door. Do not mutate.</summary>
        public static Dictionary<string, Component> AllDoors { get { return doorsByPath; } }

        public static bool TryGetDoor(string path, out Component door)
        {
            return doorsByPath.TryGetValue(path, out door);
        }
    }
}
