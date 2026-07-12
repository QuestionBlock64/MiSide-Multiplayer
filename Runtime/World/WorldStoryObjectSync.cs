using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer
{
    /// <summary>
    /// Syncs GameObject.activeSelf for Location*-tagged objects and
    /// World/Quests. When a quest object activates, teleports the
    /// remote player to the sender so they don't miss quest events.
    /// </summary>
    public sealed class WorldStoryObjectSync
    {
        private RpcDispatcher dispatcher;
        private string        localPlayerId;

        private float nextSampleTime;
        private float nextFullResyncTime;
        private const float SampleInterval     = 0.5f;
        private const float FullResyncInterval = 10f;

        private readonly Dictionary<string, bool> lastKnownActive = new Dictionary<string, bool>();
        private readonly HashSet<string>          syncedPaths     = new HashSet<string>();

        private bool loggedFirstSend;
        private string lastSceneName;

        public void Configure(RpcDispatcher rpcDispatcher, string playerId)
        {
            dispatcher    = rpcDispatcher;
            localPlayerId = playerId;

            if (dispatcher != null)
            {
                dispatcher.RemoteStoryObjectStateReceived -= OnRemoteStoryObjectState;
                dispatcher.RemoteStoryObjectStateReceived += OnRemoteStoryObjectState;
            }
        }

        public void Tick()
        {
            if (dispatcher == null) return;

            string sceneName = SceneManager.GetActiveScene().name;
            if (sceneName != lastSceneName)
            {
                lastSceneName = sceneName;
                syncedPaths.Clear();
                lastKnownActive.Clear();
            }

            bool timeToSample = Time.unscaledTime >= nextSampleTime;
            bool timeToResync = Time.unscaledTime >= nextFullResyncTime;
            if (!timeToSample && !timeToResync) return;

            if (timeToSample) nextSampleTime     = Time.unscaledTime + SampleInterval;
            if (timeToResync) nextFullResyncTime = Time.unscaledTime + FullResyncInterval;

            WorldStoryObjectRegistry.EnsureScannedForCurrentScene();
            foreach (KeyValuePair<string, GameObject> pair in WorldStoryObjectRegistry.AllTracked)
            {
                if (pair.Value != null)
                    SyncObject(pair.Key, pair.Value);
            }

            SyncWorldSubHierarchy("Quests");
        }

        private void SyncWorldSubHierarchy(string subPath)
        {
            GameObject worldGO = GameObject.Find("World");
            if (worldGO == null) return;

            Transform node = worldGO.transform.Find(subPath);
            if (node == null) return;

            SyncHierarchyRecursive(node);
        }

        private void SyncHierarchyRecursive(Transform node)
        {
            if (node == null) return;

            string path = LocalPlayerLocator.GetPath(node);
            SyncObject(path, node.gameObject);

            for (int i = 0; i < node.childCount; i++)
                SyncHierarchyRecursive(node.GetChild(i));
        }

        private void SyncObject(string path, GameObject go)
        {
            if (go == null) return;

            bool currentActive = go.activeSelf;

            bool known;
            bool hasKnown = lastKnownActive.TryGetValue(path, out known);
            bool changed  = !hasKnown || known != currentActive;

            if (!changed && Time.unscaledTime < nextFullResyncTime) return;

            lastKnownActive[path] = currentActive;

            Transform localPlayer = LocalPlayerLocator.FindHardcodedPlayerPath();
            NetVector3 senderPos = new NetVector3();
            if (localPlayer != null)
                senderPos = NetVector3.FromUnity(localPlayer.position);

            WorldStoryObjectState msg = new WorldStoryObjectState();
            msg.senderId       = localPlayerId;
            msg.sceneName      = lastSceneName;
            msg.path           = path;
            msg.isActive       = currentActive;
            msg.senderPosition = senderPos;
            dispatcher.SendStoryObjectState(msg);

            if (!loggedFirstSend)
            {
                loggedFirstSend = true;
                DiagnosticLog.Info(
                    "WorldStoryObjectSync: first send '" + path + "' = " + currentActive);
            }
        }

        private void OnRemoteStoryObjectState(WorldStoryObjectState state)
        {
            if (state == null || string.IsNullOrEmpty(state.path)) return;
            if (!string.IsNullOrEmpty(localPlayerId) && state.senderId == localPlayerId) return;

            GameObject go = FindByPath(state.path);
            if (go == null)
            {
                string[] parts = state.path.Split('/');
                if (parts.Length > 0)
                    go = GameObject.Find(parts[parts.Length - 1]);
            }

            if (go == null) return;
            if (go.activeSelf == state.isActive) return;

            try
            {
                go.SetActive(state.isActive);
                lastKnownActive[state.path] = state.isActive;

                if (state.isActive && IsQuestPath(state.path))
                {
                    TeleportLocalPlayerToSender(state);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning(
                    "WorldStoryObjectSync: failed to apply '" + state.path + "': " + ex.Message);
            }
        }

        private bool IsQuestPath(string path)
        {
            return path.IndexOf("Quests", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void TeleportLocalPlayerToSender(WorldStoryObjectState state)
        {
            Transform localPlayer = LocalPlayerLocator.FindHardcodedPlayerPath();
            if (localPlayer == null) return;

            Vector3 targetPos = state.senderPosition.ToUnity();
            if (targetPos == Vector3.zero) return;

            float dist = Vector3.Distance(localPlayer.position, targetPos);
            if (dist < 5f) return;

            localPlayer.position = targetPos;
            DiagnosticLog.Info(
                "WorldStoryObjectSync: teleported local player to '" +
                state.senderId + "' for quest '" + state.path + "'");
        }

        private static GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            string[] parts = path.Split('/');
            if (parts.Length == 0) return null;

            Scene scene = SceneManager.GetActiveScene();
            GameObject[] roots = scene.GetRootGameObjects();

            for (int r = 0; r < roots.Length; r++)
            {
                if (roots[r].name == parts[0])
                {
                    Transform current = roots[r].transform;
                    bool found = true;
                    for (int i = 1; i < parts.Length; i++)
                    {
                        current = current.Find(parts[i]);
                        if (current == null)
                        {
                            found = false;
                            break;
                        }
                    }
                    if (found && current != null)
                        return current.gameObject;
                }
            }

            return null;
        }
    }
}