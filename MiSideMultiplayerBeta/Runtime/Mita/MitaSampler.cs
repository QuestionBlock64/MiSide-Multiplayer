using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace MiSideMultiplayer
{
    public sealed class MitaSampler
    {
        private sealed class Entry
        {
            public Component MitaPerson;
            public Transform MitaRoot;
            public Transform PersonRoot;
            public NavMeshAgent Nma;
            public FieldInfo MagnetTargetField;
            public GameObject GO;
            public float NextSendTime;
            public bool HasNavDestination;
            public bool WarnedInvalidDestination;
            public bool WarnedInvalidRootPose;
            public Dictionary<string, BoneTransformData> LastGoodBones;
            public float LastGoodBoneSampleTime;
            public Vector3 LastNavDestination;
            public float NextHeartbeatLogTime;
            public float NextEmptyBoneSampleLogTime;
            public float NextPersonRootScanTime;
        }

        private static MitaSampler _active;

        private RpcDispatcher rpcDispatcher;
        private string localPlayerId;
        // Ten authoritative bone snapshots per second keep NPC motion current
        // without letting full-rig poses crowd the relay connection.
        private const float StateHeartbeatSeconds = 0.10f;
        private const float SyncDiagnosticIntervalSeconds = 5f;

        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        private readonly List<Transform> _scanStack = new List<Transform>(256);
        private float nextFindTime;
        private string lastSceneName;
        private const float FindInterval = 2f;

        public void Configure(RpcDispatcher dispatcher, string playerId, float rate)
        {
            rpcDispatcher = dispatcher;
            localPlayerId = playerId;
            MitaEventSync.Configure(dispatcher, playerId);
            MitaSnapshotSync.Configure(dispatcher);
            _active = this;
        }

        public static void SendSnapshotNow()
        {
            MitaSampler sampler = _active;
            if (sampler == null || !MitaHost.IsHost) return;

            sampler.nextFindTime = 0f;
            sampler.RefreshInstances();
            foreach (Entry entry in sampler._entries.Values)
                if (entry != null) entry.NextSendTime = 0f;
            sampler.Tick();
            sampler.LateTick();
        }

        public static void TickLate()
        {
            MitaSampler sampler = _active;
            if (sampler != null) sampler.LateTick();
        }

        public static void SendFullSnapshot()
        {
            MitaSampler sampler = _active;
            if (sampler == null || sampler.rpcDispatcher == null || !MitaHost.IsHost) return;

            try
            {
                string sceneName = SceneManager.GetActiveScene().name;
                if (sceneName != sampler.lastSceneName)
                {
                    sampler.lastSceneName = sceneName;
                    sampler._entries.Clear();
                    MitaBoneSync.ClearBindings();
                    MitaEventSync.ResetForScene(sceneName);
                }

                sampler.nextFindTime = 0f;
                sampler.RefreshInstances();

                List<KeyValuePair<string, Entry>> active = new List<KeyValuePair<string, Entry>>();
                foreach (KeyValuePair<string, Entry> pair in sampler._entries)
                {
                    Entry entry = pair.Value;
                    if (MinigameSync.IsGameCardMitaPath(pair.Key) ||
                        entry == null || entry.MitaPerson == null || entry.MitaRoot == null ||
                        !entry.MitaPerson.gameObject.activeInHierarchy)
                        continue;
                    RefreshPersonRoot(entry);
                    if (!HasFiniteRootPose(entry))
                    {
                        WarnInvalidRootPose(entry, pair.Key);
                        continue;
                    }
                    active.Add(pair);
                }

                // Include the host's current movement command in the baseline,
                // even if it did not change during this scene.
                for (int i = 0; i < active.Count; i++)
                {
                    string path = active[i].Key;
                    Entry entry = active[i].Value;
                    EmitMoveTargetChange(path, entry);
                }

                string snapshotId = Guid.NewGuid().ToString("N");
                long lastEventSequence = MitaEventSync.LastSequence;

                for (int i = 0; i < active.Count; i++)
                {
                    KeyValuePair<string, Entry> pair = active[i];
                    MitaState state = sampler.CreateState(pair.Key, pair.Value, true);
                    state.sceneName = sceneName;
                    state.isFullSnapshot = true;
                    state.snapshotId = snapshotId;
                    state.snapshotIndex = i;
                    state.snapshotCount = active.Count;
                    state.lastEventSequence = lastEventSequence;
                    state.events = MitaEventSync.GetSnapshotEvents(pair.Key);
                    sampler.rpcDispatcher.SendMitaState(state);
                    DiagnosticLog.Verbose("MitaSampler: sent snapshot record " + (i + 1) + "/" +
                                          active.Count + " path='" + pair.Key + "' pos=" +
                                          state.position.ToUnity() + " bones=" +
                                          (state.Bones == null ? 0 : state.Bones.Count) + " events=" +
                                          (state.events == null ? 0 : state.events.Length) + ".");
                }

                MitaSnapshotSync.SendComplete(snapshotId, sceneName, active.Count);
                DiagnosticLog.Info("MitaSampler: sent host snapshot '" + snapshotId + "' with " +
                                   active.Count + " Mita state record(s) for '" + sceneName + "'.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MitaSampler.SendFullSnapshot failed: " + ex.Message);
            }
        }

        public static void ForceRescan()
        {
            MitaSampler s = _active;
            if (s == null) return;
            s._entries.Clear();
            s.nextFindTime = 0f;
            string sceneName = SceneManager.GetActiveScene().name;
            if (sceneName != s.lastSceneName) MitaEventSync.ResetForScene(sceneName);
            s.lastSceneName = sceneName;
            MitaBoneSync.ClearBindings();
            s.RefreshInstances();
            DiagnosticLog.Info("MitaSampler: ForceRescan complete — " + s._entries.Count + " instance(s).");
        }

        public void Tick()
        {
            if (rpcDispatcher == null) return;

            if (!MitaHost.IsHost) return;

            string sceneName = SceneManager.GetActiveScene().name;
            if (sceneName != lastSceneName)
            {
                lastSceneName = sceneName;
                _entries.Clear();
                MitaBoneSync.ClearBindings();
                nextFindTime = 0f;
                MitaEventSync.ResetForScene(sceneName);
            }

            if (Time.unscaledTime >= nextFindTime)
            {
                nextFindTime = Time.unscaledTime + FindInterval;
                RefreshInstances();
            }
        }

        public void LateTick()
        {
            bool isHost = MitaHost.IsHost;
            if (rpcDispatcher == null || !isHost) return;

            if (_entries.Count == 0) return;

            float interval = StateHeartbeatSeconds;
            MitaEvent[] pending = MitaEventSync.DrainPending(isHost);

            foreach (var kv in _entries)
            {
                string path = kv.Key;
                Entry e = kv.Value;
                if (e == null || e.MitaPerson == null || e.MitaRoot == null) continue;
                // Game Card is a fully local minigame. Its Mita and animation
                // state must never be sent through the general Mita channel.
                if (MinigameSync.IsGameCardMitaPath(path)) continue;
                try
                {
                    if (!e.MitaPerson.gameObject.activeInHierarchy) continue;
                    RefreshPersonRoot(e);
                    if (!HasFiniteRootPose(e))
                    {
                        WarnInvalidRootPose(e, path);
                        continue;
                    }

                    if (Time.unscaledTime < e.NextSendTime) continue;
                    e.NextSendTime = Time.unscaledTime + interval;

                    if (isHost) EmitMoveTargetChange(path, e);

                    MitaState state = CreateState(path, e, true);
                    state.events = isHost
                        ? FilterEvents(pending, path)
                        : new MitaEvent[0];
                    rpcDispatcher.SendMitaState(state);
                    if (Time.unscaledTime >= e.NextHeartbeatLogTime)
                    {
                        e.NextHeartbeatLogTime = Time.unscaledTime + SyncDiagnosticIntervalSeconds;
                        DiagnosticLog.Info("[MITA-SYNC TX] authority='" + localPlayerId + "' scene='" +
                                           state.sceneName + "' path='" + path + "' pos=" +
                                           state.position.ToUnity() + " bones=" +
                                            (state.Bones == null ? 0 : state.Bones.Count) +
                                            " lifeEmotion=" + (state.lifeEmotion != null) +
                                           " sentToDispatcher=true.");
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warning("MitaSampler.Tick failed for '" + path + "': " + ex.Message);
                }
            }
        }

        private MitaState CreateState(string path, Entry entry, bool includeBones)
        {
            MitaState state = new MitaState();
            state.senderId = localPlayerId;
            state.hostSendTime = Time.unscaledTime;
            state.sceneName = lastSceneName;
            state.mitaPath = path;
            Transform poseRoot = entry.PersonRoot != null ? entry.PersonRoot : entry.MitaRoot;
            state.position = NetVector3.FromUnity(poseRoot.position);
            state.rotation = NetQuaternion.FromUnity(poseRoot.rotation);
            state.lifeEmotion = MitaLifeEmotionSync.Capture(entry.GO);
            if (includeBones)
            {
                Dictionary<string, BoneTransformData> sampledBones = SampleBones(entry.PersonRoot);
                if (sampledBones != null && sampledBones.Count > 0)
                {
                    entry.LastGoodBones = sampledBones;
                    entry.LastGoodBoneSampleTime = Time.unscaledTime;
                    state.Bones = sampledBones;
                }
                else
                {
                    state.Bones = entry.LastGoodBones;
                    if (Time.unscaledTime >= entry.NextEmptyBoneSampleLogTime)
                    {
                        entry.NextEmptyBoneSampleLogTime =
                            Time.unscaledTime + SyncDiagnosticIntervalSeconds;
                        int currentTransforms = MitaBoneSync.CountSyncTransforms(entry.PersonRoot);
                        DiagnosticLog.Warning("[MITA-SYNC SAMPLE] path='" + path +
                                              "' sampledBones=0 rigTransforms=" + currentTransforms +
                                              " lastGoodBones=" +
                                              (entry.LastGoodBones == null ? 0 : entry.LastGoodBones.Count) +
                                              " staleSeconds=" +
                                              (entry.LastGoodBoneSampleTime <= 0f
                                                  ? "unknown"
                                                  : (Time.unscaledTime - entry.LastGoodBoneSampleTime).ToString("F2")) +
                                              "; holding the last host pose during rig rebuild and retrying. " +
                                              MitaBoneSync.DescribeRig(entry.PersonRoot));
                    }
                }
            }
            else
                state.Bones = null;

            state.gazeTargetPlayerId = MitaGazeState.GetCurrent(path);

            if (entry.Nma != null)
            {
                state.navEnabled = entry.Nma.enabled;
                Vector3 destination;
                if (TryGetFiniteAgentDestination(entry.Nma, out destination))
                    state.navDestination = NetVector3.FromUnity(destination);
                else if (HasAgentPath(entry.Nma) && !entry.WarnedInvalidDestination)
                {
                    entry.WarnedInvalidDestination = true;
                    DiagnosticLog.Warning("[MITA-SYNC NAV] path='" + path +
                                          "' NavMeshAgent reported a non-finite destination; using the zero default.");
                }
            }

            if (entry.MagnetTargetField != null)
            {
                Transform magnet = entry.MagnetTargetField.GetValue(entry.MitaPerson) as Transform;
                if (magnet != null)
                {
                    if (IsFinite(magnet.position) && IsFinite(magnet.rotation))
                    {
                        state.magnetActive = true;
                        state.magnetTargetPos = NetVector3.FromUnity(magnet.position);
                        state.magnetTargetRot = NetQuaternion.FromUnity(magnet.rotation);
                    }
                }
            }

            return state;
        }

        private static MitaEvent[] FilterEvents(MitaEvent[] all, string path)
        {
            if (all == null || all.Length == 0) return new MitaEvent[0];
            List<MitaEvent> keep = null;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                if (all[i].mitaPath == path)
                {
                    if (keep == null) keep = new List<MitaEvent>();
                    keep.Add(all[i]);
                }
            }
            return keep == null ? new MitaEvent[0] : keep.ToArray();
        }

        private void RefreshInstances()
        {
            try
            {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded) return;
            lastSceneName = scene.name;

            HashSet<string> seen = new HashSet<string>();
            GameObject[] sceneRoots = scene.GetRootGameObjects();
            _scanStack.Clear();
            for (int i = sceneRoots.Length - 1; i >= 0; i--)
            {
                GameObject root = sceneRoots[i];
                if (root != null) _scanStack.Add(root.transform);
            }

            while (_scanStack.Count > 0)
            {
                int last = _scanStack.Count - 1;
                Transform current = _scanStack[last];
                _scanStack.RemoveAt(last);
                if (current == null) continue;

                GameObject go = current.gameObject;
                if (go == null || !go.activeInHierarchy) continue;

                Component comp = go.GetComponent("MitaPerson");
                if (comp != null)
                {
                    string path = MitaBoneSync.GetNetworkPath(go.transform);
                    if (!seen.Add(path))
                        DiagnosticLog.Warning("MitaSampler: duplicate Mita network path '" + path +
                                              "'; hierarchy identity is ambiguous on this scene.");

                    Entry existing;
                    if (!_entries.TryGetValue(path, out existing) || existing.MitaPerson != comp)
                    {
                        Entry e = new Entry();
                        e.GO = go;
                        e.MitaPerson = comp;
                        e.MitaRoot = go.transform;
                        e.PersonRoot = MitaBoneSync.FindPersonRoot(go.transform);
                        e.Nma = MitaUnityAccess.ResolveAgent(comp, go);
                        e.MagnetTargetField = comp.GetType().GetField("magnetTarget", BindingFlags.NonPublic | BindingFlags.Instance);
                        e.NextSendTime = 0f;

                        _entries[path] = e;
                        int syncTransforms = MitaBoneSync.CountSyncTransforms(e.PersonRoot);
                        DiagnosticLog.Info("MitaSampler: bound to '" + go.name + "' at '" + path +
                                           "' personRoot='" + (e.PersonRoot != null ? e.PersonRoot.name : "<missing>") +
                                           "' poseRoot='" + (e.PersonRoot != null
                                               ? MitaBoneSync.GetNetworkPath(e.PersonRoot)
                                               : "<missing>") +
                                           "' syncTransforms=" + syncTransforms + " agent=" +
                                           (e.Nma != null ? "bound" : "<missing>") + ".");
                        if (syncTransforms == 0)
                            DiagnosticLog.Warning("MitaSampler: no Armature transforms found under '" +
                                                  path + "'; remote pose will not update.");
                    }
                }

                for (int i = current.childCount - 1; i >= 0; i--)
                {
                    Transform child = current.GetChild(i);
                    if (child != null) _scanStack.Add(child);
                }
            }

            List<string> toRemove = null;
            foreach (var kv in _entries)
            {
                if (!seen.Contains(kv.Key))
                {
                    if (toRemove == null) toRemove = new List<string>();
                    toRemove.Add(kv.Key);
                }
            }
            if (toRemove != null)
            {
                for (int i = 0; i < toRemove.Count; i++)
                {
                    _entries.Remove(toRemove[i]);
                    MitaGazeState.ClearCurrent(toRemove[i]);
                    DiagnosticLog.Info("MitaSampler: unbind '" + toRemove[i] + "' (gone).");
                }
            }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("MitaSampler.RefreshInstances failed in scene '" +
                                      SceneManager.GetActiveScene().name + "': " +
                                      ex.GetType().Name + ": " + ex.Message +
                                      "; keeping current bindings and retrying.");
            }
        }

        private static void EmitMoveTargetChange(string path, Entry entry)
        {
            if (!HasAgentPath(entry.Nma)) return;
            Vector3 destination;
            if (!TryGetFiniteAgentDestination(entry.Nma, out destination))
            {
                if (!entry.WarnedInvalidDestination)
                {
                    entry.WarnedInvalidDestination = true;
                    DiagnosticLog.Warning("[MITA-SYNC NAV] path='" + path +
                                          "' ignored a non-finite NavMeshAgent destination.");
                }
                return;
            }
            if (entry.HasNavDestination && Vector3.Distance(entry.LastNavDestination, destination) <= 0.15f)
                return;

            entry.HasNavDestination = true;
            entry.LastNavDestination = destination;
            MitaEventSync.SendMoveToNode(path, "", destination);
        }

        private static Dictionary<string, BoneTransformData> SampleBones(Transform personRoot)
        {
            return MitaBoneSync.Sample(personRoot);
        }

        private static void RefreshPersonRoot(Entry entry)
        {
            if (entry == null || entry.MitaRoot == null) return;
            if (entry.PersonRoot != null && Time.unscaledTime < entry.NextPersonRootScanTime) return;

            entry.NextPersonRootScanTime = Time.unscaledTime + 0.5f;
            Transform previous = entry.PersonRoot;
            Transform refreshed = MitaBoneSync.FindPersonRoot(entry.MitaRoot);
            if (refreshed == null || refreshed == previous) return;

            entry.PersonRoot = refreshed;
            DiagnosticLog.Info("[MITA-SYNC RIG] host refreshed pose root at '" +
                               MitaBoneSync.GetNetworkPath(entry.MitaRoot) + "' to '" +
                               MitaBoneSync.GetNetworkPath(refreshed) + "' with " +
                               MitaBoneSync.CountSyncTransforms(refreshed) +
                               " current rig transforms.");
        }

        private static bool HasFiniteRootPose(Entry entry)
        {
            Transform poseRoot = entry == null
                ? null
                : (entry.PersonRoot != null ? entry.PersonRoot : entry.MitaRoot);
            return poseRoot != null && IsFinite(poseRoot.position) && IsFinite(poseRoot.rotation);
        }

        private static void WarnInvalidRootPose(Entry entry, string path)
        {
            if (entry == null || entry.WarnedInvalidRootPose) return;
            entry.WarnedInvalidRootPose = true;
            DiagnosticLog.Warning("[MITA-SYNC ROOT] path='" + path +
                                  "' has a non-finite root position/rotation; snapshot skipped.");
        }

        private static bool TryGetFiniteAgentDestination(NavMeshAgent agent, out Vector3 destination)
        {
            destination = Vector3.zero;
            if (!HasAgentPath(agent)) return false;
            try
            {
                Vector3 candidate = agent.destination;
                if (!IsFinite(candidate)) return false;
                destination = candidate;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool HasAgentPath(NavMeshAgent agent)
        {
            if (agent == null) return false;
            try { return agent.enabled && agent.isOnNavMesh && agent.hasPath; }
            catch { return false; }
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(Quaternion value)
        {
            return IsFinite(value.x) && IsFinite(value.y) &&
                   IsFinite(value.z) && IsFinite(value.w);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
