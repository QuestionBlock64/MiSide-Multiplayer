using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime;
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
        }

        private static MitaSampler _active;

        private RpcDispatcher rpcDispatcher;
        private string localPlayerId;
        private float sendRate = 1f / 60f;

        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        private float nextFindTime;
        private string lastSceneName;
        private const float FindInterval = 2f;

        public void Configure(RpcDispatcher dispatcher, string playerId, float rate)
        {
            rpcDispatcher = dispatcher;
            localPlayerId = playerId;
            sendRate = Mathf.Max(1f, rate);
            _active = this;
        }

        public static void ForceRescan()
        {
            MitaSampler s = _active;
            if (s == null) return;
            s._entries.Clear();
            s.nextFindTime = 0f;
            s.lastSceneName = SceneManager.GetActiveScene().name;
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
                nextFindTime = 0f;
            }

            if (Time.unscaledTime >= nextFindTime)
            {
                nextFindTime = Time.unscaledTime + FindInterval;
                RefreshInstances();
            }

            if (_entries.Count == 0) return;

            float interval = 1f / sendRate;
            MitaEvent[] pending = MitaEventSync.DrainPending(true);

            foreach (var kv in _entries)
            {
                string path = kv.Key;
                Entry e = kv.Value;
                if (e == null || e.MitaPerson == null || e.MitaRoot == null) continue;
                if (!e.MitaPerson.gameObject.activeInHierarchy) continue;

                if (Time.unscaledTime < e.NextSendTime) continue;
                e.NextSendTime = Time.unscaledTime + interval;

                try
                {
                    MitaState state = new MitaState();
                    state.senderId = localPlayerId;
                    state.hostSendTime = Time.unscaledTime;
                    state.sceneName = lastSceneName;
                    state.mitaPath = path;
                    state.position = NetVector3.FromUnity(e.MitaRoot.position);
                    state.rotation = NetQuaternion.FromUnity(e.MitaRoot.rotation);
                    state.Bones = SampleBones(e.PersonRoot);
                    state.gazeTargetPlayerId = MitaGazeState.GetCurrent(path);
                    state.events = FilterEvents(pending, path);

                    if (e.Nma != null)
                    {
                        state.navEnabled = e.Nma.enabled;
                        state.navDestination = NetVector3.FromUnity(e.Nma.destination);
                    }

                    if (e.MagnetTargetField != null)
                    {
                        Transform magnet = e.MagnetTargetField.GetValue(e.MitaPerson) as Transform;
                        state.magnetActive = magnet != null;
                        if (magnet != null)
                        {
                            state.magnetTargetPos = NetVector3.FromUnity(magnet.position);
                            state.magnetTargetRot = NetQuaternion.FromUnity(magnet.rotation);
                        }
                    }

                    rpcDispatcher.SendMitaState(state);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warning("MitaSampler.Tick failed for '" + path + "': " + ex.Message);
                }
            }
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
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded) return;
            lastSceneName = scene.name;

            HashSet<string> seen = new HashSet<string>();

            UnityEngine.Object[] allObjects = Resources.FindObjectsOfTypeAll(Il2CppType.Of<GameObject>());
            for (int i = 0; i < allObjects.Length; i++)
            {
                GameObject go = allObjects[i].TryCast<GameObject>();
                if (go == null) continue;
                if (!go.activeInHierarchy) continue;
                if (!go.scene.IsValid() || !go.scene.isLoaded) continue;

                Component comp = go.GetComponent("MitaPerson");
                if (comp == null) continue;

                string path = LocalPlayerLocator.GetPath(go.transform);
                seen.Add(path);

                Entry existing;
                if (_entries.TryGetValue(path, out existing) && existing.MitaPerson == comp)
                    continue;

                Entry e = new Entry();
                e.GO = go;
                e.MitaPerson = comp;
                e.MitaRoot = go.transform;
                e.PersonRoot = FindPersonInChildren(go.transform);
                e.Nma = comp.GetComponent<NavMeshAgent>();
                e.MagnetTargetField = comp.GetType().GetField("magnetTarget", BindingFlags.NonPublic | BindingFlags.Instance);
                e.NextSendTime = 0f;

                _entries[path] = e;
                DiagnosticLog.Info("MitaSampler: bound to '" + go.name + "' at '" + path + "'.");
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

        private static Transform FindPersonInChildren(Transform root)
        {
            if (root == null) return null;
            if (root.name == "Person") return root;
            Transform person = root.Find("Person");
            if (person != null) return person;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform result = FindPersonInChildren(root.GetChild(i));
                if (result != null) return result;
            }
            return null;
        }

        private static Dictionary<string, BoneTransformData> SampleBones(Transform personRoot)
        {
            if (personRoot == null) return null;
            var result = new Dictionary<string, BoneTransformData>(BoneSyncData.Count);
            for (int i = 0; i < BoneSyncData.Count; i++)
            {
                string path = BoneSyncData.BonePaths[i];
                if (string.IsNullOrEmpty(path)) continue;
                Transform bone = personRoot.Find(path);
                if (bone == null) continue;
                BoneTransformData data = new BoneTransformData();
                data.PosX = bone.localPosition.x;
                data.PosY = bone.localPosition.y;
                data.PosZ = bone.localPosition.z;
                data.RotX = bone.localRotation.x;
                data.RotY = bone.localRotation.y;
                data.RotZ = bone.localRotation.z;
                data.RotW = bone.localRotation.w;
                result[path] = data;
            }
            return result;
        }
    }
}