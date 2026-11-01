using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer
{
    /// <summary>
    /// Indexes GameObjects that carry a "story progression" script, so their
    /// active/inactive state can be synced between players. Matches any
    /// component whose type name is "Location" followed by a digit.
    /// </summary>
    public static class WorldStoryObjectRegistry
    {
        private static readonly Dictionary<string, GameObject> objectsByPath =
            new Dictionary<string, GameObject>();

        private static string scannedForScene;

        public static int TrackedCount { get { return objectsByPath.Count; } }

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
            objectsByPath.Clear();
        }

        private static void RescanNow(string sceneName)
        {
            scannedForScene = sceneName;
            objectsByPath.Clear();

            try
            {
                GameObject[] roots = SceneManager.GetActiveScene().GetRootGameObjects();
                int found = 0;

                for (int i = 0; i < roots.Length; i++)
                {
                    if (roots[i] == null) continue;
                    found += ScanRecursive(roots[i].transform);
                }

                DiagnosticLog.Info(
                    "WorldStoryObjectRegistry: scanned scene '" + sceneName +
                    "' — found " + found + " Location*-tagged object(s).");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning(
                    "WorldStoryObjectRegistry: scan failed for scene '" + sceneName +
                    "': " + ex.Message);
            }
        }

        private static int ScanRecursive(Transform node)
        {
            if (node == null) return 0;
            int found = 0;

            try
            {
                if (HasLocationScript(node.gameObject))
                {
                    string path = LocalPlayerLocator.GetPath(node);
                    if (!string.IsNullOrEmpty(path))
                    {
                        objectsByPath[path] = node.gameObject;
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

        private static bool HasLocationScript(GameObject go)
        {
            Component[] components = go.GetComponents<Component>();
            if (components == null) return false;

            for (int i = 0; i < components.Length; i++)
            {
                if (components[i] == null) continue;
                string typeName = components[i].GetType().Name;
                if (IsLocationScriptName(typeName))
                    return true;
            }
            return false;
        }

        // Matches "Location" followed immediately by a digit.
        private static bool IsLocationScriptName(string typeName)
        {
            const string prefix = "Location";
            if (string.IsNullOrEmpty(typeName) || typeName.Length <= prefix.Length)
                return false;
            if (!typeName.StartsWith(prefix, StringComparison.Ordinal))
                return false;
            return char.IsDigit(typeName[prefix.Length]);
        }

        public static Dictionary<string, GameObject> AllTracked { get { return objectsByPath; } }

        public static bool TryGetObject(string path, out GameObject go)
        {
            return objectsByPath.TryGetValue(path, out go);
        }
    }
}