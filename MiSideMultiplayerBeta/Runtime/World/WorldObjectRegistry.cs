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
    /// string identifier.
    /// </summary>
    public static class WorldObjectRegistry
    {
        public const string DoorTypeName = "ObjectDoor";

        private static readonly Dictionary<string, Component> doorsByPath =
            new Dictionary<string, Component>();

        private static string scannedForScene;

        public static int DoorCount { get { return doorsByPath.Count; } }

        public static void EnsureScannedForCurrentScene()
        {
            string sceneName = SceneManager.GetActiveScene().name;
            if (sceneName == scannedForScene)
                return;

            RescanNow(sceneName);
        }

        /// <summary>
        /// Force a full rescan on next access, even if the scene name hasn't
        /// changed. Used by WorldTransitionSync when a new "world" activates
        /// inside the same scene.
        /// </summary>
        public static void ForceRescan()
        {
            scannedForScene = null;
            doorsByPath.Clear();
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

        public static Dictionary<string, Component> AllDoors { get { return doorsByPath; } }

        public static bool TryGetDoor(string path, out Component door)
        {
            return doorsByPath.TryGetValue(path, out door);
        }
    }
}