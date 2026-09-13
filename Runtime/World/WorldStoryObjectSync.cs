using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer
{
    public sealed class WorldStoryObjectSync
    {
        private static WorldStoryObjectSync _active;

        private RpcDispatcher dispatcher;
        private string        localPlayerId;

        private float nextSampleTime;
        private float nextFullResyncTime;
        private const float SampleInterval     = 1.0f;
        private const float FullResyncInterval = 10f;

        private readonly List<KeyValuePair<string, GameObject>> _cachedQuestsNodes =
            new List<KeyValuePair<string, GameObject>>();
        private float _nextQuestsRecacheTime;
        private const float QuestsRecacheInterval = 60f;

        private readonly List<KeyValuePair<string, GameObject>> _cachedTrackedNodes =
            new List<KeyValuePair<string, GameObject>>();
        private float _nextTrackedRecacheTime;
        private const float TrackedRecacheInterval = 60f;

        private readonly Dictionary<string, bool> lastKnownActive = new Dictionary<string, bool>();
        private readonly HashSet<string>          syncedPaths     = new HashSet<string>();

        private readonly Dictionary<string, float> _lastTeleportByPath = new Dictionary<string, float>();
        private const float TeleportPathCooldown = 10f;

        private bool loggedFirstSend;
        private string lastSceneName;

        public void Configure(RpcDispatcher rpcDispatcher, string playerId)
        {
            dispatcher    = rpcDispatcher;
            localPlayerId = playerId;
            _active       = this;

            if (dispatcher != null)
            {
                dispatcher.RemoteStoryObjectStateReceived -= OnRemoteStoryObjectState;
                dispatcher.RemoteStoryObjectStateReceived += OnRemoteStoryObjectState;
            }
        }

        public static void ResetCachesForWorldChange()
        {
            WorldStoryObjectSync s = _active;
            if (s == null) return;

            s.lastKnownActive.Clear();
            s.syncedPaths.Clear();
            s._cachedQuestsNodes.Clear();
            s._cachedTrackedNodes.Clear();
            s._lastTeleportByPath.Clear();
            s._nextQuestsRecacheTime = 0f;
            s._nextTrackedRecacheTime = 0f;
            s.nextSampleTime     = 0f;
            s.nextFullResyncTime = 0f;

            DiagnosticLog.Info("WorldStoryObjectSync: caches cleared for world transition.");
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
                _cachedQuestsNodes.Clear();
                _cachedTrackedNodes.Clear();
                _lastTeleportByPath.Clear();
                _nextQuestsRecacheTime = 0f;
                _nextTrackedRecacheTime = 0f;
            }

            bool timeToSample = Time.unscaledTime >= nextSampleTime;
            bool timeToResync = Time.unscaledTime >= nextFullResyncTime;
            if (!timeToSample && !timeToResync) return;

            if (timeToSample) nextSampleTime     = Time.unscaledTime + SampleInterval;
            if (timeToResync) nextFullResyncTime = Time.unscaledTime + FullResyncInterval;

            SyncTrackedFromCache();
            SyncQuestsFromCache();
        }

        // ── Location*-registry feed ────────────────────────────────────────

        private void SyncTrackedFromCache()
        {
            if (_cachedTrackedNodes.Count == 0 ||
                Time.unscaledTime >= _nextTrackedRecacheTime)
            {
                RebuildTrackedCache();
            }

            for (int i = 0; i < _cachedTrackedNodes.Count; i++)
            {
                var kv = _cachedTrackedNodes[i];
                if (kv.Value == null) continue;
                SyncObject(kv.Key, kv.Value);
            }
        }

        private void RebuildTrackedCache()
        {
            _nextTrackedRecacheTime = Time.unscaledTime + TrackedRecacheInterval;
            _cachedTrackedNodes.Clear();

            WorldStoryObjectRegistry.EnsureScannedForCurrentScene();

            foreach (KeyValuePair<string, GameObject> pair in WorldStoryObjectRegistry.AllTracked)
            {
                GameObject go = pair.Value;
                if (go == null) continue;

                if (SubtreeHasDialogue3DText(go)) continue;

                _cachedTrackedNodes.Add(new KeyValuePair<string, GameObject>(pair.Key, go));
            }

            DiagnosticLog.Info("WorldStoryObjectSync: cached " + _cachedTrackedNodes.Count +
                               " tracked Location* node(s).");
        }

        // ── Quests subtree feed ────────────────────────────────────────────

        private void SyncQuestsFromCache()
        {
            if (_cachedQuestsNodes.Count == 0 ||
                Time.unscaledTime >= _nextQuestsRecacheTime)
            {
                RebuildQuestsCache();
            }

            for (int i = 0; i < _cachedQuestsNodes.Count; i++)
            {
                var kv = _cachedQuestsNodes[i];
                if (kv.Value == null) continue;
                SyncObject(kv.Key, kv.Value);
            }
        }

        private void RebuildQuestsCache()
        {
            _nextQuestsRecacheTime = Time.unscaledTime + QuestsRecacheInterval;
            _cachedQuestsNodes.Clear();

            GameObject worldGO = GameObject.Find("World");
            if (worldGO == null) return;

            Transform node = worldGO.transform.Find("Quests");
            if (node == null) return;

            CollectQuestsSubtree(node, _cachedQuestsNodes);

            DiagnosticLog.Info("WorldStoryObjectSync: cached " + _cachedQuestsNodes.Count +
                               " Quests node(s).");
        }

        private void CollectQuestsSubtree(Transform node,
                                          List<KeyValuePair<string, GameObject>> output)
        {
            if (node == null) return;

            // Skip this node and everything below it if it contains a
            // Dialogue_3DText anywhere in its subtree. Dialogue_3DText.Start
            // re-fires on every OnEnable and re-prints the pre-localized
            // string, which is what stacked on top of the localized text.
            if (SubtreeHasDialogue3DText(node.gameObject))
                return;

            string path = LocalPlayerLocator.GetPath(node);
            output.Add(new KeyValuePair<string, GameObject>(path, node.gameObject));

            for (int i = 0; i < node.childCount; i++)
                CollectQuestsSubtree(node.GetChild(i), output);
        }

        // ── Dialogue detection ─────────────────────────────────────────────

        private static bool SubtreeHasDialogue3DText(GameObject go)
        {
            if (go == null) return false;
            return SubtreeHasDialogue3DTextRecursive(go.transform);
        }

        private static bool SubtreeHasDialogue3DTextRecursive(Transform t)
        {
            if (t == null) return false;

            if (NodeHasDialogue3DText(t.gameObject)) return true;

            int childCount = t.childCount;
            for (int i = 0; i < childCount; i++)
            {
                Transform child = t.GetChild(i);
                if (child == null) continue;
                if (SubtreeHasDialogue3DTextRecursive(child)) return true;
            }

            return false;
        }

        private static bool NodeHasDialogue3DText(GameObject go)
        {
            if (go == null) return false;

            Component[] comps;
            try { comps = go.GetComponents<Component>(); }
            catch { return false; }
            if (comps == null) return false;

            for (int i = 0; i < comps.Length; i++)
            {
                Component c = comps[i];
                if (c == null) continue;
                if (c.GetType().Name == "Dialogue_3DText")
                    return true;
            }

            return false;
        }

        // ── Sync ───────────────────────────────────────────────────────────

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

                if (state.isActive && ShouldTeleportForPath(state.path))
                    TeleportLocalPlayerToSender(state);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning(
                    "WorldStoryObjectSync: failed to apply '" + state.path + "': " + ex.Message);
            }
        }

        private static bool ShouldTeleportForPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            if (path.IndexOf("TriggerRoom", StringComparison.OrdinalIgnoreCase) < 0 &&
                path.IndexOf("SpawnPoint", StringComparison.OrdinalIgnoreCase) < 0 &&
                path.IndexOf("Teleport", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            return true;
        }

        private void TeleportLocalPlayerToSender(WorldStoryObjectState state)
        {
            Transform localPlayer = LocalPlayerLocator.FindHardcodedPlayerPath();
            if (localPlayer == null) return;

            float lastTime;
            if (_lastTeleportByPath.TryGetValue(state.path, out lastTime) &&
                Time.unscaledTime - lastTime < TeleportPathCooldown)
                return;

            Vector3 targetPos = state.senderPosition.ToUnity();
            if (targetPos == Vector3.zero) return;
            if (targetPos.y < -1f) return;

            float dist = Vector3.Distance(localPlayer.position, targetPos);
            if (dist < 15f) return;

            _lastTeleportByPath[state.path] = Time.unscaledTime;

            localPlayer.position = targetPos;
            DiagnosticLog.Info(
                "WorldStoryObjectSync: teleported local player to '" +
                state.senderId + "' for quest '" + state.path + "' (cooldown " +
                TeleportPathCooldown + "s)");
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